// =============================================================================
// WedgePreview.cs
// The cosmetics live preview surface: one player's wedge, drawn at 1:1.
//
// The picker used to drop the whole four-wedge TableFelt into a 420px box, which
// scaled the entire table down and left the prop at desktop table offsets — the
// centrepiece of the screen showed an X of diagonals with a prop in the corner
// (review item 22). This draws only the self wedge: a felt triangle with its
// apex away from the viewer, exactly the patch an opponent across the table sees
// in front of you, at real pixel scale so the prop and frame read true.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public partial class WedgePreview : Control
    {
        private const float DiagonalWidth = 2f;

        private Color _surface = DoloTokens.FeltQuiet;

        /// <summary>Recolour the wedge to the player's chosen surface.</summary>
        public void SetSurface(Color surface)
        {
            _surface = surface;
            QueueRedraw();
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            // The sliver of table outside the wedge stays the quiet felt tone, so the
            // two diagonals read as the edge of one table rather than a floating shape.
            DrawRect(new Rect2(Vector2.Zero, Size), DoloTokens.FeltQuiet);

            var apex = new Vector2(Size.X * 0.5f, 0f);
            var bottomLeft  = new Vector2(0f, Size.Y);
            var bottomRight = new Vector2(Size.X, Size.Y);

            DrawColoredPolygon(new[] { apex, bottomRight, bottomLeft }, _surface);
            DrawLine(apex, bottomLeft,  DoloTokens.HairlineMedium, DiagonalWidth);
            DrawLine(apex, bottomRight, DoloTokens.HairlineMedium, DiagonalWidth);
        }
    }
}
