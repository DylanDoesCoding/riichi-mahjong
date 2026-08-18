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
        [Signal] public delegate void KyuushuPressedEventHandler();
        [Signal] public delegate void NextHandPressedEventHandler();
        [Signal] public delegate void MenuPressedEventHandler();
        [Signal] public delegate void YakuReferencePressedEventHandler();      // opens hand-reference overlay
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
        private Label    _wallCountLabel  = null!;
        private TileNode[] _doraTileNodes = new TileNode[5];  // Up to 5 dora indicators

        // Discard pools (one HFlowContainer per player)
        private Control[] _discardPools = null!;

        // Action buttons
        private Button _btnRiichi   = null!;
        private Button _btnTsumo    = null!;
        private Button _btnRon      = null!;
        private Button _btnPon      = null!;
        private Button _btnChi      = null!;
        private Button _btnKan      = null!;
        private Button _btnPass     = null!;
        private Button _btnNext     = null!;
        private Button _btnKyuushu  = null!;

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
        private Label     _furitenLabel  = null!;
        private ColorRect _furitenStrike = null!;  // 2px rule through the word (pass 10)
        private Control   _furitenBadge  = null!;  // holder for label + strike
        private bool      _isFuriten;              // drives the hatch on wait tiles

        // Win-call overlay — "RON!" / "TSUMO!" / "RIICHI!" slam animation
        private ColorRect _winCallBackdrop   = null!;
        private Label     _winCallText       = null!;
        private Label     _winCallName       = null!;
        private Tween?    _callOverlayTween  = null;  // so we can kill any in-flight animation

        // Scoring overlay — shown after a Tsumo or Ron win, and reused for Game Over
        private ColorRect       _scoringBackdrop  = null!;
        private PanelContainer  _scoringPanel     = null!;
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
            DoloTheme.Apply(this);

            // The felt is a sibling drawn underneath the hands, so the HUD reaches it
            // rather than owning it — a riichi stick belongs on the table, not in the HUD.
            _felt = GetParent()?.GetNodeOrNull<TableFelt>("TableFelt");

            BuildLayout();
        }

        private TableFelt? _felt;

        // =====================================================================
        // Public API — called by GameController
        // =====================================================================

        /// <summary>
        /// Refresh score/wind/round info from raw arrays (network mode — no GameState available).
        /// <paramref name="isAllLast"/> = true when this is the final hand (South 4).
        /// </summary>
        public void UpdateAll(string[] names, int[] points, int dealerSeat,
                              string roundWind, int counters, bool isAllLast = false)
        {
            for (int i = 0; i < 4; i++)
            {
                _nameLabels[i].Text  = names.Length > i ? names[i] : $"Player {i}";
                _scoreLabels[i].Text = points.Length > i ? points[i].ToString("N0") : "0";
                SetSeatWind(i, (i - dealerSeat + 4) % 4);
            }
            _roundWindLabel.Text = isAllLast ? "All Last (オーラス)" : $"{roundWind} Round";
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

            SetActiveDoraFromIndicators(indicators);
        }

        /// <summary>Refresh all score/wind/round info from current game state.</summary>
        public void UpdateAll(GameState game)
        {
            for (int i = 0; i < 4; i++)
            {
                var p = game.Players[i];
                _nameLabels[i].Text  = p.Name;
                _scoreLabels[i].Text = p.Points.ToString("N0");
                SetSeatWind(i, (int)p.GetSeatWind(game.DealerIndex) - 1);
            }

            bool isAllLast = game.RoundWind == WindDirection.South && game.DealerIndex == 3;
            _roundWindLabel.Text = isAllLast ? "All Last (オーラス)" : $"{game.RoundWind} Round";
            _counterLabel.Text   = game.Counters > 0 ? $"×{game.Counters}" : "";

            // Dora indicator tiles — show each indicator as an actual tile image
            if (game.Wall != null)
            {
                _wallCountLabel.Text = $"{game.Wall.TilesRemaining} LEFT";

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

                SetActiveDoraFromIndicators(indicators);
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
            ApplyRiverDoraGlow(node);

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

                var riverTile = DoloLayout.RiverTile;

                var wrapper = new Control();
                wrapper.CustomMinimumSize = new Vector2(riverTile.Y, riverTile.X);  // landscape slot

                node.CustomMinimumSize = new Vector2(riverTile.X, riverTile.Y);     // stays portrait
                node.Position          = new Vector2((riverTile.Y - riverTile.X) * 0.5f,
                                                     (riverTile.X - riverTile.Y) * 0.5f);
                node.PivotOffset       = new Vector2(riverTile.X * 0.5f, riverTile.Y * 0.5f);
                node.RotationDegrees   = 90f;

                wrapper.AddChild(node);
                pool.AddChild(wrapper);
            }
            else
            {
                node.CustomMinimumSize = new Vector2(DoloLayout.RiverTile.X, DoloLayout.RiverTile.Y);
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
            _felt?.SetRiichiStick(playerIndex, true);
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

            _felt?.ClearRiichiSticks();
        }

        // =====================================================================
        // Dora glow + hover tile-matching
        // =====================================================================

        // Live dora tiles (indicator+1) — kept current by UpdateDoraIndicators/UpdateAll
        private readonly List<Tile> _activeDoraTiles = new();
        private Label? _tileInfoLabel;

        private void SetActiveDoraFromIndicators(IReadOnlyList<Tile> indicators)
        {
            _activeDoraTiles.Clear();
            foreach (var ind in indicators)
                _activeDoraTiles.Add(TileWall.GetDoraTile(ind));

            // A kan can reveal a new indicator mid-hand — refresh existing rivers
            foreach (var pool in _discardPools)
                foreach (var node in RiverTileNodes(pool))
                    ApplyRiverDoraGlow(node);
        }

        private void ApplyRiverDoraGlow(TileNode node)
        {
            var t = node.TileData;
            node.SetDoraGlow(t != null && (t.IsRedDora || _activeDoraTiles.Any(d => d == t)));
        }

        /// <summary>All TileNodes in a discard pool, including riichi-rotated wrappers.</summary>
        private static IEnumerable<TileNode> RiverTileNodes(Node pool)
        {
            foreach (var child in pool.GetChildren())
            {
                if (child is TileNode tn) yield return tn;
                else
                    foreach (var inner in child.GetChildren())
                        if (inner is TileNode itn) yield return itn;
            }
        }

        /// <summary>
        /// Cyan-highlight every visible copy of <paramref name="tile"/> in the four
        /// discard rivers and the dora indicator row. Pass null to clear.
        /// Returns the number of copies highlighted.
        /// </summary>
        public int SetDiscardMatchHighlights(Tile? tile)
        {
            int count = 0;

            foreach (var pool in _discardPools)
                foreach (var node in RiverTileNodes(pool))
                {
                    bool match = tile != null && node.TileData == tile;
                    node.SetMatchHighlight(match);
                    if (match) count++;
                }

            foreach (var dn in _doraTileNodes)
            {
                bool match = tile != null && dn.Visible && dn.TileData == tile;
                dn.SetMatchHighlight(match);
                if (match) count++;
            }

            return count;
        }

        /// <summary>Show the hover info line ("2 Sou — 1 discarded, 2 unseen").</summary>
        public void ShowTileInfo(string text)
        {
            // On touch the same line becomes the content of the info card above the
            // hand, rather than outlined text floating over the felt.
            if (DoloLayout.IsTouch)
            {
                ShowTouchInfoCard(text);
                return;
            }

            if (_tileInfoLabel == null)
            {
                _tileInfoLabel = new Label();
                _tileInfoLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
                _tileInfoLabel.OffsetLeft   = 18;
                _tileInfoLabel.OffsetTop    = -178;
                _tileInfoLabel.OffsetRight  = 460;
                _tileInfoLabel.OffsetBottom = -152;
                _tileInfoLabel.MouseFilter  = MouseFilterEnum.Ignore;

                var settings = new LabelSettings();
                settings.FontSize     = 16;
                settings.FontColor    = new Color(0.95f, 0.93f, 0.75f);
                settings.OutlineSize  = 4;
                settings.OutlineColor = new Color(0f, 0f, 0f, 0.85f);
                _tileInfoLabel.LabelSettings = settings;

                AddChild(_tileInfoLabel);
            }
            _tileInfoLabel.Text    = text;
            _tileInfoLabel.Visible = true;
        }

        public void HideTileInfo()
        {
            if (_tileInfoLabel != null) _tileInfoLabel.Visible = false;
            HideTouchInfoCard();
        }

        // ---- Button visibility ----------------------------------------------

        public void ShowActionButtons(bool canTsumo, bool canRiichi, bool canKan = false, bool canKyuushu = false)
        {
            _btnRiichi.Visible   = canRiichi;
            _btnTsumo.Visible    = canTsumo;
            _btnKan.Visible      = canKan;
            _btnKyuushu.Visible  = canKyuushu;
            _btnRon.Visible      = false;
            _btnPon.Visible      = false;
            _btnChi.Visible      = false;
            _btnPass.Visible     = false;
        }

        public void ShowClaimButtons(bool canRon, bool canPon, bool canChi, bool canKan = false)
        {
            _claimWindow.Visible = true;
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
            _btnRiichi.Visible  = false;
            _btnTsumo.Visible   = false;
            _btnRon.Visible     = false;
            _btnPon.Visible     = false;
            _btnChi.Visible     = false;
            _btnKan.Visible     = false;
            _btnPass.Visible    = false;
            _btnKyuushu.Visible = false;

            _claimWindow.Visible = false;
            HideChiPicker();
        }

        public void HideClaimButtons()
        {
            _btnRon.Visible  = false;
            _btnPon.Visible  = false;
            _btnChi.Visible  = false;
            _btnKan.Visible  = false;
            _btnPass.Visible = false;

            _claimWindow.Visible = false;
            HideChiPicker();
            ClearCountdownTile();
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
            _isFuriten            = isFuriten;
            _furitenBadge.Visible = isFuriten;
            if (!isFuriten) return;

            _furitenLabel.Text = isPermanent ? "FURITEN" : "FURITEN TEMP";

            // Permanent furiten strikes the word through; temporary furiten leaves it
            // legible, so the two states differ by more than a shade of red.
            _furitenStrike.Visible = isPermanent;

            var tint = isPermanent ? DoloTokens.FuritenRed : DoloTokens.DoraGold;
            _furitenLabel.AddThemeColorOverride("font_color", tint);
            _furitenStrike.Color = tint;
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

            // ---- Win-call overlay (RON!/TSUMO!) — above game, below scoring panel ----
            BuildWinCallOverlay();

            // ---- Win scoring overlay — MUST be added last so it renders on top ----
            BuildScoringPanel();
        }

        // Wind tile art, used as the seat badge. Pass 10 asks for the 東南西北 glyph on
        // the plate; drawing it from the tile pack rather than as text guarantees it
        // renders, since neither Source Sans 3 nor Godot's default font carries CJK.
        private static readonly string[] WindTileArt = { "Ton", "Nan", "Shaa", "Pei" };
        private static readonly string[] WindLetters = { "E", "S", "W", "N" };

        private TextureRect[] _windBadges = null!;

        private void BuildScorePanels()
        {
            _windBadges = new TextureRect[4];

            // Anchoring every plate to the table centre rather than to a screen corner
            // keeps the four in the same relationship to the felt on any aspect ratio.
            var rects = DoloLayout.NameplateRects;
            var plateSize = DoloLayout.NameplateSize;
            bool touch = DoloLayout.IsTouch;

            for (int i = 0; i < 4; i++)
            {
                var panel = new PanelContainer();
                panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                panel.OffsetLeft   = rects[i].l;
                panel.OffsetTop    = rects[i].t;
                panel.OffsetRight  = rects[i].r;
                panel.OffsetBottom = rects[i].b;
                panel.CustomMinimumSize = new Vector2(plateSize.X, plateSize.Y);
                panel.MouseFilter  = MouseFilterEnum.Ignore;
                panel.AddThemeStyleboxOverride("panel", NameplateStyle());

                var vbox = new VBoxContainer();
                vbox.AddThemeConstantOverride("separation", 2);

                // ---- Wind: tile glyph plus the letter ----
                // Two channels for the same fact, so the seat reads without colour.
                // The desktop plate has room for both; the phone chip takes the letter
                // only, and its badge is built but left hidden so SetSeatWind can stay
                // one code path.
                _windBadges[i] = new TextureRect
                {
                    CustomMinimumSize = new Vector2(20, 27),
                    ExpandMode        = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode       = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter       = MouseFilterEnum.Ignore,
                };

                _windLabels[i] = new Label { Text = "E" };
                _windLabels[i].ThemeTypeVariation = DoloTheme.Mono;
                _windLabels[i].VerticalAlignment  = VerticalAlignment.Center;

                _nameLabels[i]  = new Label { Text = $"Player {i}" };
                _scoreLabels[i] = new Label { Text = "25,000" };

                _nameLabels[i].ThemeTypeVariation  = DoloTheme.PlateName;
                _nameLabels[i].TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                _scoreLabels[i].ThemeTypeVariation = touch ? DoloTheme.Mono : DoloTheme.MonoLarge;

                // Riichi stick on the plate. The felt stick (pass 10) is the cue that
                // survives greyscale; this one keeps the plate self-contained. On a
                // 96 x 40 chip there is no room for a stick, so it becomes an underline.
                var stick = new ColorRect
                {
                    Color             = DoloTokens.RiichiStick,
                    CustomMinimumSize = touch ? new Vector2(0, 4) : new Vector2(60, 5),
                    Visible           = false,
                    MouseFilter       = MouseFilterEnum.Ignore,
                };
                _riichiSticks[i] = stick;

                if (touch)
                {
                    // The chip carries name, wind and score only.
                    _windBadges[i].Visible = false;
                    _nameLabels[i].AddThemeFontSizeOverride("font_size", DoloTokens.PhoneSizeName);
                    _scoreLabels[i].AddThemeFontSizeOverride("font_size", DoloTokens.PhoneSizeMono);

                    var topRow = new HBoxContainer();
                    topRow.AddThemeConstantOverride("separation", 6);
                    _nameLabels[i].SizeFlagsHorizontal = SizeFlags.ExpandFill;

                    // The badge stays in the tree but hidden — containers skip invisible
                    // children, so it costs no space and SetSeatWind keeps one path.
                    topRow.AddChild(_windBadges[i]);
                    topRow.AddChild(_windLabels[i]);
                    topRow.AddChild(_nameLabels[i]);

                    vbox.AddChild(topRow);
                    vbox.AddChild(_scoreLabels[i]);
                    vbox.AddChild(stick);
                }
                else
                {
                    var windRow = new HBoxContainer();
                    windRow.AddThemeConstantOverride("separation", 6);
                    windRow.AddChild(_windBadges[i]);
                    windRow.AddChild(_windLabels[i]);

                    vbox.AddChild(windRow);
                    vbox.AddChild(_nameLabels[i]);
                    vbox.AddChild(_scoreLabels[i]);
                    vbox.AddChild(stick);
                }

                // Furiten badge — only on the human's own panel (index 0).
                // On the chip it shrinks to a corner dot; there is no room for the word.
                if (i == 0)
                {
                    var badge = touch ? BuildFuritenDot(panel) : BuildFuritenBadge();
                    if (!touch) vbox.AddChild(badge);
                }

                panel.AddChild(vbox);
                AddChild(panel);
            }
        }

        /// <summary>
        /// Set one plate's seat wind. <paramref name="windIndex"/> is 0=East .. 3=North.
        /// Sets both the tile glyph and the letter, so the seat is legible whether or
        /// not the player reads the kanji.
        /// </summary>
        private void SetSeatWind(int panelIndex, int windIndex)
        {
            if (windIndex < 0 || windIndex > 3) return;

            _windLabels[panelIndex].Text = WindLetters[windIndex];
            _windBadges[panelIndex].Texture = GD.Load<Texture2D>(
                $"res://Assets/Tiles/riichi-mahjong-tiles-master/" +
                $"{GameSettings.TileThemeFolder}/{WindTileArt[windIndex]}.svg");
        }

        /// <summary>The nameplate surface: a card tone at plate scale rather than screen scale.</summary>
        private static StyleBoxFlat NameplateStyle()
        {
            var box = DoloStyles.Flat(DoloTokens.Card, DoloTokens.RadiusBoard,
                                      DoloTokens.HairlineMedium, borderWidth: 1);
            box.ContentMarginLeft   = 12;
            box.ContentMarginRight  = 12;
            box.ContentMarginTop    = 8;
            box.ContentMarginBottom = 8;
            box.ShadowColor  = DoloTokens.ShadowButtonColor;
            box.ShadowSize   = 10;
            box.ShadowOffset = new Vector2(0, 4);
            return box;
        }

        /// <summary>
        /// The furiten badge. Pass 10 replaces the red tint with a strike through the
        /// word itself: a 2px rule across the label, which survives a greyscale strip
        /// where the tint does not. The red is kept for players who can see it.
        /// </summary>
        private Control BuildFuritenBadge()
        {
            var holder = new Control
            {
                CustomMinimumSize = new Vector2(0, 18),
                MouseFilter       = MouseFilterEnum.Ignore,
                Visible           = false,
            };

            _furitenLabel = new Label { Text = "FURITEN" };
            _furitenLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _furitenLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _furitenLabel.ThemeTypeVariation  = DoloTheme.MonoSmall;
            _furitenLabel.AddThemeColorOverride("font_color", DoloTokens.FuritenRed);
            holder.AddChild(_furitenLabel);

            _furitenStrike = new ColorRect
            {
                Color       = DoloTokens.FuritenRed,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _furitenStrike.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterLeft);
            _furitenStrike.AnchorRight  = 1f;
            _furitenStrike.OffsetLeft   = 22;
            _furitenStrike.OffsetRight  = -22;
            _furitenStrike.OffsetTop    = -1;
            _furitenStrike.OffsetBottom = 1;
            holder.AddChild(_furitenStrike);

            _furitenBadge = holder;
            return holder;
        }

        /// <summary>
        /// The phone equivalent of the furiten badge: a dot in the chip's corner.
        /// Attached to the plate directly rather than stacked in the column, since a
        /// 96 x 40 chip has no spare row.
        /// </summary>
        private Control BuildFuritenDot(Control plate)
        {
            var dot = new Panel
            {
                MouseFilter = MouseFilterEnum.Ignore,
                Visible     = false,
            };
            dot.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
            dot.OffsetLeft   = -14;
            dot.OffsetTop    = 4;
            dot.OffsetRight  = -4;
            dot.OffsetBottom = 14;

            var dotStyle = DoloStyles.Flat(DoloTokens.FuritenRed, 5, DoloTokens.Ivory, borderWidth: 1);
            dot.AddThemeStyleboxOverride("panel", dotStyle);
            plate.AddChild(dot);

            // SetFuriten writes to the label and the strike as well as the badge, so the
            // phone path keeps both in the tree, hidden, rather than as orphans.
            _furitenBadge  = dot;
            _furitenLabel  = new Label     { Visible = false };
            _furitenStrike = new ColorRect { Visible = false };
            dot.AddChild(_furitenLabel);
            dot.AddChild(_furitenStrike);
            return dot;
        }

        private void BuildCentrePanel()
        {
            // Centre panel: round wind, counters, dora indicator tiles.
            // Wide enough to fit up to 5 dora tile images (5 × 34px = 170 + margins).
            var panel = new PanelContainer();
            var plaque = DoloLayout.PlaqueSize;
            panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            panel.OffsetLeft   = -plaque.X * 0.5f;
            panel.OffsetTop    = -plaque.Y * 0.5f;
            panel.OffsetRight  =  plaque.X * 0.5f;
            panel.OffsetBottom =  plaque.Y * 0.5f;
            panel.MouseFilter  = MouseFilterEnum.Ignore;
            panel.AddThemeStyleboxOverride("panel", CentrePlaqueStyle());

            var vbox = new VBoxContainer();
            vbox.Alignment = BoxContainer.AlignmentMode.Center;
            vbox.AddThemeConstantOverride("separation", 4);

            _roundWindLabel = new Label { Text = "East Round" };
            _counterLabel   = new Label { Text = "" };

            _roundWindLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.HorizontalAlignment   = HorizontalAlignment.Center;
            _roundWindLabel.ThemeTypeVariation  = DoloTheme.PlateName;
            _counterLabel.ThemeTypeVariation    = DoloTheme.Mono;

            // Wall count — table state the design's state list calls for, and the one
            // number that tells a player how much hand is left to play.
            _wallCountLabel = new Label { Text = "" };
            _wallCountLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _wallCountLabel.ThemeTypeVariation  = DoloTheme.MonoSmall;

            // "Dora:" label + row of tile images
            var doraLabel = new Label { Text = "DORA" };
            doraLabel.HorizontalAlignment = HorizontalAlignment.Center;
            doraLabel.ThemeTypeVariation  = DoloTheme.MonoSmall;

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
            vbox.AddChild(_wallCountLabel);
            vbox.AddChild(DoloStyles.HairlineRow());
            vbox.AddChild(doraLabel);
            vbox.AddChild(doraRow);
            panel.AddChild(vbox);
            AddChild(panel);
        }

        /// <summary>The centre plaque: the one raised surface in the middle of the felt.</summary>
        private static StyleBoxFlat CentrePlaqueStyle()
        {
            var box = DoloStyles.Flat(DoloTokens.Card, DoloTokens.RadiusBoard,
                                      DoloTokens.HairlineStrong, borderWidth: 1);
            DoloStyles.SetPadding(box, 12);
            box.ShadowColor  = DoloTokens.ShadowButtonColor;
            box.ShadowSize   = 14;
            box.ShadowOffset = new Vector2(0, 5);
            return box;
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
            // Pass 02 makes all four rivers identical at 178 x 158 — six columns of
            // 28 x 38 tiles — rather than the old 430 x 127 for south/north against
            // 165 x 160 for the sides. Every river now holds the same shape, and each
            // sits far enough from its neighbours that none crosses a diagonal.
            var configs   = DoloLayout.RiverRects;
            int separation = DoloLayout.RiverSeparation;

            for (int i = 0; i < 4; i++)
            {
                // Backdrop — the river rect plus 10px on every side, so an empty
                // river is still a visible place on the table rather than nothing.
                var backdrop = new Panel();
                backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                backdrop.OffsetLeft   = configs[i].l - RiverBackdropInset;
                backdrop.OffsetTop    = configs[i].t - RiverBackdropInset;
                backdrop.OffsetRight  = configs[i].r + RiverBackdropInset;
                backdrop.OffsetBottom = configs[i].b + RiverBackdropInset;
                backdrop.MouseFilter  = MouseFilterEnum.Ignore;
                backdrop.AddThemeStyleboxOverride("panel", RiverBackdropStyle());
                AddChild(backdrop);

                // Outer sizing container — controls the exact river rect
                var outer = new Control();
                outer.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                outer.OffsetLeft   = configs[i].l;
                outer.OffsetTop    = configs[i].t;
                outer.OffsetRight  = configs[i].r;
                outer.OffsetBottom = configs[i].b;
                outer.ClipContents = true;
                outer.MouseFilter  = MouseFilterEnum.Ignore;

                // Flow container fills the outer rect
                var pool = new HFlowContainer();
                pool.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                pool.AddThemeConstantOverride("h_separation", separation);
                pool.AddThemeConstantOverride("v_separation", separation);
                outer.AddChild(pool);
                AddChild(outer);

                _discardPools[i] = pool;
            }
        }

        private const int RiverBackdropInset = 10;

        /// <summary>The recessed pad a river sits on: darker than the felt, hairline edge.</summary>
        private static StyleBoxFlat RiverBackdropStyle()
            => DoloStyles.Flat(new Color(0f, 0f, 0f, 0.20f), DoloTokens.RadiusBoard,
                               DoloTokens.HairlineFaint, borderWidth: 1);

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
            ClearCountdownTile();
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

            // The ring on the discarded tile is the primary cue; this bar is the
            // numeric readout that goes with it.
            UpdateCountdownRing(fraction);
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

            // The calls keep five distinguishable hues (pass 10: colour is decoration,
            // the calls are ranked by position and label) but re-tinted into the Dolo
            // palette - warm, earthy, muted - instead of the old cobalt / magenta / orange
            // that read as pasted in from another application. RON, the win, carries the
            // gold; the melds sit on clay / plum / teal; PASS recedes; RIICHI is oxblood.
            _btnRiichi  = MakeButton("RIICHI",      new Color("#a8463d"));  // oxblood - the commitment
            _btnTsumo   = MakeButton("TSUMO",       new Color("#6f9463"));  // jade - the self-draw win
            _btnRon     = MakeButton("RON",         new Color("#cf9a4b"));  // brass-gold - the prize call
            _btnPon     = MakeButton("PON",         new Color("#b3663f"));  // terracotta
            _btnChi     = MakeButton("CHI",         new Color("#8f6a86"));  // dusty plum
            _btnKan     = MakeButton("KAN",         new Color("#4f7f86"));  // muted teal-slate
            _btnPass    = MakeButton("PASS",        new Color("#4a423b"));  // recessive warm grey
            _btnNext    = MakeButton("NEXT HAND →", new Color("#b98f4f"));  // calm brass - continue
            _btnKyuushu = MakeButton("KYUUSHU",     new Color("#5b6b80"));  // muted steel blue

            _btnRiichi.Pressed   += () => EmitSignal(SignalName.RiichiPressed);
            _btnTsumo.Pressed    += () => EmitSignal(SignalName.TsumoPressed);
            _btnRon.Pressed      += () => EmitSignal(SignalName.RonPressed);
            _btnPon.Pressed      += () => EmitSignal(SignalName.PonPressed);
            _btnChi.Pressed      += () => EmitSignal(SignalName.ChiPressed);
            _btnKan.Pressed      += () => EmitSignal(SignalName.KanPressed);
            _btnPass.Pressed     += () => EmitSignal(SignalName.PassPressed);
            _btnNext.Pressed     += () => EmitSignal(SignalName.NextHandPressed);
            _btnKyuushu.Pressed  += () => EmitSignal(SignalName.KyuushuPressed);

            // Own-turn actions stay in the bottom bar.
            bar.AddChild(_btnRiichi);
            bar.AddChild(_btnTsumo);
            bar.AddChild(_btnKyuushu);
            bar.AddChild(_btnNext);
            AddChild(bar);

            // Calls move into a window over the felt (pass 08), so the decision sits
            // near the tile it is about rather than in a strip at the bottom of the
            // screen. They are ranked ron, pon, chi, kan, pass - highest-commitment
            // first, so the ordering itself says which call matters most.
            _claimWindow = new PanelContainer { Visible = false };
            _claimWindow.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);

            var callSize = DoloLayout.ActionButton;
            float windowWidth  = callSize.X * 5 + 48;
            float windowHeight = callSize.Y + 40;
            _claimWindow.OffsetLeft   = -windowWidth * 0.5f;
            _claimWindow.OffsetRight  =  windowWidth * 0.5f;
            _claimWindow.OffsetTop    = DoloLayout.IsTouch ? 46 : 78;
            _claimWindow.OffsetBottom = _claimWindow.OffsetTop + windowHeight;
            _claimWindow.AddThemeStyleboxOverride("panel", ClaimWindowStyle());

            var callRow = new HBoxContainer();
            callRow.AddThemeConstantOverride("separation", 8);
            callRow.Alignment = BoxContainer.AlignmentMode.Center;

            foreach (var call in new[] { _btnRon, _btnPon, _btnChi, _btnKan, _btnPass })
            {
                call.CustomMinimumSize = new Vector2(callSize.X, callSize.Y);
                callRow.AddChild(call);
            }

            _claimWindow.AddChild(callRow);
            AddChild(_claimWindow);

            HideActionButtons();
            _btnNext.Visible = false;
        }

        private PanelContainer _claimWindow = null!;

        /// <summary>The claim window sits over the felt, so it needs a real edge and lift.</summary>
        private static StyleBoxFlat ClaimWindowStyle()
        {
            var box = DoloStyles.Flat(DoloTokens.Card, DoloTokens.RadiusCard,
                                      DoloTokens.HairlineStrong, borderWidth: 2);
            DoloStyles.SetPadding(box, 16);
            box.ShadowColor  = DoloTokens.ShadowCardColor;
            box.ShadowSize   = 28;
            box.ShadowOffset = new Vector2(0, 10);
            return box;
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

                col.AddChild(node);

                if (remaining != null && remaining.TryGetValue(w.TileId, out int left))
                {
                    // Pass 10: a dead wait is no longer greyed out — greying reads as
                    // ordinary dimming. The face is dropped for a brass outline and the
                    // count states the position outright. Furiten adds the 135° hatch.
                    node.SetWaitDisplay(left, _isFuriten);

                    var countLabel = new Label { Text = $"{left} left" };
                    countLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    countLabel.ThemeTypeVariation  = DoloTheme.MonoSmall;
                    countLabel.AddThemeColorOverride("font_color",
                        left <= 0 ? DoloTokens.Brass : DoloTokens.BodyText);
                    col.AddChild(countLabel);
                }
                else if (_isFuriten)
                {
                    node.SetWaitDisplay(1, furiten: true);
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

        private void BuildWinCallOverlay()
        {
            // Full-screen dark backdrop — sits above game, below scoring panel
            _winCallBackdrop = new ColorRect();
            _winCallBackdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _winCallBackdrop.Color       = new Color(0f, 0f, 0f, 0.55f);
            _winCallBackdrop.MouseFilter = MouseFilterEnum.Stop;
            _winCallBackdrop.Visible     = false;

            // Centre container
            var centre = new VBoxContainer();
            centre.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            centre.OffsetLeft   = -350;
            centre.OffsetTop    = -120;
            centre.OffsetRight  =  350;
            centre.OffsetBottom =  120;
            centre.AddThemeConstantOverride("separation", 14);
            centre.Alignment = BoxContainer.AlignmentMode.Center;

            // Big call text ("RON!" / "TSUMO!")
            _winCallText = new Label();
            _winCallText.HorizontalAlignment = HorizontalAlignment.Center;
            _winCallText.AddThemeFontSizeOverride("font_size", 88);

            var textSettings = new LabelSettings();
            textSettings.OutlineSize  = 6;
            textSettings.OutlineColor = new Color(1f, 1f, 1f, 1f);
            textSettings.ShadowSize   = 4;
            textSettings.ShadowColor  = new Color(0f, 0f, 0f, 0.70f);
            textSettings.ShadowOffset = new Vector2(3, 3);
            _winCallText.LabelSettings = textSettings;

            // Player name subtitle
            _winCallName = new Label();
            _winCallName.HorizontalAlignment = HorizontalAlignment.Center;
            _winCallName.AddThemeFontSizeOverride("font_size", 24);
            _winCallName.AddThemeColorOverride("font_color", new Color(0.90f, 0.90f, 0.90f, 1f));

            var nameSettings = new LabelSettings();
            nameSettings.OutlineSize  = 3;
            nameSettings.OutlineColor = new Color(0f, 0f, 0f, 0.80f);
            _winCallName.LabelSettings = nameSettings;

            centre.AddChild(_winCallText);
            centre.AddChild(_winCallName);

            _winCallBackdrop.AddChild(centre);
            AddChild(_winCallBackdrop);
        }

        /// <summary>
        /// Show a brief "RON!" or "TSUMO!" slam-in animation, then call
        /// <paramref name="onComplete"/> once the animation finishes so the
        /// caller can show the scoring panel.
        /// </summary>
        public void ShowWinCall(bool isTsumo, string playerName, Action onComplete)
        {
            Color accentColor = isTsumo
                ? new Color(1.00f, 0.84f, 0.00f, 1f)   // gold    — tsumo
                : new Color(0.90f, 0.15f, 0.15f, 1f);  // crimson — ron
            ShowCallOverlay(
                callText:      isTsumo ? "TSUMO!" : "RON!",
                accentColor:   accentColor,
                playerName:    playerName,
                backdropAlpha: 0.55f,
                holdSec:       1.05f,
                blockInput:    true,
                onComplete:    onComplete);
        }

        /// <summary>
        /// Show a fire-and-forget "RIICHI!" slam-in overlay.
        /// Non-blocking — input passes through and the game continues underneath.
        /// </summary>
        public void ShowRiichiCall(string playerName)
        {
            ShowCallOverlay(
                callText:      "RIICHI!",
                accentColor:   new Color(0.62f, 0.28f, 0.92f, 1f),   // violet
                playerName:    playerName,
                backdropAlpha: 0.38f,
                holdSec:       0.70f,
                blockInput:    false,
                onComplete:    null);
        }

        /// <summary>
        /// Core slam-in overlay shared by win calls and riichi declarations.
        /// </summary>
        private void ShowCallOverlay(
            string  callText,
            Color   accentColor,
            string  playerName,
            float   backdropAlpha,
            float   holdSec,
            bool    blockInput,
            Action? onComplete)
        {
            // Kill any in-flight animation so back-to-back calls don't stack
            _callOverlayTween?.Kill();

            // ---- Text & colour ----
            _winCallText.Text = callText;
            _winCallText.LabelSettings!.FontColor = accentColor;
            _winCallName.Text = playerName;

            // ---- Reset state ----
            _winCallBackdrop.Color       = new Color(0f, 0f, 0f, backdropAlpha);
            _winCallBackdrop.MouseFilter = blockInput
                ? MouseFilterEnum.Stop
                : MouseFilterEnum.Ignore;
            _winCallBackdrop.Visible  = true;
            _winCallBackdrop.Modulate = new Color(1f, 1f, 1f, 0f);  // start transparent

            // Pivot at visual centre — size may not be finalised yet so use a
            // fixed estimate for the 88-px font; Godot still scales from the correct
            // origin because the label is centred horizontally inside its container.
            _winCallText.PivotOffset = new Vector2(_winCallText.Size.X * 0.5f, 60f);
            _winCallText.Scale    = new Vector2(2.0f, 2.0f);
            _winCallText.Modulate = new Color(1f, 1f, 1f, 0f);

            _winCallName.Modulate = new Color(1f, 1f, 1f, 0f);

            // ---- Tween sequence ----
            _callOverlayTween = CreateTween();

            // 1. Backdrop fades in
            _callOverlayTween.TweenProperty(_winCallBackdrop, "modulate:a", 1.0f, 0.12f)
                 .SetTrans(Tween.TransitionType.Linear);

            // 2. Call text slams in (scale 2→1, fade 0→1) — simultaneous with backdrop
            _callOverlayTween.Parallel()
                 .TweenProperty(_winCallText, "scale", Vector2.One, 0.18f)
                 .SetTrans(Tween.TransitionType.Quart)
                 .SetEase(Tween.EaseType.Out);
            _callOverlayTween.Parallel()
                 .TweenProperty(_winCallText, "modulate:a", 1.0f, 0.12f)
                 .SetTrans(Tween.TransitionType.Linear);

            // 3. Player name fades in with a slight delay
            _callOverlayTween.TweenProperty(_winCallName, "modulate:a", 1.0f, 0.18f)
                 .SetTrans(Tween.TransitionType.Linear);

            // 4. Hold
            _callOverlayTween.TweenInterval(holdSec);

            // 5. Everything fades out together
            _callOverlayTween.TweenProperty(_winCallBackdrop, "modulate:a", 0.0f, 0.28f)
                 .SetTrans(Tween.TransitionType.Linear);

            // 6. Cleanup + optional callback
            _callOverlayTween.TweenCallback(Callable.From(() =>
            {
                _winCallBackdrop.Visible = false;
                onComplete?.Invoke();
            }));
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
            int             dealerSeat,
            IReadOnlyList<Tile>? winningHand = null)
        {
            // ── Title ──
            SetScoringVariant(isTsumo ? ScoringVariant.Tsumo : ScoringVariant.Ron);
            _scoringTitle.Text = $"{winnerName} wins by {(isTsumo ? "tsumo" : "ron")}".ToUpperInvariant();
            _scoringTitle.AddThemeColorOverride("font_color", DoloTokens.Ivory);

            // ── Yaku rows ──
            foreach (var child in _scoringYakuRows.GetChildren()) child.QueueFree();

            foreach (var y in yaku.Yaku)
            {
                string fanText = y.IsYakuman ? "Yakuman" : $"{y.Fan} han";
                Color  fanCol  = y.IsYakuman ? DoloTokens.DoraGold : DoloTokens.Ivory;
                _scoringYakuRows.AddChild(MakeScoringRow(
                    $"{y.Name}  ({y.NameJP})", fanText, fanCol));
            }
            if (ctx.DoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Dora", $"{ctx.DoraCount} han", DoloTokens.DoraGold, goldWedge: true));
            if (ctx.RedDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Red 5 (aka)", $"{ctx.RedDoraCount} han", DoloTokens.DoraGold, goldWedge: true));
            if (ctx.IsRiichi && ctx.UraDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Ura dora", $"{ctx.UraDoraCount} han", DoloTokens.DoraGold, goldWedge: true));

            // ── Han / Fu / Limit summary ──
            bool hasLimit = score.Limit != HandLimit.None;
            _scoringHanFuLabel.Text = hasLimit
                ? $"{score.TotalFan} han"
                : $"{score.TotalFan} han   {score.Fu.Total} fu";
            _scoringLimitLabel.Text = hasLimit ? ScoreCalculator.LimitName(score.Limit) : "";

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
                        $"−{amount:N0}", DoloTokens.Negative));
                }
            }
            else
            {
                _scoringPayRows.AddChild(MakeScoringRow(
                    $"  {discarderName} pays:",
                    $"−{score.RonPayment:N0}", DoloTokens.Negative));
            }
            if (score.CounterBonus > 0)
                _scoringPayRows.AddChild(MakeScoringRow(
                    "Honba bonus", $"+{score.CounterBonus:N0}", DoloTokens.BodyText));
            if (score.RiichiBetsWon > 0)
                _scoringPayRows.AddChild(MakeScoringRow(
                    "Riichi sticks", $"+{score.RiichiBetsWon:N0}", DoloTokens.DoraGold));

            // ── Payout: who pays, the arithmetic, the total ──
            // The sum is shown the way a player would work it out, so a number that
            // just changed their score can be checked rather than trusted.
            string payerLine = isTsumo
                ? "Tsumo — paid by all three"
                : $"Ron — paid by {discarderName}";

            var parts = new List<string>();
            if (isTsumo)
            {
                if (score.IsDealer)
                    parts.Add($"{score.TsumoPaymentOther:N0} x 3");
                else
                    parts.Add($"{score.TsumoPaymentEast:N0} + {score.TsumoPaymentOther:N0} x 2");
            }
            else
            {
                parts.Add($"{score.RonPayment:N0}");
            }
            if (score.CounterBonus > 0) parts.Add($"{score.CounterBonus:N0} honba");
            if (score.RiichiBetsWon > 0) parts.Add($"{score.RiichiBetsWon:N0} sticks");

            SetPayout(payerLine, string.Join("  +  ", parts),
                      $"+{score.TotalPointsWon:N0}", DoloTokens.DoraGold);

            SetWinningHand(winningHand, ctx.WinningTile);

            FillStandings(allPlayerNames, allPlayerPoints, dealerSeat, winnerSeat);

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
            int[]    yakuFans,
            bool[]   yakuIsYakuman,
            int      han,
            int      fu,
            int      doraCount,
            int      uraDoraCount,
            int      redDoraCount,
            int      totalPointsWon)
        {
            // ── Title ──
            SetScoringVariant(isTsumo ? ScoringVariant.Tsumo : ScoringVariant.Ron);
            _scoringTitle.Text = $"{winnerName} wins by {(isTsumo ? "tsumo" : "ron")}".ToUpperInvariant();
            _scoringTitle.AddThemeColorOverride("font_color", DoloTokens.Ivory);

            // ── Yaku rows (with fan counts) ──
            foreach (var child in _scoringYakuRows.GetChildren()) child.QueueFree();
            for (int i = 0; i < yakuNames.Length; i++)
            {
                bool isYakuman = i < yakuIsYakuman.Length && yakuIsYakuman[i];
                int  fan       = i < yakuFans.Length ? yakuFans[i] : 0;
                string fanText = isYakuman ? "Yakuman" : (fan > 0 ? $"{fan} han" : "");
                Color  fanCol  = isYakuman ? DoloTokens.DoraGold : DoloTokens.Ivory;
                _scoringYakuRows.AddChild(MakeScoringRow(yakuNames[i], fanText, fanCol));
            }
            if (doraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Dora", $"{doraCount} han", DoloTokens.DoraGold, goldWedge: true));
            if (redDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Red 5 (aka)", $"{redDoraCount} han", DoloTokens.DoraGold, goldWedge: true));
            if (uraDoraCount > 0)
                _scoringYakuRows.AddChild(MakeScoringRow(
                    "Ura dora", $"{uraDoraCount} han", DoloTokens.DoraGold, goldWedge: true));

            // ── Han / Fu / Limit ──
            // Derive limit from total han (mirrors ScoreCalculator.ClassifyLimit)
            bool isYakumanHand = yakuIsYakuman.Any(y => y);
            string limitName = isYakumanHand ? (han >= 26 ? "Double Yakuman" : "Yakuman")
                : han switch { >= 11 => "Sanbaiman", >= 8 => "Baiman", >= 6 => "Haneman", >= 5 => "Mangan", _ => "" };
            bool hasLimit = !string.IsNullOrEmpty(limitName);
            _scoringHanFuLabel.Text = hasLimit ? $"{han} han" : $"{han} han   {fu} fu";
            _scoringLimitLabel.Text = hasLimit ? limitName : "";

            // ── Payout ──
            // The server sends the settled total rather than its parts, so the
            // arithmetic here is what the client can honestly show: the sum itself.
            foreach (var child in _scoringPayRows.GetChildren()) child.QueueFree();
            if (!isTsumo && !string.IsNullOrEmpty(payerName) && totalPointsWon > 0)
                _scoringPayRows.AddChild(MakeScoringRow(
                    $"{payerName} pays", $"−{totalPointsWon:N0}", DoloTokens.Negative));

            SetPayout(
                isTsumo ? "Tsumo — paid by all three"
                        : $"Ron — paid by {payerName}",
                $"{han} han" + (hasLimit ? $" · {limitName}" : $" · {fu} fu"),
                totalPointsWon > 0 ? $"+{totalPointsWon:N0}" : "",
                DoloTokens.DoraGold);

            // No hand data over the wire, so this layout has no tile row to show.
            SetWinningHand(null, null);

            FillStandings(allNames, allPoints, dealerSeat, winnerSeat);

            _scoringBackdrop.Visible = true;
        }

        /// <summary>Hide the scoring overlay and clear dynamic rows for the next hand.</summary>
        public void HideScoringPanel()
        {
            _scoringBackdrop.Visible = false;
            DoloWidgets.SetIconButtonText(_scoringNextBtn, "Next Hand");  // Restore from any "Play Again" state
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
                DoloWidgets.SetIconButtonText(_scoringNextBtn, "Play Again");
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

            // Yaku reference button — top-left, just below the menu button
            var yakuBtn = new Button { Text = "?" };
            yakuBtn.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            yakuBtn.OffsetLeft   = 8;
            yakuBtn.OffsetTop    = 50;
            yakuBtn.OffsetRight  = 48;
            yakuBtn.OffsetBottom = 84;
            yakuBtn.TooltipText  = "Yaku Reference";
            yakuBtn.AddThemeFontSizeOverride("font_size", 18);

            var yakuStyle = new StyleBoxFlat();
            yakuStyle.BgColor = new Color(0.18f, 0.28f, 0.50f);
            yakuStyle.SetCornerRadiusAll(6);
            yakuBtn.AddThemeStyleboxOverride("normal", yakuStyle);
            var yakuHover = (StyleBoxFlat)yakuStyle.Duplicate();
            yakuHover.BgColor = yakuStyle.BgColor.Lightened(0.18f);
            yakuBtn.AddThemeStyleboxOverride("hover", yakuHover);
            yakuBtn.AddThemeColorOverride("font_color", Colors.White);

            yakuBtn.Pressed += () => EmitSignal(SignalName.YakuReferencePressed);
            AddChild(yakuBtn);
        }

        private static Button MakeButton(string text, Color color)
        {
            var btn  = new Button { Text = text };
            btn.CustomMinimumSize = new Vector2(90, 44);
            btn.AddThemeFontSizeOverride("font_size", 14);

            // A face darker than the fill gives every call a defined edge on the felt;
            // the ink flips to dark on the lighter (gold / jade) hues so the label stays
            // legible without a white that would shout against the muted palette.
            bool lightFace = color.Luminance > 0.42f;
            Color ink      = lightFace ? new Color("#1d1610") : DoloTokens.Ivory;

            StyleBoxFlat Face(Color fill)
            {
                var box = DoloStyles.Flat(fill, DoloTokens.RadiusInset,
                                          color.Darkened(0.35f), borderWidth: 1);
                box.ContentMarginLeft = box.ContentMarginRight = 14;
                box.ContentMarginTop  = box.ContentMarginBottom = 10;
                return box;
            }

            btn.AddThemeStyleboxOverride("normal",   Face(color));
            btn.AddThemeStyleboxOverride("hover",    Face(color.Lightened(0.12f)));
            btn.AddThemeStyleboxOverride("pressed",  Face(color.Darkened(0.14f)));
            btn.AddThemeStyleboxOverride("disabled", Face(color.Darkened(0.45f)));

            btn.AddThemeColorOverride("font_color",         ink);
            btn.AddThemeColorOverride("font_hover_color",   ink);
            btn.AddThemeColorOverride("font_pressed_color", ink);
            return btn;
        }
    }
}
