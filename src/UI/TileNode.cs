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

        private Panel       _backPanel           = null!;  // Shown when face-down
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
        private Panel       _matchHighlight      = null!;  // Cyan edge: matches the hovered hand tile

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

            // ---- Back panel (shown when face-down) ----
            // Using a styled Panel avoids depending on Back.png whose .import
            // file from the original tile pack conflicts with Godot's importer.
            _backPanel = new Panel();
            _backPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _backPanel.MouseFilter = MouseFilterEnum.Ignore;
            _backPanel.Visible     = false;
            var backStyle = new StyleBoxFlat();
            backStyle.BgColor                 = new Color(0.15f, 0.30f, 0.55f, 1f);  // Dark blue
            backStyle.BorderColor             = new Color(0.35f, 0.55f, 0.80f, 1f);
            backStyle.BorderWidthTop    = backStyle.BorderWidthBottom =
            backStyle.BorderWidthLeft   = backStyle.BorderWidthRight  = 2;
            backStyle.CornerRadiusTopLeft     = backStyle.CornerRadiusTopRight    =
            backStyle.CornerRadiusBottomLeft  = backStyle.CornerRadiusBottomRight = 4;
            _backPanel.AddThemeStyleboxOverride("panel", backStyle);
            AddChild(_backPanel);

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

            // Cyan edge — this tile matches the hand tile currently hovered
            _matchHighlight = new Panel();
            _matchHighlight.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _matchHighlight.MouseFilter = MouseFilterEnum.Ignore;
            _matchHighlight.Visible     = false;
            var matchStyle = new StyleBoxFlat();
            matchStyle.BgColor     = new Color(0.20f, 0.80f, 1f, 0.22f);
            matchStyle.BorderColor = new Color(0.25f, 0.85f, 1f, 1f);
            matchStyle.BorderWidthTop    = matchStyle.BorderWidthBottom =
            matchStyle.BorderWidthLeft   = matchStyle.BorderWidthRight  = 3;
            matchStyle.CornerRadiusTopLeft    = matchStyle.CornerRadiusTopRight    =
            matchStyle.CornerRadiusBottomLeft = matchStyle.CornerRadiusBottomRight = 4;
            _matchHighlight.AddThemeStyleboxOverride("panel", matchStyle);
            AddChild(_matchHighlight);

            Pressed += OnPressed;
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
                // Face-down: show the styled back panel, hide face-up layers
                _backPanel.Visible        = true;
                _tileBody.Visible         = false;
                _textureRect.Visible      = false;
                _selectionOverlay.Visible = false;
                _valueLabel.Visible       = false;
            }
            else
            {
                // Face-up: load SVG artwork on top of tile body
                string fileName = GetTileFileName(TileData);
                string path = $"{TileBasePath}/{GameSettings.TileThemeFolder}/{fileName}.svg";
                _textureRect.Texture = GD.Load<Texture2D>(path);

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

                _backPanel.Visible        = false;
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

        /// <summary>Gold edge marking this tile as a live dora (or red five).</summary>
        public void SetDoraGlow(bool active)
        {
            if (_doraGlow == null) return;
            _doraGlow.Visible = active && !FaceDown;
        }

        /// <summary>Cyan edge marking this tile as matching the hovered hand tile.</summary>
        public void SetMatchHighlight(bool active)
        {
            if (_matchHighlight == null) return;
            _matchHighlight.Visible = active && !FaceDown;
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
