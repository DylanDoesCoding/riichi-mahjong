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
        private const int  AiThinkMs          = 400;    // CPU pause before acting
        private const int  AiClaimMs          = 200;    // pause before AI claim resolution
        private const int  HandResultPauseMs  = 500;    // minimum score-panel display before the
                                                        // host's Next Hand click can take effect
        private const int  MaxPlayers         = 4;

        // ---- Identity --------------------------------------------------------
        public  string  Code        { get; }
        public  bool    GameStarted { get; private set; }

        // ---- Players (index = global seat 0-3) --------------------------------
        // null entry = CPU seat
        private readonly PlayerConnection?[] _connections = new PlayerConnection?[MaxPlayers];
        private readonly string[]            _names       = ["CPU 1", "CPU 2", "CPU 3", "CPU 4"];
        private readonly string?[]           _playerUuids = new string?[MaxPlayers];  // for reconnection
        private readonly object              _lobbyLock   = new();  // guards seats/names/uuids/host
        private int                          _hostSeat    = 0;
        private int                          _playerCount = 0;   // humans currently in lobby
        private int                          _startedFlag = 0;   // Interlocked guard against double start
        private volatile bool                _abandoned   = false; // all humans left mid-game — stop the loop

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
        // Built by HandleClaimWindowAsync. Each eligible human's response is
        // recorded under _claimLock; the TCS completes when every eligible seat
        // has responded. Responses from seats that were not offered a claim are
        // ignored, so one client can neither close nor steal another player's
        // claim window.
        private readonly object _claimLock = new();
        private Dictionary<int, (bool ron, bool pon, bool chi, bool kan)>  _claimEligible  = new();
        private Dictionary<int, (string action, TileDto? t1, TileDto? t2)> _claimResponses = new();
        private TaskCompletionSource<bool>? _claimAllTcs;

        // ---- Next-hand gate ---------------------------------------------------
        // Human host must send NextHand before we advance
        private TaskCompletionSource<bool>? _nextHandTcs;
        // Set to true if host sends "nextHand" before the TCS is created (during the
        // initial result-display pause), so the early click isn't silently dropped.
        private bool _nextHandPending = false;

        // ---- Accounts (lifetime stats) ------------------------------------------
        // Snapshot of each seat's account id, captured at game start so stats are
        // recorded even for players who disconnect before the game ends.
        private readonly Auth.IAccountStore? _accounts;
        private readonly long?[]             _accountIds = new long?[MaxPlayers];

        // =====================================================================
        public GameRoom(string code, Auth.IAccountStore? accounts = null)
        {
            Code      = code;
            _accounts = accounts;
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

            lock (_lobbyLock)
            {
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
        }

        public void RemovePlayer(PlayerConnection conn)
        {
            int seat = conn.Seat;
            if (seat < 0 || seat >= MaxPlayers) return;

            lock (_lobbyLock)
            {
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

                // Stop driving the game loop once no humans remain — the room is
                // about to be removed and CPU-vs-CPU play would just burn cycles.
                if (GameStarted && _playerCount <= 0)
                    _abandoned = true;
            }

            // A departed player can no longer respond to an open claim window
            RecordClaimResponse(seat, "pass", null, null);
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

            lock (_lobbyLock)
            {
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
        }

        /// <summary>
        /// Attempt to reconnect a player mid-game using their client-generated UUID.
        /// Replaces the dead connection, sends a full state snapshot, returns true on success.
        /// </summary>
        public async Task<bool> RejoinAsync(string uuid, PlayerConnection newConn)
        {
            if (_game == null) return false;

            PlayerConnection? old;
            lock (_lobbyLock)
            {
                // Find the seat that belongs to this UUID
                int s2 = -1;
                for (int s = 0; s < MaxPlayers; s++)
                {
                    if (_playerUuids[s] == uuid) { s2 = s; break; }
                }
                if (s2 < 0) return false;

                // Swap in the new connection. If the old socket is still open
                // (zombie connection, or the same UUID connecting twice) it gets
                // closed below — exactly one live connection may hold a seat.
                old = _connections[s2];
                newConn.Seat        = s2;
                newConn.DisplayName = _names[s2];
                _connections[s2]    = newConn;
                _playerCount        = _connections.Count(c => c != null);
            }
            int seat = newConn.Seat;

            if (old != null && old != newConn && old.IsAlive)
                await old.CloseAsync();

            // Build and send a full state snapshot under the lock
            await _gameSem.WaitAsync();
            try
            {
                Enqueue((seat, BuildStateSnapshot(seat)));
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
            // A duplicate startGame message (or a matchmaking race) must not spawn
            // a second concurrent game loop over the same GameState.
            if (Interlocked.Exchange(ref _startedFlag, 1) == 1) return;

            GameStarted = true;

            // Build player names — CPU fills empty seats
            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_connections[s] == null)
                    _names[s] = $"CPU {s + 1}";
                _accountIds[s] = _connections[s]?.AccountId;
            }

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
                    RecordClaimResponse(seat, "pon", null, null);
                    break;

                case ClientMessageType.Chi:
                    RecordClaimResponse(seat, "chi", msg.T1, msg.T2);
                    break;

                case ClientMessageType.Ron:
                    RecordClaimResponse(seat, "ron", null, null);
                    break;

                case ClientMessageType.Kan:
                    // No tile during a claim window = daiminkan claim; otherwise
                    // it's an ankan/kakan on the player's own turn.
                    if (msg.Tile == null && RecordClaimResponse(seat, "kan", null, null))
                        break;
                    await HandleKanAsync(seat, msg.Tile);
                    break;

                case ClientMessageType.Pass:
                    RecordClaimResponse(seat, "pass", null, null);
                    break;

                case ClientMessageType.NextHand:
                    if (IsHost(conn))
                    {
                        _nextHandPending = true;          // capture even if TCS not live yet
                        _nextHandTcs?.TrySetResult(true); // signal immediately if TCS is ready
                    }
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
            bool ok = false;
            try
            {
                var tile = tileDto.FindIn(_game.Players[seat].Hand.ClosedTiles);
                if (tile != null)
                {
                    ok = isRiichi
                        ? _game.DeclareRiichi(seat, tile)
                        : _game.Discard(seat, tile);
                }
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();
            if (ok && _game.Phase == TurnPhase.ClaimWindow)
                await HandleClaimWindowAsync();
        }

        private async Task HandleTsumoAsync(int seat)
        {
            if (_game == null) return;

            await _gameSem.WaitAsync();
            bool ok = false;
            try
            {
                // DeclareTsumo() acts on the current player, so only that seat's
                // own message may trigger it.
                if (seat == _game.CurrentPlayerIndex)
                    ok = _game.DeclareTsumo();
            }
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
            else if (_game.Phase == TurnPhase.HandEnd)
                await WaitForNextHandAsync();    // suukaikan abort on the fourth kan
        }

        // =====================================================================
        // Claim window
        // =====================================================================

        private async Task HandleClaimWindowAsync()
        {
            if (_game == null || _abandoned) return;

            var pendingTile = _game.PendingDiscard!;
            var discarder   = _game.DiscarderIndex;

            // Determine which human seats can claim
            var eligibleHumans = GetEligibleClaimers(pendingTile, discarder);

            if (eligibleHumans.Count > 0)
            {
                TaskCompletionSource<bool> allTcs;
                lock (_claimLock)
                {
                    _claimEligible = eligibleHumans.ToDictionary(
                        e => e.seat,
                        e => (e.canRon, e.canPon, e.canChi, e.canKan));
                    _claimResponses = new();
                    allTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _claimAllTcs = allTcs;
                }

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

                // Wait until every eligible human responds, or the window times out.
                // Waiting for all responses (rather than the first) means one player
                // passing cannot close the window on another player's ron, and two
                // humans ronning the same tile are both honoured.
                await Task.WhenAny(allTcs.Task, Task.Delay(TimeSpan.FromSeconds(ClaimWindowSeconds)));

                Dictionary<int, (string action, TileDto? t1, TileDto? t2)> responses;
                lock (_claimLock)
                {
                    responses       = _claimResponses;
                    _claimEligible  = new();
                    _claimResponses = new();
                    _claimAllTcs    = null;
                }

                // Resolve by claim priority: ron (possibly multiple) > kan > pon > chi
                var ronSeats = responses.Where(kv => kv.Value.action == "ron")
                                        .Select(kv => kv.Key).ToList();
                if (ronSeats.Count > 0 && await ApplyHumanRonAsync(ronSeats, pendingTile))
                    return;

                foreach (var claimType in new[] { "kan", "pon", "chi" })
                {
                    foreach (var kv in responses.Where(kv => kv.Value.action == claimType))
                    {
                        if (await ApplyHumanClaimAsync(kv.Key, claimType, pendingTile,
                                                       kv.Value.t1, kv.Value.t2))
                            return;
                    }
                }
            }

            // No human acted — let AI resolve
            await Task.Delay(AiClaimMs);
            await ResolveAiClaimsAsync();
        }

        /// <summary>
        /// Record an eligible player's response to the open claim window.
        /// Returns false (recording nothing) when no window is open, the seat was
        /// not offered a claim, the seat already responded, or the claim type was
        /// not one offered to that seat.
        /// </summary>
        private bool RecordClaimResponse(int seat, string action, TileDto? t1, TileDto? t2)
        {
            lock (_claimLock)
            {
                if (_claimAllTcs == null) return false;
                if (!_claimEligible.TryGetValue(seat, out var can)) return false;
                if (_claimResponses.ContainsKey(seat)) return false;

                bool allowed = action switch
                {
                    "ron"  => can.ron,
                    "pon"  => can.pon,
                    "chi"  => can.chi,
                    "kan"  => can.kan,
                    "pass" => true,
                    _      => false,
                };
                if (!allowed) return false;

                _claimResponses[seat] = (action, t1, t2);
                if (_claimResponses.Count == _claimEligible.Count)
                    _claimAllTcs.TrySetResult(true);
                return true;
            }
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

        private async Task<bool> ApplyHumanRonAsync(List<int> humanRonSeats, Tile pendingTile)
        {
            if (_game == null) return false;

            await _gameSem.WaitAsync();
            bool ok;
            try
            {
                // Include any AI seats that simultaneously want to ron on the same
                // tile so ClaimRonMulti can detect double-ron and sanchahou correctly.
                var candidates = new List<int>(humanRonSeats);
                int discarderIdx = _game.DiscarderIndex;
                for (int i = 1; i <= 3; i++)
                {
                    int s = (discarderIdx + i) % 4;
                    if (candidates.Contains(s)) continue;   // already included
                    if (_connections[s] != null) continue;  // human seat — only rons they declared
                    var h = _game.Players[s].Hand;
                    if (_ai[s].ShouldClaimRon(pendingTile, h, _game, s))
                        candidates.Add(s);
                }
                ok = _game.ClaimRonMulti(candidates.ToArray());
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (!ok) return false;
            await WaitForNextHandAsync();
            return true;
        }

        private async Task<bool> ApplyHumanClaimAsync(int seat, string claimType, Tile pendingTile,
                                                      TileDto? t1, TileDto? t2)
        {
            if (_game == null) return false;

            await _gameSem.WaitAsync();
            bool ok = false;
            try
            {
                switch (claimType)
                {
                    case "pon":
                        ok = _game.ClaimPon(seat);
                        break;
                    case "chi":
                    {
                        // Use the combination the client chose when provided; fall
                        // back to the AI's pick if absent or invalid.
                        var hand = _game.Players[seat].Hand;
                        var c1   = t1?.FindIn(hand.ClosedTiles);
                        var c2   = t2?.FindIn(hand.ClosedTiles);
                        if (c1 != null && c2 != null && !ReferenceEquals(c1, c2))
                            ok = _game.ClaimChi(seat, c1, c2);
                        if (!ok)
                        {
                            var combo = _ai[seat].BestChiCombination(pendingTile, hand);
                            if (combo != null)
                                ok = _game.ClaimChi(seat, combo.Value.t1, combo.Value.t2);
                        }
                        break;
                    }
                    case "kan":
                        ok = _game.ClaimDaiminkan(seat);
                        break;
                }
            }
            finally { _gameSem.Release(); }

            await FlushOutboxAsync();

            if (!ok) return false;

            if (_game.Phase == TurnPhase.DrawPhase)
            {
                await AdvanceDrawPhaseAsync();
            }
            // Pon/Chi leave the game in ActionPhase — the claiming human must
            // discard next; HandleDiscardAsync drives things forward from there.
            return true;
        }

        private async Task ResolveAiClaimsAsync()
        {
            if (_game == null || _abandoned) return;

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
                    // ClaimDaiminkan draws the rinshan internally → game is already in ActionPhase,
                    // not DrawPhase. Drive the CPU's post-kan discard from ActionPhase.
                    if (ok) { await AdvanceFromActionPhaseAsync(); return; }
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
            else if (_game.Phase == TurnPhase.HandEnd)
            {
                // PassAllClaims triggered an abortive draw (suufonren da,
                // suucha riichi) — wait for the host instead of stalling.
                await WaitForNextHandAsync();
            }
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
            if (_game == null || _abandoned || _game.Phase != TurnPhase.DrawPhase) return;

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

            if (_game.Phase == TurnPhase.HandEnd)
            {
                // Exhaustive draw — gate on the host's Next Hand like every other
                // hand ending, otherwise the game stalls with nobody listening.
                await WaitForNextHandAsync();
                return;
            }
            if (drawn == null) return;

            await AdvanceFromActionPhaseAsync();
        }

        private async Task AdvanceFromActionPhaseAsync()
        {
            if (_game == null || _abandoned) return;

            int seat = _game.CurrentPlayerIndex;

            if (_connections[seat] != null)
            {
                var hand = _game.Players[seat].Hand;

                // Riichi auto-discard: a player in riichi can only discard the drawn tile,
                // so if they cannot tsumo and cannot legally ankan, the server discards
                // for them immediately — no wait timer needed.
                if (hand.IsRiichi)
                {
                    bool canTsumo = _game.WinChecker_CanWinTsumo(seat);
                    bool canAnkan = hand.DrawnTile != null
                                 && _game.Wall.KanCount < 4
                                 && hand.CanRiichiAnkan(hand.DrawnTile);

                    if (!canTsumo && !canAnkan)
                    {
                        await Task.Delay(AiThinkMs);   // brief pause so it feels natural
                        await _gameSem.WaitAsync();
                        try
                        {
                            var drawn = _game.Players[seat].Hand.DrawnTile;
                            if (drawn != null) _game.Discard(seat, drawn);
                        }
                        finally { _gameSem.Release(); }

                        await FlushOutboxAsync();

                        if (_game.Phase == TurnPhase.ClaimWindow)
                            await HandleClaimWindowAsync();
                        else if (_game.Phase == TurnPhase.DrawPhase)
                            await AdvanceDrawPhaseAsync();
                        return;
                    }
                }

                // Human seat (not in forced-discard riichi) — wait for their message.
                // HandlePlayerActionAsync will drive things forward.
                return;
            }

            // CPU seat — decide action
            await RunCpuActionAsync(seat);
        }

        private async Task RunCpuActionAsync(int seat)
        {
            if (_game == null || _abandoned) return;

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
            if (_abandoned) return;

            // Give players time to see the scoring panel
            await Task.Delay(HandResultPauseMs);

            // No gate needed when no humans are connected — advance straight away.
            if (_playerCount > 0)
            {
                // If the host already clicked "Next Hand" during the display pause, skip the wait.
                if (!_nextHandPending)
                {
                    // Wait for host to send "nextHand" (or auto-advance after a longer pause)
                    _nextHandTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var timeout  = Task.Delay(TimeSpan.FromSeconds(30));
                    await Task.WhenAny(_nextHandTcs.Task, timeout);
                    _nextHandTcs = null;
                }
                _nextHandPending = false;
            }

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
            _nextHandPending = false;  // clear stale flag from previous hand

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

                Enqueue((s, new ServerMessage
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
                    Enqueue((s, new ServerMessage
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
                    Enqueue((s, new ServerMessage
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
                Enqueue((s, new ServerMessage
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
                Enqueue((s, new ServerMessage
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
                Enqueue((s, new ServerMessage
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
                    msg.YakuNames     = _game.LastYakuResult.Yaku.Select(y => y.Name).ToArray();
                    msg.YakuFans      = _game.LastYakuResult.Yaku.Select(y => y.Fan).ToArray();
                    msg.YakuIsYakuman = _game.LastYakuResult.Yaku.Select(y => y.IsYakuman).ToArray();
                }
                if (_game.LastWinContext != null)
                {
                    msg.DoraCount    = _game.LastWinContext.DoraCount;
                    msg.UraDoraCount = _game.LastWinContext.UraDoraCount;
                    msg.RedDoraCount = _game.LastWinContext.RedDoraCount;
                }

                // Reveal all hands for wins (tsumo/ron) — includes actual closed tiles
                // so clients can show what everyone held, not just placeholder tiles.
                if (reason is HandEndReason.Tsumo or HandEndReason.Ron)
                {
                    msg.RevealedHands = allHands;
                }

                // For exhaustive draw, reveal only tenpai hands and include waiting tiles
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

                Enqueue((s, msg));
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
                Enqueue((s, new ServerMessage
                {
                    Type       = ServerMessageType.GameOver,
                    ScoreBoard = scoreBoard,
                }));
            }

            // Record lifetime stats for account players (fire-and-forget — this
            // handler runs synchronously under _gameSem, so no IO here).
            if (_accounts != null)
            {
                int topScore = Enumerable.Range(0, MaxPlayers).Max(s => _game.Players[s].Points);
                var results  = new List<(long id, bool won, int points)>();
                for (int s = 0; s < MaxPlayers; s++)
                {
                    if (_accountIds[s] is long id)
                        results.Add((id, _game.Players[s].Points == topScore, _game.Players[s].Points));
                }

                if (results.Count > 0)
                {
                    var store = _accounts;
                    _ = Task.Run(async () =>
                    {
                        foreach (var (id, won, points) in results)
                        {
                            try { await store.RecordGameResultAsync(id, won, points); }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Auth] Failed to record stats for account {id}: {ex.Message}");
                            }
                        }
                    });
                }
            }
        }

        // =====================================================================
        // Outbox flush + send helpers
        // =====================================================================

        /// <summary>Queue an outbound message (targetSeat -1 = broadcast). Thread-safe.</summary>
        private void Enqueue((int seat, ServerMessage msg) item)
        {
            lock (_outbox) _outbox.Add(item);
        }

        private async Task FlushOutboxAsync()
        {
            // The outbox is filled under _gameSem, but flushes run outside it and
            // can overlap between the game loop and per-connection action handlers.
            List<(int seat, ServerMessage msg)> items;
            lock (_outbox)
            {
                if (_outbox.Count == 0) return;
                items = new List<(int, ServerMessage)>(_outbox);
                _outbox.Clear();
            }

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
