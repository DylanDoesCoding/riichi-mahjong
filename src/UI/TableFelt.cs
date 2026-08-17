// =============================================================================
// TableFelt.cs
// The playing surface underneath everything else on the game table.
//
// Pass 02 splits the felt along both diagonals into four triangles, one per
// seat, so each player has a visible patch of table that is theirs.  That wedge
// is what the cosmetics picker (pass 04) recolours, and it is where a player's
// prop sits.
//
// A wedge is keyed to the *visual* seat index, not the absolute seat wind, so
// it always reads as "the wedge in front of that player" on every client even
// though each client rotates the seats to put itself at the bottom.
//
// This node draws below the hands, the rivers and the HUD, and never takes
// input - it is scenery, not a control.
// =============================================================================

using Godot;

namespace RiichiMahjong.UI
{
    public partial class TableFelt : Control
    {
        /// <summary>Visual seat order, matching HUD: 0 = self (south), 1 = right, 2 = top, 3 = left.</summary>
        public const int SeatSelf  = 0;
        public const int SeatRight = 1;
        public const int SeatTop   = 2;
        public const int SeatLeft  = 3;

        private const float DiagonalWidth = 2f;

        // Riichi sticks lie on bare felt between the centre plaque and each river.
        // Side seats get the stick turned to face their own edge of the table.
        private static readonly Vector2[] StickOffsets =
        {
            new(-26,  132),
            new(352,  -26),
            new(-26, -140),
            new(-404, -26),
        };

        private static readonly float[] StickRotations = { 0f, 90f, 0f, 90f };

        // ---- Per-seat state --------------------------------------------------

        private readonly Color[] _surfaces =
        {
            DoloTokens.FeltQuiet,
            DoloTokens.FeltQuiet,
            DoloTokens.FeltQuiet,
            DoloTokens.FeltQuiet,
        };

        private readonly TextureRect[] _props        = new TextureRect[4];
        private readonly DashedRing[]  _propPockets  = new DashedRing[4];
        private readonly RiichiStick[] _riichiSticks = new RiichiStick[4];

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;

            BuildPropPockets();
            BuildRiichiSticks();

            Resized += QueueRedraw;
        }

        public override void _Draw()
        {
            var centre = Size * 0.5f;
            var topLeft     = Vector2.Zero;
            var topRight    = new Vector2(Size.X, 0f);
            var bottomRight = Size;
            var bottomLeft  = new Vector2(0f, Size.Y);

            // One triangle per seat, sharing the table centre as their apex.
            DrawColoredPolygon(new[] { centre, bottomLeft,  bottomRight }, _surfaces[SeatSelf]);
            DrawColoredPolygon(new[] { centre, bottomRight, topRight    }, _surfaces[SeatRight]);
            DrawColoredPolygon(new[] { centre, topRight,    topLeft     }, _surfaces[SeatTop]);
            DrawColoredPolygon(new[] { centre, topLeft,     bottomLeft  }, _surfaces[SeatLeft]);

            // A 2px brass line on each diagonal, so the four wedges read as a
            // division of one table rather than four unrelated rectangles.
            DrawLine(topLeft,  bottomRight, DoloTokens.HairlineMedium, DiagonalWidth);
            DrawLine(topRight, bottomLeft,  DoloTokens.HairlineMedium, DiagonalWidth);
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Set one seat's wedge colour. Called by the cosmetics system with that
        /// player's chosen surface; defaults to the quiet felt tone.
        /// </summary>
        public void SetSeatSurface(int visualSeat, Color surface)
        {
            if (!IsSeat(visualSeat)) return;
            _surfaces[visualSeat] = surface;
            QueueRedraw();
        }

        /// <summary>
        /// Place a player's prop in their pocket. Passing null leaves the dashed
        /// placeholder showing, which is what the three unfinished props get.
        /// </summary>
        public void SetSeatProp(int visualSeat, Texture2D? prop)
        {
            if (!IsSeat(visualSeat)) return;

            _props[visualSeat].Texture      = prop;
            _props[visualSeat].Visible      = prop != null;
            _propPockets[visualSeat].Visible = prop == null;
        }

        /// <summary>Show or hide the 1,000-point stick a riichi declaration puts on the table.</summary>
        public void SetRiichiStick(int visualSeat, bool declared)
        {
            if (!IsSeat(visualSeat)) return;
            _riichiSticks[visualSeat].Visible = declared;
        }

        /// <summary>Clear every riichi stick. Called at the start of each hand.</summary>
        public void ClearRiichiSticks()
        {
            foreach (var stick in _riichiSticks) stick.Visible = false;
        }

        /// <summary>Reset every wedge to the default felt tone.</summary>
        public void ResetSurfaces()
        {
            for (int seat = 0; seat < _surfaces.Length; seat++)
                _surfaces[seat] = DoloTokens.FeltQuiet;
            QueueRedraw();
        }

        // =====================================================================
        // Construction
        // =====================================================================

        private void BuildPropPockets()
        {
            var offsets = DoloLayout.PropOffsets;
            int propSize = DoloLayout.PropSize;

            for (int seat = 0; seat < 4; seat++)
            {
                var offset = offsets[seat];

                // The dashed pocket shows wherever a prop has not been supplied.
                // Coffee, teapot and snack bowl are still placeholders.
                var pocket = new DashedRing { RingColor = DoloTokens.HairlineMedium };
                PlaceAtCentreOffset(pocket, offset, propSize, propSize);
                AddChild(pocket);
                _propPockets[seat] = pocket;

                var prop = new TextureRect
                {
                    ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = MouseFilterEnum.Ignore,
                    Visible     = false,

                    // Knocked down so the sprite reads as an object on the felt
                    // rather than a sticker pasted over it.
                    SelfModulate = new Color(0.88f, 0.88f, 0.88f),
                };
                PlaceAtCentreOffset(prop, offset, propSize, propSize);
                AddChild(prop);
                _props[seat] = prop;
            }
        }

        private void BuildRiichiSticks()
        {
            for (int seat = 0; seat < 4; seat++)
            {
                var stick = new RiichiStick { Visible = false };
                PlaceAtCentreOffset(stick, StickOffsets[seat],
                                    RiichiStick.StickWidth, RiichiStick.StickHeight);

                stick.PivotOffset     = new Vector2(RiichiStick.StickWidth * 0.5f,
                                                    RiichiStick.StickHeight * 0.5f);
                stick.RotationDegrees = StickRotations[seat];

                AddChild(stick);
                _riichiSticks[seat] = stick;
            }
        }

        /// <summary>
        /// Anchor a child to the table centre and offset it, so the whole felt
        /// keeps its geometry when the viewport is a shape other than 16:9.
        /// </summary>
        private static void PlaceAtCentreOffset(Control node, Vector2 offset, int width, int height)
        {
            node.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            node.OffsetLeft   = offset.X;
            node.OffsetTop    = offset.Y;
            node.OffsetRight  = offset.X + width;
            node.OffsetBottom = offset.Y + height;
        }

        private static bool IsSeat(int visualSeat) => visualSeat >= 0 && visualSeat < 4;
    }
}
