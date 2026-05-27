// =============================================================================
// GameRoom.cs
// Manages one active game room — owns the GameState, drives the game loop,
// routes player actions, and broadcasts filtered events to each connection.
//
// Thread safety: all GameState mutations run inside _gameSem (SemaphoreSlim 1,1).
// Event handlers queue outbound messages; they are flushed after the semaphore
// is released so no IO happens while holding the lock.
// =============================================================================

using System.Text.Json;
using RiichiMahjong.AI;
using RiichiMahjong.Core;
using RiichiServer.Messages;

namespace RiichiServer
{
    public class GameRoom
    {
        // ---- Constants -------------------------------------------------------
        private const int  ClaimWindowSeconds = 8;
        private const int  AiThinkMs          = 800;
        private const int  AiClaimMs          = 400;
        private const int  HandResultPauseMs  = 3000;   // Show score before next hand
        private const int  MaxPlayers         = 4;

        // ---- Identity --------------------------------------------------------
        public  string  Code        { get; }
        public  bool    GameStarted { get; private set; }

        // ---- Players (index = global seat 0-3) --------------------------------
        // null entry = CPU seat
        private readonly PlayerConnection?[] _connections = new PlayerConnection?[MaxPlayers];
        private readonly string[]            _names       = ["CPU 1", "CPU 2", "CPU 3", "CPU 4"];
        private readonly string?[]           _playerUuids = new string?[MaxPlayers];  // for reconnection
        private int                          _hostSeat    = 0;
        private int                          _playerCount = 0;   // humans currently in lobby

        // ---- Game logic -------------------------------------------------------
        private GameState?  _game;
        private AIPlayer[]  _ai   = Enumerable.Range(0, 4)
                                               .Select(_ => new AIPlayer(AIDifficulty.Medium))
                                               .ToArray();

        // ---- Thread safety ----------------------------------------------------
        private readonly SemaphoreSlim _gameSem = new(1, 1);

        // ---- Outbox (filled during synchronous events, flushed after) ----------
        // Item: (targetSeat or -1 for broadcast, message)
        private readonly List<(int seat, ServerMessage msg)> _outbox = new();

        // ---- Claim window coordination ----------------------------------------
        // Set by OnTileDiscarded_Handler; completed by human claim/pass action
        private TaskCompletionSource<string>? _claimTcs;   // value = "pon"|"chi"|"ron"|"kan"|"pass"
        private CancellationTokenSource?      _claimCts;
        private int                           _claimWindowSeat = -1;  // human seat waiting

        // ---- Next-hand gate ---------------------------------------------------
        // Human host must send NextHand before we advance
        private TaskCompletionSource<bool>? _nextHandTcs;

        // =====================================================================
        public GameRoom(string code)
        {
            Code = code;
        }

        // =====================================================================
        // Lobby management
        // =====================================================================

        /// <summary>
        /// Add a human player to the room. Returns their assigned seat, or -1 if full.
        /// </summary>
        public int AddPlayer(PlayerConnection conn)
        {
            if (GameStarted) return -1;

            for (int seat = 0; seat < MaxPlayers; seat++)
            {
                if (_connections[seat] == null)
                {
                    _connections[seat]   = conn;
                    _names[seat]         = conn.DisplayName;
                    _playerUuids[seat]   = conn.PlayerUuid;
                    conn.Seat            = seat;
                    _playerCount++;
                    if (_playerCount == 1) _hostSeat = seat;
                    return seat;
                }
            }
            return -1;   // full
        }

        public void RemovePlayer(PlayerConnection conn)
        {
            int seat = conn.Seat;
            if (seat < 0 || seat >= MaxPlayers) return;
            if (_connections[seat] != conn) return;

            _connections[seat] = null;
            _playerCount--;

            // Re-assign host if needed
            if (seat == _hostSeat)
            {
                for (int s = 0; s < MaxPlayers; s++)
                {
                    if (_connections[s] != null) { _hostSeat = s; break; }
                }
            }

            // If game is running, let claim/next-hand waiters know
            _claimTcs?.TrySetResult("pass");
            _nextHandTcs?.TrySetResult(true);
        }

        public bool IsEmpty => _playerCount == 0;
        public bool IsHost(PlayerConnection conn) => conn.Seat == _hostSeat;

        /// <summary>
        /// Reconnect a player who dropped during the lobby phase (before game started).
        /// Finds seat by UUID, restores the connection, returns true on success.
        /// Caller should send roomJoined + broadcast playerJoined after this returns true.
        /// </summary>
        public bool RejoinLobby(string uuid, PlayerConnection newConn)
        {
            if (GameStarted) return false;

            int seat = Array.IndexOf(_playerUuids, uuid);
            if (seat < 0) return false;

            // Only reclaim if the slot is genuinely empty (player fully disconnected)
            if (_connections[seat] != null) return false;

            newConn.Seat        = seat;
            newConn.DisplayName = _names[seat];
            _connections[seat]  = newConn;
            _playerCount++;

            // Restore host if this was the host seat and no-one else claimed it
            if (_playerCount == 1) _hostSeat = seat;

            return true;
        }

        /// <summary>
        /// Attempt to reconnect a player mid-game using their client-generated UUID.
        /// Replaces the dead connection, sends a full state snapshot, returns true on success.
        /// </summary>
        public async Task<bool> RejoinAsync(string uuid, PlayerConnection newConn)
        {
            if (_game == null) return false;

            // Find the seat that belongs to this UUID
            int seat = -1;
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_playerUuids[s] == uuid) { seat = s; break; }
            }
            if (seat < 0) return false;

            // Swap in the new connection
            newConn.Seat        = seat;
            newConn.DisplayName = _names[seat];
            _connections[seat]  = newConn;
            _playerCount        = Array.IndexOf(_connections, null) < 0
                                  ? MaxPlayers
                                  : _connections.Count(c => c != null);

            // Build and send a full state snapshot under the lock
            await _gameSem.WaitAsync();
            try
            {
                _outbox.Add((seat, BuildStateSnapshot(seat)));
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            return true;
        }

        /// <summary>Build a complete board-state message for one player (used on rejoin).</summary>
        private ServerMessage BuildStateSnapshot(int seat)
        {
            if (_game == null) throw new InvalidOperationException("Game not started");

            return new ServerMessage
            {
                Type        = ServerMessageType.GameStateSnapshot,
                YourSeat    = seat,
                YourTiles   = _game.Players[seat].Hand.ClosedTiles
                                   .Select(TileDto.From).ToList(),
                TileCounts  = Enumerable.Range(0, MaxPlayers)
                                   .Select(s => _game.Players[s].Hand.ClosedTiles.Count)
                                   .ToArray(),
                Scores      = Enumerable.Range(0, MaxPlayers)
                                   .Select(s => _game.Players[s].Points)
                                   .ToArray(),
                Names       = _names,
                DealerSeat  = _game.DealerIndex,
                RoundWind   = _game.RoundWind.ToString(),
                Counters    = _game.Counters,
                CurrentTurn = _game.CurrentPlayerIndex,
                Discards    = Enumerable.Range(0, MaxPlayers)
                                   .Select(s => _game.Players[s].Discards
                                                     .Select(TileDto.From).ToList())
                                   .ToList(),
                Melds       = Enumerable.Range(0, MaxPlayers)
                                   .Select(s => _game.Players[s].Hand.OpenMelds
                                                     .Select(MeldDto.From).ToList())
                                   .ToList(),
                RiichiSeats    = Enumerable.Range(0, MaxPlayers)
                                   .Where(s => _game.Players[s].Hand.IsRiichi)
                                   .ToArray(),
                DoraIndicators = _game.Wall.DoraIndicators.Select(TileDto.From).ToList(),
            };
        }

        public List<PlayerInfoDto> GetPlayerList()
        {
            var list = new List<PlayerInfoDto>();
            for (int s = 0; s < MaxPlayers; s++)
                list.Add(new PlayerInfoDto
                {
                    Seat  = s,
                    Name  = _names[s],
                    IsCpu = _connections[s] == null
                });
            return list;
        }

        // =====================================================================
        // Game start (called by host)
        // =====================================================================

        public async Task StartGameAsync()
        {
            GameStarted = true;

            // Build player names — CPU fills empty seats
            for (int s = 0; s < MaxPlayers; s++)
                if (_connections[s] == null)
                    _names[s] = $"CPU {s + 1}";

            // Create GameState — humanSeat = -1 means no single "the" human;
            // the room itself knows which seats are networked.
            _game = new GameState(humanSeat: -1, playerNames: _names);

            // Subscribe to game events (all synchronous, fire inside GameState methods)
            _game.OnTileDrawn     += OnTileDrawn_Handler;
            _game.OnTileDiscarded += OnTileDiscarded_Handler;
            _game.OnMeldDeclared  += OnMeldDeclared_Handler;
            _game.OnRiichiDeclared+= OnRiichiDeclared_Handler;
            _game.OnHandEnd       += OnHandEnd_Handler;
            _game.OnNewHand       += OnNewHand_Handler;
            _game.OnGameOver      += OnGameOver_Handler;

            // Notify all players the game is starting
            for (int s = 0; s < MaxPlayers; s++)
            {
                await SendToSeatAsync(s, new ServerMessage
                {
                    Type     = ServerMessageType.GameStarted,
                    YourSeat = s,
                    Names    = _names,
                    Code     = Code,   // included so matchmade players can rejoin without a prior roomJoined
                });
            }

            // Kick off — StartNewHand fires OnNewHand then leaves game in ActionPhase
            await _gameSem.WaitAsync();
            try   { _game.StartGame(); }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            await AdvanceFromActionPhaseAsync();
        }

        // =====================================================================
        // Inbound player actions (called from WebSocketHandler per-connection loop)
        // =====================================================================

        public async Task HandlePlayerActionAsync(PlayerConnection conn, ClientMessage msg)
        {
            if (_game == null) return;
            int seat = conn.Seat;

            switch (msg.Type)
            {
                case ClientMessageType.Discard:
                    await HandleDiscardAsync(seat, msg.Tile, isRiichi: false);
                    break;

                case ClientMessageType.Riichi:
                    await HandleDiscardAsync(seat, msg.Tile, isRiichi: true);
                    break;

                case ClientMessageType.Tsumo:
                    await HandleTsumoAsync(seat);
                    break;

                case ClientMessageType.Pon:
                    _claimWindowSeat = seat;
                    _claimTcs?.TrySetResult("pon");
                    break;

                case ClientMessageType.Chi:
                    _claimWindowSeat = seat;
                    _claimTcs?.TrySetResult("chi");
                    break;

                case ClientMessageType.Ron:
                    _claimWindowSeat = seat;
                    _claimTcs?.TrySetResult("ron");
                    break;

                case ClientMessageType.Kan:
                    await HandleKanAsync(seat, msg.Tile);
                    break;

                case ClientMessageType.Pass:
                    _claimTcs?.TrySetResult("pass");
                    break;

                case ClientMessageType.NextHand:
                    if (IsHost(conn)) _nextHandTcs?.TrySetResult(true);
                    break;

                case ClientMessageType.Kyuushu:
                    await HandleKyuushuAsync(seat);
                    break;
            }
        }

        // =====================================================================
        // Action handlers
        // =====================================================================

        private async Task HandleDiscardAsync(int seat, TileDto? tileDto, bool isRiichi)
        {
            if (_game == null || tileDto == null) return;

            await _gameSem.WaitAsync();
            bool ok;
            try
            {
                var tile = tileDto.FindIn(_game.Players[seat].Hand.ClosedTiles);
                if (tile == null) { _gameSem.Release(); return; }

                ok = isRiichi
                    ? _game.DeclareRiichi(seat, tile)
                    : _game.Discard(seat, tile);
            }
            finally { if (_gameSem.CurrentCount == 0) _gameSem.Release(); }

            await FlushOutboxAsync();
            if (ok && _game.Phase == TurnPhase.ClaimWindow)
                await HandleClaimWindowAsync();
        }

        private async Task HandleTsumoAsync(int seat)
        {
            if (_game == null) return;

            await _gameSem.WaitAsync();
            bool ok;
            try   { ok = _game.DeclareTsumo(); }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            // OnHandEnd will have fired; wait for host to advance
            if (ok) await WaitForNextHandAsync();
        }

        private async Task HandleKyuushuAsync(int seat)
        {
            if (_game == null) return;

            await _gameSem.WaitAsync();
            bool ok;
            try   { ok = _game.DeclareKyuushuKyuuhai(seat); }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            if (ok) await WaitForNextHandAsync();
        }

        private async Task HandleKanAsync(int seat, TileDto? tileDto)
        {
            if (_game == null) return;

            await _gameSem.WaitAsync();
            bool ok = false;
            try
            {
                if (tileDto != null)
                {
                    // Ankan or kakan on the player's own turn.
                    // Client sends the specific tile; try ankan first (no chankan window),
                    // then kakan (which opens the chankan window for opponents).
                    var tile = tileDto.FindIn(_game.Players[seat].Hand.ClosedTiles);
                    if (tile != null)
                        ok = _game.DeclareAnkan(seat, tile)
                          || _game.DeclareKakan(seat, tile);
                }
                else if (_game.Phase == TurnPhase.ClaimWindow)
                {
                    // Daiminkan: claiming the pending discard during a claim window.
                    ok = _game.ClaimDaiminkan(seat);
                }
                else
                {
                    // Fallback: client sent no tile during action phase.
                    // Scan the hand and attempt the first valid kan.
                    var hand = _game.Players[seat].Hand;
                    // Kakan (pon extension) takes priority because it opens a chankan window.
                    foreach (var meld in hand.OpenMelds)
                    {
                        if (meld.Type == MeldType.Pon)
                        {
                            var fourth = hand.ClosedTiles.FirstOrDefault(t => t == meld.Lead);
                            if (fourth != null && _game.DeclareKakan(seat, fourth)) { ok = true; break; }
                        }
                    }
                    // Ankan: four identical tiles in closed hand.
                    if (!ok)
                    {
                        var counts = hand.ClosedTiles
                            .GroupBy(t => t.TileId)
                            .Where(g => g.Count() >= 4)
                            .Select(g => g.First())
                            .FirstOrDefault();
                        if (counts != null)
                            ok = _game.DeclareAnkan(seat, counts);
                    }
                }
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (!ok) return;

            // After ankan/daiminkan the rinshan tile is drawn inside GameState and
            // OnTileDrawn fires synchronously → already in the outbox.  Game is in
            // ActionPhase, not DrawPhase, so the human client drives the next action
            // after receiving the tileDrawn message.
            // After kakan the chankan window is open — handle it here.
            if (_game.Phase == TurnPhase.ClaimWindow && _game.IsChankanWindow)
                await HandleClaimWindowAsync();
            else if (_game.Phase == TurnPhase.DrawPhase)
                await AdvanceDrawPhaseAsync();   // safety net — should not normally be reached
        }

        // =====================================================================
        // Claim window
        // =====================================================================

        private async Task HandleClaimWindowAsync()
        {
            if (_game == null) return;

            var pendingTile = _game.PendingDiscard!;
            var discarder   = _game.DiscarderIndex;

            // Determine which human seats can claim
            var eligibleHumans = GetEligibleClaimers(pendingTile, discarder);

            if (eligibleHumans.Count > 0)
            {
                // Tell eligible humans about the claim window
                foreach (var (s, canRon, canPon, canChi, canKan) in eligibleHumans)
                {
                    await SendToSeatAsync(s, new ServerMessage
                    {
                        Type          = ServerMessageType.ClaimWindowOpened,
                        DiscarderSeat = discarder,
                        Tile          = TileDto.From(pendingTile),
                        CanRon        = canRon,
                        CanPon        = canPon,
                        CanChi        = canChi,
                        CanKan        = canKan,
                    });
                }

                // Wait for a human claim or timeout
                _claimTcs        = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _claimCts        = new CancellationTokenSource();
                _claimWindowSeat = -1;

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(ClaimWindowSeconds), _claimCts.Token);
                var claimTask   = _claimTcs.Task;

                string claimResult = "pass";
                try
                {
                    var winner = await Task.WhenAny(timeoutTask, claimTask);
                    if (winner == claimTask)
                        claimResult = claimTask.Result;
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _claimCts.Cancel();
                    _claimTcs = null;
                    _claimCts = null;
                }

                // Apply human claim if any
                if (claimResult != "pass" && _claimWindowSeat >= 0)
                {
                    bool applied = await ApplyHumanClaimAsync(_claimWindowSeat, claimResult, pendingTile);
                    if (applied) return;   // game advanced in ApplyHumanClaim
                }
            }

            // No human acted — let AI resolve
            await Task.Delay(AiClaimMs);
            await ResolveAiClaimsAsync();
        }

        private List<(int seat, bool canRon, bool canPon, bool canChi, bool canKan)> GetEligibleClaimers(Tile tile, int discarder)
        {
            var result = new List<(int, bool, bool, bool, bool)>();
            if (_game == null) return result;

            bool isChankan = _game.IsChankanWindow;

            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_connections[s] == null) continue;   // CPU seat
                if (s == discarder) continue;

                var hand    = _game.Players[s].Hand;
                int leftOf  = (s - 1 + 4) % 4;

                bool canRon = !_game.Players[s].Furiten.IsFuriten
                           && hand.IsTenpai()
                           && hand.IsWaitingFor(tile);

                // Chankan window: only Ron is available (robbing the kan)
                bool canPon = !isChankan && !hand.IsRiichi
                           && hand.ClosedTiles.Count(t => t == tile) >= 2;
                bool canChi = !isChankan && discarder == leftOf
                           && !hand.IsRiichi
                           && _ai[s].BestChiCombination(tile, hand) != null;
                bool canKan = !isChankan && !hand.IsRiichi
                           && hand.ClosedTiles.Count(t => t == tile) >= 3
                           && _game.Wall.KanCount < 4;

                if (canRon || canPon || canChi || canKan)
                    result.Add((s, canRon, canPon, canChi, canKan));
            }
            return result;
        }

        private async Task<bool> ApplyHumanClaimAsync(int seat, string claimType, Tile pendingTile)
        {
            if (_game == null) return false;

            await _gameSem.WaitAsync();
            bool ok = false;
            try
            {
                switch (claimType)
                {
                    case "ron":
                    {
                        // Include any AI seats that simultaneously want to ron on the same tile.
                        // This lets ClaimRonMulti detect double-ron and sanchahou correctly.
                        // Other human seats are skipped — they had the claim window and can
                        // respond separately; forcing their ron would bypass their choice.
                        var candidates = new List<int> { seat };
                        int discarderIdx = _game.DiscarderIndex;
                        for (int i = 1; i <= 3; i++)
                        {
                            int s = (discarderIdx + i) % 4;
                            if (s == seat) continue;               // already included
                            if (_connections[s] != null) continue; // other human seat
                            var h = _game.Players[s].Hand;
                            if (_ai[s].ShouldClaimRon(pendingTile, h, _game, s))
                                candidates.Add(s);
                        }
                        ok = _game.ClaimRonMulti(candidates.ToArray());
                        break;
                    }
                    case "pon":
                        ok = _game.ClaimPon(seat);
                        break;
                    case "chi":
                        var combo = _ai[seat].BestChiCombination(pendingTile, _game.Players[seat].Hand);
                        if (combo != null)
                            ok = _game.ClaimChi(seat, combo.Value.t1, combo.Value.t2);
                        break;
                    case "kan":
                        ok = _game.ClaimDaiminkan(seat);
                        break;
                }
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (!ok) return false;

            if (claimType == "ron")
            {
                await WaitForNextHandAsync();
            }
            else if (_game.Phase == TurnPhase.ActionPhase)
            {
                // Pon/Chi — human must discard, wait for their discard message
                // (HandleDiscardAsync will drive things forward)
            }
            else if (_game.Phase == TurnPhase.DrawPhase)
            {
                await AdvanceDrawPhaseAsync();
            }
            return true;
        }

        private async Task ResolveAiClaimsAsync()
        {
            if (_game == null) return;

            var tile     = _game.PendingDiscard;
            if (tile == null) return;
            int discarder = _game.DiscarderIndex;

            // Priority: Ron > Daiminkan > Pon > Chi
            //
            // Ron: collect ALL AI candidates first so ClaimRonMulti can detect
            // double-ron (2 winners split pot) and sanchahou (3 winners → abort).
            {
                var ronCandidates = new List<int>();
                for (int i = 1; i <= 3; i++)
                {
                    int s = (discarder + i) % 4;
                    if (_connections[s] != null) continue;  // human seat
                    var hand = _game.Players[s].Hand;
                    if (_ai[s].ShouldClaimRon(tile, hand, _game, s))
                        ronCandidates.Add(s);
                }
                if (ronCandidates.Count > 0)
                {
                    await _gameSem.WaitAsync();
                    bool ok;
                    try   { ok = _game.ClaimRonMulti(ronCandidates.ToArray()); }
                    finally { _gameSem.Release(); }

                    await FlushOutboxAsync();
                    if (ok) { await WaitForNextHandAsync(); return; }
                }
            }

            for (int i = 1; i <= 3; i++)
            {
                int s = (discarder + i) % 4;
                if (_connections[s] != null) continue;
                var hand = _game.Players[s].Hand;

                if (_ai[s].ShouldClaimDaiminkan(tile, hand, _game, s))
                {
                    await _gameSem.WaitAsync();
                    bool ok;
                    try   { ok = _game.ClaimDaiminkan(s); }
                    finally { _gameSem.Release(); }

                    await FlushOutboxAsync();
                    if (ok) { await AdvanceDrawPhaseAsync(); return; }
                }
            }

            for (int i = 1; i <= 3; i++)
            {
                int s = (discarder + i) % 4;
                if (_connections[s] != null) continue;
                var hand = _game.Players[s].Hand;

                if (_ai[s].ShouldClaimPon(tile, hand, _game, s))
                {
                    await _gameSem.WaitAsync();
                    bool ok;
                    try   { ok = _game.ClaimPon(s); }
                    finally { _gameSem.Release(); }

                    await FlushOutboxAsync();
                    if (ok) { await RunCpuActionAsync(s); return; }
                }
            }

            int leftSeat = (discarder + 1) % 4;
            if (_connections[leftSeat] == null)
            {
                var hand  = _game.Players[leftSeat].Hand;
                var combo = _ai[leftSeat].BestChiCombination(tile, hand);
                if (combo != null && _ai[leftSeat].ShouldClaimChi(tile, combo.Value.t1, combo.Value.t2, hand, _game, leftSeat))
                {
                    await _gameSem.WaitAsync();
                    bool ok;
                    try   { ok = _game.ClaimChi(leftSeat, combo.Value.t1, combo.Value.t2); }
                    finally { _gameSem.Release(); }

                    await FlushOutboxAsync();
                    if (ok) { await RunCpuActionAsync(leftSeat); return; }
                }
            }

            // Nobody claimed
            bool wasChankan;
            await _gameSem.WaitAsync();
            try
            {
                wasChankan = _game.IsChankanWindow;
                if (wasChankan) _game.ResolveChankan();  // draw rinshan, stay in ActionPhase
                else            _game.PassAllClaims();
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (wasChankan)
                await AdvanceFromActionPhaseAsync();   // kakan player acts on rinshan tile
            else
            {
                await NotifyTemporaryFuritenAsync();
                await AdvanceDrawPhaseAsync();
            }
        }

        /// <summary>
        /// After PassAllClaims(), send a "furitenChanged" message to any human
        /// seat that is now in temporary furiten (they were in tenpai but passed
        /// on an opponent's discard that would have completed their hand).
        /// Each player only receives the message once — when they enter the state.
        /// The client clears the flag when that player's next tileDrawn arrives.
        /// </summary>
        private async Task NotifyTemporaryFuritenAsync()
        {
            if (_game == null) return;
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_connections[s] == null) continue;  // CPU seat — no socket to notify
                if (!_game.Players[s].Furiten.IsTemporaryFuriten) continue;

                await SendToSeatAsync(s, new ServerMessage
                {
                    Type               = ServerMessageType.FuritenChanged,
                    IsTemporaryFuriten = true,
                });
            }
        }

        // =====================================================================
        // Game loop driving
        // =====================================================================

        private async Task AdvanceDrawPhaseAsync()
        {
            if (_game == null || _game.Phase != TurnPhase.DrawPhase) return;

            int seat = _game.CurrentPlayerIndex;

            if (_connections[seat] == null)
            {
                // CPU seat — draw after think delay
                await Task.Delay(AiThinkMs);
            }

            await _gameSem.WaitAsync();
            Tile? drawn;
            try   { drawn = _game.DrawForCurrentPlayer(); }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (_game.Phase == TurnPhase.HandEnd) return;   // Exhaustive draw
            if (drawn == null) return;

            await AdvanceFromActionPhaseAsync();
        }

        private async Task AdvanceFromActionPhaseAsync()
        {
            if (_game == null) return;

            int seat = _game.CurrentPlayerIndex;

            if (_connections[seat] != null)
            {
                // Human seat — wait for their discard/tsumo/riichi/kan message.
                // HandlePlayerActionAsync will drive things forward.
                return;
            }

            // CPU seat — decide action
            await RunCpuActionAsync(seat);
        }

        private async Task RunCpuActionAsync(int seat)
        {
            if (_game == null) return;

            await Task.Delay(AiThinkMs);

            await _gameSem.WaitAsync();
            bool advanced = false;
            try
            {
                var hand = _game.Players[seat].Hand;

                // Tsumo?
                if (_game.WinChecker_CanWinTsumo(seat))
                {
                    if (_game.DeclareTsumo()) { advanced = true; goto done; }
                }

                // Ankan? (riichi ankan allowed if waits don't change)
                if (hand.IsRiichi)
                {
                    // In riichi the only legal ankan is the drawn tile completing a set of 4
                    // without altering the wait set.
                    var drawn = hand.DrawnTile;
                    if (drawn != null && _game.Wall.KanCount < 4 && hand.CanRiichiAnkan(drawn))
                    {
                        if (_game.DeclareAnkan(seat, drawn)) { advanced = true; goto done; }
                    }
                }
                else
                {
                    var ankanTile = _ai[seat].GetAnkanTile(hand, _game, seat);
                    if (ankanTile != null && _game.Wall.KanCount < 4)
                    {
                        if (_game.DeclareAnkan(seat, ankanTile)) { advanced = true; goto done; }
                    }
                }

                // Riichi?
                if (!hand.IsRiichi && _ai[seat].ShouldDeclareRiichi(hand, _game, seat))
                {
                    var candidates = GetRiichiCandidates(hand);
                    if (candidates.Count > 0)
                    {
                        var discard = candidates.First();
                        if (_game.DeclareRiichi(seat, discard)) { advanced = true; goto done; }
                    }
                }

                // Plain discard (respect kuikae — cannot discard the just-claimed chi tile
                // or its ryanmen equivalent immediately after a chi call)
                {
                    var discard = _ai[seat].ChooseDiscard(hand, _game, seat);
                    bool ok = discard != null && _game.Discard(seat, discard);
                    if (!ok)
                    {
                        // Preferred pick was kuikae-forbidden (or null); fall back to first legal tile
                        foreach (var t in hand.ClosedTiles)
                        {
                            if (_game.Discard(seat, t)) { ok = true; break; }
                        }
                    }
                    if (ok) advanced = true;
                }

                done: ;
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (!advanced) return;

            if (_game.Phase == TurnPhase.ClaimWindow)
                await HandleClaimWindowAsync();   // discard or chankan claim window
            else if (_game.Phase == TurnPhase.DrawPhase)
                await AdvanceDrawPhaseAsync();
            else if (_game.Phase == TurnPhase.ActionPhase)
                await AdvanceFromActionPhaseAsync();   // CPU ankan/daiminkan — same player discards
            else if (_game.Phase == TurnPhase.HandEnd)
                await WaitForNextHandAsync();
        }

        // =====================================================================
        // Next hand gate
        // =====================================================================

        private async Task WaitForNextHandAsync()
        {
            // Give players time to see the scoring panel
            await Task.Delay(HandResultPauseMs);

            // Wait for host to send "nextHand" (or auto-advance after a longer pause)
            _nextHandTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout  = Task.Delay(TimeSpan.FromSeconds(30));
            await Task.WhenAny(_nextHandTcs.Task, timeout);
            _nextHandTcs = null;

            if (_game == null || _game.Phase == TurnPhase.GameOver) return;

            await _gameSem.WaitAsync();
            try   { _game.BeginNextHand(); }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            await AdvanceFromActionPhaseAsync();
        }

        // =====================================================================
        // GameState event handlers (synchronous — only queue outbox, no IO)
        // =====================================================================

        private void OnNewHand_Handler()
        {
            if (_game == null) return;

            // Send each player their own tiles (filtered — never send others' tiles)
            for (int s = 0; s < MaxPlayers; s++)
            {
                var tiles      = _game.Players[s].Hand.ClosedTiles.Select(TileDto.From).ToList();
                var tileCounts = Enumerable.Range(0, MaxPlayers)
                                           .Select(i => _game.Players[i].Hand.ClosedTiles.Count)
                                           .ToArray();
                var scores     = Enumerable.Range(0, MaxPlayers)
                                           .Select(i => _game.Players[i].Points)
                                           .ToArray();

                _outbox.Add((s, new ServerMessage
                {
                    Type           = ServerMessageType.HandDealt,
                    YourSeat       = s,
                    YourTiles      = tiles,
                    TileCounts     = tileCounts,
                    Scores         = scores,
                    DealerSeat     = _game.DealerIndex,
                    RoundWind      = _game.RoundWind.ToString(),
                    Counters       = _game.Counters,
                    Names          = _names,
                    DoraIndicators = _game.Wall.DoraIndicators.Select(TileDto.From).ToList(),
                }));
            }
        }

        private void OnTileDrawn_Handler(int seat)
        {
            if (_game == null) return;

            var drawnTile = _game.Players[seat].Hand.DrawnTile;

            // A rinshan draw follows a kan declaration — include the updated dora
            // indicator list so every client sees the newly-flipped indicator
            // (the kan revealed it before the chankan window was resolved).
            List<TileDto>? doraUpdate = _game.IsRinshanDraw
                ? _game.Wall.DoraIndicators.Select(TileDto.From).ToList()
                : null;

            for (int s = 0; s < MaxPlayers; s++)
            {
                if (s == seat)
                {
                    // Send actual tile to the player who drew it
                    _outbox.Add((s, new ServerMessage
                    {
                        Type           = ServerMessageType.TileDrawn,
                        Seat           = seat,
                        Tile           = drawnTile != null ? TileDto.From(drawnTile) : null,
                        DoraIndicators = doraUpdate,
                    }));
                }
                else
                {
                    // Everyone else just sees a face-down draw (count goes up)
                    _outbox.Add((s, new ServerMessage
                    {
                        Type           = ServerMessageType.TileDrawn,
                        Seat           = seat,
                        Tile           = null,   // null = hidden draw
                        DoraIndicators = doraUpdate,
                    }));
                }
            }
        }

        private void OnTileDiscarded_Handler(int seat, Tile tile)
        {
            if (_game == null) return;

            bool isRiichi = _game.Players[seat].Hand.IsRiichi
                         && _game.Players[seat].DeclaredRiichi
                         && _game.Players[seat].RiichiBetTurn == _game.TurnNumber;

            // Broadcast discard to all players — discards are public
            for (int s = 0; s < MaxPlayers; s++)
            {
                _outbox.Add((s, new ServerMessage
                {
                    Type            = ServerMessageType.TileDiscarded,
                    Seat            = seat,
                    Tile            = TileDto.From(tile),
                    IsRiichiDiscard = isRiichi,
                }));
            }
        }

        private void OnMeldDeclared_Handler(int seat, Meld meld)
        {
            // Always include current dora indicators — a kan reveals a new one,
            // so the client must update its display after every meld.
            var doraIndicators = _game!.Wall.DoraIndicators.Select(TileDto.From).ToList();
            for (int s = 0; s < MaxPlayers; s++)
            {
                _outbox.Add((s, new ServerMessage
                {
                    Type           = ServerMessageType.MeldDeclared,
                    Seat           = seat,
                    Meld           = MeldDto.From(meld),
                    DoraIndicators = doraIndicators,
                }));
            }
        }

        private void OnRiichiDeclared_Handler(int seat)
        {
            for (int s = 0; s < MaxPlayers; s++)
            {
                _outbox.Add((s, new ServerMessage
                {
                    Type = ServerMessageType.RiichiDeclared,
                    Seat = seat,
                }));
            }
        }

        private void OnHandEnd_Handler(HandEndReason reason, int[] winners)
        {
            if (_game == null) return;

            var scoreBoard = Enumerable.Range(0, MaxPlayers)
                                       .Select(s => new ScoreEntryDto
                                       {
                                           Seat   = s,
                                           Name   = _names[s],
                                           Points = _game.Players[s].Points,
                                       }).ToList();

            // Reveal all hands at end — everyone sees everyone's tiles
            var allHands = Enumerable.Range(0, MaxPlayers)
                                     .Select(s => _game.Players[s].Hand.ClosedTiles
                                                       .Select(TileDto.From).ToList())
                                     .ToList();

            for (int s = 0; s < MaxPlayers; s++)
            {
                var msg = new ServerMessage
                {
                    Type       = ServerMessageType.HandEnded,
                    Reason     = reason.ToString(),
                    Winners    = winners,
                    ScoreBoard = scoreBoard,
                    WinnerSeat = winners.Length > 0 ? winners[0] : -1,
                    PayerSeat  = _game.LastDiscarderSeat,
                };

                // Attach score info if there was a win
                if (_game.LastScoreResult != null)
                {
                    msg.Han        = _game.LastScoreResult.TotalFan;
                    msg.Fu         = _game.LastScoreResult.Fu.Total;
                    msg.BasePoints = _game.LastScoreResult.TotalPointsWon;
                }
                if (_game.LastYakuResult != null)
                {
                    msg.YakuNames = _game.LastYakuResult.Yaku.Select(y => y.Name).ToArray();
                }
                if (_game.LastWinContext != null)
                {
                    msg.DoraCount    = _game.LastWinContext.DoraCount;
                    msg.UraDoraCount = _game.LastWinContext.UraDoraCount;
                }

                // For exhaustive draw, reveal tenpai hands and send waiting tiles
                if (reason == HandEndReason.ExhaustiveDraw)
                {
                    var tenpaiSet = new HashSet<int>(winners);
                    msg.RevealedHands = Enumerable.Range(0, MaxPlayers)
                        .Select(seat => tenpaiSet.Contains(seat)
                            ? _game.Players[seat].Hand.ClosedTiles.Select(TileDto.From).ToList()
                            : new List<TileDto>())
                        .ToList();
                    msg.TenpaiWaits = Enumerable.Range(0, MaxPlayers)
                        .Select(seat => tenpaiSet.Contains(seat)
                            ? _game.Players[seat].Hand.GetWaitingTiles().Select(TileDto.From).ToList()
                            : new List<TileDto>())
                        .ToList();
                }

                _outbox.Add((s, msg));
            }
        }

        private void OnGameOver_Handler()
        {
            if (_game == null) return;

            var scoreBoard = Enumerable.Range(0, MaxPlayers)
                                       .Select(s => new ScoreEntryDto
                                       {
                                           Seat   = s,
                                           Name   = _names[s],
                                           Points = _game.Players[s].Points,
                                       }).ToList();

            for (int s = 0; s < MaxPlayers; s++)
            {
                _outbox.Add((s, new ServerMessage
                {
                    Type       = ServerMessageType.GameOver,
                    ScoreBoard = scoreBoard,
                }));
            }
        }

        // =====================================================================
        // Outbox flush + send helpers
        // =====================================================================

        private async Task FlushOutboxAsync()
        {
            var items = _outbox.ToList();
            _outbox.Clear();

            foreach (var (targetSeat, msg) in items)
            {
                if (targetSeat < 0)
                {
                    // Broadcast
                    foreach (var conn in _connections)
                        if (conn != null) await conn.SendAsync(msg);
                }
                else
                {
                    await SendToSeatAsync(targetSeat, msg);
                }
            }
        }

        private async Task SendToSeatAsync(int seat, ServerMessage msg)
        {
            if (seat >= 0 && seat < MaxPlayers && _connections[seat] != null)
                await _connections[seat]!.SendAsync(msg);
        }

        private async Task BroadcastAsync(ServerMessage msg)
        {
            foreach (var conn in _connections)
                if (conn != null) await conn.SendAsync(msg);
        }

        /// <summary>Send to all human connections except the given one (used for lobby notifications).</summary>
        public async Task BroadcastExceptAsync(PlayerConnection exclude, ServerMessage msg)
        {
            foreach (var conn in _connections)
                if (conn != null && conn != exclude) await conn.SendAsync(msg);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>
        /// Returns tiles that can be discarded from a 14-tile hand to leave it in tenpai.
        /// Mirrors the same logic as GameController.GetRiichiCandidates().
        /// </summary>
        private static List<Tile> GetRiichiCandidates(Hand hand)
        {
            var candidates = new List<Tile>();
            var seen       = new HashSet<int>();

            foreach (var tile in hand.ClosedTiles)
            {
                if (!seen.Add(tile.TileId)) continue;   // skip duplicates

                var test = hand.ClosedTiles.ToList();
                test.Remove(tile);

                var testHand = new Hand();
                testHand.AddTiles(test);
                if (testHand.IsTenpai())
                    candidates.Add(tile);
            }
            return candidates;
        }
    }
}
