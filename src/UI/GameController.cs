// =============================================================================
// GameController.cs
// Root script of the GameTable scene — bridges game logic and Godot rendering.
//
// Supports two modes detected at startup:
//   Local mode  — owns GameState + AIPlayer[]; same as original single-player flow.
//   Network mode — subscribes to NetworkManager events; routes human actions through
//                  NetworkManager.Send*(); the server drives everything else.
//
// The human seat is stored in _humanSeat (local: always 0; network: from server).
// GetHandDisplay() uses _humanSeat so seat-rotation works identically in both modes.
// =============================================================================

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;
using RiichiMahjong.AI;
using static RiichiMahjong.Core.Hand;

namespace RiichiMahjong.UI
{
    public partial class GameController : Node
    {
        // ---- Mode -----------------------------------------------------------

        private bool _isNetworkMode;
        private bool _isGameOver;          // true after OnGameOver / Net_OnGameOver fires
        private int  _humanSeat;           // 0 in local mode; server-assigned in network mode

        // ---- Local (single-player) objects ----------------------------------

        private GameState  _game = null!;
        private AIPlayer[] _ai   = null!;

        // ---- Reconnection state (network mode only) -------------------------

        private bool   _isReconnecting       = false;
        private int    _reconnectAttempts     = 0;
        private float  _reconnectTimer        = 0f;
        private bool   _waitingForSocketOpen  = false;
        private const int   MaxReconnectAttempts = 3;
        private const float ReconnectRetryDelay  = 3.0f;  // seconds between attempts
        private const float SocketOpenTimeout     = 5.0f;  // seconds to wait for WS to open

        // Reconnect overlay — shown while disconnected (built in code)
        private Control? _reconnectOverlay;
        private Label?   _reconnectStatusLabel;

        // ---- Network-mode display state -------------------------------------
        // Populated/updated by incoming server messages; drives HandDisplay + HUD.

        private string[] _netNames      = new string[4];
        private int[]    _netScores     = new int[4];
        private int[]    _netTileCounts = new int[4];
        private int      _netDealerSeat;
        private string   _netRoundWind  = "East";
        private int      _netCounters;
        private bool     _netIsInRiichi;          // human is in riichi this hand
        private bool     _netIsTemporaryFuriten;  // server notified us of a missed-discard furiten

        // Per-seat open melds built up from meldDeclared messages
        private readonly List<Meld>[] _netMelds =
            { new(), new(), new(), new() };

        // Human's own closed tiles (updated on handDealt / tileDrawn / meldDeclared)
        private readonly List<Tile> _netMyTiles    = new();
        private Tile?                _netDrawnTile  = null;

        // Human's own discard history — used to compute permanent furiten in network mode
        private readonly List<Tile> _netMyDiscards = new();

        // Last discard info — used to remove the correct tile when a meld is claimed
        private int   _netLastDiscarderSeat   = -1;
        private Tile? _netLastDiscardedTile   = null;

        // Best chi combo pre-computed when claim window opens
        private (Tile t1, Tile t2)? _netChiCombo;

        // Minimal AI helper used only for chi-combo / riichi-candidate checks in network mode
        private readonly AIPlayer _helperAi = new(AIDifficulty.Medium);

        // ---- Tile sort comparer (used in network mode to sort _netMyTiles) -----

        private static readonly IComparer<Tile> _tileOrder = Comparer<Tile>.Create((a, b) =>
        {
            int s = ((int)a.Suit).CompareTo((int)b.Suit);
            return s != 0 ? s : a.Value.CompareTo(b.Value);
        });

        // ---- AI turn delay (seconds — local mode only) ----------------------

        [Export] public float AIThinkDelay = 0.8f;
        [Export] public float AIClaimDelay = 0.4f;

        private float  _aiTimer         = 0f;
        private bool   _aiTimerActive   = false;
        private string _aiPendingAction = "";

        // ---- Claim window (local mode only) ---------------------------------

        private float _claimWindowTimer  = 0f;
        private bool  _claimWindowActive = false;
        private const float ClaimWindowDuration = 8.0f;

        // ---- Riichi selection mode (shared between modes) -------------------

        private bool       _riichiMode       = false;
        private List<Tile> _riichiCandidates = new();

        // ---- Riichi discard tracking ----------------------------------------

        private bool _nextDiscardIsRiichi = false;
        private int  _riichiDiscardSeat   = -1;

        // ---- Post-riichi auto-discard timer (local mode only) ---------------

        private bool  _autoDiscardPending = false;
        private float _autoDiscardTimer   = 0f;
        private const float AutoDiscardDelay = 0.9f;

        // ---- Action countdown (network mode only) ----------------------------
        // Fires an auto-pass or auto-discard when the player takes too long.

        private bool  _countdownActive  = false;
        private float _countdownTimer   = 0f;
        private bool  _countdownIsClaim = false;   // true = claim window, false = discard turn
        private const float ActionCountdownDuration = 20f;

        // ---- Child node references ------------------------------------------

        private HandDisplay _playerHand = null!;
        private HandDisplay _topHand    = null!;
        private HandDisplay _leftHand   = null!;
        private HandDisplay _rightHand  = null!;
        private HUD         _hud        = null!;

        // ---- Audio ----------------------------------------------------------

        private AudioStreamPlayer _bgMusic = null!;

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            _playerHand = GetNode<HandDisplay>("UI/PlayerHand");
            _topHand    = GetNode<HandDisplay>("UI/TopHand");
            _leftHand   = GetNode<HandDisplay>("UI/LeftHand");
            _rightHand  = GetNode<HandDisplay>("UI/RightHand");
            _hud        = GetNode<HUD>("UI/HUD");

            _playerHand.IsInteractive = true;
            _playerHand.FaceDown      = false;

            _playerHand.TileDiscarded += OnHumanTileDiscarded;
            _playerHand.TileSelected  += OnHumanTileSelected;

            _hud.RiichiPressed          += OnHumanRiichi;
            _hud.TsumoPressed           += OnHumanTsumo;
            _hud.RonPressed             += OnHumanRon;
            _hud.PonPressed             += OnHumanPon;
            _hud.ChiPressed             += OnHumanChi;
            _hud.KanPressed             += OnHumanKan;
            _hud.PassPressed            += OnHumanPass;
            _hud.KyuushuPressed         += OnHumanKyuushu;
            _hud.NextHandPressed        += OnNextHand;
            _hud.MenuPressed            += ReturnToMenu;
            _hud.ScoringNextHandPressed += OnNextHand;
            _hud.ScoringMenuPressed     += ReturnToMenu;

            // ---- Background music -------------------------------------------
            _bgMusic = new AudioStreamPlayer { Bus = "Master" };
            AddChild(_bgMusic);  // must be in the tree before Play() is called
            var music = GD.Load<AudioStream>(
                "res://Assets/Sounds/Whispering_Bamboo_Garden_2026-05-18T203144.wav");
            if (music != null)
            {
                _bgMusic.Stream   = music;
                _bgMusic.VolumeDb = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _bgMusic.Finished += () => _bgMusic.Play();
                _bgMusic.Play();
            }

            // ---- Detect and initialise mode ---------------------------------
            // Require both a valid seat AND an open socket so that a stale
            // LocalSeat from a previous multiplayer session doesn't accidentally
            // pull single-player into network mode.
            _isNetworkMode = NetworkManager.Instance != null
                             && NetworkManager.Instance.LocalSeat >= 0
                             && NetworkManager.Instance.IsSocketConnected;

            if (_isNetworkMode) InitNetworkMode();
            else                InitLocalMode();
        }

        public override void _ExitTree()
        {
            if (_isNetworkMode) UnsubscribeNetworkEvents();
        }

        // =====================================================================
        // Mode initialisation
        // =====================================================================

        private void InitLocalMode()
        {
            _humanSeat = 0;

            _game = new GameState(humanSeat: 0);
            _game.OnTileDrawn      += OnTileDrawn;
            _game.OnTileDiscarded  += OnTileDiscarded;
            _game.OnMeldDeclared   += OnMeldDeclared;
            _game.OnChankanOpened  += OnChankanOpened;
            _game.OnRiichiDeclared += OnRiichiDeclared;
            _game.OnHandEnd        += OnHandEnd;
            _game.OnNewHand        += OnNewHand;
            _game.OnGameOver       += OnGameOver;

            _ai = new[]
            {
                new AIPlayer(AIDifficulty.Medium),   // seat 0 — human (slot unused)
                new AIPlayer(AIDifficulty.Medium),   // seat 1
                new AIPlayer(AIDifficulty.Medium),   // seat 2
                new AIPlayer(AIDifficulty.Medium),   // seat 3
            };

            _game.StartGame();
        }

        private void InitNetworkMode()
        {
            _humanSeat = NetworkManager.Instance!.LocalSeat;
            foreach (var ml in _netMelds) ml.Clear();
            SubscribeNetworkEvents();
            _hud.SetStatus("Waiting for hand to be dealt…");
        }

        // =====================================================================
        // NetworkManager event wiring
        // =====================================================================

        private void SubscribeNetworkEvents()
        {
            var nm = NetworkManager.Instance!;
            nm.OnHandDealt          += Net_OnHandDealt;
            nm.OnTileDrawn          += Net_OnTileDrawn;
            nm.OnTileDiscarded      += Net_OnTileDiscarded;
            nm.OnMeldDeclared       += Net_OnMeldDeclared;
            nm.OnRiichiDeclared     += Net_OnRiichiDeclared;
            nm.OnClaimWindowOpened  += Net_OnClaimWindowOpened;
            nm.OnHandEnded          += Net_OnHandEnded;
            nm.OnGameOver           += Net_OnGameOver;
            nm.OnDisconnected       += Net_OnDisconnected;
            nm.OnGameStateSnapshot  += Net_OnGameStateSnapshot;
            nm.OnRejoinSuccess      += Net_OnRejoinSuccess;
            nm.OnDoraUpdated        += Net_OnDoraUpdated;
            nm.OnFuritenChanged     += Net_OnFuritenChanged;
        }

        private void UnsubscribeNetworkEvents()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;
            nm.OnHandDealt         -= Net_OnHandDealt;
            nm.OnTileDrawn         -= Net_OnTileDrawn;
            nm.OnTileDiscarded     -= Net_OnTileDiscarded;
            nm.OnMeldDeclared      -= Net_OnMeldDeclared;
            nm.OnRiichiDeclared    -= Net_OnRiichiDeclared;
            nm.OnClaimWindowOpened -= Net_OnClaimWindowOpened;
            nm.OnHandEnded         -= Net_OnHandEnded;
            nm.OnDoraUpdated       -= Net_OnDoraUpdated;
            nm.OnGameOver          -= Net_OnGameOver;
            nm.OnDisconnected      -= Net_OnDisconnected;
            nm.OnGameStateSnapshot -= Net_OnGameStateSnapshot;
            nm.OnRejoinSuccess     -= Net_OnRejoinSuccess;
            nm.OnFuritenChanged    -= Net_OnFuritenChanged;
        }

        // =====================================================================
        // Network-mode event handlers (server → UI)
        // =====================================================================

        private void Net_OnHandDealt(List<Tile> yourTiles, int[] tileCounts, int[] scores,
            int dealerSeat, string roundWind, int counters, string[] names)
        {
            StopActionCountdown();
            ExitRiichiMode();
            _autoDiscardPending      = false;
            _nextDiscardIsRiichi     = false;
            _riichiDiscardSeat       = -1;
            _netChiCombo             = null;
            _netIsInRiichi           = false;
            _netIsTemporaryFuriten   = false;
            _netLastDiscarderSeat    = -1;
            _netLastDiscardedTile    = null;

            _netNames      = names;
            _netScores     = scores;
            _netTileCounts = tileCounts;
            _netDealerSeat = dealerSeat;
            _netRoundWind  = roundWind;
            _netCounters   = counters;

            _netMyTiles.Clear();
            _netMyTiles.AddRange(yourTiles);
            _netDrawnTile = null;
            _netMyDiscards.Clear();
            foreach (var ml in _netMelds) ml.Clear();

            _playerHand.FaceDown = false;
            _topHand.FaceDown    = true;
            _leftHand.FaceDown   = true;
            _rightHand.FaceDown  = true;

            _hud.ClearAllDiscards();
            _hud.HideActionButtons();
            _hud.SetFuriten(false, false);
            _btnNextVisible(false);

            NetRebuildMyHand();
            for (int s = 0; s < 4; s++)
                if (s != _humanSeat) NetRebuildOpponentHand(s);

            _playerHand.StartDealAnimation();
            _topHand   .StartDealAnimation();
            _leftHand  .StartDealAnimation();
            _rightHand .StartDealAnimation();

            NetUpdateHud();
            _hud.SetStatus("Hand dealt — waiting for your turn…");
        }

        private void Net_OnTileDrawn(int seat, Tile? tile)
        {
            SoundManager.Instance?.Play(Sound.TileDraw);

            if (seat == _humanSeat)
            {
                // Drawing a tile clears temporary furiten — mirrors FuritenTracker.OnDraw()
                _netIsTemporaryFuriten = false;

                if (tile != null)
                {
                    _netMyTiles.Add(tile);
                    _netDrawnTile = tile;
                    // Sort all tiles except the drawn tile so it stays at the end
                    // (mirrors Hand.Sort() behaviour — drawn tile is always shown lifted at right)
                    if (_netMyTiles.Count > 1)
                        _netMyTiles.Sort(0, _netMyTiles.Count - 1, _tileOrder);
                }
                _netTileCounts[seat] = _netMyTiles.Count;
                NetRebuildMyHand();

                if (_netIsInRiichi)
                {
                    // Already in riichi — check tsumo
                    var hand = NetBuildHand();
                    bool canTsumo = hand.Shanten() == -1;
                    if (canTsumo)
                    {
                        _hud.ShowActionButtons(canTsumo: true, canRiichi: false, canKan: false);
                        _hud.SetStatus("TSUMO available! Click TSUMO to win, or discard drawn tile to pass.");
                        StartActionCountdown(isClaim: false);
                    }
                    else
                    {
                        // No decision to make — auto-discard the drawn tile after a short pause
                        // (mirrors local-mode behaviour; no 20-second countdown shown).
                        _hud.HideActionButtons();
                        _hud.SetStatus("In Riichi — discarding drawn tile…");
                        _autoDiscardPending = true;
                        _autoDiscardTimer   = AutoDiscardDelay;
                    }
                }
                else
                {
                    ShowHumanActionButtonsNet();
                    StartActionCountdown(isClaim: false);
                }
            }
            else
            {
                _netTileCounts[seat]++;
                NetRebuildOpponentHand(seat);
                _hud.SetStatus("");
            }
        }

        private void Net_OnTileDiscarded(int seat, Tile tile, bool isRiichi)
        {
            SoundManager.Instance?.Play(Sound.TileDiscard);

            _netLastDiscarderSeat = seat;
            _netLastDiscardedTile = tile;

            if (seat == _humanSeat)
            {
                // Remove from our closed tile list
                for (int i = _netMyTiles.Count - 1; i >= 0; i--)
                {
                    if (_netMyTiles[i] == tile) { _netMyTiles.RemoveAt(i); break; }
                }
                _netMyDiscards.Add(tile);
                _netDrawnTile = null;
                _netTileCounts[seat] = _netMyTiles.Count;
                GetHandDisplay(seat).RemoveTile(tile);
            }
            else
            {
                _netTileCounts[seat] = Math.Max(0, _netTileCounts[seat] - 1);
                NetRebuildOpponentHand(seat);
            }

            bool isRiichiDiscard = isRiichi && _nextDiscardIsRiichi && seat == _riichiDiscardSeat;
            if (isRiichiDiscard) { _nextDiscardIsRiichi = false; _riichiDiscardSeat = -1; }

            _hud.AddDiscard(ToVisualSeat(seat), tile, isRiichiDiscard);
            NetUpdateHud();
        }

        private void Net_OnDoraUpdated(List<Tile> indicators)
        {
            _hud.UpdateDoraIndicators(indicators);
        }

        private void Net_OnFuritenChanged(bool isTemporaryFuriten)
        {
            _netIsTemporaryFuriten = isTemporaryFuriten;
            NetUpdateHud();
        }

        private void Net_OnMeldDeclared(int seat, NetMeldDto dto)
        {
            var meld    = NetMeldDtoToMeld(dto);
            string type = dto.Type.ToLower();

            // Remove the claimed tile from the discarder's pool
            if (type is "chi" or "pon" or "kanopen")
                if (_netLastDiscarderSeat >= 0)
                    _hud.RemoveLastDiscard(ToVisualSeat(_netLastDiscarderSeat));

            _netMelds[seat].Add(meld);

            if (seat == _humanSeat)
            {
                // Remove hand tiles that went into the meld.
                // Open melds: one tile came from the discard (not in our hand), rest from hand.
                var meldTiles = dto.Tiles.Select(t => t.ToTile()).ToList();

                int fromHand = type switch
                {
                    "chi"         => 2,   // 2 of 3 tiles were in hand
                    "pon"         => 2,   // 2 of 3 tiles were in hand
                    "kanopen"     => 3,   // 3 of 4 tiles were in hand
                    "kanclosed"   => 4,   // all 4 from hand
                    "kanextended" => 1,   // just the one extra tile
                    _             => 0
                };

                // For open melds, skip one copy of the claimed (discard) tile
                var tilesToRemove = new List<Tile>(meldTiles);
                if (type is "chi" or "pon" or "kanopen" && _netLastDiscardedTile != null)
                {
                    for (int i = tilesToRemove.Count - 1; i >= 0; i--)
                    {
                        if (tilesToRemove[i] == _netLastDiscardedTile)
                        {
                            tilesToRemove.RemoveAt(i);
                            break;
                        }
                    }
                }

                // Remove at most `fromHand` tiles from our closed list
                int removed = 0;
                foreach (var t in tilesToRemove)
                {
                    if (removed >= fromHand) break;
                    for (int i = _netMyTiles.Count - 1; i >= 0; i--)
                    {
                        if (_netMyTiles[i] == t) { _netMyTiles.RemoveAt(i); removed++; break; }
                    }
                }

                _netDrawnTile        = null;
                _netTileCounts[seat] = _netMyTiles.Count;
                NetRebuildMyHand();

                // After pon/chi the player must discard; after kan, a rinshan draw will arrive
                if (type is "chi" or "pon")
                {
                    ShowHumanActionButtonsNet();
                    StartActionCountdown(isClaim: false);
                }
                else
                    _hud.SetStatus("Kan declared — waiting for rinshan draw…");
            }
            else
            {
                // Opponent: adjust closed-tile count and rebuild face-down display
                int fromHand = type switch
                {
                    "chi" or "pon" => 2,
                    "kanopen"      => 3,
                    "kanclosed"    => 4,
                    "kanextended"  => 1,
                    _              => 0
                };
                _netTileCounts[seat] = Math.Max(0, _netTileCounts[seat] - fromHand);
                NetRebuildOpponentHand(seat);
            }

            NetUpdateHud();
        }

        private void Net_OnRiichiDeclared(int seat)
        {
            SoundManager.Instance?.Play(Sound.Riichi);
            _nextDiscardIsRiichi = true;
            _riichiDiscardSeat   = seat;
            if (seat == _humanSeat) _netIsInRiichi = true;
            _hud.ShowRiichiStick(ToVisualSeat(seat));
            _hud.ShowRiichiCall(_netNames[seat]);
            NetUpdateHud();
            _hud.SetStatus($"{_netNames[seat]} declares Riichi!");
        }

        private void Net_OnClaimWindowOpened(int discarderSeat, Tile tile,
            bool canRon, bool canPon, bool canChi, bool canKan)
        {
            _hud.ShowClaimButtons(canRon: canRon, canPon: canPon, canChi: canChi, canKan: canKan);
            _hud.HighlightLastDiscard(ToVisualSeat(discarderSeat));

            // Pre-compute best chi combo so CHI button just sends it
            _netChiCombo = null;
            if (canChi)
            {
                var hand  = NetBuildHand();
                var combo = _helperAi.BestChiCombination(tile, hand);
                if (combo != null) _netChiCombo = combo;
            }

            // Highlight the hand tiles involved in the potential meld
            if (canPon)
                _playerHand.HighlightClaimTiles(new[] { tile, tile });
            else if (canChi && _netChiCombo != null)
                _playerHand.HighlightClaimTiles(new[] { _netChiCombo.Value.t1, _netChiCombo.Value.t2 });
            else if (canKan)
                _playerHand.HighlightClaimTiles(new[] { tile, tile, tile });

            _hud.SetStatus(canRon ? "RON available! Click RON or PASS." : "Claim window — act or PASS.");
            StartActionCountdown(isClaim: true);
        }

        private void Net_OnHandEnded(string reason, int[] winners, List<NetScoreEntry> scoreBoard,
            int han, int fu, int basePoints, string[] yakuNames, int doraCount, int uraDoraCount,
            int winnerSeat, int payerSeat)
        {
            StopActionCountdown();
            ExitRiichiMode();
            _nextDiscardIsRiichi = false;
            _riichiDiscardSeat   = -1;
            _netIsInRiichi       = false;

            if (_netLastDiscarderSeat >= 0)
                _hud.ClearLastDiscardHighlight(ToVisualSeat(_netLastDiscarderSeat));
            _playerHand.ClearClaimTileHighlights();

            // Reveal hands — in network mode opponents just flip their face-down tiles
            for (int i = 0; i < 4; i++)
                GetHandDisplay(i).RevealAll();

            _hud.HideActionButtons();

            // Apply updated scores from scoreboard
            foreach (var entry in scoreBoard)
                if (entry.Seat >= 0 && entry.Seat < 4) _netScores[entry.Seat] = entry.Points;
            NetUpdateHud();

            string r = reason.ToLower();
            string msg = r switch
            {
                "tsumo"          => $"🀄 {_netNames[winnerSeat]} wins by Tsumo!",
                "ron"            => $"🀄 {_netNames[winnerSeat]} wins by Ron!",
                "nagashimangan"  => winnerSeat >= 0 && winnerSeat < 4
                                    ? $"🀄 {_netNames[winnerSeat]} — Nagashi Mangan!"
                                    : "Nagashi Mangan!",
                "exhaustivedraw" => "Exhaustive draw (Ryuukyoku)",
                _                => "Hand over"
            };
            _hud.SetStatus(msg);

            if (r == "nagashimangan")
            {
                SoundManager.Instance?.Play(Sound.WinTsumo);
                string nagashiName = winnerSeat >= 0 && winnerSeat < 4
                    ? _netNames[winnerSeat] : "Nagashi";
                _hud.ShowWinCall(isTsumo: true, playerName: nagashiName, onComplete: () =>
                {
                    NetUpdateHud();
                    _btnNextVisible(true);
                });
                return;
            }

            bool isWin = r is "tsumo" or "ron";
            if (isWin)
            {
                bool   isTsumo    = r == "tsumo";
                string winnerName = _netNames[winnerSeat];
                string payerName  = payerSeat >= 0 && payerSeat < 4 ? _netNames[payerSeat] : "";

                SoundManager.Instance?.Play(isTsumo ? Sound.WinTsumo : Sound.WinRon);

                // Capture everything the scoring panel needs before the lambda
                string[]        capturedNames     = _netNames;
                int[]           capturedScores    = (int[])_netScores.Clone();
                int             capturedWinner    = winnerSeat;
                int             capturedDealer    = _netDealerSeat;
                string[]        capturedYaku      = yakuNames;
                int             capturedHan       = han;
                int             capturedFu        = fu;
                int             capturedDora      = doraCount;
                int             capturedUraDora   = uraDoraCount;
                int             capturedBase      = basePoints;

                _hud.ShowWinCall(isTsumo, winnerName, onComplete: () =>
                    _hud.ShowScoringPanelNet(
                        winnerName:     winnerName,
                        isTsumo:        isTsumo,
                        payerName:      payerName,
                        allNames:       capturedNames,
                        allPoints:      capturedScores,
                        winnerSeat:     capturedWinner,
                        dealerSeat:     capturedDealer,
                        yakuNames:      capturedYaku,
                        han:            capturedHan,
                        fu:             capturedFu,
                        doraCount:      capturedDora,
                        uraDoraCount:   capturedUraDora,
                        totalPointsWon: capturedBase));
            }
            else  // exhaustivedraw (and anything else)
            {
                SoundManager.Instance?.Play(Sound.ExhaustiveDraw);

                // Build the ryuukyoku reveal panel from server-supplied data
                var nm = NetworkManager.Instance;
                var revealedHands = nm?.LastRevealedHands;
                var revealedWaits = nm?.LastTenpaiWaits;

                var tenpaiSet  = new System.Collections.Generic.HashSet<int>(winners);
                int tenpaiCount = tenpaiSet.Count;
                int notenCount  = 4 - tenpaiCount;
                int perTenpai = tenpaiCount > 0 && notenCount > 0 ? 3000 / tenpaiCount : 0;
                int perNoten  = tenpaiCount > 0 && notenCount > 0 ? 3000 / notenCount  : 0;

                var names   = new string[4];
                var points  = new int[4];
                var tenpai  = new bool[4];
                var waits   = new List<Tile>[4];
                var deltas  = new int[4];

                for (int gs = 0; gs < 4; gs++)
                {
                    int vs     = ToVisualSeat(gs);
                    names[vs]  = _netNames[gs];
                    points[vs] = _netScores[gs];
                    tenpai[vs] = tenpaiSet.Contains(gs);
                    deltas[vs] = tenpai[vs]
                        ? (notenCount  > 0 ? +perTenpai : 0)
                        : (tenpaiCount > 0 ? -perNoten  : 0);
                    waits[vs]  = tenpai[vs] && revealedWaits != null && gs < revealedWaits.Count
                        ? revealedWaits[gs]
                        : new List<Tile>();

                    // Rebuild tenpai opponent hands with actual tiles, then highlight waits
                    if (tenpai[vs] && revealedHands != null && gs < revealedHands.Count
                        && revealedHands[gs].Count > 0)
                    {
                        GetHandDisplay(gs).Rebuild(
                            revealedHands[gs], _netMelds[gs], drawnTile: null);
                    }
                    if (tenpai[vs] && waits[vs].Count > 0)
                        GetHandDisplay(gs).HighlightClaimTiles(waits[vs]);
                }

                _hud.ShowRyuukyokuPanel(
                    names, points, ToVisualSeat(_netDealerSeat),
                    tenpai, waits, deltas);
            }
        }

        private void Net_OnGameOver(List<NetScoreEntry> scoreBoard)
        {
            StopActionCountdown();
            _isGameOver = true;
            SoundManager.Instance?.Play(Sound.GameOver);
            _hud.SetStatus("");
            var points = new int[4];
            foreach (var e in scoreBoard)
                if (e.Seat >= 0 && e.Seat < 4) points[e.Seat] = e.Points;

            _hud.ShowGameOverPanel(
                reason:        "Game over",
                playerNames:   _netNames,
                playerPoints:  points,
                dealerSeat:    _netDealerSeat,
                showPlayAgain: false);
        }

        private void Net_OnDisconnected()
        {
            if (_isReconnecting) return;   // already handling a disconnect

            _isReconnecting   = true;
            _reconnectAttempts = 0;
            _hud?.HideActionButtons();

            ShowReconnectOverlay("Disconnected — reconnecting…");
            BeginReconnectAttempt();
        }

        private void Net_OnRejoinSuccess()
        {
            // Server accepted the rejoin — hide overlay, game resumes via Net_OnGameStateSnapshot
            _isReconnecting = false;
            _waitingForSocketOpen = false;
            _reconnectOverlay?.QueueFree();
            _reconnectOverlay = null;
        }

        private void Net_OnGameStateSnapshot(
            List<Tile> yourTiles, int[] tileCounts, int[] scores,
            int dealerSeat, string roundWind, int counters, string[] names,
            List<List<NetTileDto>> discardDtos, List<List<NetMeldDto>> meldDtos,
            int[] riichiSeats, int currentTurn)
        {
            // Reset everything just like a new hand
            ExitRiichiMode();
            _nextDiscardIsRiichi     = false;
            _riichiDiscardSeat       = -1;
            _netChiCombo             = null;
            _netIsInRiichi           = false;
            _netIsTemporaryFuriten   = false;
            _netLastDiscarderSeat    = -1;
            _netLastDiscardedTile    = null;

            _netNames      = names;
            _netScores     = scores;
            _netTileCounts = tileCounts;
            _netDealerSeat = dealerSeat;
            _netRoundWind  = roundWind;
            _netCounters   = counters;

            _netMyTiles.Clear();
            _netMyTiles.AddRange(yourTiles);
            _netDrawnTile = null;
            _netMyDiscards.Clear();
            foreach (var ml in _netMelds) ml.Clear();

            // Apply riichi flags
            foreach (int rs in riichiSeats)
            {
                _hud.ShowRiichiStick(ToVisualSeat(rs));
                if (rs == _humanSeat) _netIsInRiichi = true;
            }

            // Convert and apply melds per seat
            for (int s = 0; s < 4 && s < meldDtos.Count; s++)
                foreach (var dto in meldDtos[s])
                    _netMelds[s].Add(NetMeldDtoToMeld(dto));

            // Reset face-down flags
            _playerHand.FaceDown = false;
            _topHand.FaceDown    = true;
            _leftHand.FaceDown   = true;
            _rightHand.FaceDown  = true;

            _hud.ClearAllDiscards();

            // Rebuild hands
            NetRebuildMyHand();
            for (int s = 0; s < 4; s++)
                if (s != _humanSeat) NetRebuildOpponentHand(s);

            // Replay discards for each seat (including riichi rotations)
            for (int s = 0; s < 4 && s < discardDtos.Count; s++)
                foreach (var tDto in discardDtos[s])
                    _hud.AddDiscard(ToVisualSeat(s), tDto.ToTile());

            NetUpdateHud();

            // Determine if it's our turn and show appropriate buttons
            if (currentTurn == _humanSeat)
            {
                ShowHumanActionButtonsNet();
                StartActionCountdown(isClaim: false);
            }
            else
                _hud.SetStatus("Reconnected — waiting for your turn…");
        }

        // =====================================================================
        // Reconnection logic (network mode only)
        // =====================================================================

        private void BeginReconnectAttempt()
        {
            _reconnectAttempts++;
            SetReconnectStatus($"Reconnecting… (attempt {_reconnectAttempts}/{MaxReconnectAttempts})");

            var nm = NetworkManager.Instance;
            if (nm == null) { OnReconnectFailed(); return; }

            nm.Connect(GameSettings.ServerUrl);
            _waitingForSocketOpen = true;
            _reconnectTimer       = SocketOpenTimeout;
        }

        private void SetReconnectStatus(string text)
        {
            if (_reconnectStatusLabel != null)
                _reconnectStatusLabel.Text = text;
        }

        private void OnReconnectFailed()
        {
            _isReconnecting       = false;
            _waitingForSocketOpen = false;
            SetReconnectStatus("Could not reconnect. Please return to the menu.");
            // Show only the Menu button by removing the reconnect button
        }

        private void ShowReconnectOverlay(string status)
        {
            _reconnectOverlay?.QueueFree();

            // Full-screen dim
            var overlay = new Control();
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            overlay.MouseFilter = Control.MouseFilterEnum.Stop;

            var bg = new ColorRect();
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            bg.Color = new Color(0f, 0f, 0f, 0.75f);
            overlay.AddChild(bg);

            // Centred card
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            card.OffsetLeft   = -220;
            card.OffsetTop    = -100;
            card.OffsetRight  =  220;
            card.OffsetBottom =  100;

            var style = new StyleBoxFlat();
            style.BgColor     = new Color(0.08f, 0.10f, 0.18f, 1f);
            style.BorderColor = new Color(0.30f, 0.45f, 0.70f, 1f);
            style.BorderWidthTop = style.BorderWidthBottom =
            style.BorderWidthLeft = style.BorderWidthRight = 2;
            style.CornerRadiusTopLeft    = style.CornerRadiusTopRight    =
            style.CornerRadiusBottomLeft = style.CornerRadiusBottomRight = 8;
            card.AddThemeStyleboxOverride("panel", style);

            var vbox = new VBoxContainer();
            vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            vbox.OffsetLeft   = 20;
            vbox.OffsetTop    = 16;
            vbox.OffsetRight  = -20;
            vbox.OffsetBottom = -16;
            vbox.Alignment    = BoxContainer.AlignmentMode.Center;
            vbox.AddThemeConstantOverride("separation", 14);

            var title = new Label { Text = "⚠  Disconnected" };
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.AddThemeFontSizeOverride("font_size", 20);
            title.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.3f));
            vbox.AddChild(title);

            _reconnectStatusLabel = new Label { Text = status };
            _reconnectStatusLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _reconnectStatusLabel.AddThemeFontSizeOverride("font_size", 14);
            _reconnectStatusLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.90f, 1f));
            vbox.AddChild(_reconnectStatusLabel);

            var menuBtn = new Button { Text = "← Return to Menu" };
            menuBtn.CustomMinimumSize = new Vector2(200, 42);
            menuBtn.AddThemeFontSizeOverride("font_size", 15);
            var btnStyle = new StyleBoxFlat();
            btnStyle.BgColor = new Color(0.25f, 0.28f, 0.38f);
            btnStyle.CornerRadiusTopLeft    = btnStyle.CornerRadiusTopRight    =
            btnStyle.CornerRadiusBottomLeft = btnStyle.CornerRadiusBottomRight = 6;
            menuBtn.AddThemeStyleboxOverride("normal", btnStyle);
            menuBtn.AddThemeColorOverride("font_color", Colors.White);
            menuBtn.Pressed += ReturnToMenu;
            vbox.AddChild(menuBtn);

            card.AddChild(vbox);
            overlay.AddChild(card);
            AddChild(overlay);

            _reconnectOverlay = overlay;
        }

        // =====================================================================
        // Network-mode helpers
        // =====================================================================

        /// <summary>Build a Hand from current local tile list (for Shanten / tenpai checks).</summary>
        private Hand NetBuildHand()
        {
            var hand = new Hand();
            hand.AddTiles(_netMyTiles);
            return hand;
        }

        private void NetRebuildMyHand()
        {
            _playerHand.Rebuild(_netMyTiles, _netMelds[_humanSeat], _netDrawnTile);
        }

        private void NetRebuildOpponentHand(int seat)
        {
            int count = Math.Max(0, _netTileCounts[seat]);
            var dummy = Enumerable.Repeat(new Tile(TileSuit.Man, 1, false), count).ToList();
            GetHandDisplay(seat).Rebuild(dummy, _netMelds[seat], drawnTile: null);
        }

        private void NetUpdateHud()
        {
            // HUD panels are indexed by VISUAL position (0=self/bottom, 1=right, 2=top, 3=left)
            // but _netNames/_netScores/_netDealerSeat are indexed by global server seat.
            // Rotate them so each visual slot shows the correct player.
            var rotNames  = new string[4];
            var rotScores = new int[4];
            for (int i = 0; i < 4; i++)
            {
                int vs = ToVisualSeat(i);
                rotNames[vs]  = _netNames[i];
                rotScores[vs] = _netScores[i];
            }
            _hud.UpdateAll(rotNames, rotScores, ToVisualSeat(_netDealerSeat), _netRoundWind, _netCounters);

            // Furiten indicator — only shown while waiting (no drawn tile, 13 tiles in hand).
            // Permanent furiten: own discard matches a current wait — computed client-side.
            // Temporary furiten: missed an opponent's winning discard — server notifies via
            // "furitenChanged"; we store the flag in _netIsTemporaryFuriten and clear it on draw.
            bool showFuriten = false;
            bool isPermanent = false;
            if (_netDrawnTile == null && _netMyTiles.Count == 13)
            {
                var h = NetBuildHand();
                if (h.IsTenpai())
                {
                    var waits = h.GetWaitingTiles();
                    isPermanent = waits.Any(w => _netMyDiscards.Any(d => d == w));
                    showFuriten = isPermanent || _netIsTemporaryFuriten;
                }
            }
            _hud.SetFuriten(showFuriten, isPermanent);
        }

        private void ShowHumanActionButtonsNet()
        {
            var hand = NetBuildHand();
            hand.Sort();

            bool tsumo = hand.Shanten() == -1;

            bool hasOpenMelds = _netMelds[_humanSeat].Any(m => m.IsOpen);
            bool riichi = false;
            if (!_netIsInRiichi && !hasOpenMelds && _netMyTiles.Count == 14)
                riichi = GetRiichiCandidatesFromTiles(_netMyTiles).Count > 0;

            bool kan = NetCanHumanKan();
            _hud.ShowActionButtons(canTsumo: tsumo, canRiichi: riichi, canKan: kan);
        }

        private bool NetCanHumanKan()
        {
            var counts = new Dictionary<int, int>();
            foreach (var t in _netMyTiles)
                counts[t.TileId] = counts.GetValueOrDefault(t.TileId, 0) + 1;
            if (counts.Values.Any(v => v >= 4)) return true;

            foreach (var meld in _netMelds[_humanSeat])
                if (meld.Type == MeldType.Pon && _netMyTiles.Any(t => t == meld.Lead))
                    return true;

            return false;
        }

        /// <summary>
        /// Convert a wire-format NetMeldDto into a Core Meld for display purposes.
        /// ClaimedTile / Source are approximated — they are not used for rendering.
        /// </summary>
        private static Meld NetMeldDtoToMeld(NetMeldDto dto)
        {
            var tiles = dto.Tiles.Select(t => t.ToTile()).ToList();
            if (tiles.Count == 0) tiles.Add(new Tile(TileSuit.Man, 1, false));

            return dto.Type.ToLower() switch
            {
                "chi"         => tiles.Count >= 3
                                 ? Meld.Chi(tiles[0], tiles[1], tiles[2], tiles[0])
                                 : Meld.Pair(tiles[0]),
                "pon"         => Meld.Pon(tiles[0], tiles[0], ClaimSource.Right),
                "kanopen"     => Meld.KanOpen(tiles[0], tiles[0], ClaimSource.Right),
                "kanclosed"   => Meld.KanClosed(tiles[0]),
                "kanextended" => Meld.KanExtended(tiles[0]),
                _             => Meld.Pair(tiles[0])
            };
        }

        /// <summary>
        /// Finds tiles that, when discarded from the given list, leave the remaining 13 in tenpai.
        /// Works from a raw tile list — usable in both local and network modes.
        /// </summary>
        private static List<Tile> GetRiichiCandidatesFromTiles(IList<Tile> allTiles)
        {
            var candidates = new List<Tile>();
            var seenIds    = new HashSet<int>();

            for (int i = 0; i < allTiles.Count; i++)
            {
                var candidate = allTiles[i];
                if (!seenIds.Add(candidate.TileId)) continue;

                var testTiles = allTiles.ToList();
                testTiles.RemoveAt(i);

                var testHand = new Hand();
                testHand.AddTiles(testTiles);

                if (testHand.IsTenpai())
                    candidates.Add(candidate);
            }
            return candidates;
        }

        // =====================================================================
        // Godot lifecycle — _Process, _Input
        // =====================================================================

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.Escape)
                    ReturnToMenu();
                else if (key.Keycode == Key.F12 && !_isNetworkMode)
                    PrintInputDiagnostics();
            }
        }

        public override void _Process(double delta)
        {
            // Network-mode: poll for reconnection
            if (_isNetworkMode && _isReconnecting)
            {
                if (_waitingForSocketOpen)
                {
                    var nm = NetworkManager.Instance;
                    if (nm != null && nm.IsSocketConnected)
                    {
                        // Socket is open — send rejoin request
                        _waitingForSocketOpen = false;
                        nm.SendRejoinRoom(nm.RoomCode);
                        SetReconnectStatus("Reconnected — syncing game state…");
                    }
                    else
                    {
                        _reconnectTimer -= (float)delta;
                        if (_reconnectTimer <= 0f)
                        {
                            // Timed out waiting for socket
                            if (_reconnectAttempts < MaxReconnectAttempts)
                            {
                                _reconnectTimer = ReconnectRetryDelay;
                                BeginReconnectAttempt();
                            }
                            else
                            {
                                OnReconnectFailed();
                            }
                        }
                    }
                }
                return;   // skip AI timers while reconnecting
            }

            // Network-mode: action countdown (auto-pass / auto-discard)
            if (_isNetworkMode && _countdownActive)
            {
                _countdownTimer -= (float)delta;
                _hud.UpdateCountdown(_countdownTimer, ActionCountdownDuration);
                if (_countdownTimer <= 0f)
                {
                    _countdownActive = false;
                    if (_countdownIsClaim) AutoPassClaim();
                    else                   AutoDiscardTurn();
                }
            }

            // Auto-discard timer — runs in both modes (riichi auto-discard in network,
            // and post-riichi-declaration discard in local mode).
            if (_autoDiscardPending)
            {
                _autoDiscardTimer -= (float)delta;
                if (_autoDiscardTimer <= 0f)
                {
                    _autoDiscardPending = false;
                    if (_isNetworkMode) ExecuteNetRiichiAutoDiscard();
                    else                ExecuteAutoDiscard();
                }
            }

            // AI and claim-window timers only run in local mode
            if (_isNetworkMode) return;

            if (_aiTimerActive)
            {
                _aiTimer -= (float)delta;
                if (_aiTimer <= 0f) { _aiTimerActive = false; ExecuteAIPendingAction(); }
            }

            if (_claimWindowActive)
            {
                _claimWindowTimer -= (float)delta;
                if (_claimWindowTimer <= 0f) { _claimWindowActive = false; AutoResolveClaimWindow(); }
            }
        }

        // =====================================================================
        // Local (single-player) GameState event handlers
        // =====================================================================

        private void OnNewHand()
        {
            ExitRiichiMode();
            _autoDiscardPending  = false;
            _nextDiscardIsRiichi = false;
            _riichiDiscardSeat   = -1;

            _playerHand.FaceDown = false;
            _topHand.FaceDown    = true;
            _leftHand.FaceDown   = true;
            _rightHand.FaceDown  = true;

            _hud.ClearAllDiscards();
            _hud.HideRyuukyokuPanel();
            RebuildAllHands();
            _playerHand.StartDealAnimation();
            _topHand   .StartDealAnimation();
            _leftHand  .StartDealAnimation();
            _rightHand .StartDealAnimation();
            HudUpdateLocal();
            _hud.SetStatus("");
            _hud.HideActionButtons();
            _btnNextVisible(false);

            TriggerCurrentPlayerAction();
        }

        private void OnNextHand()
        {
            if (_isGameOver)
            {
                // Reload the scene for a fresh game (local) or return to menu (network)
                _bgMusic?.Stop();
                if (_isNetworkMode) ReturnToMenu();
                else                GetTree().ChangeSceneToFile("res://Scenes/GameTable.tscn");
                return;
            }

            _hud.HideScoringPanel();
            _hud.HideRyuukyokuPanel();
            _btnNextVisible(false);

            if (_isNetworkMode) NetworkManager.Instance?.SendNextHand();
            else                _game.BeginNextHand();
        }

        private void _btnNextVisible(bool v) => _hud.SetNextHandButtonVisible(v);

        private void OnTileDrawn(int playerIndex)
        {
            SoundManager.Instance?.Play(Sound.TileDraw);

            var hand    = _game.Players[playerIndex].Hand;
            hand.Sort();
            GetHandDisplay(playerIndex).Rebuild(hand.ClosedTiles, hand.OpenMelds, hand.DrawnTile);
            HudUpdateLocal();

            if (playerIndex == _humanSeat)
            {
                if (hand.IsRiichi)
                {
                    bool canTsumo = hand.Shanten() == -1;
                    if (canTsumo)
                    {
                        _hud.ShowActionButtons(canTsumo: true, canRiichi: false, canKan: false);
                        _hud.SetStatus("TSUMO available! Click TSUMO to win, or discard to pass.");
                    }
                    else
                    {
                        _hud.HideActionButtons();
                        _hud.SetStatus("In Riichi — auto-discarding drawn tile…");
                        _autoDiscardPending = true;
                        _autoDiscardTimer   = AutoDiscardDelay;
                    }
                }
                else
                {
                    ShowHumanActionButtons();
                }
            }
            else
            {
                ScheduleAIAction(playerIndex, "discard");
            }
        }

        private void ExecuteAutoDiscard()
        {
            if (!_game.IsHumanTurn || _game.Phase != TurnPhase.ActionPhase) return;
            var hand = _game.Players[_humanSeat].Hand;
            if (!hand.IsRiichi || hand.DrawnTile == null) return;

            if (hand.Shanten() == -1)
            {
                _hud.ShowActionButtons(canTsumo: true, canRiichi: false, canKan: false);
                _hud.SetStatus("TSUMO available! Click TSUMO to win, or discard to pass.");
                return;
            }
            _game.Discard(_humanSeat, hand.DrawnTile);
        }

        private void OnTileDiscarded(int playerIndex, Tile tile)
        {
            SoundManager.Instance?.Play(Sound.TileDiscard);

            GetHandDisplay(playerIndex).RemoveTile(tile);

            bool isRiichiDiscard = _nextDiscardIsRiichi && playerIndex == _riichiDiscardSeat;
            if (isRiichiDiscard) { _nextDiscardIsRiichi = false; _riichiDiscardSeat = -1; }

            _hud.AddDiscard(ToVisualSeat(playerIndex), tile, isRiichiDiscard);
            HudUpdateLocal();

            OpenClaimWindow(playerIndex, tile);
        }

        private void OnMeldDeclared(int playerIndex, Meld meld)
        {
            if (meld.Type is MeldType.Chi or MeldType.Pon or MeldType.KanOpen)
                _hud.RemoveLastDiscard(ToVisualSeat(_game.DiscarderIndex));

            RebuildHand(playerIndex);
            HudUpdateLocal();
        }

        /// <summary>
        /// Fired when an opponent declares a kakan (extended kan).
        /// Opens a brief Ron-only claim window — the human may rob the kan (chankan);
        /// if they can't or choose not to, AI seats get the same chance, then the
        /// rinshan tile is drawn automatically via ResolveChankan().
        /// </summary>
        private void OnChankanOpened(int playerIndex, Tile tile)
        {
            var humanHand = _game.Players[_humanSeat].Hand;

            bool humanRon = playerIndex != _humanSeat
                            && !_game.Players[_humanSeat].Furiten.IsFuriten
                            && humanHand.IsTenpai()
                            && humanHand.IsWaitingFor(tile);

            if (humanRon)
            {
                _hud.ShowClaimButtons(canRon: true, canPon: false, canChi: false, canKan: false);
                _hud.HighlightLastDiscard(ToVisualSeat(playerIndex));
                _claimWindowActive = true;
                _claimWindowTimer  = ClaimWindowDuration;
            }
            else
            {
                ResolveChankanAI();
            }
        }

        /// <summary>
        /// Let AI seats attempt chankan ron.
        /// If no one robs, call ResolveChankan() so the kakan player draws their rinshan.
        /// </summary>
        private void ResolveChankanAI()
        {
            if (!_game.IsChankanWindow) return;
            var tile = _game.PendingDiscard!;

            // Check all seats (excluding the kakan player and human) for ron
            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _humanSeat) continue;
                if (_ai[seat].ShouldClaimRon(tile, _game.Players[seat].Hand, _game, seat))
                    if (_game.ClaimRon(seat)) return;   // OnHandEnd handles the rest
            }

            // No one robs — proceed to rinshan draw
            _game.ResolveChankan();
            TriggerCurrentPlayerAction();
        }

        private void OnRiichiDeclared(int playerIndex)
        {
            SoundManager.Instance?.Play(Sound.Riichi);
            _nextDiscardIsRiichi = true;
            _riichiDiscardSeat   = playerIndex;
            _hud.ShowRiichiStick(ToVisualSeat(playerIndex));
            _hud.ShowRiichiCall(_game.Players[playerIndex].Name);
            HudUpdateLocal();
            _hud.SetStatus($"{_game.Players[playerIndex].Name} declares Riichi!");
        }

        private void OnHandEnd(HandEndReason reason, int[] winners)
        {
            ExitRiichiMode();
            _claimWindowActive  = false;
            _aiTimerActive      = false;
            _autoDiscardPending = false;
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(ToVisualSeat(_game.DiscarderIndex));
            _playerHand.ClearClaimTileHighlights();

            for (int i = 0; i < 4; i++) GetHandDisplay(i).RevealAll();

            if (reason == HandEndReason.Ron && winners.Length > 0)
            {
                if (_game.LastDiscarderSeat >= 0) _hud.RemoveLastDiscard(ToVisualSeat(_game.LastDiscarderSeat));
                RebuildHand(winners[0]);
                GetHandDisplay(winners[0]).DrawnTileNode?.SetClaimHighlight(true);
            }

            _hud.HideActionButtons();
            _hud.SetFuriten(false, false);
            HudUpdateLocal();

            string msg = reason switch
            {
                HandEndReason.Tsumo          => $"🀄 {_game.Players[winners[0]].Name} wins by Tsumo!",
                HandEndReason.Ron when winners.Length == 2
                                             => $"🀄 Double Ron! {_game.Players[winners[0]].Name} & {_game.Players[winners[1]].Name}!",
                HandEndReason.Ron            => $"🀄 {_game.Players[winners[0]].Name} wins by Ron!",
                HandEndReason.NagashiMangan  => winners.Length == 1
                    ? $"🀄 {_game.Players[winners[0]].Name} — Nagashi Mangan!"
                    : "Nagashi Mangan!",
                HandEndReason.ExhaustiveDraw => "Exhaustive draw (Ryuukyoku)",
                HandEndReason.AbortiveDraw   => "Abortive draw",
                _                            => "Hand over"
            };
            _hud.SetStatus(msg);

            if (reason is HandEndReason.Tsumo or HandEndReason.Ron)
            {
                bool   isTsumo     = reason == HandEndReason.Tsumo;
                string winnerName  = winners.Length == 2
                    ? $"{_game.Players[winners[0]].Name} & {_game.Players[winners[1]].Name}"
                    : _game.Players[winners[0]].Name;
                int    winnerSeatL = winners[0];   // capture for lambda

                SoundManager.Instance?.Play(isTsumo ? Sound.WinTsumo : Sound.WinRon);

                _hud.ShowWinCall(isTsumo, winnerName, onComplete: () =>
                    ShowScoringOverlay(reason, winnerSeatL));
            }
            else if (reason == HandEndReason.NagashiMangan)
            {
                SoundManager.Instance?.Play(Sound.WinTsumo);   // mangan fanfare
                string nagashiNames = string.Join(" & ",
                    winners.Select(w => _game.Players[w].Name));
                _hud.ShowWinCall(isTsumo: true, playerName: nagashiNames, onComplete: () =>
                {
                    HudUpdateLocal();
                    _btnNextVisible(true);
                });
            }
            else if (reason == HandEndReason.AbortiveDraw)
            {
                // Abortive draw: show as ryuukyoku with no tenpai payments, specific message
                SoundManager.Instance?.Play(Sound.ExhaustiveDraw);
                ShowAbortiveDrawPanel();
            }
            else  // ExhaustiveDraw
            {
                SoundManager.Instance?.Play(Sound.ExhaustiveDraw);
                ShowRyuukyokuPanel(winners);
            }
        }

        /// <summary>
        /// Build and display the ryuukyoku (exhaustive draw) reveal panel in local mode.
        /// <paramref name="tenpaiGlobalSeats"/> = the winners[] array from OnHandEnd (tenpai seats).
        /// </summary>
        private void ShowRyuukyokuPanel(int[] tenpaiGlobalSeats)
        {
            var tenpaiSet  = new System.Collections.Generic.HashSet<int>(tenpaiGlobalSeats);
            int tenpaiCount = tenpaiSet.Count;
            int notenCount  = 4 - tenpaiCount;
            int perTenpai = tenpaiCount > 0 && notenCount > 0 ? 3000 / tenpaiCount : 0;
            int perNoten  = tenpaiCount > 0 && notenCount > 0 ? 3000 / notenCount  : 0;

            var names   = new string[4];
            var points  = new int[4];
            var tenpai  = new bool[4];
            var waits   = new List<Tile>[4];
            var deltas  = new int[4];

            for (int gs = 0; gs < 4; gs++)
            {
                int vs       = ToVisualSeat(gs);
                names[vs]    = _game.Players[gs].Name;
                points[vs]   = _game.Players[gs].Points;
                tenpai[vs]   = tenpaiSet.Contains(gs);
                deltas[vs]   = tenpai[vs]
                    ? (notenCount  > 0 ? +perTenpai : 0)
                    : (tenpaiCount > 0 ? -perNoten  : 0);
                waits[vs] = tenpai[vs]
                    ? _game.Players[gs].Hand.GetWaitingTiles()
                    : new List<Tile>();

                // Highlight waiting tiles on the revealed hand display
                if (tenpai[vs] && waits[vs].Count > 0)
                    GetHandDisplay(gs).HighlightClaimTiles(waits[vs]);
            }

            _hud.ShowRyuukyokuPanel(
                names, points, ToVisualSeat(_game.DealerIndex),
                tenpai, waits, deltas);
        }

        /// <summary>
        /// Show an abortive-draw panel (Kyuushu, Suufonren, Suukaikan, Sanchahou).
        /// No tenpai payments — the hand is simply discarded. Treat it like an
        /// all-no-ten ryuukyoku so the panel infrastructure is reused.
        /// </summary>
        private void ShowAbortiveDrawPanel()
        {
            var names  = new string[4];
            var points = new int[4];
            for (int gs = 0; gs < 4; gs++)
            {
                int vs     = ToVisualSeat(gs);
                names[vs]  = _game.Players[gs].Name;
                points[vs] = _game.Players[gs].Points;
            }

            // Reuse the ryuukyoku panel with no-tenpai for everyone (no payments, no waits shown)
            _hud.ShowRyuukyokuPanel(
                names, points, ToVisualSeat(_game.DealerIndex),
                new bool[4], new List<Tile>[4].Select(_ => new List<Tile>()).ToArray(), new int[4]);
        }

        private void ShowScoringOverlay(HandEndReason reason, int winnerSeat)
        {
            var score = _game.LastScoreResult;
            var yaku  = _game.LastYakuResult;
            var ctx   = _game.LastWinContext;
            if (score == null || yaku == null || ctx == null) { _btnNextVisible(true); return; }

            bool     isTsumo      = reason == HandEndReason.Tsumo;
            string   winnerName   = _game.Players[winnerSeat].Name;
            string   discarderName = _game.LastDiscarderSeat >= 0
                                     ? _game.Players[_game.LastDiscarderSeat].Name : "";
            string[] allNames  = _game.Players.Select(p => p.Name).ToArray();
            int[]    allPoints = _game.Players.Select(p => p.Points).ToArray();

            _hud.ShowScoringPanel(
                score, yaku, ctx,
                winnerName, isTsumo, discarderName,
                allNames, allPoints, winnerSeat, _game.DealerIndex);
        }

        private void OnGameOver()
        {
            _isGameOver         = true;
            _claimWindowActive  = false;
            _aiTimerActive      = false;
            _autoDiscardPending = false;

            SoundManager.Instance?.Play(Sound.GameOver);
            _hud.SetStatus("");
            _hud.ShowGameOverPanel(
                reason:        _game.GameOverReason,
                playerNames:   _game.Players.Select(p => p.Name).ToArray(),
                playerPoints:  _game.Players.Select(p => p.Points).ToArray(),
                dealerSeat:    _game.DealerIndex,
                showPlayAgain: true);
        }

        // =====================================================================
        // Human input handlers (UI → GameState or NetworkManager)
        // =====================================================================

        private void OnHumanTileSelected(TileNode tile)
        {
            if (_riichiMode)
            {
                if (tile.TileData != null && _riichiCandidates.Any(c => c == tile.TileData))
                    ShowWaitsForRiichi(tile.TileData);
                else
                    _hud.HideWaitsPopup();
                return;
            }

            if (_isNetworkMode) ShowHumanActionButtonsNet();
            else                ShowHumanActionButtons();
        }

        private void OnHumanTileDiscarded(TileNode tile)
        {
            if (tile.TileData == null) return;

            // ---- Network mode ------------------------------------------------
            if (_isNetworkMode)
            {
                if (_riichiMode)
                {
                    if (!_riichiCandidates.Any(c => c == tile.TileData))
                    {
                        _hud.SetStatus("Discard a highlighted (green) tile to declare Riichi.");
                        return;
                    }
                    var candidate = tile.TileData;
                    StopActionCountdown();
                    ExitRiichiMode();
                    NetworkManager.Instance?.SendRiichi(candidate);
                    _hud.HideActionButtons();
                    _hud.SetStatus("Riichi declared — waiting for server…");
                    return;
                }

                StopActionCountdown();
                NetworkManager.Instance?.SendDiscard(tile.TileData);
                _hud.HideActionButtons();
                return;
            }

            // ---- Local mode --------------------------------------------------
            if (_game.Phase != TurnPhase.ActionPhase) return;
            if (!_game.IsHumanTurn) return;

            var hand = _game.Players[_humanSeat].Hand;

            if (_riichiMode)
            {
                if (!_riichiCandidates.Any(c => c == tile.TileData))
                {
                    _hud.SetStatus("Discard a highlighted (green) tile to declare Riichi.");
                    return;
                }
                var candidate = tile.TileData;
                ExitRiichiMode();
                if (!_game.DeclareRiichi(_humanSeat, candidate))
                    _hud.SetStatus("Cannot declare Riichi right now.");
                return;
            }

            if (hand.IsRiichi)
            {
                var drawnNode = _playerHand.DrawnTileNode;
                if (drawnNode != null && drawnNode != tile)
                {
                    _hud.SetStatus("You are in Riichi — you can only discard your drawn tile.");
                    return;
                }
                _autoDiscardPending = false;
            }

            if (!_game.Discard(_humanSeat, tile.TileData))
                _hud.SetStatus("Cannot discard that tile right now.");
        }

        private void OnHumanRiichi()
        {
            if (_riichiMode)
            {
                ExitRiichiMode();
                if (_isNetworkMode) ShowHumanActionButtonsNet();
                else                ShowHumanActionButtons();
                return;
            }

            List<Tile> candidates = _isNetworkMode
                ? GetRiichiCandidatesFromTiles(_netMyTiles)
                : GetRiichiCandidates();

            if (candidates.Count == 0) { _hud.SetStatus("No valid riichi discard."); return; }

            _riichiMode       = true;
            _riichiCandidates = candidates;
            _playerHand.SetRiichiCandidates(_riichiCandidates);

            Tile? drawnTile = _isNetworkMode
                ? _netDrawnTile
                : _game.Players[_humanSeat].Hand.DrawnTile;

            var initial = candidates.FirstOrDefault(
                c => drawnTile != null && c.TileId == drawnTile.TileId) ?? candidates[0];
            ShowWaitsForRiichi(initial);

            _hud.SetStatus("Riichi — select a green tile to see waits, then discard it.");
        }

        private void OnHumanTsumo()
        {
            if (_isNetworkMode)
            {
                StopActionCountdown();
                NetworkManager.Instance?.SendTsumo();
                _hud.HideActionButtons();
                return;
            }
            if (!_game.DeclareTsumo()) _hud.SetStatus("Cannot declare tsumo — hand is not complete.");
        }

        private void OnHumanRon()
        {
            if (_isNetworkMode)
            {
                StopActionCountdown();
                NetworkManager.Instance?.SendRon();
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                return;
            }

            // Include any AI seats that also want to ron (double ron / sanchahou check)
            var tile = _game.PendingDiscard;
            var candidates = new List<int> { _humanSeat };
            if (tile != null)
            {
                for (int i = 1; i <= 3; i++)
                {
                    int seat = (_game.DiscarderIndex + i) % 4;
                    if (seat == _humanSeat) continue;
                    if (_ai[seat].ShouldClaimRon(tile, _game.Players[seat].Hand, _game, seat))
                        candidates.Add(seat);
                }
            }
            if (!_game.ClaimRonMulti(candidates.ToArray()))
                _hud.SetStatus("Cannot declare ron — furiten or invalid hand.");
        }

        private void OnHumanPon()
        {
            if (_isNetworkMode)
            {
                StopActionCountdown();
                NetworkManager.Instance?.SendPon();
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                _hud.SetStatus("Pon declared — waiting for server…");
                return;
            }

            if (_game.PendingDiscard == null) return;
            if (!_game.ClaimPon(_humanSeat)) { _hud.SetStatus("Cannot pon."); return; }
            _claimWindowActive = false;
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            ShowHumanActionButtons();
        }

        private void OnHumanChi()
        {
            if (_isNetworkMode)
            {
                if (_netChiCombo == null) { _hud.SetStatus("No valid chi."); return; }
                StopActionCountdown();
                NetworkManager.Instance?.SendChi(_netChiCombo.Value.t1, _netChiCombo.Value.t2);
                _netChiCombo = null;
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                _hud.SetStatus("Chi declared — waiting for server…");
                return;
            }

            if (_game.PendingDiscard == null) return;
            var combo = _ai[_humanSeat].BestChiCombination(
                _game.PendingDiscard, _game.Players[_humanSeat].Hand);
            if (combo == null) { _hud.SetStatus("No valid chi."); return; }
            if (!_game.ClaimChi(_humanSeat, combo.Value.t1, combo.Value.t2))
                { _hud.SetStatus("Cannot chi."); return; }
            _claimWindowActive = false;
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            ShowHumanActionButtons();
        }

        private void OnHumanKan()
        {
            if (_isNetworkMode)
            {
                StopActionCountdown();

                // Daiminkan (claim window): the server already knows the tile from PendingDiscard,
                // so send no tile.  Ankan / kakan (action phase): we must tell the server which
                // tile to use, otherwise it wrongly falls into the daiminkan branch.
                // Discriminator: if the player has a drawn tile they're in their action phase.
                Tile? kanTile = null;
                if (_netDrawnTile != null)
                {
                    // Kakan: pon meld whose lead tile appears in the closed hand (4th copy drawn)
                    foreach (var meld in _netMelds[_humanSeat])
                    {
                        if (meld.Type == MeldType.Pon)
                        {
                            var match = _netMyTiles.FirstOrDefault(t => t == meld.Lead);
                            if (match != null) { kanTile = match; break; }
                        }
                    }
                    // Ankan: four identical tiles in the closed hand
                    if (kanTile == null)
                    {
                        var counts = new Dictionary<int, int>();
                        var first  = new Dictionary<int, Tile>();
                        foreach (var t in _netMyTiles)
                        {
                            counts[t.TileId] = counts.GetValueOrDefault(t.TileId) + 1;
                            first.TryAdd(t.TileId, t);
                        }
                        foreach (var (id, cnt) in counts)
                            if (cnt >= 4) { kanTile = first[id]; break; }
                    }
                }

                NetworkManager.Instance?.SendKan(kanTile);
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                _hud.SetStatus("Kan declared — waiting for server…");
                return;
            }

            if (_game.Phase == TurnPhase.ClaimWindow)
            {
                if (!_game.ClaimDaiminkan(_humanSeat)) { _hud.SetStatus("Cannot declare kan."); return; }
                _claimWindowActive = false;
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
            }
            else if (_game.Phase == TurnPhase.ActionPhase && _game.IsHumanTurn)
            {
                var hand = _game.Players[_humanSeat].Hand;

                foreach (var meld in hand.OpenMelds)
                {
                    if (meld.Type == MeldType.Pon && hand.ClosedTiles.Any(t => t == meld.Lead))
                        if (_game.DeclareKakan(_humanSeat, meld.Lead)) return;
                }

                foreach (var t in Hand.AllTileTypes())
                {
                    if (hand.ClosedTiles.Count(c => c == t) >= 4)
                        if (_game.DeclareAnkan(_humanSeat, t)) return;
                }

                _hud.SetStatus("No valid kan available.");
            }
        }

        private void OnHumanPass()
        {
            if (_isNetworkMode)
            {
                StopActionCountdown();
                NetworkManager.Instance?.SendPass();
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                _hud.SetStatus("Passed — waiting for server…");
                return;
            }

            _claimWindowActive = false;
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(ToVisualSeat(_game.DiscarderIndex));
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            if (_game.IsChankanWindow) ResolveChankanAI();
            else                       ResolveAIClaims();
        }

        private void OnHumanKyuushu()
        {
            if (_isNetworkMode) return;  // server-side implementation pending
            if (_game.DeclareKyuushuKyuuhai(_humanSeat))
                _hud.HideActionButtons();
        }

        // =====================================================================
        // Riichi helpers (shared between modes)
        // =====================================================================

        private List<Tile> GetRiichiCandidates()
            => GetRiichiCandidatesFromTiles(_game.Players[_humanSeat].Hand.ClosedTiles.ToList());

        private void ShowWaitsForRiichi(Tile discardTile)
        {
            IList<Tile> source = _isNetworkMode
                ? (IList<Tile>)_netMyTiles
                : (IList<Tile>)_game.Players[_humanSeat].Hand.ClosedTiles.ToList();

            var testTiles = source.ToList();
            for (int i = 0; i < testTiles.Count; i++)
                if (testTiles[i] == discardTile) { testTiles.RemoveAt(i); break; }

            var testHand = new Hand();
            testHand.AddTiles(testTiles);
            var waits = testHand.GetWaitingTiles();

            // Remaining-count computation only available in local mode (we can see all discards)
            Dictionary<int, int>? remaining = _isNetworkMode
                ? null
                : ComputeRemainingCounts(waits, testTiles);

            _hud.ShowWaitsPopup(waits, remaining, discardTile);
        }

        private Dictionary<int, int> ComputeRemainingCounts(
            List<Tile> waitTiles, IEnumerable<Tile>? ownClosedTiles = null)
        {
            var remaining = new Dictionary<int, int>();
            foreach (var t in waitTiles) remaining.TryAdd(t.TileId, 4);

            for (int seat = 0; seat < 4; seat++)
            {
                foreach (var d in _game.Players[seat].Discards)
                    if (remaining.ContainsKey(d.TileId)) remaining[d.TileId]--;
                foreach (var meld in _game.Players[seat].Hand.OpenMelds)
                    foreach (var mt in meld.Tiles)
                        if (remaining.ContainsKey(mt.TileId)) remaining[mt.TileId]--;
            }

            if (ownClosedTiles != null)
                foreach (var t in ownClosedTiles)
                    if (remaining.ContainsKey(t.TileId)) remaining[t.TileId]--;

            foreach (var key in remaining.Keys.ToList())
                remaining[key] = Math.Max(0, remaining[key]);

            return remaining;
        }

        private void ExitRiichiMode()
        {
            if (!_riichiMode) return;
            _riichiMode = false;
            _riichiCandidates.Clear();
            _playerHand.ClearRiichiCandidates();
            _hud.HideWaitsPopup();
        }

        // =====================================================================
        // Local-mode claim window
        // =====================================================================

        private void OpenClaimWindow(int discarderIndex, Tile tile)
        {
            if (_game.Phase != TurnPhase.ClaimWindow) return;

            var humanHand = _game.Players[_humanSeat].Hand;

            bool humanRon = discarderIndex != _humanSeat
                            && !_game.Players[_humanSeat].Furiten.IsFuriten
                            && humanHand.IsTenpai()
                            && humanHand.IsWaitingFor(tile);

            bool humanPon = !humanHand.IsRiichi
                            && discarderIndex != _humanSeat
                            && humanHand.ClosedTiles.Count(t => t == tile) >= 2;

            int  leftOfHuman = (_humanSeat - 1 + 4) % 4;
            bool humanChi    = discarderIndex == leftOfHuman
                               && !humanHand.IsRiichi
                               && _ai[_humanSeat].BestChiCombination(tile, humanHand) != null;

            bool humanKan = discarderIndex != _humanSeat
                            && !humanHand.IsRiichi
                            && humanHand.ClosedTiles.Count(t => t == tile) >= 3
                            && _game.Wall.KanCount < 4;

            bool humanCanAct = humanRon || humanPon || humanChi || humanKan;

            if (humanCanAct)
            {
                _hud.ShowClaimButtons(canRon: humanRon, canPon: humanPon,
                                      canChi: humanChi, canKan: humanKan);
                _hud.HighlightLastDiscard(ToVisualSeat(discarderIndex));

                if (humanPon)
                    _playerHand.HighlightClaimTiles(new[] { tile, tile });
                else if (humanChi)
                {
                    var combo = _ai[_humanSeat].BestChiCombination(tile, humanHand);
                    if (combo != null)
                        _playerHand.HighlightClaimTiles(new[] { combo.Value.t1, combo.Value.t2 });
                }
                else if (humanKan)
                    _playerHand.HighlightClaimTiles(new[] { tile, tile, tile });

                _claimWindowActive = !humanRon;
                if (_claimWindowActive) _claimWindowTimer = ClaimWindowDuration;
            }
            else
            {
                ScheduleAIAction(discarderIndex, "claim");
            }
        }

        private void AutoResolveClaimWindow()
        {
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(ToVisualSeat(_game.DiscarderIndex));
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            if (_game.IsChankanWindow) ResolveChankanAI();
            else                       ResolveAIClaims();
        }

        private void ResolveAIClaims()
        {
            if (_game.Phase != TurnPhase.ClaimWindow) return;
            if (_game.PendingDiscard == null) return;

            var tile = _game.PendingDiscard;

            // Collect all AI seats that want to ron, then resolve together (double ron / sanchahou).
            var ronCandidates = new List<int>();
            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _humanSeat) continue;
                if (_ai[seat].ShouldClaimRon(tile, _game.Players[seat].Hand, _game, seat))
                    ronCandidates.Add(seat);
            }
            if (ronCandidates.Count > 0 && _game.ClaimRonMulti(ronCandidates.ToArray())) return;

            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _humanSeat) continue;
                if (_ai[seat].ShouldClaimDaiminkan(tile, _game.Players[seat].Hand, _game, seat))
                    if (_game.ClaimDaiminkan(seat)) return;
            }

            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _humanSeat) continue;
                if (_ai[seat].ShouldClaimPon(tile, _game.Players[seat].Hand, _game, seat))
                    if (_game.ClaimPon(seat)) { TriggerCurrentPlayerAction(); return; }
            }

            int leftSeat = (_game.DiscarderIndex + 1) % 4;
            if (leftSeat != _humanSeat)
            {
                var aiHand = _game.Players[leftSeat].Hand;
                var combo  = _ai[leftSeat].BestChiCombination(tile, aiHand);
                if (combo != null && _ai[leftSeat].ShouldClaimChi(
                        tile, combo.Value.t1, combo.Value.t2, aiHand, _game, leftSeat))
                    if (_game.ClaimChi(leftSeat, combo.Value.t1, combo.Value.t2))
                        { TriggerCurrentPlayerAction(); return; }
            }

            _game.PassAllClaims();
            TriggerCurrentPlayerAction();
        }

        // =====================================================================
        // Local-mode AI scheduling
        // =====================================================================

        private void ScheduleAIAction(int playerIndex, string action)
        {
            _aiPendingAction = $"{action}:{playerIndex}";
            _aiTimer         = action == "discard" ? AIThinkDelay : AIClaimDelay;
            _aiTimerActive   = true;
        }

        private void ExecuteAIPendingAction()
        {
            var parts  = _aiPendingAction.Split(':');
            var action = parts[0];
            int seat   = int.Parse(parts[1]);
            if (action == "discard") ExecuteAIDiscard(seat);
            else if (action == "claim") ResolveAIClaims();
        }

        private void ExecuteAIDiscard(int seat)
        {
            if (_game.Phase != TurnPhase.ActionPhase) return;
            if (_game.CurrentPlayerIndex != seat) return;

            var hand = _game.Players[seat].Hand;

            // Kyuushu Kyuuhai: AI always declares the optional abort when eligible
            if (_game.CanDeclareKyuushu(seat) && _game.DeclareKyuushuKyuuhai(seat)) return;

            if (_game.DeclareTsumo()) return;

            var ankanTile = _ai[seat].GetAnkanTile(hand, _game, seat);
            if (ankanTile != null && _game.DeclareAnkan(seat, ankanTile)) return;

            var kakanTile = _ai[seat].GetKakanTile(hand, _game, seat);
            if (kakanTile != null && _game.DeclareKakan(seat, kakanTile)) return;

            if (_ai[seat].ShouldDeclareRiichi(hand, _game, seat))
            {
                var closedList = hand.ClosedTiles.ToList();
                var seenIds    = new HashSet<int>();
                for (int i = 0; i < closedList.Count; i++)
                {
                    if (!seenIds.Add(closedList[i].TileId)) continue;
                    var testTiles = closedList.ToList();
                    testTiles.RemoveAt(i);
                    var testHand = new Hand();
                    testHand.AddTiles(testTiles);
                    if (testHand.IsTenpai())
                        if (_game.DeclareRiichi(seat, closedList[i])) return;
                }
            }

            _game.Discard(seat, _ai[seat].ChooseDiscard(hand, _game, seat));
        }

        private void TriggerCurrentPlayerAction()
        {
            if (_game.Phase == TurnPhase.DrawPhase)
            {
                _game.DrawForCurrentPlayer();  // OnTileDrawn fires and handles the rest
            }
            else if (_game.Phase == TurnPhase.ActionPhase)
            {
                if (_game.CurrentPlayerIndex == _humanSeat) ShowHumanActionButtons();
                else ScheduleAIAction(_game.CurrentPlayerIndex, "discard");
            }
        }

        // =====================================================================
        // Local-mode UI helpers
        // =====================================================================

        private void ShowHumanActionButtons()
        {
            if (!_game.IsHumanTurn) return;

            var  hand  = _game.Players[_humanSeat].Hand;
            bool tsumo = _game.WinChecker_CanWinTsumo(_humanSeat);

            bool riichi = false;
            if (!hand.IsRiichi && hand.IsFullyClosed
                && _game.Wall.TilesRemaining >= 4
                && _game.Players[_humanSeat].Points >= GameState.RiichiBetAmount)
            {
                riichi = GetRiichiCandidates().Count > 0;
            }

            bool kyuushu = _game.CanDeclareKyuushu(_humanSeat);
            _hud.ShowActionButtons(canTsumo: tsumo, canRiichi: riichi, canKan: CanHumanKan(), canKyuushu: kyuushu);
        }

        private bool CanHumanKan()
        {
            if (_game.Wall.KanCount >= 4) return false;
            var hand = _game.Players[_humanSeat].Hand;
            if (hand.IsRiichi) return false;

            var counts = new Dictionary<int, int>();
            foreach (var t in hand.ClosedTiles)
                counts[t.TileId] = counts.GetValueOrDefault(t.TileId, 0) + 1;
            if (counts.Values.Any(v => v >= 4)) return true;

            return hand.OpenMelds.Any(m => m.Type == MeldType.Pon
                                           && hand.ClosedTiles.Any(t => t == m.Lead));
        }

        /// <summary>
        /// Refresh all local-mode HUD panels and update the furiten badge.
        /// Replaces bare _hud.UpdateAll(_game) throughout local mode so the
        /// furiten indicator stays in sync without extra call sites.
        /// </summary>
        private void HudUpdateLocal()
        {
            _hud.UpdateAll(_game);
            var player = _game.Players[_humanSeat];
            // Show furiten only while waiting (no drawn tile) and in tenpai —
            // that's the only phase where it can actually block a ron claim.
            bool waiting = player.Hand.DrawnTile == null && player.Hand.IsTenpai();
            _hud.SetFuriten(
                waiting && player.Furiten.IsFuriten,
                player.Furiten.IsPermanentFuriten);
        }

        private void RebuildAllHands()
        {
            for (int i = 0; i < 4; i++) RebuildHand(i);
        }

        private void RebuildHand(int seat)
        {
            var hand = _game.Players[seat].Hand;
            GetHandDisplay(seat).Rebuild(hand.ClosedTiles, hand.OpenMelds, hand.DrawnTile);
        }

        // =====================================================================
        // Action countdown helpers (network mode only)
        // =====================================================================

        /// <summary>
        /// Start the 20-second HUD countdown.
        /// <paramref name="isClaim"/> = true during a claim window (auto-pass on expiry);
        /// false during a discard turn (auto-discard drawn tile on expiry).
        /// </summary>
        private void StartActionCountdown(bool isClaim)
        {
            _countdownActive  = true;
            _countdownTimer   = ActionCountdownDuration;
            _countdownIsClaim = isClaim;
            _hud.StartCountdown(ActionCountdownDuration);
        }

        private void StopActionCountdown()
        {
            if (!_countdownActive) return;
            _countdownActive = false;
            _hud.StopCountdown();
        }

        /// <summary>Countdown expired during a claim window — send Pass automatically.</summary>
        private void AutoPassClaim()
        {
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            _hud.StopCountdown();
            _hud.SetStatus("⏱ Time's up — passing.");
            NetworkManager.Instance?.SendPass();
        }

        /// <summary>
        /// Countdown expired during a discard turn — discard the drawn tile
        /// (or the last tile in hand as a fallback).
        /// </summary>
        private void AutoDiscardTurn()
        {
            ExitRiichiMode();
            _hud.HideActionButtons();
            _hud.StopCountdown();

            Tile? toDiscard = _netDrawnTile
                ?? (_netMyTiles.Count > 0 ? _netMyTiles[^1] : null);
            if (toDiscard == null) return;

            _hud.SetStatus("⏱ Time's up — auto-discarding.");
            NetworkManager.Instance?.SendDiscard(toDiscard);
        }

        /// <summary>
        /// Called after the short auto-discard delay fires while in riichi (network mode).
        /// Sends the drawn tile back to the server without showing a countdown.
        /// </summary>
        private void ExecuteNetRiichiAutoDiscard()
        {
            if (!_netIsInRiichi || _netDrawnTile == null) return;
            _hud.HideActionButtons();
            NetworkManager.Instance?.SendDiscard(_netDrawnTile);
        }

        // =====================================================================
        // Shared helpers
        // =====================================================================

        private void ReturnToMenu()
        {
            _aiTimerActive      = false;
            _claimWindowActive  = false;
            _autoDiscardPending = false;
            StopActionCountdown();
            _bgMusic?.Stop();
            if (_isNetworkMode) NetworkManager.Instance?.Disconnect();
            GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
        }

        /// <summary>
        /// Convert a global server seat (0-3) to the visual position index used by HUD and
        /// discard-pool arrays: 0=bottom (self), 1=right, 2=top, 3=left.
        /// For local mode (_humanSeat == 0) this is an identity function.
        /// </summary>
        private int ToVisualSeat(int globalSeat) => (globalSeat - _humanSeat + 4) % 4;

        /// <summary>Map a global seat number to the correct HandDisplay using seat rotation.</summary>
        private HandDisplay GetHandDisplay(int seat)
        {
            return ((seat - _humanSeat + 4) % 4) switch
            {
                0 => _playerHand,   // self — bottom
                1 => _rightHand,    // next clockwise — right
                2 => _topHand,      // across — top
                3 => _leftHand,     // counter-clockwise — left
                _ => _playerHand,
            };
        }



        // =====================================================================
        // Debug
        // =====================================================================

        private void PrintInputDiagnostics()
        {
            GD.Print("=== INPUT DIAGNOSTICS (F12) ===");
            GD.Print($"  Window={DisplayServer.WindowGetSize()}  VP={GetViewport().GetVisibleRect()}");
            GD.Print($"  HUD.MouseFilter={_hud.MouseFilter}");
            GD.Print($"  PlayerHand.IsInteractive={_playerHand.IsInteractive}");
            var inner = _playerHand.GetChildCount() > 0 ? _playerHand.GetChild(0) : null;
            if (inner != null)
                for (int i = 0; i < Math.Min(3, inner.GetChildCount()); i++)
                    if (inner.GetChild(i) is TileNode tn)
                        GD.Print($"  TileNode[{i}] Filter={tn.MouseFilter} Rect={tn.GetGlobalRect()}");
            GD.Print("===============================");
        }
    }
}
