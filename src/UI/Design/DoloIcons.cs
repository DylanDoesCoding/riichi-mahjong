// =============================================================================
// DoloIcons.cs
// The geometric icon set, and the DOLO wordmark.
//
// The client had no icons - every one was an emoji sitting in a label
// (🀄 ⚡ 🏆 ⚙ ▶ ✕ ← 🌐 👤 🎵 🔊 ＋ ☀ 🌙 ✓).  Emoji render differently on every
// platform, ignore the palette, and read as decoration rather than as part of
// the interface.  Pass 05 replaces them with shapes drawn from boxes, rings and
// hairlines, which is also what the art budget allows.
//
// Each icon is drawn in a unit square and scaled to the control, so the same
// icon works at 16px in a row and at 28px on a button.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public enum DoloIcon
    {
        Play, Bolt, Trophy, Gear, Back, Close, Globe,
        Person, Music, Speaker, Plus, Sun, Moon, Check, Tile,
    }

    /// <summary>A single icon, drawn geometrically at whatever size the layout gives it.</summary>
    public partial class DoloIconRect : Control
    {
        private DoloIcon _icon = DoloIcon.Play;
        private Color    _color = DoloTokens.Ivory;

        public DoloIcon Icon
        {
            get => _icon;
            set { _icon = value; QueueRedraw(); }
        }

        public Color IconColor
        {
            get => _color;
            set { _color = value; QueueRedraw(); }
        }

        public DoloIconRect() { }

        public DoloIconRect(DoloIcon icon, int size = 18, Color? color = null)
        {
            _icon  = icon;
            _color = color ?? DoloTokens.Ivory;
            CustomMinimumSize = new Vector2(size, size);
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            if (CustomMinimumSize == Vector2.Zero)
                CustomMinimumSize = new Vector2(18, 18);
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            // Work in a square so an icon never stretches with its container.
            float side   = Mathf.Min(Size.X, Size.Y);
            if (side <= 0f) return;
            var   origin = new Vector2((Size.X - side) * 0.5f, (Size.Y - side) * 0.5f);

            // Unit-space helper: (0,0) is the icon's top-left, (1,1) its bottom-right.
            Vector2 P(float x, float y) => origin + new Vector2(x * side, y * side);
            float   stroke = Mathf.Max(1.5f, side * 0.10f);

            switch (_icon)
            {
                case DoloIcon.Play:
                    DrawColoredPolygon(new[] { P(0.26f, 0.16f), P(0.82f, 0.50f), P(0.26f, 0.84f) }, _color);
                    break;

                case DoloIcon.Bolt:
                    DrawColoredPolygon(new[]
                    {
                        P(0.56f, 0.08f), P(0.24f, 0.54f), P(0.46f, 0.54f),
                        P(0.40f, 0.92f), P(0.76f, 0.44f), P(0.53f, 0.44f),
                    }, _color);
                    break;

                case DoloIcon.Trophy:
                    // Cup, stem, base — three primitives read as a trophy at any size.
                    DrawColoredPolygon(new[]
                    {
                        P(0.26f, 0.14f), P(0.74f, 0.14f), P(0.62f, 0.56f), P(0.38f, 0.56f),
                    }, _color);
                    DrawRect(new Rect2(P(0.45f, 0.56f), new Vector2(side * 0.10f, side * 0.18f)), _color);
                    DrawRect(new Rect2(P(0.30f, 0.74f), new Vector2(side * 0.40f, side * 0.12f)), _color);
                    break;

                case DoloIcon.Gear:
                    // A ring with four teeth. Cheaper than a real cog and reads the same.
                    DrawArc(P(0.5f, 0.5f), side * 0.28f, 0f, Mathf.Tau, 32, _color, stroke);
                    DrawRect(new Rect2(P(0.44f, 0.06f), new Vector2(side * 0.12f, side * 0.14f)), _color);
                    DrawRect(new Rect2(P(0.44f, 0.80f), new Vector2(side * 0.12f, side * 0.14f)), _color);
                    DrawRect(new Rect2(P(0.06f, 0.44f), new Vector2(side * 0.14f, side * 0.12f)), _color);
                    DrawRect(new Rect2(P(0.80f, 0.44f), new Vector2(side * 0.14f, side * 0.12f)), _color);
                    break;

                case DoloIcon.Back:
                    DrawLine(P(0.86f, 0.50f), P(0.20f, 0.50f), _color, stroke);
                    DrawLine(P(0.20f, 0.50f), P(0.46f, 0.24f), _color, stroke);
                    DrawLine(P(0.20f, 0.50f), P(0.46f, 0.76f), _color, stroke);
                    break;

                case DoloIcon.Close:
                    DrawLine(P(0.22f, 0.22f), P(0.78f, 0.78f), _color, stroke);
                    DrawLine(P(0.78f, 0.22f), P(0.22f, 0.78f), _color, stroke);
                    break;

                case DoloIcon.Globe:
                    DrawArc(P(0.5f, 0.5f), side * 0.36f, 0f, Mathf.Tau, 32, _color, stroke);
                    DrawLine(P(0.14f, 0.50f), P(0.86f, 0.50f), _color, stroke * 0.8f);
                    // The meridian, as two arcs meeting at the poles.
                    DrawArc(P(0.5f, 0.5f), side * 0.36f, -Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, 20,
                            _color, stroke * 0.8f);
                    DrawArc(P(0.5f, 0.5f), side * 0.18f, Mathf.Pi * 0.5f, Mathf.Pi * 1.5f, 20,
                            _color, stroke * 0.8f);
                    break;

                case DoloIcon.Person:
                    DrawCircle(P(0.5f, 0.30f), side * 0.17f, _color);
                    DrawColoredPolygon(new[]
                    {
                        P(0.20f, 0.88f), P(0.30f, 0.58f), P(0.70f, 0.58f), P(0.80f, 0.88f),
                    }, _color);
                    break;

                case DoloIcon.Music:
                    DrawCircle(P(0.32f, 0.74f), side * 0.13f, _color);
                    DrawCircle(P(0.72f, 0.64f), side * 0.13f, _color);
                    DrawLine(P(0.44f, 0.74f), P(0.44f, 0.20f), _color, stroke * 0.9f);
                    DrawLine(P(0.84f, 0.64f), P(0.84f, 0.14f), _color, stroke * 0.9f);
                    DrawLine(P(0.44f, 0.20f), P(0.84f, 0.14f), _color, stroke * 0.9f);
                    break;

                case DoloIcon.Speaker:
                    DrawColoredPolygon(new[]
                    {
                        P(0.14f, 0.38f), P(0.34f, 0.38f), P(0.56f, 0.16f),
                        P(0.56f, 0.84f), P(0.34f, 0.62f), P(0.14f, 0.62f),
                    }, _color);
                    DrawArc(P(0.56f, 0.50f), side * 0.20f, -Mathf.Pi * 0.4f, Mathf.Pi * 0.4f, 16,
                            _color, stroke * 0.8f);
                    DrawArc(P(0.56f, 0.50f), side * 0.32f, -Mathf.Pi * 0.4f, Mathf.Pi * 0.4f, 16,
                            _color, stroke * 0.8f);
                    break;

                case DoloIcon.Plus:
                    DrawRect(new Rect2(P(0.44f, 0.16f), new Vector2(side * 0.12f, side * 0.68f)), _color);
                    DrawRect(new Rect2(P(0.16f, 0.44f), new Vector2(side * 0.68f, side * 0.12f)), _color);
                    break;

                case DoloIcon.Sun:
                    DrawCircle(P(0.5f, 0.5f), side * 0.22f, _color);
                    for (int ray = 0; ray < 8; ray++)
                    {
                        float angle = ray * Mathf.Tau / 8f;
                        var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        DrawLine(P(0.5f, 0.5f) + direction * side * 0.30f,
                                 P(0.5f, 0.5f) + direction * side * 0.44f, _color, stroke * 0.8f);
                    }
                    break;

                case DoloIcon.Moon:
                    // Crescent as a polygon, so it needs no knowledge of the background.
                    DrawCrescent(origin, side);
                    break;

                case DoloIcon.Check:
                    DrawLine(P(0.18f, 0.52f), P(0.42f, 0.76f), _color, stroke);
                    DrawLine(P(0.42f, 0.76f), P(0.84f, 0.26f), _color, stroke);
                    break;

                case DoloIcon.Tile:
                    // A mahjong tile: the mark the app is actually about.
                    DrawRect(new Rect2(P(0.24f, 0.10f), new Vector2(side * 0.52f, side * 0.80f)),
                             _color, filled: false, width: stroke);
                    DrawLine(P(0.36f, 0.34f), P(0.64f, 0.34f), _color, stroke * 0.8f);
                    DrawLine(P(0.36f, 0.52f), P(0.64f, 0.52f), _color, stroke * 0.8f);
                    DrawLine(P(0.36f, 0.70f), P(0.64f, 0.70f), _color, stroke * 0.8f);
                    break;
            }
        }

        /// <summary>
        /// The crescent is a thick partial arc rather than a filled polygon.
        ///
        /// The obvious construction - trace the outer edge, then return along an inner
        /// arc offset to one side - produces a self-intersecting polygon whenever the
        /// offset pushes the inner arc past the outer arc's endpoints, and Godot's
        /// triangulator rejects it. A stroked arc gives the same shape and cannot fail.
        /// </summary>
        private void DrawCrescent(Vector2 origin, float side)
        {
            var centre = origin + new Vector2(side * 0.5f, side * 0.5f);

            // Roughly 250 degrees, open towards the upper right.
            const float start = Mathf.Pi * 0.30f;
            const float sweep = Mathf.Pi * 1.40f;

            DrawArc(centre, side * 0.30f, start, start + sweep, 32, _color, side * 0.20f);
        }
    }

    /// <summary>
    /// The DOLO wordmark: the table's own four-wedge geometry as the mark, with the
    /// name beside it in wide-tracked semibold.
    ///
    /// "Dolo" is a personal handle, not a studio, so the identity leans on the idea
    /// of one person's table rather than on a product logo - which makes the split
    /// felt the obvious mark to use.
    /// </summary>
    public partial class DoloWordmark : Control
    {
        /// <summary>Height of the square mark; the type is sized from it.</summary>
        public int MarkSize { get; set; } = 44;

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            if (CustomMinimumSize == Vector2.Zero)
                CustomMinimumSize = new Vector2(260, MarkSize + 8);
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            float mark = MarkSize;
            float top  = (Size.Y - mark) * 0.5f;

            var centre      = new Vector2(mark * 0.5f, top + mark * 0.5f);
            var topLeft     = new Vector2(0f, top);
            var topRight    = new Vector2(mark, top);
            var bottomRight = new Vector2(mark, top + mark);
            var bottomLeft  = new Vector2(0f, top + mark);

            // The mark is the table: one square, split along both diagonals, with the
            // player's own wedge picked out in brass.
            DrawColoredPolygon(new[] { centre, bottomLeft, bottomRight }, DoloTokens.Brass);
            DrawColoredPolygon(new[] { centre, bottomRight, topRight },   DoloTokens.FeltQuiet);
            DrawColoredPolygon(new[] { centre, topRight, topLeft },       DoloTokens.FeltQuiet);
            DrawColoredPolygon(new[] { centre, topLeft, bottomLeft },     DoloTokens.FeltQuiet);

            DrawLine(topLeft, bottomRight, DoloTokens.HairlineStrong, 2f);
            DrawLine(topRight, bottomLeft, DoloTokens.HairlineStrong, 2f);
            DrawRect(new Rect2(topLeft, new Vector2(mark, mark)),
                     DoloTokens.HairlineStrong, filled: false, width: 2f);

            // The name, tracked wide so it reads as a mark rather than as a word.
            var font = WordmarkFont();
            if (font == null) return;

            int   size     = Mathf.RoundToInt(mark * 0.86f);
            float baseline = top + mark * 0.5f + size * 0.36f;
            DrawString(font, new Vector2(mark + mark * 0.42f, baseline), "DOLO",
                       HorizontalAlignment.Left, -1, size, DoloTokens.Ivory);
        }

        private static Font? _wordmarkFont;

        private static Font? WordmarkFont()
        {
            if (_wordmarkFont != null) return _wordmarkFont;
            if (DoloTheme.SansSemiBold is not Font baseFont) return null;

            // Godot exposes tracking as glyph spacing on a FontVariation, which is the
            // only way to get letter-spacing on a drawn string.
            _wordmarkFont = new FontVariation
            {
                BaseFont     = baseFont,
                SpacingGlyph = 10,
            };
            return _wordmarkFont;
        }
    }
}
