// =============================================================================
// DoloTheme.cs
// The one shared Godot Theme for the whole client.
//
// The handoff is explicit that type is set once in a Theme rather than as
// per-widget font_size overrides, so this builds a Theme with the design's type
// scale expressed as *theme type variations*:
//
//     var title = new Label { Text = "RESULTS" };
//     title.ThemeTypeVariation = DoloTheme.ScreenTitle;
//
// That gives a Label its font, size and colour in one line, and means a change
// to the scale happens here rather than in nine screens.
//
// Fonts: Source Sans 3 (variable, 400/600) for text, IBM Plex Mono for every
// number so columns align.  Both are SIL Open Font License.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public static class DoloTheme
    {
        private const string FontPath = "res://Assets/Fonts";

        // ---- Theme type variation names -------------------------------------
        // Assign to Control.ThemeTypeVariation.

        public static readonly StringName ScreenTitle = "DoloScreenTitle";
        public static readonly StringName SectionHead = "DoloSectionHead";
        public static readonly StringName NameLarge   = "DoloNameLarge";
        public static readonly StringName NameSmall   = "DoloNameSmall";
        public static readonly StringName Row         = "DoloRow";
        public static readonly StringName Body        = "DoloBody";
        public static readonly StringName BodySmall   = "DoloBodySmall";
        public static readonly StringName Dim         = "DoloDim";

        /// <summary>Player name on a table nameplate - smaller than a screen name, still semibold.</summary>
        public static readonly StringName PlateName = "DoloPlateName";

        public static readonly StringName Mono        = "DoloMono";
        public static readonly StringName MonoSmall   = "DoloMonoSmall";
        public static readonly StringName MonoNumber  = "DoloMonoNumber";
        public static readonly StringName MonoLarge   = "DoloMonoLarge";

        public static readonly StringName ButtonPrimary   = "DoloButtonPrimary";
        public static readonly StringName ButtonSecondary = "DoloButtonSecondary";
        public static readonly StringName ButtonGhost     = "DoloButtonGhost";

        public static readonly StringName CardPanel  = "DoloCardPanel";
        public static readonly StringName InsetPanel = "DoloInsetPanel";
        public static readonly StringName BoardPanel = "DoloBoardPanel";

        // ---- Fonts -----------------------------------------------------------

        private static Font? _sans;
        private static Font? _sansSemiBold;
        private static Font? _mono;
        private static Font? _monoSemiBold;
        private static Theme? _shared;

        /// <summary>Source Sans 3 at weight 400 - labels and body copy.</summary>
        public static Font Sans => _sans ??= LoadSans(DoloTokens.WeightRegular);

        /// <summary>Source Sans 3 at weight 600 - names, calls and headings.</summary>
        public static Font SansSemiBold => _sansSemiBold ??= LoadSans(DoloTokens.WeightSemiBold);

        /// <summary>IBM Plex Mono - every number, so columns align.</summary>
        public static Font MonoFont => _mono ??= LoadFont("IBMPlexMono-Regular.ttf");

        public static Font MonoSemiBoldFont => _monoSemiBold ??= LoadFont("IBMPlexMono-SemiBold.ttf");

        /// <summary>The shared Theme. Assign to the root Control of each screen.</summary>
        public static Theme Shared => _shared ??= Build();

        /// <summary>
        /// Apply the shared theme to a screen root. Children inherit it, so this is
        /// the only call a screen needs to pick up the whole design system.
        /// </summary>
        public static void Apply(Control root) => root.Theme = Shared;

        // =====================================================================
        // Font loading
        // =====================================================================

        private static Font LoadFont(string fileName)
        {
            var font = GD.Load<Font>($"{FontPath}/{fileName}");
            if (font == null)
                GD.PushWarning($"DoloTheme: missing font {fileName} - falling back to the default font.");
            return font!;
        }

        /// <summary>
        /// Source Sans 3 ships as a variable font, so a weight is a FontVariation over
        /// the same file rather than a second file.
        /// </summary>
        private static Font LoadSans(int weight)
        {
            var baseFont = GD.Load<FontFile>($"{FontPath}/SourceSans3.ttf");
            if (baseFont == null)
            {
                GD.PushWarning("DoloTheme: SourceSans3.ttf missing - falling back to the default font.");
                return null!;
            }

            var textServer = TextServerManager.GetPrimaryInterface();
            var axes = new Godot.Collections.Dictionary
            {
                { (int)textServer.NameToTag("weight"), weight },
            };

            return new FontVariation { BaseFont = baseFont, VariationOpentype = axes };
        }

        // =====================================================================
        // Theme construction
        // =====================================================================

        private static Theme Build()
        {
            var theme = new Theme
            {
                DefaultFont     = Sans,
                DefaultFontSize = DoloTokens.SizeBody,
            };

            BuildLabelVariations(theme);
            BuildPanelVariations(theme);
            BuildButtons(theme);
            BuildFields(theme);

            return theme;
        }

        private static void BuildLabelVariations(Theme theme)
        {
            // (variation, font, size, colour)
            AddLabel(theme, ScreenTitle, SansSemiBold, DoloTokens.SizeScreenTitle, DoloTokens.Ivory);
            AddLabel(theme, SectionHead, SansSemiBold, DoloTokens.SizeSectionHead, DoloTokens.Ivory);
            AddLabel(theme, NameLarge,   SansSemiBold, DoloTokens.SizeNameLarge,   DoloTokens.Ivory);
            AddLabel(theme, NameSmall,   SansSemiBold, DoloTokens.SizeNameSmall,   DoloTokens.Ivory);
            AddLabel(theme, Row,         Sans,         DoloTokens.SizeRow,         DoloTokens.BodyText);
            AddLabel(theme, Body,        Sans,         DoloTokens.SizeBody,        DoloTokens.BodyText);
            AddLabel(theme, BodySmall,   Sans,         DoloTokens.SizeBodySmall,   DoloTokens.BodyText);
            AddLabel(theme, Dim,         Sans,         DoloTokens.SizeBodySmall,   DoloTokens.DimText);
            AddLabel(theme, PlateName,   SansSemiBold, DoloTokens.SizeRow,         DoloTokens.Ivory);

            AddLabel(theme, Mono,       MonoFont,         DoloTokens.SizeMonoLabel, DoloTokens.MonoLabel);
            AddLabel(theme, MonoSmall,  MonoFont,         DoloTokens.SizeMonoSmall, DoloTokens.MonoDim);
            AddLabel(theme, MonoNumber, MonoSemiBoldFont, DoloTokens.SizeRow,       DoloTokens.Ivory);
            AddLabel(theme, MonoLarge,  MonoSemiBoldFont, DoloTokens.SizeNameSmall, DoloTokens.Ivory);

            // Base Label type, for anything that forgets to pick a variation.
            theme.SetFont("font", "Label", Sans);
            theme.SetFontSize("font_size", "Label", DoloTokens.SizeBody);
            theme.SetColor("font_color", "Label", DoloTokens.BodyText);
        }

        private static void AddLabel(Theme theme, StringName variation, Font font, int size, Color color)
        {
            theme.SetTypeVariation(variation, "Label");
            theme.SetFont("font", variation, font);
            theme.SetFontSize("font_size", variation, size);
            theme.SetColor("font_color", variation, color);
        }

        private static void BuildPanelVariations(Theme theme)
        {
            theme.SetStylebox("panel", "PanelContainer", DoloStyles.Card());
            theme.SetStylebox("panel", "Panel",          DoloStyles.Card());

            AddPanel(theme, CardPanel,  DoloStyles.Card());
            AddPanel(theme, InsetPanel, DoloStyles.Inset());
            AddPanel(theme, BoardPanel, DoloStyles.Board());
        }

        private static void AddPanel(Theme theme, StringName variation, StyleBox box)
        {
            theme.SetTypeVariation(variation, "PanelContainer");
            theme.SetStylebox("panel", variation, box);
        }

        private static void BuildButtons(Theme theme)
        {
            // Base Button: secondary styling, so an unstyled button still looks right.
            ApplyButtonStyle(theme, "Button", DoloStyles.ButtonSecondary,
                             DoloTokens.Ivory, DoloTokens.SizeButton);

            AddButtonVariation(theme, ButtonSecondary, DoloStyles.ButtonSecondary, DoloTokens.Ivory);
            AddButtonVariation(theme, ButtonGhost,     _ => DoloStyles.ButtonGhost(), DoloTokens.BodyText);

            // Primary carries a dark label on brass - the one inverted control.
            AddButtonVariation(theme, ButtonPrimary, DoloStyles.ButtonPrimary, new Color("#1d1610"));
        }

        private static void AddButtonVariation(Theme theme, StringName variation,
                                               System.Func<float, StyleBoxFlat> factory, Color fontColor)
        {
            theme.SetTypeVariation(variation, "Button");
            ApplyButtonStyle(theme, variation, factory, fontColor, DoloTokens.SizeButton);
        }

        private static void ApplyButtonStyle(Theme theme, StringName type,
                                             System.Func<float, StyleBoxFlat> factory,
                                             Color fontColor, int fontSize)
        {
            // Hover lifts the fill, pressed sinks it, disabled drops to half.
            theme.SetStylebox("normal",   type, factory(1.00f));
            theme.SetStylebox("hover",    type, factory(1.18f));
            theme.SetStylebox("pressed",  type, factory(0.86f));
            theme.SetStylebox("disabled", type, factory(0.55f));
            theme.SetStylebox("focus",    type, DoloStyles.ButtonFocus());

            theme.SetFont("font", type, DoloTheme.SansSemiBold);
            theme.SetFontSize("font_size", type, fontSize);
            theme.SetColor("font_color", type, fontColor);
            theme.SetColor("font_hover_color", type, fontColor);
            theme.SetColor("font_pressed_color", type, fontColor);
            theme.SetColor("font_disabled_color", type, new Color(fontColor, 0.45f));
        }

        private static void BuildFields(Theme theme)
        {
            theme.SetStylebox("normal", "LineEdit", DoloStyles.Field());
            theme.SetStylebox("focus",  "LineEdit", DoloStyles.FieldFocus());
            theme.SetFont("font", "LineEdit", Sans);
            theme.SetFontSize("font_size", "LineEdit", DoloTokens.SizeRow);
            theme.SetColor("font_color", "LineEdit", DoloTokens.Ivory);
            theme.SetColor("font_placeholder_color", "LineEdit", DoloTokens.DimText);
            theme.SetColor("caret_color", "LineEdit", DoloTokens.Brass);
            theme.SetColor("selection_color", "LineEdit", DoloTokens.Hairline(0.30f));
        }
    }
}
