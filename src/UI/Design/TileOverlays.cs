// =============================================================================
// TileOverlays.cs
// Small drawn Controls that give tile states a second, non-colour channel.
//
// Pass 10 of the redesign requires every rules-relevant state to survive a full
// greyscale strip: hue is retained for players who can see it, but nothing
// depends on it.  A StyleBoxFlat can only give us fill and border, so the cues
// that need a shape - a corner wedge, a hatch, a dashed edge - are drawn here.
//
// Each is a leaf Control that draws itself and ignores the mouse, so it can be
// layered over a TileNode without disturbing hit testing.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    /// <summary>
    /// Solid triangle in the top-right corner marking a live dora or a red five.
    /// Pairs with the gold border: the border carries the hue, the wedge carries
    /// the shape, and the wedge alone is enough in greyscale.
    /// </summary>
    public partial class DoraCornerWedge : Control
    {
        // Proportional to the tile so it reads at both hand (66px) and river (28px)
        // sizes, clamped so it never dominates a small tile or vanishes on a large one.
        private const float WedgeFraction = 0.34f;
        private const float WedgeMin      = 11f;
        private const float WedgeMax      = 22f;

        // A dark seam along the hypotenuse separates the gold from bright tile faces
        // and dark felt alike, so the wedge is legible on any background - this is the
        // cue that has to survive greyscale, so it must not depend on the felt colour.
        private static readonly Color Seam = new(0.10f, 0.08f, 0.05f, 0.85f);

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Anchored full-rect, this is size 0 until the container lays the tile out.
            // Without a redraw on resize it would draw once at nothing and never again.
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            float w = Size.X;
            float wedge = Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * WedgeFraction,
                                      WedgeMin, WedgeMax);

            var inner = new Vector2(w - wedge, 0f);
            var top   = new Vector2(w, 0f);
            var down  = new Vector2(w, wedge);

            DrawColoredPolygon(new[] { inner, top, down }, DoloTokens.DoraGold);
            DrawLine(inner, down, Seam, 1.5f);
        }
    }

    /// <summary>
    /// 135-degree hatch at 3px on a 6px pitch, drawn across furiten wait tiles.
    /// Reads as "this one is barred to you" without relying on the red tint that
    /// a protanope cannot separate from the tile body.
    /// </summary>
    public partial class HatchOverlay : Control
    {
        private const float Thickness = 3f;
        private const float Pitch     = 6f;

        public override void _Ready()
        {
            MouseFilter  = MouseFilterEnum.Ignore;
            ClipContents = true;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Without this the hatch draws once at size 0 and never renders at all.
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            float w = Size.X, h = Size.Y;

            // 135 degrees: run bottom-left to top-right. Start the sweep far enough
            // left that the first line still crosses the top-left corner.
            for (float x = -h; x < w + h; x += Pitch)
            {
                DrawLine(new Vector2(x, h), new Vector2(x + h, 0f),
                         DoloTokens.FuritenHatch, Thickness);
            }
        }
    }

    /// <summary>
    /// Dashed rectangular outline. Replaces the solid cyan fill that used to mark
    /// a hover match, so the cue is a dash pattern first and a hue second.
    /// </summary>
    public partial class DashedRing : Control
    {
        private const float Dash      = 6f;
        private const float Gap       = 4f;
        private const float Thickness = 3f;
        private const float Inset     = 1.5f;

        private Color _color = DoloTokens.RiichiCyan;

        /// <summary>Ring colour. Kept settable so the same shape serves several states.</summary>
        public Color RingColor
        {
            get => _color;
            set { _color = value; QueueRedraw(); }
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Redraw once the container has given the ring a real size (starts at 0).
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            float w = Size.X - Inset * 2f;
            float h = Size.Y - Inset * 2f;
            if (w <= 0 || h <= 0) return;

            var topLeft     = new Vector2(Inset, Inset);
            var topRight    = new Vector2(Inset + w, Inset);
            var bottomRight = new Vector2(Inset + w, Inset + h);
            var bottomLeft  = new Vector2(Inset, Inset + h);

            DrawDashedEdge(topLeft, topRight);
            DrawDashedEdge(topRight, bottomRight);
            DrawDashedEdge(bottomRight, bottomLeft);
            DrawDashedEdge(bottomLeft, topLeft);
        }

        private void DrawDashedEdge(Vector2 from, Vector2 to)
        {
            float length = from.DistanceTo(to);
            if (length <= 0f) return;

            var direction = (to - from) / length;
            for (float travelled = 0f; travelled < length; travelled += Dash + Gap)
            {
                float dashEnd = Mathf.Min(travelled + Dash, length);
                DrawLine(from + direction * travelled,
                         from + direction * dashEnd,
                         _color, Thickness);
            }
        }
    }

    /// <summary>
    /// The claim countdown, drawn as a ring around the discarded tile rather than as a
    /// bar somewhere else on screen. The thing running out of time is that tile, so the
    /// timer belongs on it - the player never has to look away from the decision to see
    /// how long is left.
    /// </summary>
    public partial class CountdownRing : Control
    {
        private const float Thickness = 3f;

        private float _fraction = 1f;

        /// <summary>How much time is left, 0..1. Linear over the window.</summary>
        public float Fraction
        {
            get => _fraction;
            set { _fraction = Mathf.Clamp(value, 0f, 1f); QueueRedraw(); }
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            // Redraw once the container has given the ring a real size (starts at 0).
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            var centre = Size * 0.5f;
            float radius = Mathf.Min(Size.X, Size.Y) * 0.5f + 4f;
            if (radius <= 0f) return;

            // The full track, so the ring reads as "time left out of a whole"
            // rather than as an arc of unknown length.
            DrawArc(centre, radius, 0f, Mathf.Tau, 40, DoloTokens.Hairline(0.25f), Thickness);

            if (_fraction <= 0f) return;

            // Sweep clockwise from twelve o'clock.
            float start = -Mathf.Pi * 0.5f;
            float end   = start + Mathf.Tau * _fraction;

            // Colour is a courtesy for players who can see it; the arc length is the
            // information, and it survives greyscale on its own.
            var tint = _fraction switch
            {
                < 0.25f => DoloTokens.FuritenRed,
                < 0.50f => DoloTokens.DoraGold,
                _       => DoloTokens.Brass,
            };

            DrawArc(centre, radius, start, end, 40, tint, Thickness + 1f);
        }
    }

    /// <summary>
    /// The riichi stick laid on the felt: 52 x 5 ivory with a 1px dark outline.
    /// One per declaring seat, positioned by TableFelt inside that seat's wedge.
    /// </summary>
    public partial class RiichiStick : Control
    {
        public const int StickWidth  = 52;
        public const int StickHeight = 5;

        public override void _Ready()
        {
            MouseFilter       = MouseFilterEnum.Ignore;
            CustomMinimumSize = new Vector2(StickWidth, StickHeight);
            Size              = new Vector2(StickWidth, StickHeight);
        }

        public override void _Draw()
        {
            var body = new Rect2(Vector2.Zero, new Vector2(StickWidth, StickHeight));
            DrawRect(body, DoloTokens.RiichiStick);
            DrawRect(body, new Color(0.08f, 0.06f, 0.05f, 0.9f), filled: false, width: 1f);

            // The single red dot that marks a 1,000-point stick.
            DrawCircle(new Vector2(StickWidth * 0.5f, StickHeight * 0.5f), 1.6f,
                       new Color("#c0453f"));
        }
    }
}
