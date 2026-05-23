// =============================================================================
// HUD.cs
// The heads-up display — everything on screen that isn't tiles in a hand.
//
// Contains:
//   - Score panels for all 4 players (name, points, seat wind)
//   - Round wind + counter indicator (centre)
//   - Dora indicator display
//   - Discard pools for all 4 players
//   - Action buttons: Discard (via tile click), Riichi, Tsumo, Ron, Pon, Chi, Pass
//   - Status message bar
//   - "Next Hand" / "Final Scores" overlay
// =============================================================================

using Godot;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public partial class HUD : Control
    {
        // ---- Signals (relayed to GameController) ----------------------------

        [Signal] public delegate void RiichiPressedEventHandler();
        [Signal] public delegate void TsumoPressedEventHandler();
        [Signal] public delegate void RonPressedEventHandler();
        [Signal] public delegate void PonPressedEventHandler();
        [Signal] public delegate void ChiPressedEventHandler();
        [Signal] public delegate void KanPressedEventHandler();
        [Signal] public delegate void PassPressedEventHandler();
        [Signal] public delegate void NextHandPressedEventHandler();
        [Signal] public delegate void MenuPressedEventHandler();
        [Signal] public delegate void ScoringContinuePressedEventHandler();   // kept for compat
        [Signal] public delegate void ScoringNextHandPressedEventHandler();
        [Signal] public delegate void ScoringMenuPressedEventHandler();

        // ---- Child node references ------------------------------------------

        // Score panels
        private Label[] _nameLabels   = null!;
        private Label[] _scoreLabels  = null!;
        private Label[] _windLabels   = null!;

        // Centre info
        private Label    _roundWindLabel  = null!;
        private Label    _counterLabel    = null!;
        private TileNode[] _doraTileNodes = new TileNode[5];  // Up to 5 dora indicators

        // Discard pools (one HFlowContainer per player)
        private Control[] _discardPools = null!;

        // Action buttons
        private Button _btnRiichi = null!;
        private Button _btnTsumo  = null!;
        private Button _btnRon    = null!;
        private Button _btnPon    = null!;
        private Button _btnChi    = null!;
        private Button _btnKan    = null!;
        private Button _btnPass   = null!;
        private Button _btnNext   = null!;

        // Status bar
        private Label _statusLabel = null!;

        // Riichi stick indicators (one per player)
        private Control[] _riichiSticks = null!;

        // Waits popup — shown when player selects a riichi candidate tile
        private Panel        _waitsPopup = null!;
        private Label        _waitsTitle = null!;
        private HBoxContainer _waitsRow  = null!;

        // Countdown bar (network mode — shown above action buttons when it's the player's turn)
        private Control  _countdownContainer = null!;
        private ColorRect _countdownFill     = null!;
        private Label    _countdownLabel     = null!;

        // Furiten warning — human player panel only
        private Label _furitenLabel = null!;

        // Scoring overlay — shown after a Tsumo or Ron win, and reused for Game Over
        private ColorRect     _scoringBackdrop   = null!;
        private Panel         _scoringPanel      = null!;
        private Label         _scoringTitle      = null!;
        private VBoxContainer _scoringYakuRows   = null!;
        private Label         _scoringHanFuLabel = null!;
        private Label         _scoringLimitLabel = null!;
        private VBoxContainer _scoringPayRows    = null!;
        private Label         _scoringTotalWon   = null!;
        private VBoxContainer _scoringAllScores  = null!;  // Per-player point totals
        private Button        _scoringNextBtn    = null!;  // Hidden on game-over screen
        private Button        _scoringMenuBtn    = null!;

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            BuildLayout();
        }

        // =====================================================================
        // Public API — called by GameController
        // =====================================================================

        /// <summary>
        /// Refresh score/wind/round info from raw arrays (network mode — no GameState available).
        /// </summary>
        public void UpdateAll(string[] names, int[] points, int dealerSeat,
                              string roundWind, int counters)
        {
            string[] windLetters = { "E", "S", "W", "N" };
            for (int i = 0; i < 4; i++)
            {
                _nameLabels[i].Text  = names.Length > i ? names[i] : $"Player {i}";
                _scoreLabels[i].Text = points.Length > i ? points[i].ToString("N0") : "0";
                int windOff = (i - dealerSeat + 4) % 4;
                _windLabels[i].Text  = windLetters[windOff];
            }
            _roundWindLabel.Text = $"{roundWind} Round";
            _counterLabel.Text   = counters > 0 ? $"×{counters}" : "";
        }

        /// <summary>
        /// Display dora indicator tiles (network mode). Called on hand start and after kans.
        /// </summary>
        public void UpdateDoraIndicators(IReadOnlyList<Tile> indicators)
        {
            for (int i = 0; i < _doraTileNodes.Length; i++)
            {
                if (i < indicators.Count)
                {
                    _doraTileNodes[i].SetTile(indicators[i], faceDown: false);
                    _doraTileNodes[i].Visible = true;
                }
                else
                {
                    _doraTileNodes[i].Visible = false;
                }
            }
        }

        /// <summary>Refresh all score/wind/round info from current game state.</summary>
        public void UpdateAll(GameState game)
        {
            for (int i = 0; i < 4; i++)
            {
                var p = game.Players[i];
                _nameLabels[i].Text  = p.Name;
                _scoreLabels[i].Text = p.Points.ToString("N0");
                var wind = p.GetSeatWind(game.DealerIndex);
                _windLabels[i].Text  = wind.ToString()[..1];  // "E", "S", "W", "N"
            }

            _roundWindLabel.Text = $"{game.RoundWind} Round";
            _counterLabel.Text   = game.Counters > 0 ? $"×{game.Counters}" : "";

            // Dora indicator tiles — show each indicator as an actual tile image
            if (game.Wall != null)
            {
                var indicators = game.Wall.DoraIndicators;
                for (int i = 0; i < _doraTileNodes.Length; i++)
                {
                    if (i < indicators.Count)
                    {
                        // Show the indicator tile itself (the actual dora is the NEXT tile,
                        // but the indicator is what gets displayed face-up on the table)
                        _doraTileNodes[i].SetTile(indicators[i], faceDown: false);
                        _doraTileNodes[i].Visible = true;
                    }
                    else
                    {
                        _doraTileNodes[i].Visible = false;
                    }
                }
            }
        }

        /// <summary>Add a tile to a player's discard pool display.</summary>
        /// <param name="isRiichiDiscard">
        /// True for the one tile placed sideways when riichi is declared.
        /// The tile is rotated 90° and shown in a landscape slot in the river.
        /// </param>
        public void AddDiscard(int playerIndex, Tile tile, bool isRiichiDiscard = false)
        {
            var pool = _discardPools[playerIndex];
            var node = new TileNode();
            node.SetTile(tile, faceDown: false);
            node.SetInteractive(false);

            if (isRiichiDiscard)
            {
                // The riichi-declare tile is placed on its side in the river (landscape orientation).
                //
                // A portrait tile is 28 wide × 38 tall. Rotated 90° CW it occupies
                // 38 wide × 28 tall of layout space.  We use a wrapper Control sized
                // for the landscape footprint so the HFlowContainer allocates the right
                // space, then rotate the TileNode inside it around its own centre.
                //
                // Rotation math (90° CW around tile centre at (14, 19) relative to tile origin,
                // tile offset (5, −5) inside the 38×28 wrapper):
                //   After rotation each corner lands exactly on the wrapper's corners — no clipping.

                var wrapper = new Control();
                wrapper.CustomMinimumSize = new Vector2(38, 28);   // landscape slot for the flow

                node.CustomMinimumSize = new Vector2(28, 38);      // tile keeps its natural portrait size
                node.Position          = new Vector2(5f, -5f);     // offset so rotation fills the wrapper
                node.PivotOffset       = new Vector2(14f, 19f);    // rotate around the tile's own centre
                node.RotationDegrees   = 90f;

                wrapper.AddChild(node);
                pool.AddChild(wrapper);
            }
            else
            {
                // 28×38: fits 3 rows of 6 in the 127px-tall river area with 2px gaps
                node.CustomMinimumSize = new Vector2(28, 38);
                pool.AddChild(node);
            }
        }

        /// <summary>Add a meld label to the player's area (simplified display for now).</summary>
        public void AddMeld(int playerIndex, Meld meld)
        {
            // In a future iteration this will show face-up meld tiles in the player's area
            // For now, update the status message
            SetStatus($"{meld} declared");
        }

        /// <summary>Show the riichi stick indicator for a player.</summary>
        public void ShowRiichiStick(int playerIndex)
        {
            _riichiSticks[playerIndex].Visible = true;
        }

        /// <summary>Remove the most recent tile from a player's discard pool (tile was claimed).</summary>
        public void RemoveLastDiscard(int playerIndex)
        {
            var pool = _discardPools[playerIndex];
            var children = pool.GetChildren();
            if (children.Count > 0)
                children[children.Count - 1].QueueFree();
        }

        /// <summary>
        /// Pulse-highlight the most recent discard in a player's river to show it is claimable.
        /// </summary>
        public void HighlightLastDiscard(int playerIndex)
        {
            GetLastDiscardTileNode(playerIndex)?.SetClaimHighlight(true);
        }

        /// <summary>Remove the claim highlight from the most recent discard tile.</summary>
        public void ClearLastDiscardHighlight(int playerIndex)
        {
            GetLastDiscardTileNode(playerIndex)?.SetClaimHighlight(false);
        }

        /// <summary>
        /// Finds the TileNode for the most recent discard in the given player's pool.
        /// Handles both direct TileNode children and riichi-wrapper children.
        /// </summary>
        private TileNode? GetLastDiscardTileNode(int playerIndex)
        {
            var pool = _discardPools[playerIndex];
            var children = pool.GetChildren();
            if (children.Count == 0) return null;

            var last = children[children.Count - 1];
            if (last is TileNode tn) return tn;

            // Riichi-wrapper case: the TileNode is a child of a plain Control wrapper
            foreach (var child in last.GetChildren())
                if (child is TileNode inner) return inner;

            return null;
        }

        /// <summary>Clear all discard pools and riichi sticks for a new hand.</summary>
        public void ClearAllDiscards()
        {
            foreach (var pool in _discardPools)
                foreach (var child in pool.GetChildren())
                    child.QueueFree();

            foreach (var stick in _riichiSticks)
                stick.Visible = false;
        }

        // ---- Button visibility ----------------------------------------------

        public void ShowActionButtons(bool canTsumo, bool canRiichi, bool canKan = false)
        {
            _btnRiichi.Visible = canRiichi;
            _btnTsumo.Visible  = canTsumo;
            _btnKan.Visible    = canKan;
            _btnRon.Visible    = false;
            _btnPon.Visible    = false;
            _btnChi.Visible    = false;
            _btnPass.Visible   = false;
        }

        public void ShowClaimButtons(bool canRon, bool canPon, bool canChi, bool canKan = false)
        {
            _btnRon.Visible    = canRon;
            _btnPon.Visible    = canPon;
            _btnChi.Visible    = canChi;
            _btnKan.Visible    = canKan;
            _btnPass.Visible   = true;
            _btnRiichi.Visible = false;
            _btnTsumo.Visible  = false;
        }

        public void HideActionButtons()
        {
            _btnRiichi.Visible = false;
            _btnTsumo.Visible  = false;
            _btnRon.Visible    = false;
            _btnPon.Visible    = false;
            _btnChi.Visible    = false;
            _btnKan.Visible    = false;
            _btnPass.Visible   = false;
        }

        public void HideClaimButtons()
        {
            _btnRon.Visible  = false;
            _btnPon.Visible  = false;
            _btnChi.Visible  = false;
            _btnKan.Visible  = false;
            _btnPass.Visible = false;
        }

        public void ShowNextHandButton() => _btnNext.Visible = true;
        public void SetNextHandButtonVisible(bool v) => _btnNext.Visible = v;

        public void SetStatus(string message)
        {
            _statusLabel.Text = message;
        }

        /// <summary>
        /// Show or hide the FURITEN warning badge on the human player's score panel.
        /// <paramref name="isPermanent"/> = true → red (own-discard / riichi miss);
        /// false → orange (temporary — clears on next draw).
        /// </summary>
        public void SetFuriten(bool isFuriten, bool isPermanent)
        {
            _furitenLabel.Visible = isFuriten;
            if (!isFuriten) return;

            _furitenLabel.Text = isPermanent ? "⚠ FURITEN" : "⚠ FURITEN (temp)";
            _furitenLabel.AddThemeColorOverride("font_color",
                isPermanent
                    ? new Color(1.00f, 0.25f, 0.25f)   // bright red  — permanent
                    : new Color(1.00f, 0.58f, 0.08f));  // orange      — temporary
        }

        public void ShowFinalScores(GameState game)
        {
            string msg = "Final Scores:\n";
            foreach (var p in game.Players)
                msg += $"  {p.Name}: {p.Points:N0}\n";
            SetStatus(msg);
        }

        // =====================================================================
        // Layout construction (all built in code — no .tscn required)
        // =====================================================================

        private void BuildLayout()
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Ignore: _gui_find_control_at_pos skips HUD itself but still finds its children
            // (buttons). This lets clicks fall through to TileNodes in PlayerHand's subtree.
            MouseFilter = MouseFilterEnum.Ignore;

            _nameLabels   = new Label[4];
            _scoreLabels  = new Label[4];
            _windLabels   = new Label[4];
            _discardPools = new Control[4];
            _riichiSticks = new Control[4];

            // ---- Score panels (4 corners) ----
            BuildScorePanels();

            // ---- Centre info panel ----
            BuildCentrePanel();

            // ---- Discard pools ----
            BuildDiscardPools();

            // ---- Action button bar (bottom of screen) ----
            BuildActionButtons();

            // ---- Menu / back button (top-left corner) ----
            BuildMenuButton();

            // ---- Riichi waits popup ----
            BuildWaitsPopup();

            // ---- Status label (top of screen, centred) ----
            _statusLabel = new Label();
            _statusLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
            _statusLabel.OffsetTop    = 8;
            _statusLabel.OffsetBottom = 40;
            _statusLabel.OffsetLeft   = 120;  // Don't overlap the menu button
            _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _statusLabel.AddThemeFontSizeOverride("font_size", 16);
            AddChild(_statusLabel);

            // ---- Countdown bar (above action buttons, hidden until network turn) ----
            BuildCountdownBar();

            // ---- Win scoring overlay — MUST be added last so it renders on top ----
            BuildScoringPanel();
        }

        private void BuildScorePanels()
        {
            // Score panels — positioned at each player's side, inset from the tile strips
            // [0]=Human bottom-left, [1]=Right CPU, [2]=Top CPU, [3]=Left CPU
            // Each panel: (anchor preset, left offset, top offset, right offset, bottom offset)
            var panels = new (LayoutPreset preset, float l, float t, float r, float b)[]
            {
                (LayoutPreset.BottomLeft,  60,   -105, 220,  -10),  // Human — sits beside the hand panel
                (LayoutPreset.CenterRight, -220, -45,  -60,  45),   // Right CPU
                (LayoutPreset.TopRight,    -220, 8,    -60,  88),   // Top CPU
                (LayoutPreset.CenterLeft,  60,   -45,  220,  45),   // Left CPU
            };

            for (int i = 0; i < 4; i++)
            {
                var panel = new PanelContainer();
                panel.SetAnchorsAndOffsetsPreset(panels[i].preset);
                panel.OffsetLeft   = panels[i].l;
                panel.OffsetTop    = panels[i].t;
                panel.OffsetRight  = panels[i].r;
                panel.OffsetBottom = panels[i].b;
                panel.CustomMinimumSize = new Vector2(150, 80);

                var vbox = new VBoxContainer();

                _nameLabels[i]  = new Label { Text = $"Player {i}" };
                _scoreLabels[i] = new Label { Text = "30,000" };
                _windLabels[i]  = new Label { Text = "E" };

                _nameLabels[i].AddThemeFontSizeOverride("font_size", 14);
                _scoreLabels[i].AddThemeFontSizeOverride("font_size", 18);

                // Riichi stick (hidden by default)
                var stick = new ColorRect();
                stick.Color   = new Color(1f, 0.9f, 0.3f);
                stick.CustomMinimumSize = new Vector2(60, 8);
                stick.Visible = false;
                _riichiSticks[i] = stick;

                vbox.AddChild(_windLabels[i]);
                vbox.AddChild(_nameLabels[i]);
                vbox.AddChild(_scoreLabels[i]);
                vbox.AddChild(stick);

                // Furiten badge — only on the human's own panel (index 0)
                if (i == 0)
                {
                    _furitenLabel = new Label { Text = "⚠ FURITEN" };
                    _furitenLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    _furitenLabel.AddThemeFontSizeOverride("font_size", 12);
                    _furitenLabel.AddThemeColorOverride("font_color", new Color(1f, 0.25f, 0.25f));
                    _furitenLabel.Visible = false;
                    vbox.AddChild(_furitenLabel);
                }

                panel.AddChild(vbox);
                AddChild(panel);
            }
        }

        private void BuildCentrePanel()
        {
            // Centre panel: round wind, counters, dora indicator tiles.
            // Wide enough to fit up to 5 dora tile images (5 × 34px = 170 + margins).
            var panel = new PanelContainer();
            panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            panel.OffsetLeft   = -130;
            panel.OffsetTop    = -68;
            panel.OffsetRight  =  130;
            panel.OffsetBottom =  68;

            var vbox = new VBoxContainer();
            vbox.Alignment = BoxContainer.AlignmentMode.Center;
            vbox.AddThemeConstantOverride("separation", 4);

            _roundWindLabel = new Label { Text = "East Round" };
            _counterLabel   = new Label { Text = "" };

            _roundWindLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.HorizontalAlignment   = HorizontalAlignment.Center;
            _roundWindLabel.AddThemeFontSizeOverride("font_size", 13);

            // "Dora:" label + row of tile images
            var doraLabel = new Label { Text = "Dora" };
            doraLabel.HorizontalAlignment = HorizontalAlignment.Center;
            doraLabel.AddThemeFontSizeOverride("font_size", 11);

            var doraRow = new HBoxContainer();
            doraRow.Alignment = BoxContainer.AlignmentMode.Center;
            doraRow.AddThemeConstantOverride("separation", 3);

            // Pre-create 5 TileNode slots (start hidden; revealed as doras are added)
            for (int i = 0; i < _doraTileNodes.Length; i++)
            {
                var node = new TileNode();
                node.CustomMinimumSize = new Vector2(28, 38);
                node.SetInteractive(false);
                node.Visible = false;
                doraRow.AddChild(node);
                _doraTileNodes[i] = node;
            }

            vbox.AddChild(_roundWindLabel);
            vbox.AddChild(_counterLabel);
            vbox.AddChild(doraLabel);
            vbox.AddChild(doraRow);
            panel.AddChild(vbox);
            AddChild(panel);
        }

        private void BuildDiscardPools()
        {
            // Four river/discard pools arranged around the centre panel.
            // Each pool is a sized Control (so anchors define the exact rect) that
            // wraps an HFlowContainer.  The outer Control clips overflow tiles and
            // shows a subtle background so the area is visible even when empty.
            //
            // Layout (offsets from screen centre, LayoutPreset.Center):
            //   Human (south) : below centre panel
            //   Right (west)  : right side
            //   Top  (north)  : above centre panel
            //   Left (east)   : left side
            var configs = new (float l, float t, float r, float b)[]
            {
                (-215,  58,  215, 185),   // Human  (south)
                ( 120, -80,  285,  80),   // Right  (west)
                (-215,-185,  215, -58),   // Top    (north)
                (-285, -80, -120,  80),   // Left   (east)
            };

            for (int i = 0; i < 4; i++)
            {
                // Outer sizing container — controls the exact river rect
                var outer = new Control();
                outer.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                outer.OffsetLeft   = configs[i].l;
                outer.OffsetTop    = configs[i].t;
                outer.OffsetRight  = configs[i].r;
                outer.OffsetBottom = configs[i].b;
                outer.ClipContents = true;

                // Flow container fills the outer rect
                var pool = new HFlowContainer();
                pool.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                pool.AddThemeConstantOverride("h_separation", 2);
                pool.AddThemeConstantOverride("v_separation", 2);
                outer.AddChild(pool);
                AddChild(outer);

                _discardPools[i] = pool;
            }
        }

        // =====================================================================
        // Countdown bar — public API
        // =====================================================================

        /// <summary>Show the countdown bar and reset it to full.</summary>
        public void StartCountdown(float totalSecs)
        {
            _countdownContainer.Visible = true;
            UpdateCountdown(totalSecs, totalSecs);
        }

        /// <summary>Hide the countdown bar.</summary>
        public void StopCountdown()
        {
            _countdownContainer.Visible = false;
        }

        /// <summary>
        /// Update the fill width and colour. Call every frame from GameController._Process.
        /// <paramref name="remainingSecs"/> counts down to zero;
        /// <paramref name="totalSecs"/> is the original duration used to compute the fraction.
        /// </summary>
        public void UpdateCountdown(float remainingSecs, float totalSecs)
        {
            float fraction = totalSecs > 0f
                ? Mathf.Clamp(remainingSecs / totalSecs, 0f, 1f)
                : 0f;

            _countdownFill.AnchorRight = fraction;
            _countdownFill.OffsetRight = 0f;    // ensure no residual pixel offset

            // green → yellow → red as time runs out
            Color fillColor;
            if (fraction >= 0.5f)
                fillColor = new Color(0.20f, 0.78f, 0.30f)
                    .Lerp(new Color(0.92f, 0.78f, 0.10f), (1f - fraction) * 2f);
            else
                fillColor = new Color(0.92f, 0.78f, 0.10f)
                    .Lerp(new Color(0.92f, 0.22f, 0.22f), (0.5f - fraction) * 2f);
            _countdownFill.Color = fillColor;

            int secs = (int)MathF.Ceiling(remainingSecs);
            _countdownLabel.Text = $"Auto in {secs}s";
        }

        // =====================================================================
        // Countdown bar — layout construction
        // =====================================================================

        private void BuildCountdownBar()
        {
            // Thin bar positioned just above the action button row.
            // Action buttons:  OffsetTop=-148  OffsetBottom=-90  (from BottomWide)
            // Countdown bar:   OffsetTop=-167  OffsetBottom=-151  (16px, 3px gap above)
            _countdownContainer = new Control();
            _countdownContainer.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            _countdownContainer.OffsetLeft   =  180;
            _countdownContainer.OffsetRight  =  -10;
            _countdownContainer.OffsetTop    = -167;
            _countdownContainer.OffsetBottom = -151;
            _countdownContainer.MouseFilter  = MouseFilterEnum.Ignore;
            _countdownContainer.Visible      = false;

            // Dark trough background
            var bg = new ColorRect();
            bg.AnchorRight  = 1f;
            bg.AnchorBottom = 1f;
            bg.Color        = new Color(0.07f, 0.07f, 0.10f, 0.92f);
            bg.MouseFilter  = MouseFilterEnum.Ignore;
            _countdownContainer.AddChild(bg);

            // Coloured fill — AnchorRight is updated each frame to animate the drain
            _countdownFill = new ColorRect();
            _countdownFill.AnchorRight  = 1f;
            _countdownFill.AnchorBottom = 1f;
            _countdownFill.Color        = new Color(0.20f, 0.78f, 0.30f);
            _countdownFill.MouseFilter  = MouseFilterEnum.Ignore;
            _countdownContainer.AddChild(_countdownFill);

            // Seconds label centred over the bar
            _countdownLabel = new Label();
            _countdownLabel.AnchorRight         = 1f;
            _countdownLabel.AnchorBottom        = 1f;
            _countdownLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _countdownLabel.VerticalAlignment   = VerticalAlignment.Center;
            _countdownLabel.AddThemeFontSizeOverride("font_size", 11);
            _countdownLabel.AddThemeColorOverride("font_color", Colors.White);
            _countdownLabel.MouseFilter = MouseFilterEnum.Ignore;
            _countdownContainer.AddChild(_countdownLabel);

            AddChild(_countdownContainer);
        }

        private void BuildActionButtons()
        {
            var bar = new HBoxContainer();
            bar.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            bar.OffsetTop    = -148;
            bar.OffsetBottom = -90;
            bar.OffsetLeft   = 180;
            bar.OffsetRight  = -10;
            bar.Alignment    = BoxContainer.AlignmentMode.Center;
            bar.AddThemeConstantOverride("separation", 8);

            _btnRiichi = MakeButton("RIICHI", new Color(0.8f, 0.2f, 0.2f));
            _btnTsumo  = MakeButton("TSUMO",  new Color(0.2f, 0.6f, 0.2f));
            _btnRon    = MakeButton("RON",    new Color(0.8f, 0.4f, 0.1f));
            _btnPon    = MakeButton("PON",    new Color(0.2f, 0.4f, 0.8f));
            _btnChi    = MakeButton("CHI",    new Color(0.5f, 0.2f, 0.7f));
            _btnKan    = MakeButton("KAN",    new Color(0.6f, 0.3f, 0.0f));
            _btnPass   = MakeButton("PASS",   new Color(0.4f, 0.4f, 0.4f));
            _btnNext   = MakeButton("NEXT HAND →", new Color(0.2f, 0.6f, 0.4f));

            _btnRiichi.Pressed += () => EmitSignal(SignalName.RiichiPressed);
            _btnTsumo.Pressed  += () => EmitSignal(SignalName.TsumoPressed);
            _btnRon.Pressed    += () => EmitSignal(SignalName.RonPressed);
            _btnPon.Pressed    += () => EmitSignal(SignalName.PonPressed);
            _btnChi.Pressed    += () => EmitSignal(SignalName.ChiPressed);
            _btnKan.Pressed    += () => EmitSignal(SignalName.KanPressed);
            _btnPass.Pressed   += () => EmitSignal(SignalName.PassPressed);
            _btnNext.Pressed   += () => EmitSignal(SignalName.NextHandPressed);

            bar.AddChild(_btnRiichi);
            bar.AddChild(_btnTsumo);
            bar.AddChild(_btnRon);
            bar.AddChild(_btnPon);
            bar.AddChild(_btnChi);
            bar.AddChild(_btnKan);
            bar.AddChild(_btnPass);
            bar.AddChild(_btnNext);

            HideActionButtons();
            _btnNext.Visible = false;

            AddChild(bar);
        }

        private void BuildWaitsPopup()
        {
            // Dark translucent panel just above the action button bar
            _waitsPopup = new Panel();
            _waitsPopup.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            _waitsPopup.OffsetLeft   =  120;
            _waitsPopup.OffsetRight  = -120;
            _waitsPopup.OffsetTop    = -248;
            _waitsPopup.OffsetBottom = -155;
            _waitsPopup.Visible      = false;

            var popupStyle = new StyleBoxFlat();
            popupStyle.BgColor     = new Color(0.08f, 0.08f, 0.12f, 0.93f);
            popupStyle.BorderColor = new Color(0.10f, 0.85f, 0.25f, 1f);
            popupStyle.BorderWidthTop    = popupStyle.BorderWidthBottom =
            popupStyle.BorderWidthLeft   = popupStyle.BorderWidthRight  = 2;
            popupStyle.CornerRadiusTopLeft    = popupStyle.CornerRadiusTopRight    =
            popupStyle.CornerRadiusBottomLeft = popupStyle.CornerRadiusBottomRight = 6;
            _waitsPopup.AddThemeStyleboxOverride("panel", popupStyle);

            var vbox = new VBoxContainer();
            vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            vbox.Alignment = BoxContainer.AlignmentMode.Center;
            vbox.AddThemeConstantOverride("separation", 4);

            _waitsTitle = new Label { Text = "Waiting for:" };
            _waitsTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _waitsTitle.AddThemeFontSizeOverride("font_size", 12);
            _waitsTitle.AddThemeColorOverride("font_color", new Color(0.8f, 1f, 0.8f));

            _waitsRow = new HBoxContainer();
            _waitsRow.Alignment = BoxContainer.AlignmentMode.Center;
            _waitsRow.AddThemeConstantOverride("separation", 4);

            vbox.AddChild(_waitsTitle);
            vbox.AddChild(_waitsRow);
            _waitsPopup.AddChild(vbox);
            AddChild(_waitsPopup);
        }

        /// <summary>
        /// Show the waits popup listing tiles that complete the hand after discarding.
        /// <paramref name="remaining"/> maps TileId → copies still unseen (wall + opponents' hands).
        /// <paramref name="discardTile"/> is the tile being discarded for riichi (shown in header).
        /// Pass null to omit the count labels.
        /// </summary>
        public void ShowWaitsPopup(List<Tile> waits,
                                   System.Collections.Generic.Dictionary<int, int>? remaining = null,
                                   Tile? discardTile = null)
        {
            // Clear previous contents
            foreach (var child in _waitsRow.GetChildren())
                child.QueueFree();

            if (waits.Count == 0)
            {
                _waitsTitle.Text    = discardTile != null
                    ? $"Discarding {discardTile} — no waits found"
                    : "No waits found";
                _waitsPopup.Visible = true;
                return;
            }

            // Count live outs only (tiles actually drawable — dead waits excluded from headline)
            int totalLeft = remaining == null ? -1 : remaining.Values.Where(v => v > 0).Sum();
            int deadCount  = remaining == null ?  0 : remaining.Values.Count(v => v <= 0);
            string deadSuffix = deadCount > 0 ? $"  ({deadCount} dead)" : "";
            string discardStr = discardTile != null ? $"Discard {discardTile}  →  " : "";
            _waitsTitle.Text = totalLeft >= 0
                ? $"{discardStr}waiting ({totalLeft} live out{(totalLeft == 1 ? "" : "s")}){deadSuffix}:"
                : $"{discardStr}waiting for ({waits.Count}):";

            foreach (var w in waits)
            {
                // Each wait tile: tile image stacked above its remaining count
                var col = new VBoxContainer();
                col.Alignment = BoxContainer.AlignmentMode.Center;
                col.AddThemeConstantOverride("separation", 2);

                var node = new TileNode();
                node.CustomMinimumSize = new Vector2(30, 40);
                node.SetTile(w, faceDown: false);
                node.SetInteractive(false);

                // Dim tiles that are completely gone from the unseen pool
                bool gone = remaining != null
                            && remaining.TryGetValue(w.TileId, out int cnt)
                            && cnt <= 0;
                if (gone)
                    node.Modulate = new Color(1f, 1f, 1f, 0.40f);

                col.AddChild(node);

                if (remaining != null && remaining.TryGetValue(w.TileId, out int left))
                {
                    if (left == 0)
                    {
                        // Dead wait — bold red "✗" so it's unmissable
                        var deadLbl = new Label { Text = "✗ DEAD" };
                        deadLbl.HorizontalAlignment = HorizontalAlignment.Center;
                        deadLbl.AddThemeFontSizeOverride("font_size", 10);
                        deadLbl.AddThemeColorOverride("font_color", new Color(1f, 0.25f, 0.25f));
                        col.AddChild(deadLbl);
                    }
                    else
                    {
                        // Live wait — green count label
                        var lbl = new Label { Text = $"{left}" };
                        lbl.HorizontalAlignment = HorizontalAlignment.Center;
                        lbl.AddThemeFontSizeOverride("font_size", 11);
                        lbl.AddThemeColorOverride("font_color", new Color(0.75f, 1.00f, 0.65f));
                        col.AddChild(lbl);
                    }
                }

                _waitsRow.AddChild(col);
            }

            _waitsPopup.Visible = true;
        }

        /// <summary>Hide and clear the waits popup.</summary>
        public void HideWaitsPopup()
        {
            _waitsPopup.Visible = false;
            foreach (var child in _waitsRow.GetChildren())
                child.QueueFree();
        }

        private void BuildScoringPanel()
        {
            // Full-screen dim backdrop — added last in the HUD tree so it sits on top
            _scoringBackdrop = new ColorRect();
            _scoringBackdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _scoringBackdrop.Color       = new Color(0f, 0f, 0f, 0.70f);
            _scoringBackdrop.MouseFilter = MouseFilterEnum.Stop;  // Block all clicks through
            _scoringBackdrop.Visible     = false;

            // Centred scoring card — tall enough to hold yaku + payments + all-player scores
            _scoringPanel = new Panel();
            _scoringPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            _scoringPanel.OffsetLeft   = -370;
            _scoringPanel.OffsetTop    = -310;
            _scoringPanel.OffsetRight  =  370;
            _scoringPanel.OffsetBottom =  310;
            _scoringPanel.MouseFilter  = MouseFilterEnum.Stop;

            var cardStyle = new StyleBoxFlat();
            cardStyle.BgColor     = new Color(0.07f, 0.07f, 0.12f, 1f);
            cardStyle.BorderColor = new Color(0.75f, 0.62f, 0.18f, 1f);  // Gold border
            cardStyle.BorderWidthTop    = cardStyle.BorderWidthBottom =
            cardStyle.BorderWidthLeft   = cardStyle.BorderWidthRight  = 2;
            cardStyle.CornerRadiusTopLeft    = cardStyle.CornerRadiusTopRight    =
            cardStyle.CornerRadiusBottomLeft = cardStyle.CornerRadiusBottomRight = 10;
            _scoringPanel.AddThemeStyleboxOverride("panel", cardStyle);

            // ---- Main VBox inside the card ----
            var outerVBox = new VBoxContainer();
            outerVBox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            outerVBox.OffsetLeft   = 20;
            outerVBox.OffsetTop    = 14;
            outerVBox.OffsetRight  = -20;
            outerVBox.OffsetBottom = -14;
            outerVBox.AddThemeConstantOverride("separation", 5);

            // ── Win title ──
            _scoringTitle = new Label();
            _scoringTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _scoringTitle.AddThemeFontSizeOverride("font_size", 20);
            outerVBox.AddChild(_scoringTitle);
            outerVBox.AddChild(new HSeparator());

            // ── Yaku list (rebuilt each win) ──
            _scoringYakuRows = new VBoxContainer();
            _scoringYakuRows.AddThemeConstantOverride("separation", 2);
            outerVBox.AddChild(_scoringYakuRows);
            outerVBox.AddChild(new HSeparator());

            // ── Han / Fu / Limit summary row ──
            var summaryRow = new HBoxContainer();
            _scoringHanFuLabel = new Label();
            _scoringHanFuLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _scoringHanFuLabel.AddThemeFontSizeOverride("font_size", 13);
            _scoringLimitLabel = new Label();
            _scoringLimitLabel.HorizontalAlignment = HorizontalAlignment.Right;
            _scoringLimitLabel.AddThemeFontSizeOverride("font_size", 15);
            _scoringLimitLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.2f));
            summaryRow.AddChild(_scoringHanFuLabel);
            summaryRow.AddChild(_scoringLimitLabel);
            outerVBox.AddChild(summaryRow);
            outerVBox.AddChild(new HSeparator());

            // ── Payment rows (rebuilt each win) ──
            _scoringPayRows = new VBoxContainer();
            _scoringPayRows.AddThemeConstantOverride("separation", 2);
            outerVBox.AddChild(_scoringPayRows);

            // ── Total points won ──
            _scoringTotalWon = new Label();
            _scoringTotalWon.HorizontalAlignment = HorizontalAlignment.Center;
            _scoringTotalWon.AddThemeFontSizeOverride("font_size", 22);
            _scoringTotalWon.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.45f));
            outerVBox.AddChild(_scoringTotalWon);
            outerVBox.AddChild(new HSeparator());

            // ── All-player score table ──
            var scoresHeader = new Label { Text = "  Scores After This Hand" };
            scoresHeader.AddThemeFontSizeOverride("font_size", 13);
            scoresHeader.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.85f));
            outerVBox.AddChild(scoresHeader);

            _scoringAllScores = new VBoxContainer();
            _scoringAllScores.AddThemeConstantOverride("separation", 2);
            outerVBox.AddChild(_scoringAllScores);
            outerVBox.AddChild(new HSeparator());

            // ── Bottom button row: Next Hand | Menu ──
            var btnRow = new HBoxContainer();
            btnRow.Alignment = BoxContainer.AlignmentMode.Center;
            btnRow.AddThemeConstantOverride("separation", 16);
            btnRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            _scoringNextBtn = MakeButton("▶  Next Hand", new Color(0.15f, 0.45f, 0.22f));
            _scoringNextBtn.CustomMinimumSize = new Vector2(190, 46);
            _scoringNextBtn.Pressed += () => EmitSignal(SignalName.ScoringNextHandPressed);

            _scoringMenuBtn = MakeButton("⬅  Menu", new Color(0.28f, 0.28f, 0.35f));
            _scoringMenuBtn.CustomMinimumSize = new Vector2(150, 46);
            _scoringMenuBtn.Pressed += () => EmitSignal(SignalName.ScoringMenuPressed);

            var nextBtn = _scoringNextBtn;
            var menuBtn = _scoringMenuBtn;

            btnRow.AddChild(nextBtn);
            btnRow.AddChild(menuBtn);
            outerVBox.AddChild(btnRow);

            _scoringPanel.AddChild(outerVBox);
            _scoringBackdrop.AddChild(_scoringPanel);
            AddChild(_scoringBackdrop);
        }

        /// <summary>
        /// Populate and display the scoring overlay after a Tsumo or Ron win.
        /// Points have already been transferred; this is for display only.
        /// <paramref name="allPlayerPoints"/> = each player's point total AFTER this hand.
        /// </summary>
        public void ShowScoringPanel(
            ScoreResult     score,
            YakuCheckResult yaku,
            YakuContext     ctx,
            string          winnerName,
            bool            isTsumo,
            string          discarderName,
            string[]        allPlayerNames,
            int[]           allPlayerPoints,
            int             winnerSeat,
            int             dealerSeat)
        {
            // ── Title ──
            string method = isTsumo ? "Tsumo" : "Ron";
            _scoringTitle.Text = $"★  {winnerName} wins by {method}!  ★";
            _scoringTitle.AddThemeColorOverride("font_color",
                isTsumo ? new Color(0.35f, 1f, 0.45f) : new Color(1f, 0.55f, 0.20f));

            // ── Yaku rows ──
            foreach (var child in _scoringYakuRows.GetChildren()) child.QueueFree();

            foreach (var y in yaku.Yaku)
            {
                string fanText = y.IsYakuman ? "Yakuman" : $"{y.Fan} han";
                Color  fanCol  = y.IsYakuman
                    ? new Color(1f, 0.85f, 0.2f)
                    : Colors.White;
                _scoringYakuRows.AddChild(MakeScoringRow(
                    $"  {y.Name}  ({y.NameJP})", fanText, fanCol));
            }
            if (ctx.DoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "  Dora", $"{ctx.DoraCount} han", new Color(1f, 0.80f, 0.30f)));
            if (ctx.IsRiichi && ctx.UraDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "  Ura Dora", $"{ctx.UraDoraCount} han", new Color(1f, 0.70f, 0.20f)));

            // ── Han / Fu / Limit summary ──
            bool hasLimit = score.Limit != HandLimit.None;
            _scoringHanFuLabel.Text = hasLimit
                ? $"  {score.TotalFan} han"
                : $"  {score.TotalFan} han  {score.Fu.Total} fu";
            _scoringLimitLabel.Text = hasLimit
                ? $"— {ScoreCalculator.LimitName(score.Limit)} —"
                : "";

            // ── Payment rows ──
            foreach (var child in _scoringPayRows.GetChildren()) child.QueueFree();

            if (isTsumo)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (i == winnerSeat) continue;
                    int amount = (!score.IsDealer && i == dealerSeat)
                        ? score.TsumoPaymentEast
                        : score.TsumoPaymentOther;
                    if (amount <= 0) continue;
                    _scoringPayRows.AddChild(MakeScoringRow(
                        $"  {allPlayerNames[i]} pays:",
                        $"−{amount:N0}", new Color(1f, 0.6f, 0.6f)));
                }
            }
            else
            {
                _scoringPayRows.AddChild(MakeScoringRow(
                    $"  {discarderName} pays:",
                    $"−{score.RonPayment:N0}", new Color(1f, 0.6f, 0.6f)));
            }
            if (score.CounterBonus > 0)
                _scoringPayRows.AddChild(MakeScoringRow(
                    "  Counter bonus:", $"+{score.CounterBonus:N0}",
                    new Color(0.75f, 0.75f, 0.75f)));
            if (score.RiichiBetsWon > 0)
                _scoringPayRows.AddChild(MakeScoringRow(
                    "  Riichi sticks:", $"+{score.RiichiBetsWon:N0}",
                    new Color(1f, 0.90f, 0.30f)));

            // ── Total points won ──
            _scoringTotalWon.Text = $"Total: +{score.TotalPointsWon:N0} points";

            // ── All-player score table ──
            foreach (var child in _scoringAllScores.GetChildren()) child.QueueFree();

            // Wind suffix for each seat relative to dealer
            string[] windLetters = { "E", "S", "W", "N" };

            // Sort players by points descending so ranking is immediately visible
            var order = Enumerable.Range(0, 4)
                .OrderByDescending(i => allPlayerPoints[i])
                .ToList();

            for (int rank = 0; rank < 4; rank++)
            {
                int i       = order[rank];
                int windOff = (i - dealerSeat + 4) % 4;
                string wind = windLetters[windOff];
                bool   isWinner = i == winnerSeat;

                // Star prefix for the winner; rank number for others
                string prefix = isWinner ? "  ★ " : $"  {rank + 1}. ";
                string label  = $"{prefix}{allPlayerNames[i]}  ({wind})";

                var row = MakeScoringRow(label, $"{allPlayerPoints[i]:N0}",
                    isWinner ? new Color(0.35f, 1f, 0.45f) : Colors.White);

                // Bold-highlight the winner's row with a faint tinted background
                if (isWinner)
                {
                    var lbl = row.GetChild<Label>(0);
                    lbl.AddThemeColorOverride("font_color", new Color(0.35f, 1f, 0.45f));
                }

                _scoringAllScores.AddChild(row);
            }

            // Show overlay
            _scoringBackdrop.Visible = true;
        }

        /// <summary>
        /// Populate and display the scoring overlay from raw network data (no ScoreResult objects).
        /// </summary>
        public void ShowScoringPanelNet(
            string   winnerName,
            bool     isTsumo,
            string   payerName,
            string[] allNames,
            int[]    allPoints,
            int      winnerSeat,
            int      dealerSeat,
            string[] yakuNames,
            int      han,
            int      fu,
            int      doraCount,
            int      uraDoraCount,
            int      totalPointsWon)
        {
            // ── Title ──
            string method = isTsumo ? "Tsumo" : "Ron";
            _scoringTitle.Text = $"★  {winnerName} wins by {method}!  ★";
            _scoringTitle.AddThemeColorOverride("font_color",
                isTsumo ? new Color(0.35f, 1f, 0.45f) : new Color(1f, 0.55f, 0.20f));

            // ── Yaku rows ──
            foreach (var child in _scoringYakuRows.GetChildren()) child.QueueFree();
            foreach (var name in yakuNames)
                _scoringYakuRows.AddChild(MakeScoringRow($"  {name}", "", Colors.White));
            if (doraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "  Dora", $"{doraCount} han", new Color(1f, 0.80f, 0.30f)));
            if (uraDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "  Ura Dora", $"{uraDoraCount} han", new Color(1f, 0.70f, 0.20f)));

            // ── Han / Fu ──
            _scoringHanFuLabel.Text = $"  {han} han  {fu} fu";
            _scoringLimitLabel.Text = "";

            // ── Payment ──
            foreach (var child in _scoringPayRows.GetChildren()) child.QueueFree();
            if (!isTsumo && !string.IsNullOrEmpty(payerName))
                _scoringPayRows.AddChild(MakeScoringRow(
                    $"  {payerName} pays:", "", new Color(1f, 0.6f, 0.6f)));

            // ── Total ──
            _scoringTotalWon.Text = totalPointsWon > 0
                ? $"Total: +{totalPointsWon:N0} points" : "";

            // ── All-player scores, sorted by points ──
            foreach (var child in _scoringAllScores.GetChildren()) child.QueueFree();
            string[] windLetters = { "E", "S", "W", "N" };
            var order = Enumerable.Range(0, allNames.Length)
                                  .OrderByDescending(i => allPoints[i])
                                  .ToList();
            for (int rank = 0; rank < order.Count; rank++)
            {
                int    i       = order[rank];
                int    windOff = (i - dealerSeat + 4) % 4;
                string wind    = windLetters[Math.Min(windOff, 3)];
                bool   isWin   = i == winnerSeat;
                string prefix  = isWin ? "  ★ " : $"  {rank + 1}. ";
                _scoringAllScores.AddChild(MakeScoringRow(
                    $"{prefix}{allNames[i]}  ({wind})",
                    $"{allPoints[i]:N0}",
                    isWin ? new Color(0.35f, 1f, 0.45f) : Colors.White));
            }

            _scoringBackdrop.Visible = true;
        }

        /// <summary>Hide the scoring overlay and clear dynamic rows for the next hand.</summary>
        public void HideScoringPanel()
        {
            _scoringBackdrop.Visible = false;
            _scoringNextBtn.Text    = "▶  Next Hand";  // Restore from any "Play Again" state
            _scoringNextBtn.Visible  = true;
            foreach (var child in _scoringYakuRows.GetChildren())  child.QueueFree();
            foreach (var child in _scoringPayRows.GetChildren())   child.QueueFree();
            foreach (var child in _scoringAllScores.GetChildren()) child.QueueFree();
        }

        /// <summary>
        /// Show the final game-over screen using the scoring panel.
        /// Displays ranked standings with score deltas and uma-adjusted net scores.
        /// Uma: 1st +30 / 2nd +10 / 3rd −10 / 4th −30 (in thousands).
        /// </summary>
        public void ShowGameOverPanel(string reason, string[] playerNames, int[] playerPoints, int dealerSeat, bool showPlayAgain = false)
        {
            // Clear any leftover content from a previous hand's scoring panel
            foreach (var child in _scoringYakuRows.GetChildren())  child.QueueFree();
            foreach (var child in _scoringPayRows.GetChildren())   child.QueueFree();
            foreach (var child in _scoringAllScores.GetChildren()) child.QueueFree();

            // ── Title ──
            _scoringTitle.Text = "★  FINAL RESULTS  ★";
            _scoringTitle.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.20f));

            // ── Reason subtitle (yaku rows area) ──
            var reasonLbl = new Label { Text = reason };
            reasonLbl.HorizontalAlignment = HorizontalAlignment.Center;
            reasonLbl.AddThemeFontSizeOverride("font_size", 14);
            reasonLbl.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.85f));
            _scoringYakuRows.AddChild(reasonLbl);

            // ── Clear han/fu / limit labels (not applicable here) ──
            _scoringHanFuLabel.Text = "";
            _scoringLimitLabel.Text = "";

            // ── "Final Standings" header ──
            _scoringTotalWon.Text = "— Final Standings —";
            _scoringTotalWon.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 1f));

            // ── Column header ──
            var headerColor = new Color(0.60f, 0.60f, 0.72f);
            _scoringAllScores.AddChild(MakeGameOverRow(
                "", "  Player (Wind)", "Score", "Net",
                headerColor, headerColor, headerColor));

            // ── Ranked player rows ──
            // medals[0..2] are Unicode medal emojis; rank 4 just uses "4."
            string[] medals      = { "🥇", "🥈", "🥉", "4." };
            int[]    umaTable    = { 30, 10, -10, -30 };
            string[] windLetters = { "E", "S", "W", "N" };

            var order = Enumerable.Range(0, 4)
                .OrderByDescending(i => playerPoints[i])
                .ToList();

            for (int rank = 0; rank < 4; rank++)
            {
                int    i       = order[rank];
                int    windOff = (i - dealerSeat + 4) % 4;
                string wind    = windLetters[windOff];
                int    pts     = playerPoints[i];

                // Net score: (score − oka) ÷ 1000 (integer, truncated) + uma
                int netK   = (pts - GameState.StartingPoints) / 1_000 + umaTable[rank];
                string net = netK >= 0 ? $"+{netK}" : $"{netK}";

                // Medal / rank colours
                Color nameColor = rank switch
                {
                    0 => new Color(1.00f, 0.85f, 0.20f),   // gold
                    1 => new Color(0.82f, 0.82f, 0.88f),   // silver
                    2 => new Color(0.85f, 0.60f, 0.30f),   // bronze
                    _ => new Color(0.65f, 0.65f, 0.70f),   // grey
                };
                Color scoreColor = pts < 0
                    ? new Color(1f, 0.40f, 0.40f)          // red — bankrupt
                    : Colors.White;
                Color netColor = netK > 0
                    ? new Color(0.35f, 1f, 0.45f)           // green — positive
                    : netK < 0
                        ? new Color(1f, 0.50f, 0.50f)       // red — negative
                        : Colors.White;

                _scoringAllScores.AddChild(MakeGameOverRow(
                    medals[rank],
                    $"  {playerNames[i]}  ({wind})",
                    $"{pts:N0}",
                    net,
                    nameColor, scoreColor, netColor));
            }

            // ── Uma note (pay rows area) ──
            var umaNote = new Label { Text = "  Uma: 1st +30 / 2nd +10 / 3rd −10 / 4th −30  (Net = (score−30k)÷1k + uma)" };
            umaNote.AddThemeFontSizeOverride("font_size", 11);
            umaNote.AddThemeColorOverride("font_color", new Color(0.50f, 0.50f, 0.60f));
            _scoringPayRows.AddChild(umaNote);

            // ── Buttons: "Play Again" for local, hidden in network ──
            if (showPlayAgain)
            {
                _scoringNextBtn.Text    = "▶  Play Again";
                _scoringNextBtn.Visible = true;
            }
            else
            {
                _scoringNextBtn.Visible = false;
            }

            _scoringBackdrop.Visible = true;
        }

        /// <summary>Create a two-column label row (left label + right label).</summary>
        private static HBoxContainer MakeScoringRow(string left, string right, Color rightColor)
        {
            var row = new HBoxContainer();
            var lbl = new Label { Text = left };
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            lbl.AddThemeFontSizeOverride("font_size", 13);
            var val = new Label { Text = right };
            val.HorizontalAlignment = HorizontalAlignment.Right;
            val.AddThemeFontSizeOverride("font_size", 13);
            val.AddThemeColorOverride("font_color", rightColor);
            row.AddChild(lbl);
            row.AddChild(val);
            return row;
        }

        /// <summary>
        /// Four-column row used in the game-over standings table.
        /// Columns: medal/rank icon | player name + wind | final score | net (uma-adj) score.
        /// </summary>
        private static HBoxContainer MakeGameOverRow(
            string medal, string name, string score, string net,
            Color nameColor, Color scoreColor, Color netColor)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);

            // Medal / rank icon — fixed width so columns stay aligned
            var medalLbl = new Label { Text = medal };
            medalLbl.CustomMinimumSize = new Vector2(44, 0);
            medalLbl.AddThemeFontSizeOverride("font_size", 15);
            medalLbl.AddThemeColorOverride("font_color", nameColor);

            // Player name + wind indicator — fills remaining horizontal space
            var nameLbl = new Label { Text = name };
            nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            nameLbl.AddThemeFontSizeOverride("font_size", 14);
            nameLbl.AddThemeColorOverride("font_color", nameColor);

            // Raw final score — right-aligned, fixed width
            var scoreLbl = new Label { Text = score };
            scoreLbl.CustomMinimumSize   = new Vector2(90, 0);
            scoreLbl.HorizontalAlignment = HorizontalAlignment.Right;
            scoreLbl.AddThemeFontSizeOverride("font_size", 14);
            scoreLbl.AddThemeColorOverride("font_color", scoreColor);

            // Net (uma-adjusted) score in thousands — right-aligned, fixed width
            var netLbl = new Label { Text = net };
            netLbl.CustomMinimumSize   = new Vector2(62, 0);
            netLbl.HorizontalAlignment = HorizontalAlignment.Right;
            netLbl.AddThemeFontSizeOverride("font_size", 14);
            netLbl.AddThemeColorOverride("font_color", netColor);

            row.AddChild(medalLbl);
            row.AddChild(nameLbl);
            row.AddChild(scoreLbl);
            row.AddChild(netLbl);
            return row;
        }

        private void BuildMenuButton()
        {
            var btn = MakeButton("⬅ Menu", new Color(0.25f, 0.25f, 0.30f));
            btn.CustomMinimumSize = new Vector2(90, 34);
            btn.AddThemeFontSizeOverride("font_size", 13);

            btn.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            btn.OffsetLeft   = 8;
            btn.OffsetTop    = 8;
            btn.OffsetRight  = 100;
            btn.OffsetBottom = 42;

            btn.Pressed += () => EmitSignal(SignalName.MenuPressed);
            AddChild(btn);
        }

        private static Button MakeButton(string text, Color color)
        {
            var btn  = new Button { Text = text };
            btn.CustomMinimumSize = new Vector2(90, 44);
            btn.AddThemeFontSizeOverride("font_size", 14);

            var style = new StyleBoxFlat();
            style.BgColor               = color;
            style.CornerRadiusTopLeft     = 6;
            style.CornerRadiusTopRight    = 6;
            style.CornerRadiusBottomLeft  = 6;
            style.CornerRadiusBottomRight = 6;
            btn.AddThemeStyleboxOverride("normal", style);

            var hoverStyle = (StyleBoxFlat)style.Duplicate();
            hoverStyle.BgColor = color.Lightened(0.2f);
            btn.AddThemeStyleboxOverride("hover", hoverStyle);

            btn.AddThemeColorOverride("font_color", Colors.White);
            return btn;
        }
    }
}
