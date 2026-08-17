// =============================================================================
// DoloStyles.cs
// StyleBox and small-node factories built from DoloTokens.
//
// The UI is code-built, so these replace what a .tscn redesign would have put
// in a Theme resource: one place that knows what a card, an inset panel, a
// field or a button actually looks like.  Call these instead of hand-rolling
// another StyleBoxFlat.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public static class DoloStyles
    {
        // =====================================================================
        // Surfaces
        // =====================================================================

        /// <summary>Elevated card: #1d1712, 8px radius, faint brass hairline, deep shadow.</summary>
        public static StyleBoxFlat Card(int padding = 28)
        {
            var box = Flat(DoloTokens.Card, DoloTokens.RadiusCard,
                           DoloTokens.HairlineFaint, borderWidth: 1);
            box.ShadowColor  = DoloTokens.ShadowCardColor;
            box.ShadowSize   = DoloTokens.ShadowCard;
            box.ShadowOffset = DoloTokens.ShadowCardOffset;
            SetPadding(box, padding);
            return box;
        }

        /// <summary>Inset panel: #171209, 5px radius. Used for payout blocks and read-only fields.</summary>
        public static StyleBoxFlat Inset(int padding = 14)
        {
            var box = Flat(DoloTokens.InsetPane, DoloTokens.RadiusInset,
                           DoloTokens.HairlineFaint, borderWidth: 1);
            SetPadding(box, padding);
            return box;
        }

        /// <summary>Screen board: the flat backing behind a whole screen region. 6px radius.</summary>
        public static StyleBoxFlat Board(int padding = 20)
        {
            var box = Flat(DoloTokens.DeepField, DoloTokens.RadiusBoard,
                           DoloTokens.HairlineSoft, borderWidth: 1);
            SetPadding(box, padding);
            return box;
        }

        /// <summary>Flat background with no border - for backdrops and dimmers.</summary>
        public static StyleBoxFlat Surface(Color color, int radius = DoloTokens.RadiusBoard)
            => Flat(color, radius, default, borderWidth: 0);

        // =====================================================================
        // Text fields
        // =====================================================================

        /// <summary>Field at rest: ivory on #120e0a, 5px radius, medium hairline.</summary>
        public static StyleBoxFlat Field()
        {
            var box = Flat(DoloTokens.DeepField, DoloTokens.RadiusInset,
                           DoloTokens.HairlineMedium, borderWidth: 1);
            box.ContentMarginLeft = box.ContentMarginRight = 14;
            box.ContentMarginTop  = box.ContentMarginBottom = 12;
            return box;
        }

        /// <summary>Focused field: 2px brass ring plus the 4px outer glow.</summary>
        public static StyleBoxFlat FieldFocus()
        {
            var box = Field();
            box.BorderColor = DoloTokens.Brass;
            SetBorder(box, DoloTokens.FocusRingWidth);
            box.ShadowColor = DoloTokens.FocusGlow;
            box.ShadowSize  = 4;
            box.ShadowOffset = Vector2.Zero;
            return box;
        }

        // =====================================================================
        // Buttons
        // =====================================================================

        /// <summary>
        /// Primary action: solid brass with a dark label. The most prominent control
        /// on a screen, and there should only ever be one.
        /// </summary>
        public static StyleBoxFlat ButtonPrimary(float shade = 1f)
        {
            var bg = new Color(DoloTokens.Brass.R * shade,
                               DoloTokens.Brass.G * shade,
                               DoloTokens.Brass.B * shade);
            var box = Flat(bg, DoloTokens.RadiusBoard, default, borderWidth: 0);
            ApplyButtonShadow(box);
            SetButtonPadding(box);
            return box;
        }

        /// <summary>Secondary action: card fill with a medium brass hairline.</summary>
        public static StyleBoxFlat ButtonSecondary(float shade = 1f)
        {
            var bg = new Color(DoloTokens.Card.R * shade,
                               DoloTokens.Card.G * shade,
                               DoloTokens.Card.B * shade);
            var box = Flat(bg, DoloTokens.RadiusBoard,
                           DoloTokens.HairlineMedium, borderWidth: 1);
            ApplyButtonShadow(box);
            SetButtonPadding(box);
            return box;
        }

        /// <summary>Quiet action: no fill, hairline only. For Back / Cancel / Menu.</summary>
        public static StyleBoxFlat ButtonGhost()
        {
            var box = Flat(new Color(0, 0, 0, 0), DoloTokens.RadiusBoard,
                           DoloTokens.HairlineSoft, borderWidth: 1);
            SetButtonPadding(box);
            return box;
        }

        /// <summary>Focus ring shared by every button: 2px brass plus the outer glow.</summary>
        public static StyleBoxFlat ButtonFocus()
        {
            var box = Flat(new Color(0, 0, 0, 0), DoloTokens.RadiusBoard,
                           DoloTokens.Brass, DoloTokens.FocusRingWidth);
            box.ShadowColor  = DoloTokens.FocusGlow;
            box.ShadowSize   = 4;
            box.ShadowOffset = Vector2.Zero;
            SetButtonPadding(box);
            return box;
        }

        // =====================================================================
        // Small nodes
        // =====================================================================

        /// <summary>A 1px horizontal hairline, for splitting rows inside a card.</summary>
        public static ColorRect HairlineRow(float alpha = 0.18f, int thickness = 1)
        {
            var line = new ColorRect
            {
                Color             = DoloTokens.Hairline(alpha),
                CustomMinimumSize = new Vector2(0, thickness),
                MouseFilter       = Control.MouseFilterEnum.Ignore,
            };
            line.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            return line;
        }

        /// <summary>
        /// A vertical gradient fill. StyleBoxFlat cannot do gradients, so the few
        /// places the design calls for one (the first-place results row, the
        /// REMATCH button) get a TextureRect behind their content instead.
        /// </summary>
        public static TextureRect GradientRect(Color from, Color to, bool vertical = true)
        {
            var gradient = new Gradient();
            gradient.SetColor(0, from);
            gradient.SetColor(1, to);

            var texture = new GradientTexture2D
            {
                Gradient = gradient,
                Width    = vertical ? 1 : 64,
                Height   = vertical ? 64 : 1,
                FillFrom = Vector2.Zero,
                FillTo   = vertical ? new Vector2(0, 1) : new Vector2(1, 0),
            };

            var rect = new TextureRect
            {
                Texture     = texture,
                ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return rect;
        }

        // =====================================================================
        // Primitives
        // =====================================================================

        public static StyleBoxFlat Flat(Color bg, int radius, Color border, int borderWidth)
        {
            var box = new StyleBoxFlat { BgColor = bg };
            SetRadius(box, radius);
            if (borderWidth > 0)
            {
                box.BorderColor = border;
                SetBorder(box, borderWidth);
            }
            return box;
        }

        public static void SetRadius(StyleBoxFlat box, int radius)
        {
            box.CornerRadiusTopLeft     = radius;
            box.CornerRadiusTopRight    = radius;
            box.CornerRadiusBottomLeft  = radius;
            box.CornerRadiusBottomRight = radius;
        }

        public static void SetBorder(StyleBoxFlat box, int width)
        {
            box.BorderWidthTop    = width;
            box.BorderWidthBottom = width;
            box.BorderWidthLeft   = width;
            box.BorderWidthRight  = width;
        }

        public static void SetPadding(StyleBoxFlat box, int padding)
        {
            box.ContentMarginTop    = padding;
            box.ContentMarginBottom = padding;
            box.ContentMarginLeft   = padding;
            box.ContentMarginRight  = padding;
        }

        private static void SetButtonPadding(StyleBoxFlat box)
        {
            box.ContentMarginLeft   = 20;
            box.ContentMarginRight  = 20;
            box.ContentMarginTop    = 10;
            box.ContentMarginBottom = 10;
        }

        private static void ApplyButtonShadow(StyleBoxFlat box)
        {
            box.ShadowColor  = DoloTokens.ShadowButtonColor;
            box.ShadowSize   = DoloTokens.ShadowButton;
            box.ShadowOffset = DoloTokens.ShadowButtonOffset;
        }
    }
}
