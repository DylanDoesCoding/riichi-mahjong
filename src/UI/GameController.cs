// =============================================================================
// GameController.cs
// The root script of the GameTable scene — the bridge between GameState
// (pure C# logic) and Godot (rendering, input, timing).
//
// Responsibilities:
//   - Own the GameState and three AIPlayer instances
//   - Drive the game loop: advance AI turns automatically with a small delay
//   - Listen to GameState events and update the UI in response
//   - Route human player input (tile clicks, button presses) into GameState
//   - Handle the claim window: show Pon/Chi/Ron buttons when applicable
//
// Node structure expected in the scene (set up in GameTable.tscn):
//   GameTable (this script)
//   └── UI (Control)
//       ├── PlayerHand    (HandDisplay — bottom)
//       ├── TopHand       (HandDisplay — top)
//       ├── LeftHand      (HandDisplay — left)
//       ├── RightHand     (HandDisplay — right)
//       ├── DiscardArea   (Control — centre table)
//       └── HUD           (HUD script)
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
        // ---- Game logic objects (no Godot dependency) -----------------------

        private GameState  _game  = null!;
        private AIPlayer[] _ai    = null!;

        // ---- AI turn delay (seconds) ----------------------------------------

        [Export] public float AIThinkDelay   = 0.8f;   // Pause before AI discards
        [Export] public float AIClaimDelay   = 0.4f;   // Pause before AI decides to claim

        // ---- Pending AI timer -----------------------------------------------

        private float  _aiTimer      = 0f;
        private bool   _aiTimerActive = false;
        private string _aiPendingAction = "";

        // ---- Claim window state ---------------------------------------------

        // When a tile is discarded, we wait briefly for human to claim before AI claims
        private float  _claimWindowTimer  = 0f;
        private bool   _claimWindowActive = false;
        private const float ClaimWindowDuration = 8.0f;   // Seconds human has to click Ron/Pon/Chi

        // ---- Riichi selection mode ------------------------------------------

        // True while the player is in riichi mode (choosing which tile to discard for riichi)
        private bool       _riichiMode       = false;
        private List<Tile> _riichiCandidates = new();

        // ---- Riichi discard tracking ----------------------------------------

        // Set in OnRiichiDeclared; consumed in the immediately-following OnTileDiscarded.
        // Tells AddDiscard to render that one tile sideways in the river.
        private bool _nextDiscardIsRiichi = false;
        private int  _riichiDiscardSeat   = -1;

        // ---- Post-riichi auto-discard timer ---------------------------------

        // After declaring riichi and drawing a tile that can't tsumo, auto-discard after a brief pause
        private bool  _autoDiscardPending = false;
        private float _autoDiscardTimer   = 0f;
        private const float AutoDiscardDelay = 0.9f;   // Seconds to show drawn tile before discarding

        // ---- Child node references (assigned in _Ready) --------------------

        private HandDisplay _playerHand  = null!;
        private HandDisplay _topHand     = null!;
        private HandDisplay _leftHand    = null!;
        private HandDisplay _rightHand   = null!;
        private HUD         _hud         = null!;

        // ---- Audio -----------------------------------------------------------

        private AudioStreamPlayer _bgMusic      = null!;
        private AudioStreamPlayer _sfxTileClack = null!;
        private AudioStreamPlayer _sfxRiichi    = null!;

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            // Get child node references — these must exist in the scene tree
            _playerHand = GetNode<HandDisplay>("UI/PlayerHand");
            _topHand    = GetNode<HandDisplay>("UI/TopHand");
            _leftHand   = GetNode<HandDisplay>("UI/LeftHand");
            _rightHand  = GetNode<HandDisplay>("UI/RightHand");
            _hud        = GetNode<HUD>("UI/HUD");

            // Orientation / FaceDown / IsInteractive are set via the scene file
            // so HandDisplay._Ready() already built the correct inner container.
            // We only need to confirm the human hand is interactive here.
            _playerHand.IsInteractive = true;
            _playerHand.FaceDown      = false;

            // Connect human hand signals
            _playerHand.TileDiscarded += OnHumanTileDiscarded;
            _playerHand.TileSelected  += OnHumanTileSelected;

            // Connect HUD button signals
            _hud.RiichiPressed           += OnHumanRiichi;
            _hud.TsumoPressed            += OnHumanTsumo;
            _hud.RonPressed              += OnHumanRon;
            _hud.PonPressed              += OnHumanPon;
            _hud.ChiPressed              += OnHumanChi;
            _hud.KanPressed              += OnHumanKan;
            _hud.PassPressed             += OnHumanPass;
            _hud.NextHandPressed         += OnNextHand;
            _hud.MenuPressed             += ReturnToMenu;
            _hud.ScoringNextHandPressed  += OnNextHand;
            _hud.ScoringMenuPressed      += ReturnToMenu;

            // Create game logic
            _game = new GameState(humanSeat: 0);

            // Subscribe to game events
            _game.OnTileDrawn     += OnTileDrawn;
            _game.OnTileDiscarded += OnTileDiscarded;
            _game.OnMeldDeclared  += OnMeldDeclared;
            _game.OnRiichiDeclared += OnRiichiDeclared;
            _game.OnHandEnd       += OnHandEnd;
            _game.OnNewHand       += OnNewHand;
            _game.OnGameOver      += OnGameOver;

            // Create AI players (seats 1, 2, 3 are CPU)
            _ai = new[]
            {
                new AIPlayer(AIDifficulty.Medium),   // Seat 0 = human (unused slot)
                new AIPlayer(AIDifficulty.Medium),   // Seat 1
                new AIPlayer(AIDifficulty.Medium),   // Seat 2
                new AIPlayer(AIDifficulty.Medium),   // Seat 3
            };

            // ---- Background music -------------------------------------------
            _bgMusic = new AudioStreamPlayer();
            _bgMusic.Bus = "Master";
            var music = GD.Load<AudioStream>(
                "res://Assets/Sounds/Whispering_Bamboo_Garden_2026-05-18T203144.wav");
            if (music != null)
            {
                _bgMusic.Stream   = music;
                _bgMusic.VolumeDb = GameSettings.LinearToDb(GameSettings.MusicVolume);
                _bgMusic.Autoplay = false;
                // Loop: reconnect finished signal to restart
                _bgMusic.Finished += () => _bgMusic.Play();
                _bgMusic.Play();
            }
            AddChild(_bgMusic);

            // ---- SFX players ------------------------------------------------
            _sfxTileClack = MakeSfxPlayer(
                "res://Assets/Sounds/mahjong_tile_clack_#1-1779136185604.wav");
            _sfxRiichi = MakeSfxPlayer(
                "res://Assets/Sounds/richii_bet.wav");

            // Start the game!
            _game.StartGame();
        }

        private AudioStreamPlayer MakeSfxPlayer(string path)
        {
            var player = new AudioStreamPlayer();
            player.Bus = "Master";
            var stream = GD.Load<AudioStream>(path);
            if (stream != null)
                player.Stream = stream;
            player.VolumeDb = GameSettings.LinearToDb(GameSettings.SfxVolume);
            AddChild(player);
            return player;
        }

        private void PlaySfx(AudioStreamPlayer player)
        {
            if (player.Stream == null) return;
            player.VolumeDb = GameSettings.LinearToDb(GameSettings.SfxVolume);
            player.Stop();   // restart if already playing
            player.Play();
        }

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.Escape)
                    ReturnToMenu();
                else if (key.Keycode == Key.F12)
                    PrintInputDiagnostics();
            }
        }

        private void PrintInputDiagnostics()
        {
            GD.Print("=== INPUT DIAGNOSTICS (F12) ===");
            var winSize  = DisplayServer.WindowGetSize();
            var vpSize   = GetViewport().GetVisibleRect();
            GD.Print($"  Window size = {winSize}  Viewport rect = {vpSize}");
            GD.Print($"  HUD.MouseFilter       = {_hud.MouseFilter}");
            GD.Print($"  PlayerHand.MouseFilter = {_playerHand.MouseFilter}");
            GD.Print($"  PlayerHand.IsInteractive = {_playerHand.IsInteractive}");

            var inner = _playerHand.GetChildCount() > 0 ? _playerHand.GetChild(0) : null;
            if (inner != null)
            {
                GD.Print($"  _inner ({inner.GetType().Name}) child count = {inner.GetChildCount()}");
                for (int i = 0; i < Math.Min(3, inner.GetChildCount()); i++)
                {
                    if (inner.GetChild(i) is TileNode tn)
                        GD.Print($"    TileNode[{i}] MouseFilter={tn.MouseFilter} Disabled={tn.Disabled} Rect={tn.GetGlobalRect()}");
                }
            }
            GD.Print("===============================");
        }

        private void ReturnToMenu()
        {
            // Stop any running timers so nothing fires after the scene changes
            _aiTimerActive      = false;
            _claimWindowActive  = false;
            _autoDiscardPending = false;
            _bgMusic?.Stop();
            GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
        }

        public override void _Process(double delta)
        {
            // AI turn timer
            if (_aiTimerActive)
            {
                _aiTimer -= (float)delta;
                if (_aiTimer <= 0f)
                {
                    _aiTimerActive = false;
                    ExecuteAIPendingAction();
                }
            }

            // Claim window timer (auto-pass if human doesn't act)
            if (_claimWindowActive)
            {
                _claimWindowTimer -= (float)delta;
                if (_claimWindowTimer <= 0f)
                {
                    _claimWindowActive = false;
                    AutoResolveClaimWindow();
                }
            }

            // Post-riichi auto-discard: brief delay so the player can see the drawn tile
            if (_autoDiscardPending)
            {
                _autoDiscardTimer -= (float)delta;
                if (_autoDiscardTimer <= 0f)
                {
                    _autoDiscardPending = false;
                    ExecuteAutoDiscard();
                }
            }
        }

        // =====================================================================
        // Game event handlers (GameState → UI)
        // =====================================================================

        private void OnNewHand()
        {
            ExitRiichiMode();
            _autoDiscardPending  = false;
            _nextDiscardIsRiichi = false;
            _riichiDiscardSeat   = -1;

            // RevealAll() sets FaceDown=false on opponent displays — reset before rebuild.
            _playerHand.FaceDown = false;
            _topHand.FaceDown    = true;
            _leftHand.FaceDown   = true;
            _rightHand.FaceDown  = true;

            _hud.ClearAllDiscards();
            RebuildAllHands();
            _hud.UpdateAll(_game);
            _hud.SetStatus("");
            _hud.HideActionButtons();
            _btnNextVisible(false);

            // Dealer already has 14 tiles — trigger their action immediately
            TriggerCurrentPlayerAction();
        }

        private void OnNextHand()
        {
            _hud.HideScoringPanel();  // Safe to call even when already hidden
            _btnNextVisible(false);
            _game.BeginNextHand();
        }

        private void _btnNextVisible(bool v) => _hud.SetNextHandButtonVisible(v);

        private void OnTileDrawn(int playerIndex)
        {
            PlaySfx(_sfxTileClack);

            var hand    = _game.Players[playerIndex].Hand;
            hand.Sort();   // Keep hand ordered; drawn tile stays at end
            var display = GetHandDisplay(playerIndex);
            display.Rebuild(hand.ClosedTiles, melds: hand.OpenMelds, drawnTile: hand.DrawnTile);
            _hud.UpdateAll(_game);

            if (playerIndex == _game.HumanSeat)
            {
                if (hand.IsRiichi)
                {
                    // ---- Already in riichi: special draw handling ----
                    // In riichi, shanten == -1 means the drawn tile completes the hand.
                    // Riichi is always a yaku, so we don't need the full yaku pipeline here —
                    // just use Shanten() as a fast, reliable tsumo check.
                    bool canTsumo = hand.Shanten() == -1;
                    if (canTsumo)
                    {
                        // Pause and wait for player to click TSUMO (or manually discard the drawn tile)
                        _hud.ShowActionButtons(canTsumo: true, canRiichi: false, canKan: false);
                        _hud.SetStatus("TSUMO available! Click TSUMO to win, or discard to pass.");
                    }
                    else
                    {
                        // Auto-discard the drawn tile after a brief pause so it's visible
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

        /// <summary>Discard the drawn tile automatically (called after auto-discard delay fires).</summary>
        private void ExecuteAutoDiscard()
        {
            if (!_game.IsHumanTurn || _game.Phase != TurnPhase.ActionPhase) return;
            var hand = _game.Players[_game.HumanSeat].Hand;
            if (!hand.IsRiichi || hand.DrawnTile == null) return;

            // Safety re-check: don't auto-discard if tsumo is now available.
            // Use the same fast Shanten() == -1 check (riichi always provides yaku).
            if (hand.Shanten() == -1)
            {
                _hud.ShowActionButtons(canTsumo: true, canRiichi: false, canKan: false);
                _hud.SetStatus("TSUMO available! Click TSUMO to win, or discard to pass.");
                return;
            }

            _game.Discard(_game.HumanSeat, hand.DrawnTile);
        }

        private void OnTileDiscarded(int playerIndex, Tile tile)
        {
            PlaySfx(_sfxTileClack);

            var display = GetHandDisplay(playerIndex);
            display.RemoveTile(tile);

            // Consume the riichi flag — this exact discard is the one placed sideways.
            bool isRiichiDiscard = _nextDiscardIsRiichi && playerIndex == _riichiDiscardSeat;
            if (isRiichiDiscard)
            {
                _nextDiscardIsRiichi = false;
                _riichiDiscardSeat   = -1;
            }

            // Show the discard in the discard pool (HUD handles this)
            _hud.AddDiscard(playerIndex, tile, isRiichiDiscard);
            _hud.UpdateAll(_game);

            // Open the claim window
            OpenClaimWindow(playerIndex, tile);
        }

        private void OnMeldDeclared(int playerIndex, Meld meld)
        {
            // Only remove the discarded tile for open claims (Chi, Pon, KanOpen/daiminkan).
            // Ankan (KanClosed) and Kakan (KanExtended) involve no discard to remove.
            if (meld.Type is MeldType.Chi or MeldType.Pon or MeldType.KanOpen)
                _hud.RemoveLastDiscard(_game.DiscarderIndex);

            RebuildHand(playerIndex);
            _hud.UpdateAll(_game);
        }

        private void OnRiichiDeclared(int playerIndex)
        {
            PlaySfx(_sfxRiichi);

            // Mark the very next discard from this player as the riichi discard tile
            // so HUD.AddDiscard can render it sideways in the river.
            _nextDiscardIsRiichi = true;
            _riichiDiscardSeat   = playerIndex;

            _hud.ShowRiichiStick(playerIndex);
            _hud.UpdateAll(_game);
            _hud.SetStatus($"{_game.Players[playerIndex].Name} declares Riichi!");
        }

        private void OnHandEnd(HandEndReason reason, int[] winners)
        {
            ExitRiichiMode();
            _claimWindowActive  = false;
            _aiTimerActive      = false;
            _autoDiscardPending = false;
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(_game.DiscarderIndex);
            _playerHand.ClearClaimTileHighlights();

            // Reveal all hands so players can see what everyone held
            for (int i = 0; i < 4; i++)
                GetHandDisplay(i).RevealAll();

            // For Ron wins: the winning tile was added to the winner's hand by ClaimRon
            // (DrawnTile is set so it shows lifted) but the display still shows 13 tiles
            // and the tile is still in the discard pool.
            // → Remove it from the river and rebuild the winner's 14-tile hand so the
            //   completed hand is visible with the Ron tile clearly marked at the end.
            if (reason == HandEndReason.Ron && winners.Length > 0)
            {
                int discarderSeat = _game.LastDiscarderSeat;
                if (discarderSeat >= 0)
                    _hud.RemoveLastDiscard(discarderSeat);

                RebuildHand(winners[0]);   // FaceDown is already false from RevealAll above

                // Highlight the Ron tile (the lifted DrawnTile node) in the rebuilt hand
                var winDisplay = GetHandDisplay(winners[0]);
                winDisplay.DrawnTileNode?.SetClaimHighlight(true);
            }

            _hud.HideActionButtons();
            _hud.UpdateAll(_game);

            string msg = reason switch
            {
                HandEndReason.Tsumo          => $"🀄 {_game.Players[winners[0]].Name} wins by Tsumo!",
                HandEndReason.Ron            => $"🀄 {_game.Players[winners[0]].Name} wins by Ron!",
                HandEndReason.ExhaustiveDraw => "Exhaustive draw (Ryuukyoku)",
                _                            => "Hand over"
            };
            _hud.SetStatus(msg);

            if (reason is HandEndReason.Tsumo or HandEndReason.Ron)
            {
                // Show scoring breakdown — "Next Hand" is gated behind the Continue button
                ShowScoringOverlay(reason, winners[0]);
            }
            else
            {
                // Draw: no scoring to show — go straight to Next Hand button
                _btnNextVisible(true);
            }
        }

        private void ShowScoringOverlay(HandEndReason reason, int winnerSeat)
        {
            var score = _game.LastScoreResult;
            var yaku  = _game.LastYakuResult;
            var ctx   = _game.LastWinContext;
            if (score == null || yaku == null || ctx == null)
            {
                // Safety fallback: no data, skip overlay
                _btnNextVisible(true);
                return;
            }

            bool   isTsumo      = reason == HandEndReason.Tsumo;
            string winnerName   = _game.Players[winnerSeat].Name;
            string discarderName = _game.LastDiscarderSeat >= 0
                ? _game.Players[_game.LastDiscarderSeat].Name
                : "";
            string[] allNames   = _game.Players.Select(p => p.Name).ToArray();
            int[]    allPoints  = _game.Players.Select(p => p.Points).ToArray();

            _hud.ShowScoringPanel(
                score, yaku, ctx,
                winnerName, isTsumo, discarderName,
                allNames, allPoints, winnerSeat, _game.DealerIndex);
        }

        private void OnGameOver()
        {
            _claimWindowActive  = false;
            _aiTimerActive      = false;
            _autoDiscardPending = false;

            _hud.SetStatus("");
            _hud.ShowGameOverPanel(
                reason:       _game.GameOverReason,
                playerNames:  _game.Players.Select(p => p.Name).ToArray(),
                playerPoints: _game.Players.Select(p => p.Points).ToArray(),
                dealerSeat:   _game.DealerIndex);
        }

        // =====================================================================
        // Human input handlers (UI → GameState)
        // =====================================================================

        private void OnHumanTileSelected(TileNode tile)
        {
            if (_riichiMode)
            {
                // In riichi mode: show waits popup for the selected candidate tile
                if (tile.TileData != null && _riichiCandidates.Any(c => c == tile.TileData))
                    ShowWaitsForRiichi(tile.TileData);
                else
                    _hud.HideWaitsPopup();
                return;
            }

            // Normal mode: refresh action buttons based on selection
            ShowHumanActionButtons();
        }

        private void OnHumanTileDiscarded(TileNode tile)
        {
            if (_game.Phase != TurnPhase.ActionPhase) return;
            if (!_game.IsHumanTurn) return;
            if (tile.TileData == null) return;

            var hand = _game.Players[_game.HumanSeat].Hand;

            // ---- Riichi selection mode: discard a candidate to declare riichi ----
            if (_riichiMode)
            {
                if (!_riichiCandidates.Any(c => c == tile.TileData))
                {
                    _hud.SetStatus("Discard a highlighted (green) tile to declare Riichi.");
                    return;
                }
                var discardTile = tile.TileData;
                ExitRiichiMode();
                bool riichiOk = _game.DeclareRiichi(_game.HumanSeat, discardTile);
                if (!riichiOk) _hud.SetStatus("Cannot declare Riichi right now.");
                return;
            }

            // ---- Already in riichi: can only discard the drawn tile ----
            if (hand.IsRiichi)
            {
                var drawnNode = _playerHand.DrawnTileNode;
                if (drawnNode != null && drawnNode != tile)
                {
                    _hud.SetStatus("You are in Riichi — you can only discard your drawn tile.");
                    return;
                }
                // Player manually clicked the drawn tile — cancel any pending auto-discard
                _autoDiscardPending = false;
            }

            bool ok = _game.Discard(_game.HumanSeat, tile.TileData);
            if (!ok) _hud.SetStatus("Cannot discard that tile right now.");
        }

        private void OnHumanRiichi()
        {
            // Toggle riichi mode off if already active
            if (_riichiMode)
            {
                ExitRiichiMode();
                ShowHumanActionButtons();
                return;
            }

            var candidates = GetRiichiCandidates();
            if (candidates.Count == 0)
            {
                _hud.SetStatus("No valid riichi discard — hand may not be tenpai.");
                return;
            }

            _riichiMode       = true;
            _riichiCandidates = candidates;
            _playerHand.SetRiichiCandidates(_riichiCandidates);

            // Immediately show waits for the drawn tile (most natural riichi discard),
            // falling back to the first candidate if the drawn tile is not a valid discard.
            var drawnTile = _game.Players[_game.HumanSeat].Hand.DrawnTile;
            var initialCandidate = candidates.FirstOrDefault(
                c => drawnTile != null && c.TileId == drawnTile.TileId)
                ?? candidates[0];
            ShowWaitsForRiichi(initialCandidate);

            _hud.SetStatus("Riichi — green tiles are valid discards. Select one to see your waits, then discard it.");
        }

        // ---- Riichi mode helpers ------------------------------------------------

        /// <summary>
        /// Returns all tiles from the human's closed hand that, when discarded,
        /// leave the remaining 13 tiles in tenpai. Uses a safe 13-tile IsTenpai() check.
        /// </summary>
        private List<Tile> GetRiichiCandidates()
        {
            var hand = _game.Players[_game.HumanSeat].Hand;
            var candidates = new List<Tile>();
            var seenIds    = new HashSet<int>();

            var closedList = hand.ClosedTiles.ToList();
            for (int i = 0; i < closedList.Count; i++)
            {
                var candidate = closedList[i];
                // Skip duplicate tile types (same TileId already tested)
                if (!seenIds.Add(candidate.TileId)) continue;

                // Build a 13-tile test hand with this one tile removed
                var testTiles = closedList.ToList();
                testTiles.RemoveAt(i);

                var testHand = new Hand();
                testHand.AddTiles(testTiles);

                if (testHand.IsTenpai())
                    candidates.Add(candidate);
            }
            return candidates;
        }

        /// <summary>
        /// Compute waiting tiles after discarding <paramref name="discardTile"/> and show the popup.
        /// Always called on a 13-tile test hand, so GetWaitingTiles() is safe.
        /// Also computes how many of each wait tile are still unseen (wall + opponents' hands).
        /// </summary>
        private void ShowWaitsForRiichi(Tile discardTile)
        {
            var hand      = _game.Players[_game.HumanSeat].Hand;
            var testTiles = hand.ClosedTiles.ToList();

            // Remove the first occurrence of the discard candidate
            for (int i = 0; i < testTiles.Count; i++)
            {
                if (testTiles[i] == discardTile)
                {
                    testTiles.RemoveAt(i);
                    break;
                }
            }

            // Safe: 13 tiles, so GetWaitingTiles() iterates 34 candidates on 14-tile tests
            var testHand = new Hand();
            testHand.AddTiles(testTiles);
            var waits = testHand.GetWaitingTiles();

            // Count how many of each wait tile are still unseen by all players,
            // then subtract the player's own closed tiles (they can't draw what they hold).
            var remaining = ComputeRemainingCounts(waits, testTiles);
            _hud.ShowWaitsPopup(waits, remaining, discardTile);
        }

        /// <summary>
        /// For each tile in <paramref name="waitTiles"/>, count copies still drawable:
        ///   drawable = 4 − (discards) − (open melds) − (player's own closed hand).
        /// <paramref name="ownClosedTiles"/> = the player's hand tiles AFTER the riichi discard
        /// (so those copies are known and cannot come from the wall).
        /// </summary>
        private Dictionary<int, int> ComputeRemainingCounts(
            List<Tile> waitTiles, IEnumerable<Tile>? ownClosedTiles = null)
        {
            // Initialise: all tile types start with 4 copies
            var remaining = new Dictionary<int, int>();
            foreach (var t in waitTiles)
                remaining.TryAdd(t.TileId, 4);

            // Subtract tiles that are publicly visible (discards + open melds for every player)
            for (int seat = 0; seat < 4; seat++)
            {
                var player = _game.Players[seat];

                foreach (var d in player.Discards)
                    if (remaining.ContainsKey(d.TileId))
                        remaining[d.TileId]--;

                foreach (var meld in player.Hand.OpenMelds)
                    foreach (var mt in meld.Tiles)
                        if (remaining.ContainsKey(mt.TileId))
                            remaining[mt.TileId]--;
            }

            // Subtract tiles the player already holds — they can't draw what they have.
            // For shanpon waits (e.g. waiting for 中 while holding 中中), this correctly
            // reduces the remaining count by the copies already in hand.
            if (ownClosedTiles != null)
                foreach (var t in ownClosedTiles)
                    if (remaining.ContainsKey(t.TileId))
                        remaining[t.TileId]--;

            // Clamp to zero
            foreach (var key in remaining.Keys.ToList())
                remaining[key] = Math.Max(0, remaining[key]);

            return remaining;
        }

        /// <summary>Clear riichi selection mode and hide all related UI.</summary>
        private void ExitRiichiMode()
        {
            if (!_riichiMode) return;
            _riichiMode = false;
            _riichiCandidates.Clear();
            _playerHand.ClearRiichiCandidates();
            _hud.HideWaitsPopup();
        }

        // ---- End riichi mode helpers -------------------------------------------

        private void OnHumanTsumo()
        {
            bool ok = _game.DeclareTsumo();
            if (!ok) _hud.SetStatus("Cannot declare tsumo — hand is not complete.");
        }

        private void OnHumanRon()
        {
            bool ok = _game.ClaimRon(_game.HumanSeat);
            if (!ok) _hud.SetStatus("Cannot declare ron — furiten or invalid hand.");
        }

        private void OnHumanPon()
        {
            if (_game.PendingDiscard == null) return;
            if (!_game.ClaimPon(_game.HumanSeat)) { _hud.SetStatus("Cannot pon."); return; }
            _claimWindowActive = false;
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            // After pon, human must discard — ActionPhase is already set by ClaimPon
            ShowHumanActionButtons();
        }

        private void OnHumanChi()
        {
            if (_game.PendingDiscard == null) return;
            var combo = _ai[_game.HumanSeat].BestChiCombination(
                _game.PendingDiscard, _game.Players[_game.HumanSeat].Hand);
            if (combo == null) { _hud.SetStatus("No valid chi."); return; }
            if (!_game.ClaimChi(_game.HumanSeat, combo.Value.t1, combo.Value.t2))
                { _hud.SetStatus("Cannot chi."); return; }
            _claimWindowActive = false;
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            ShowHumanActionButtons();
        }

        private void OnHumanKan()
        {
            if (_game.Phase == TurnPhase.ClaimWindow)
            {
                // Daiminkan — claim discard to form open Kan
                bool ok = _game.ClaimDaiminkan(_game.HumanSeat);
                if (!ok) { _hud.SetStatus("Cannot declare kan."); return; }
                _claimWindowActive = false;
                _playerHand.ClearClaimTileHighlights();
                _hud.HideClaimButtons();
                // OnTileDrawn will fire from inside ClaimDaiminkan → shows action buttons
            }
            else if (_game.Phase == TurnPhase.ActionPhase && _game.IsHumanTurn)
            {
                var hand = _game.Players[_game.HumanSeat].Hand;

                // Try kakan first (extend an existing open Pon)
                foreach (var meld in hand.OpenMelds)
                {
                    if (meld.Type == MeldType.Pon && hand.ClosedTiles.Any(t => t == meld.Lead))
                    {
                        if (_game.DeclareKakan(_game.HumanSeat, meld.Lead)) return;
                    }
                }

                // Try ankan (all 4 copies in closed hand)
                foreach (var t in Hand.AllTileTypes())
                {
                    if (hand.ClosedTiles.Count(c => c == t) >= 4)
                    {
                        if (_game.DeclareAnkan(_game.HumanSeat, t)) return;
                    }
                }

                _hud.SetStatus("No valid kan available.");
            }
        }

        private void OnHumanPass()
        {
            _claimWindowActive = false;
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(_game.DiscarderIndex);
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            ResolveAIClaims();
        }

        // =====================================================================
        // Claim window logic
        // =====================================================================

        private void OpenClaimWindow(int discarderIndex, Tile tile)
        {
            if (_game.Phase != TurnPhase.ClaimWindow) return;

            var humanHand = _game.Players[_game.HumanSeat].Hand;

            // Check if human can ron — fast path: IsTenpai + IsWaitingFor (2 Shanten calls)
            // rather than GetWaitingTiles().Contains() (35 Shanten calls).
            bool humanRon  = discarderIndex != _game.HumanSeat
                             && !_game.Players[_game.HumanSeat].Furiten.IsFuriten
                             && humanHand.IsTenpai()
                             && humanHand.IsWaitingFor(tile);

            // Check if human can pon — not allowed when in riichi
            bool humanPon  = !humanHand.IsRiichi
                             && discarderIndex != _game.HumanSeat
                             && humanHand.ClosedTiles.Count(t => t == tile) >= 2;

            // Check if human can chi (left player's discard only)
            int  leftOfHuman = (_game.HumanSeat - 1 + 4) % 4;
            bool humanChi    = discarderIndex == leftOfHuman
                               && !humanHand.IsRiichi
                               && _ai[_game.HumanSeat].BestChiCombination(tile, humanHand) != null;

            // Check if human can daiminkan (3 copies in hand, any discarder)
            bool humanKan  = discarderIndex != _game.HumanSeat
                             && !humanHand.IsRiichi
                             && humanHand.ClosedTiles.Count(t => t == tile) >= 3
                             && _game.Wall.KanCount < 4;

            bool humanCanAct = humanRon || humanPon || humanChi || humanKan;

            if (humanCanAct)
            {
                _hud.ShowClaimButtons(canRon: humanRon, canPon: humanPon, canChi: humanChi, canKan: humanKan);
                _hud.HighlightLastDiscard(discarderIndex);

                // Highlight the tiles in the human's hand that would be used to complete the meld
                if (humanPon)
                {
                    // Two copies of the discarded tile
                    _playerHand.HighlightClaimTiles(new[] { tile, tile });
                }
                else if (humanChi)
                {
                    var combo = _ai[_game.HumanSeat].BestChiCombination(tile,
                        _game.Players[_game.HumanSeat].Hand);
                    if (combo != null)
                        _playerHand.HighlightClaimTiles(new[] { combo.Value.t1, combo.Value.t2 });
                }
                else if (humanKan)
                {
                    // Three copies of the discarded tile (fourth tile is the discard itself)
                    _playerHand.HighlightClaimTiles(new[] { tile, tile, tile });
                }
                // Ron: no hand tiles to highlight — the winning tile comes from the discard pool

                if (humanRon)
                {
                    // Ron on the table — pause indefinitely, player must actively decide.
                    // The timer is intentionally NOT started; only an explicit Pass or Ron
                    // click will resolve the window.
                    _claimWindowActive = false;
                }
                else
                {
                    // Pon / Chi / Kan only — keep the auto-resolve timer so the game
                    // doesn't stall if the player ignores the buttons.
                    _claimWindowActive = true;
                    _claimWindowTimer  = ClaimWindowDuration;
                }
            }
            else
            {
                // Human cannot act — let AI decide immediately (with small delay)
                ScheduleAIAction(discarderIndex, "claim");
            }
        }

        private void AutoResolveClaimWindow()
        {
            // Claim window timed out without human action — treat as pass
            if (_game.DiscarderIndex >= 0) _hud.ClearLastDiscardHighlight(_game.DiscarderIndex);
            _playerHand.ClearClaimTileHighlights();
            _hud.HideClaimButtons();
            ResolveAIClaims();
        }

        private void ResolveAIClaims()
        {
            if (_game.Phase != TurnPhase.ClaimWindow) return;
            if (_game.PendingDiscard == null) return;

            var tile = _game.PendingDiscard;

            // Priority: Ron > Daiminkan > Pon > Chi
            // Check each non-discarding player in priority order
            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _game.HumanSeat) continue;  // Human already passed

                var aiHand = _game.Players[seat].Hand;

                // AI Ron?
                if (_ai[seat].ShouldClaimRon(tile, aiHand, _game, seat))
                {
                    if (_game.ClaimRon(seat)) return;
                }
            }

            // AI Daiminkan? (check all non-discarders with 3 copies)
            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _game.HumanSeat) continue;
                var aiHand = _game.Players[seat].Hand;
                if (_ai[seat].ShouldClaimDaiminkan(tile, aiHand, _game, seat))
                {
                    if (_game.ClaimDaiminkan(seat)) return;
                }
            }

            // AI Pon? (check all non-discarders)
            for (int i = 1; i <= 3; i++)
            {
                int seat = (_game.DiscarderIndex + i) % 4;
                if (seat == _game.HumanSeat) continue;

                var aiHand = _game.Players[seat].Hand;
                if (_ai[seat].ShouldClaimPon(tile, aiHand, _game, seat))
                {
                    if (_game.ClaimPon(seat))
                    {
                        // ClaimPon sets Phase=ActionPhase but fires no OnTileDrawn, so nothing
                        // would schedule the AI's mandatory discard — trigger it explicitly here.
                        TriggerCurrentPlayerAction();
                        return;
                    }
                }
            }

            // AI Chi? (left player only)
            int leftSeat = (_game.DiscarderIndex + 1) % 4;
            if (leftSeat != _game.HumanSeat)
            {
                var aiHand = _game.Players[leftSeat].Hand;
                var combo  = _ai[leftSeat].BestChiCombination(tile, aiHand);
                if (combo != null && _ai[leftSeat].ShouldClaimChi(
                        tile, combo.Value.t1, combo.Value.t2, aiHand, _game, leftSeat))
                {
                    if (_game.ClaimChi(leftSeat, combo.Value.t1, combo.Value.t2))
                    {
                        // Same fix as Pon — ClaimChi does not fire OnTileDrawn either.
                        TriggerCurrentPlayerAction();
                        return;
                    }
                }
            }

            // Nobody claimed — advance to next player's draw
            _game.PassAllClaims();
            TriggerCurrentPlayerAction();
        }

        // =====================================================================
        // AI action scheduling
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

            if (action == "discard")
            {
                ExecuteAIDiscard(seat);
            }
            else if (action == "claim")
            {
                ResolveAIClaims();
            }
        }

        private void ExecuteAIDiscard(int seat)
        {
            if (_game.Phase != TurnPhase.ActionPhase) return;
            if (_game.CurrentPlayerIndex != seat) return;

            var player = _game.Players[seat];
            var hand   = player.Hand;

            // Try tsumo first
            if (_game.DeclareTsumo()) return;

            // Try ankan (all 4 in hand)
            var ankanTile = _ai[seat].GetAnkanTile(hand, _game, seat);
            if (ankanTile != null && _game.DeclareAnkan(seat, ankanTile)) return;

            // Try kakan (extend existing pon)
            var kakanTile = _ai[seat].GetKakanTile(hand, _game, seat);
            if (kakanTile != null && _game.DeclareKakan(seat, kakanTile)) return;

            // Try riichi — use per-tile IsTenpai() on 13-tile test hands (avoids freeze from
            // calling GetWaitingTiles() on a 14-tile hand, which can mutate and scan incorrectly)
            if (_ai[seat].ShouldDeclareRiichi(hand, _game, seat))
            {
                var closedList = hand.ClosedTiles.ToList();
                var seenIds    = new HashSet<int>();
                for (int i = 0; i < closedList.Count; i++)
                {
                    if (!seenIds.Add(closedList[i].TileId)) continue;   // Skip duplicate types

                    var testTiles = closedList.ToList();
                    testTiles.RemoveAt(i);
                    var testHand = new Hand();
                    testHand.AddTiles(testTiles);

                    if (testHand.IsTenpai())
                    {
                        if (_game.DeclareRiichi(seat, closedList[i])) return;
                    }
                }
            }

            // Normal discard
            var discard = _ai[seat].ChooseDiscard(hand, _game, seat);
            _game.Discard(seat, discard);
        }

        // Called whenever we need to kick off whoever's turn it is.
        private void TriggerCurrentPlayerAction()
        {
            if (_game.Phase == TurnPhase.DrawPhase)
            {
                var tile = _game.DrawForCurrentPlayer();
                if (tile == null) return;           // Wall empty — exhaustive draw resolved
                // OnTileDrawn fires and handles the rest
            }
            else if (_game.Phase == TurnPhase.ActionPhase)
            {
                if (_game.CurrentPlayerIndex == _game.HumanSeat)
                    ShowHumanActionButtons();
                else
                    ScheduleAIAction(_game.CurrentPlayerIndex, "discard");
            }
        }

        // =====================================================================
        // UI helpers
        // =====================================================================

        private void ShowHumanActionButtons()
        {
            if (!_game.IsHumanTurn) return;

            var hand = _game.Players[_game.HumanSeat].Hand;

            // WinChecker_CanWinTsumo works correctly on a 14-tile hand.
            bool tsumo = _game.WinChecker_CanWinTsumo(_game.HumanSeat);

            // Only show riichi if there is at least one valid discard that leaves a 13-tile
            // tenpai hand. We pre-compute candidates here rather than using IsTenpai() on the
            // 14-tile hand, which can return true even when no single discard gives tenpai
            // (e.g. hand has two lone honour tiles above the useful structure).
            bool riichi = false;
            if (!hand.IsRiichi
                && hand.IsFullyClosed
                && _game.Wall.TilesRemaining >= 4
                && _game.Players[_game.HumanSeat].Points >= GameState.RiichiBetAmount)
            {
                riichi = GetRiichiCandidates().Count > 0;
            }

            bool kan = CanHumanKan();

            _hud.ShowActionButtons(canTsumo: tsumo, canRiichi: riichi, canKan: kan);
        }

        /// <summary>True if the human can declare ankan or kakan right now.</summary>
        private bool CanHumanKan()
        {
            if (_game.Wall.KanCount >= 4) return false;
            var hand = _game.Players[_game.HumanSeat].Hand;
            if (hand.IsRiichi) return false;

            // Ankan: 4 copies of any tile in closed hand
            var tileCounts = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var t in hand.ClosedTiles)
                tileCounts[t.TileId] = tileCounts.GetValueOrDefault(t.TileId, 0) + 1;
            if (tileCounts.Values.Any(v => v >= 4)) return true;

            // Kakan: existing open Pon + 4th tile in closed hand
            if (hand.OpenMelds.Any(m => m.Type == MeldType.Pon
                                        && hand.ClosedTiles.Any(t => t == m.Lead)))
                return true;

            return false;
        }

        private void RebuildAllHands()
        {
            for (int i = 0; i < 4; i++)
                RebuildHand(i);
        }

        private void RebuildHand(int seat)
        {
            var hand    = _game.Players[seat].Hand;
            var display = GetHandDisplay(seat);
            display.Rebuild(hand.ClosedTiles, melds: hand.OpenMelds, drawnTile: hand.DrawnTile);
        }

        private HandDisplay GetHandDisplay(int seat)
        {
            int relativeSeat = (seat - _game.HumanSeat + 4) % 4;
            return relativeSeat switch
            {
                0 => _playerHand,  // Human = bottom
                1 => _rightHand,   // Next player clockwise = right
                2 => _topHand,     // Across = top
                3 => _leftHand,    // Counter-clockwise = left
                _ => _playerHand,
            };
        }
    }
}
