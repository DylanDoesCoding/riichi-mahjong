// =============================================================================
// DoloWidgets.cs
// Composite controls the redesign uses in more than one screen.
//
// Godot's Button takes a Texture2D icon, but the Dolo icons are drawn rather
// than imported, so an icon button is a Button with an icon-and-label row laid
// over it.  Rather than repeat that construction in the menu, the lobby and the
// results screen, it lives here.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public static class DoloWidgets
    {
        /// <summary>
        /// A button carrying a drawn icon and a label. The row ignores the mouse so the
        /// button underneath still receives the whole rect as its hit area.
        /// </summary>
        public static Button IconButton(DoloIcon icon, string text, StringName? variation = null,
                                        int height = 56, int iconSize = 20)
        {
            var button = new Button
            {
                CustomMinimumSize   = new Vector2(0, height),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            button.ThemeTypeVariation = variation ?? DoloTheme.ButtonSecondary;

            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 12);
            row.Alignment = BoxContainer.AlignmentMode.Center;

            bool primary = variation == DoloTheme.ButtonPrimary;
            var ink = primary ? new Color("#1d1610") : DoloTokens.Ivory;

            row.AddChild(new DoloIconRect(icon, iconSize, ink));

            var label = new Label
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.ThemeTypeVariation = DoloTheme.PlateName;
            label.AddThemeFontSizeOverride("font_size", DoloTokens.SizeButton);
            label.AddThemeColorOverride("font_color", ink);
            row.AddChild(label);

            button.AddChild(row);
            return button;
        }

        /// <summary>
        /// Re-label a button built by <see cref="IconButton"/>. The caption lives in a
        /// child Label, not the button's own Text — setting <c>button.Text</c> would make
        /// the Button draw a second caption underneath the row, doubling it. This finds
        /// that child Label and updates it instead, leaving the drawn icon in place.
        /// </summary>
        public static void SetIconButtonText(Button button, string text)
        {
            foreach (var child in button.GetChildren())
            {
                if (child is not HBoxContainer row) continue;
                foreach (var inner in row.GetChildren())
                    if (inner is Label label) { label.Text = text; return; }
            }
        }

        /// <summary>
        /// Give an existing scene Button a drawn icon. The button's own text is moved
        /// into the row and cleared, since a Button renders its Text underneath any
        /// children and would otherwise draw the label twice.
        /// </summary>
        public static void DecorateButton(Button button, DoloIcon icon,
                                          StringName? variation = null, int iconSize = 20)
        {
            string text = button.Text;
            button.Text = "";
            button.ThemeTypeVariation = variation ?? DoloTheme.ButtonSecondary;
            button.RemoveThemeFontSizeOverride("font_size");

            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 12);
            row.Alignment = BoxContainer.AlignmentMode.Center;

            bool primary = variation == DoloTheme.ButtonPrimary;
            var ink = primary ? new Color("#1d1610") : DoloTokens.Ivory;

            row.AddChild(new DoloIconRect(icon, iconSize, ink));

            var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
            label.ThemeTypeVariation = DoloTheme.PlateName;
            label.AddThemeFontSizeOverride("font_size", DoloTokens.SizeButton);
            label.AddThemeColorOverride("font_color", ink);
            row.AddChild(label);

            button.AddChild(row);
        }

        /// <summary>
        /// A labelled field. The design is explicit that the label sits above the field
        /// and a placeholder never does double duty as one.
        /// </summary>
        public static VBoxContainer LabelledField(string labelText, string placeholder,
                                                  string initial, int maxLength, out LineEdit edit)
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 6);

            var label = new Label { Text = labelText.ToUpperInvariant() };
            label.ThemeTypeVariation = DoloTheme.Mono;
            section.AddChild(label);

            edit = new LineEdit
            {
                Text                = initial,
                PlaceholderText     = placeholder,
                MaxLength           = maxLength,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(0, DoloTokens.FieldHeight),
            };
            section.AddChild(edit);
            return section;
        }

        /// <summary>A section heading: small mono label over a hairline.</summary>
        public static VBoxContainer SectionHeading(string text)
        {
            var section = new VBoxContainer();
            section.AddThemeConstantOverride("separation", 6);

            var label = new Label { Text = text.ToUpperInvariant() };
            label.ThemeTypeVariation = DoloTheme.Mono;

            section.AddChild(label);
            section.AddChild(DoloStyles.HairlineRow());
            return section;
        }

        /// <summary>
        /// A two-option segmented toggle, used for the tile set and the layout mode.
        /// The selected side carries a brass ring rather than only a fill, so the state
        /// is a shape as well as a colour.
        /// </summary>
        public static HBoxContainer SegmentedToggle(
            (DoloIcon icon, string text)[] options, int selected,
            System.Action<int> onSelect, out Button[] buttons)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var made = new Button[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var button = IconButton(options[i].icon, options[i].text,
                                        DoloTheme.ButtonSecondary, height: 48, iconSize: 18);
                button.Pressed += () => onSelect(index);
                row.AddChild(button);
                made[i] = button;
            }

            buttons = made;
            ApplySegmentedSelection(made, selected);
            return row;
        }

        /// <summary>Re-mark which segment of a toggle is active.</summary>
        public static void ApplySegmentedSelection(Button[] buttons, int selected)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                var style = DoloStyles.ButtonSecondary();
                if (i == selected)
                {
                    style.BorderColor = DoloTokens.Brass;
                    DoloStyles.SetBorder(style, 2);
                }
                buttons[i].AddThemeStyleboxOverride("normal", style);
            }
        }
    }
}
