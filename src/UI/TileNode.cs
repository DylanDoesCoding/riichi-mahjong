// =============================================================================
// TileNode.cs
// A single visual tile — a clickable button that displays one mahjong tile
// using the real SVG-exported PNG artwork from the riichi-mahjong-tiles pack.
//
// Two themes are supported (controlled by GameSettings.UseBlackTiles):
//   Regular — white/ivory tiles   (Export/Regular/*.png)
//   Black   — dark/black tiles    (Export/Black/*.png)
//
// Selected state: semi-transparent golden overlay drawn on top of the tile.
// Face-down state: Back.png from the active theme.
// Lifted state (drawn tile): tile is shifted upward slightly via Position.
// =============================================================================

using Godot;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public partial class TileNode : Button
    {
        // ---- Tile data -------------------------------------------------------

        public Tile? TileData  { get; private set; }
        public bool  FaceDown  { get; private set; } = false;
        public bool  Selected  { get; private set; } = false;
        public bool  Lifted    { get; private set; } = false;

        // ---- Sizing ----------------------------------------------------------

        public const int TileWidth  = 66;
        public const int TileHeight = 88;
        public const int LiftAmount = 10;

        // ---- Child nodes -----------------------------------------------------

        private Panel       _tileBody            = null!;  // White tile body shown behind artwork
        private TextureRect _textureRect         = null!;
        private Label       _valueLabel          = null!;  // Top-left number (1–9 for Man/Pin/Sou)
        private LabelSettings _valueLabelSettings = null!;
        private Panel       _selectionOverlay    = null!;
        private Panel       _riichiHighlight     = null!;  // Green glow: valid riichi discard
        private Panel       _riichiDimOverlay    = null!;  // Dark dim: non-candidate in riichi mode
        private Panel       _claimHighlight      = null!;  // Orange pulse: tile is claimable (pon/chi/ron)
        private Tween?      _claimTween          = null;
        private Panel       _doraGlow            = null!;  // Gold edge: tile is a live dora (or red 5)
        private DoraCornerWedge _doraWedge       = null!;  // Gold corner: the greyscale-safe half of the dora cue
        private DashedRing  _matchRing           = null!;  // Dashed edge: matches the hovered hand tile
        private HatchOverlay _furitenHatch       = null!;  // 135° hatch: this wait is barred by furiten
        private Panel       _deadWaitOutline     = null!;  // 2px brass outline replacing a dropped face
        private Label       _waitCountLabel      = null!;  // "3 left" / "0 left" under a wait tile

        /// <summary>
        /// The tile pack's back art is more saturated than this felt wants, so it is
        /// knocked back rather than used at full strength.
        /// </summary>
        private static readonly Color BackModulate = new(0.62f, 0.52f, 0.46f);

        // ---- Asset base path -------------------------------------------------
        // Use the SVG source files (Regular/ and Black/) rather than the PNG exports
        // (Export/Regular/) — the SVGs include the full tile body with background.

        private const string TileBasePath =
            "res://Assets/Tiles/riichi-mahjong-tiles-master";

        // ---- Signals ---------------------------------------------------------

        [Signal] public delegate void TileClickedEventHandler(TileNode tile);

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            if (CustomMinimumSize == Vector2.Zero)
                CustomMinimumSize = new Vector2(TileWidth, TileHeight);

            SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            SizeFlagsVertical   = SizeFlags.ShrinkCenter;
            Flat = true;

            // ---- Tile body panel (face-up background) ----
            // Provides a solid white/ivory background that the SVG artwork renders on top of.
            // Ensures strong contrast against the green table regardless of SVG transparency.
            _tileBody = new Panel();
            _tileBody.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _tileBody.MouseFilter = MouseFilterEnum.Ignore;
            _tileBody.Visible     = false;
            var bodyStyle = new StyleBoxFlat();
            bodyStyle.BgColor                = new Color(0.97f, 0.96f, 0.90f, 1f);  // Ivory/cream
            bodyStyle.BorderColor            = new Color(0.55f, 0.50f, 0.40f, 1f);  // Warm grey border
            bodyStyle.BorderWidthTop    = bodyStyle.BorderWidthBottom =
            bodyStyle.BorderWidthLeft   = bodyStyle.BorderWidthRight  = 1;
            bodyStyle.CornerRadiusTopLeft    = bodyStyle.CornerRadiusTopRight    =
            bodyStyle.CornerRadiusBottomLeft = bodyStyle.CornerRadiusBottomRight = 3;
            _tileBody.AddThemeStyleboxOverride("panel", bodyStyle);
            AddChild(_tileBody);

            // Tile image — fills the button, maintains aspect ratio
            _textureRect = new TextureRect();
            _textureRect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _textureRect.ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize;
            _textureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            _textureRect.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_textureRect);

            // Top-left value label (1–9 for Man/Pin/Sou; hidden for honours and face-down)
            _valueLabel = new Label();
            _valueLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            _valueLabel.OffsetLeft   = 2;
            _valueLabel.OffsetTop    = 1;
            _valueLabel.OffsetRight  = 18;
            _valueLabel.OffsetBottom = 15;
            _valueLabel.MouseFilter  = MouseFilterEnum.Ignore;
            _valueLabel.Visible      = false;

            _valueLabelSettings = new LabelSettings();
            _valueLabelSettings.FontSize     = 12;
            _valueLabelSettings.OutlineSize  = 2;
            _valueLabelSettings.OutlineColor = new Color(1f, 1f, 1f, 1f);
            _valueLabel.LabelSettings = _valueLabelSettings;

            AddChild(_valueLabel);

            // Golden selection overlay (on top of the tile image)
            _selectionOverlay = new Panel();
            _selectionOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _selectionOverlay.MouseFilter = MouseFilterEnum.Ignore;
            _selectionOverlay.Visible     = false;
            var selStyle = new StyleBoxFlat();
            selStyle.BgColor      = new Color(1f, 0.85f, 0.20f, 0.28f);
            selStyle.BorderColor  = new Color(1f, 0.85f, 0.20f, 1f);
            selStyle.BorderWidthTop    = 3;
            selStyle.BorderWidthBottom = 3;
            selStyle.BorderWidthLeft   = 3;
            selStyle.BorderWidthRight  = 3;
            selStyle.CornerRadiusTopLeft     = 4;
            selStyle.CornerRadiusTopRight    = 4;
            selStyle.CornerRadiusBottomLeft  = 4;
            selStyle.CornerRadiusBottomRight = 4;
            _selectionOverlay.AddThemeStyleboxOverride("panel", selStyle);
            AddChild(_selectionOverlay);

            // Green glow overlay (riichi candidate)
            _riichiHighlight = new Panel();
            _riichiHighlight.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _riichiHighlight.MouseFilter = MouseFilterEnum.Ignore;
            _riichiHighlight.Visible     = false;
            var riichiStyle = new StyleBoxFlat();
            riichiStyle.BgColor     = new Color(0.10f, 0.90f, 0.30f, 0.22f);
            riichiStyle.BorderColor = new Color(0.10f, 0.85f, 0.25f, 1f);
            riichiStyle.BorderWidthTop    = riichiStyle.BorderWidthBottom =
            riichiStyle.BorderWidthLeft   = riichiStyle.BorderWidthRight  = 3;
            riichiStyle.CornerRadiusTopLeft    = riichiStyle.CornerRadiusTopRight    =
            riichiStyle.CornerRadiusBottomLeft = riichiStyle.CornerRadiusBottomRight = 4;
            _riichiHighlight.AddThemeStyleboxOverride("panel", riichiStyle);
            AddChild(_riichiHighlight);

            // Dark dim overlay (non-candidate while riichi mode is active)
            _riichiDimOverlay = new Panel();
            _riichiDimOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _riichiDimOverlay.MouseFilter = MouseFilterEnum.Ignore;
            _riichiDimOverlay.Visible     = false;
            var dimStyle = new StyleBoxFlat();
            dimStyle.BgColor = new Color(0f, 0f, 0f, 0.45f);
            _riichiDimOverlay.AddThemeStyleboxOverride("panel", dimStyle);
            AddChild(_riichiDimOverlay);

            // Orange pulse overlay — shown on the pending discard tile during a claim window
            _claimHighlight = new Panel();
            _claimHighlight.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _claimHighlight.MouseFilter = MouseFilterEnum.Ignore;
            _claimHighlight.Visible     = false;
            var claimStyle = new StyleBoxFlat();
            claimStyle.BgColor     = new Color(1f, 0.55f, 0.05f, 0.18f);
            claimStyle.BorderColor = new Color(1f, 0.60f, 0.10f, 1f);
            claimStyle.BorderWidthTop    = claimStyle.BorderWidthBottom =
            claimStyle.BorderWidthLeft   = claimStyle.BorderWidthRight  = 3;
            claimStyle.CornerRadiusTopLeft    = claimStyle.CornerRadiusTopRight    =
            claimStyle.CornerRadiusBottomLeft = claimStyle.CornerRadiusBottomRight = 4;
            _claimHighlight.AddThemeStyleboxOverride("panel", claimStyle);
            AddChild(_claimHighlight);

            // Gold edge — this tile is a live dora (indicator+1) or a red five.
            // Border-only so it stays readable under selection/claim overlays.
            _doraGlow = new Panel();
            _doraGlow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _doraGlow.MouseFilter = MouseFilterEnum.Ignore;
            _doraGlow.Visible     = false;
            var doraStyle = new StyleBoxFlat();
            doraStyle.BgColor     = new Color(1f, 0.80f, 0.20f, 0.10f);
            doraStyle.BorderColor = new Color(1f, 0.78f, 0.15f, 0.95f);
            doraStyle.BorderWidthTop    = doraStyle.BorderWidthBottom =
            doraStyle.BorderWidthLeft   = doraStyle.BorderWidthRight  = 2;
            doraStyle.CornerRadiusTopLeft    = doraStyle.CornerRadiusTopRight    =
            doraStyle.CornerRadiusBottomLeft = doraStyle.CornerRadiusBottomRight = 4;
            _doraGlow.AddThemeStyleboxOverride("panel", doraStyle);
            AddChild(_doraGlow);

            // Gold corner wedge — the half of the dora cue that survives greyscale.
            _doraWedge = new DoraCornerWedge { Visible = false };
            AddChild(_doraWedge);

            // Dashed edge — this tile matches the hand tile currently hovered.
            // Dashed rather than a solid cyan fill so the pattern, not the hue,
            // is what distinguishes it (pass 10).
            _matchRing = new DashedRing { Visible = false };
            AddChild(_matchRing);

            // 135° hatch — this wait tile is barred by furiten.
            _furitenHatch = new HatchOverlay { Visible = false };
            AddChild(_furitenHatch);

            // Dead wait — the face is dropped and the tile becomes a brass outline.
            _deadWaitOutline = new Panel();
            _deadWaitOutline.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _deadWaitOutline.MouseFilter = MouseFilterEnum.Ignore;
            _deadWaitOutline.Visible     = false;
            var deadStyle = DoloStyles.Flat(DoloTokens.DeepField, DoloTokens.RadiusTile,
                                            DoloTokens.Brass, borderWidth: 2);
            _deadWaitOutline.AddThemeStyleboxOverride("panel", deadStyle);
            AddChild(_deadWaitOutline);

            // "3 left" / "0 left" — the count that makes a dead wait unambiguous.
            _waitCountLabel = new Label();
            _waitCountLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            _waitCountLabel.OffsetTop    = -16;
            _waitCountLabel.OffsetBottom = 0;
            _waitCountLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _waitCountLabel.MouseFilter = MouseFilterEnum.Ignore;
            _waitCountLabel.Visible     = false;
            var countSettings = new LabelSettings
            {
                Font         = DoloTheme.MonoFont,
                FontSize     = DoloTokens.SizeMonoSmall,
                FontColor    = DoloTokens.Ivory,
                OutlineSize  = 3,
                OutlineColor = new Color(0f, 0f, 0f, 0.85f),
            };
            _waitCountLabel.LabelSettings = countSettings;
            AddChild(_waitCountLabel);

            Pressed += OnPressed;
            Resized += ApplySideways;
            Refresh();
        }

        // =====================================================================
        // Public API
        // =====================================================================

        public void SetTile(Tile tile, bool faceDown = false)
        {
            TileData = tile;
            FaceDown = faceDown;
            Selected = false;
            Refresh();
        }

        public void SetFaceDown(bool faceDown)
        {
            FaceDown = faceDown;
            Refresh();
        }

        public void SetSelected(bool selected)
        {
            Selected = selected;
            Refresh();
            Position = selected
                ? new Vector2(Position.X, -LiftAmount)
                : new Vector2(Position.X, 0);
        }

        public void SetLifted(bool lifted)
        {
            Lifted = lifted;
            if (!Selected)
                Position = lifted
                    ? new Vector2(Position.X, -(LiftAmount / 2))
                    : new Vector2(Position.X, 0);
        }

        public void SetInteractive(bool interactive)
        {
            Disabled    = !interactive;
            MouseFilter = interactive ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }

        // =====================================================================
        // Visual refresh
        // =====================================================================

        private void Refresh()
        {
            if (_textureRect == null) return;

            if (FaceDown || TileData == null)
            {
                // Face-down: the real Back.svg from the pack, knocked back so it sits
                // on the felt as a tile rather than glowing against it.
                _textureRect.Texture = GD.Load<Texture2D>(
                    $"{TileBasePath}/{GameSettings.TileThemeFolder}/Back.svg");
                _textureRect.SelfModulate = BackModulate;

                _tileBody.Visible         = false;
                _textureRect.Visible      = true;
                _selectionOverlay.Visible = false;
                _valueLabel.Visible       = false;
            }
            else
            {
                // Face-up: load SVG artwork on top of tile body
                string fileName = GetTileFileName(TileData);
                string path = $"{TileBasePath}/{GameSettings.TileThemeFolder}/{fileName}.svg";
                _textureRect.Texture      = GD.Load<Texture2D>(path);
                _textureRect.SelfModulate = Colors.White;

                // Tile body colour matches the active theme
                var bodyStyle = new StyleBoxFlat();
                bool black = GameSettings.UseBlackTiles;
                bodyStyle.BgColor     = black
                    ? new Color(0.12f, 0.12f, 0.16f, 1f)   // Dark (Black theme)
                    : new Color(0.97f, 0.96f, 0.90f, 1f);  // Ivory (Regular theme)
                bodyStyle.BorderColor = black
                    ? new Color(0.30f, 0.30f, 0.40f, 1f)
                    : new Color(0.55f, 0.50f, 0.40f, 1f);
                bodyStyle.BorderWidthTop    = bodyStyle.BorderWidthBottom =
                bodyStyle.BorderWidthLeft   = bodyStyle.BorderWidthRight  = 1;
                bodyStyle.CornerRadiusTopLeft    = bodyStyle.CornerRadiusTopRight    =
                bodyStyle.CornerRadiusBottomLeft = bodyStyle.CornerRadiusBottomRight = 3;
                _tileBody.AddThemeStyleboxOverride("panel", bodyStyle);

                _tileBody.Visible         = true;
                _textureRect.Visible      = true;
                _selectionOverlay.Visible = Selected;

                // Value label — only for numbered suits (Man/Pin/Sou), not honours
                bool isNumbered = TileData.Suit is TileSuit.Man or TileSuit.Pin or TileSuit.Sou;
                _valueLabel.Visible = isNumbered;
                if (isNumbered)
                {
                    _valueLabel.Text = TileData.Value.ToString();
                    _valueLabelSettings.FontColor = new Color(0.05f, 0.05f, 0.05f, 1f);
                }
            }
        }

        // =====================================================================
        // Tile → filename mapping
        // =====================================================================

        private static string GetTileFileName(Tile tile) => tile.Suit switch
        {
            // Red dora fives use dedicated art files (Man5-Dora.png, etc.)
            TileSuit.Man    => (tile.IsRedDora && tile.Value == 5) ? "Man5-Dora" : $"Man{tile.Value}",
            TileSuit.Pin    => (tile.IsRedDora && tile.Value == 5) ? "Pin5-Dora" : $"Pin{tile.Value}",
            TileSuit.Sou    => (tile.IsRedDora && tile.Value == 5) ? "Sou5-Dora" : $"Sou{tile.Value}",
            TileSuit.Wind   => (WindDirection)tile.Value switch
            {
                WindDirection.East  => "Ton",
                WindDirection.South => "Nan",
                WindDirection.West  => "Shaa",
                WindDirection.North => "Pei",
                _                   => "Back",
            },
            TileSuit.Dragon => (DragonType)tile.Value switch
            {
                DragonType.White => "Haku",
                DragonType.Green => "Hatsu",
                DragonType.Red   => "Chun",
                _                => "Back",
            },
            _ => "Back",
        };

        /// <summary>
        /// Set the riichi highlight state for this tile.
        /// <paramref name="isCandidate"/> — true if this tile is a valid riichi discard.
        /// <paramref name="modeActive"/> — true while riichi selection mode is on.
        /// </summary>
        public void SetRiichiState(bool isCandidate, bool modeActive)
        {
            if (_riichiHighlight == null) return;
            _riichiHighlight.Visible  = modeActive && isCandidate;
            _riichiDimOverlay.Visible = modeActive && !isCandidate;
        }

        /// <summary>
        /// Show or hide the orange claim-highlight pulse on this tile.
        /// Used to mark the pending discard tile during a Pon / Chi / Ron window.
        /// </summary>
        public void SetClaimHighlight(bool active)
        {
            if (_claimHighlight == null) return;
            _claimHighlight.Visible = active;

            _claimTween?.Kill();
            _claimTween = null;

            if (active)
            {
                // Pulse the border alpha between 40 % and 100 % so it draws the eye
                _claimHighlight.SelfModulate = new Color(1f, 1f, 1f, 1f);
                _claimTween = CreateTween();
                _claimTween.SetLoops();
                _claimTween.TweenProperty(_claimHighlight, "self_modulate:a", 0.35f, 0.45f);
                _claimTween.TweenProperty(_claimHighlight, "self_modulate:a", 1.00f, 0.45f);
            }
        }

        /// <summary>
        /// Marks this tile as a live dora (or red five): a gold border for players who
        /// can see the hue, and a solid corner wedge for those who cannot.
        /// </summary>
        public void SetDoraGlow(bool active)
        {
            if (_doraGlow == null) return;
            bool show = active && !FaceDown;
            _doraGlow.Visible  = show;
            _doraWedge.Visible = show;
            if (show) _doraWedge.QueueRedraw();
        }

        /// <summary>
        /// Marks this tile as matching the hovered hand tile. Drawn as a dashed ring
        /// so the cue is a pattern first and a colour second.
        /// </summary>
        public void SetMatchHighlight(bool active)
        {
            if (_matchRing == null) return;
            _matchRing.Visible = active && !FaceDown;
            if (_matchRing.Visible) _matchRing.QueueRedraw();
        }

        /// <summary>
        /// Display this tile as one of the player's waits.
        /// <paramref name="remaining"/> is how many copies are still unseen; a dead wait
        /// (zero left) drops the face for a brass outline so it cannot be mistaken for
        /// ordinary dimming. <paramref name="furiten"/> adds the 135° hatch.
        /// </summary>
        public void SetWaitDisplay(int remaining, bool furiten, bool showCount = false)
        {
            if (_waitCountLabel == null) return;

            bool dead = remaining <= 0;

            // On a small wait tile the count reads better underneath than inside, so
            // the caller can place it and leave the in-tile label off.
            _waitCountLabel.Text    = $"{remaining} left";
            _waitCountLabel.Visible = showCount;

            // A dead wait loses its face entirely — the count and the outline carry it.
            _deadWaitOutline.Visible = dead;
            _tileBody.Visible        = !dead;
            _textureRect.Visible     = !dead;
            _valueLabel.Visible      = !dead && TileData != null
                                       && TileData.Suit is TileSuit.Man or TileSuit.Pin or TileSuit.Sou;

            _furitenHatch.Visible = furiten;
            if (furiten) _furitenHatch.QueueRedraw();
        }

        /// <summary>Clear wait-display state and return the tile to a normal face.</summary>
        public void ClearWaitDisplay()
        {
            if (_waitCountLabel == null) return;
            _waitCountLabel.Visible  = false;
            _deadWaitOutline.Visible = false;
            _furitenHatch.Visible    = false;
            Refresh();
        }

        /// <summary>
        /// Rotate the tile art 90° for the side seats, which show the same back
        /// texture turned to face their own side of the table.
        /// </summary>
        public void SetSideways(bool sideways)
        {
            _sideways = sideways;
            ApplySideways();
        }

        private bool _sideways;

        /// <summary>
        /// Rotation is about the tile's own centre, and the centre is only known once
        /// the container has laid the tile out — so this re-runs on every resize.
        /// </summary>
        private void ApplySideways()
        {
            if (_textureRect == null) return;

            var extent = Size == Vector2.Zero ? CustomMinimumSize : Size;
            _textureRect.PivotOffset     = extent * 0.5f;
            _textureRect.RotationDegrees = _sideways ? 90f : 0f;
        }

        // =====================================================================
        // Input
        // =====================================================================

        private void OnPressed()
        {
            EmitSignal(SignalName.TileClicked, this);
        }
    }
}
