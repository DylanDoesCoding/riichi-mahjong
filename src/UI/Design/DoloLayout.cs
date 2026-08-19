// =============================================================================
// DoloLayout.cs
// Which layout the client is drawing, and every measurement that differs
// between the two.
//
// Pass 03 is a real touch build, not a resize: hover does not exist on a phone,
// so discard becomes two taps and every rect on the table shrinks.  Rather than
// scatter "if (touch)" through five screens, both layouts are described here as
// data and the screens read whichever one is active.
//
// Desktop numbers come from pass 02, phone numbers from pass 03.  Where pass 03
// states a size (hand cell, river, plaque, nameplate chip) that size is used
// verbatim; the phone *positions* are derived from those sizes for 896 x 414,
// since the handoff gives phone rects only for the hand.
// =============================================================================

using Godot;
using RiichiMahjong.Core;

namespace RiichiMahjong.UI
{
    public enum LayoutMode
    {
        /// <summary>Pointer and hover. 1920 x 1080 reference.</summary>
        Desktop,

        /// <summary>Touch, no hover. 896 x 414 landscape reference.</summary>
        Touch,
    }

    /// <summary>How the layout mode is chosen. Auto detects the device; the other two force it.</summary>
    public enum LayoutPreference
    {
        Auto    = 0,
        Desktop = 1,
        Touch   = 2,
    }

    public static class DoloLayout
    {
        // =====================================================================
        // Mode
        // =====================================================================

        private static LayoutMode? _cached;

        /// <summary>The active layout. Resolved once, then cached until ResetCache().</summary>
        public static LayoutMode Mode => _cached ??= Resolve();

        public static bool IsTouch   => Mode == LayoutMode.Touch;
        public static bool IsDesktop => Mode == LayoutMode.Desktop;

        /// <summary>Re-resolve the mode. Call after the player changes the Settings override.</summary>
        public static void ResetCache() => _cached = null;

        private static LayoutMode Resolve()
        {
            switch (GameSettings.LayoutPreference)
            {
                case LayoutPreference.Desktop: return LayoutMode.Desktop;
                case LayoutPreference.Touch:   return LayoutMode.Touch;
            }

            // Auto: a touchscreen or a mobile export means touch. Checking both means
            // the phone layout can also be exercised on a touchscreen laptop.
            bool mobile = OS.HasFeature("mobile") || OS.HasFeature("android") || OS.HasFeature("ios");
            return mobile || DisplayServer.IsTouchscreenAvailable()
                ? LayoutMode.Touch
                : LayoutMode.Desktop;
        }

        // =====================================================================
        // Hand tiles
        // =====================================================================

        /// <summary>The tile artwork size - what the player sees.</summary>
        public static Vector2I HandArt => IsTouch ? new Vector2I(50, 68) : new Vector2I(66, 88);

        /// <summary>
        /// The hit rect, which is larger than the art. The gap between tiles is padding
        /// *inside* each cell rather than container separation, so a near-miss still
        /// lands on a tile instead of falling through to the felt.
        /// </summary>
        public static Vector2I HandCell => IsTouch ? new Vector2I(54, 82) : new Vector2I(70, 100);

        /// <summary>
        /// How far a selected tile rises. The cell is tall enough to absorb it, so the
        /// lift is headroom and the tile never clips or moves its own hit rect.
        /// </summary>
        public static int HandLift => IsTouch ? 14 : 12;

        /// <summary>Horizontal padding inside a hand cell, per side.</summary>
        public static int HandCellPadding => (HandCell.X - HandArt.X) / 2;

        /// <summary>Face-down tiles in the three opponent hands.</summary>
        public static Vector2I SideTile => IsTouch ? new Vector2I(30, 38) : new Vector2I(42, 52);

        // =====================================================================
        // Rivers
        // =====================================================================

        /// <summary>
        /// The active river measurements, in Core so the capacity arithmetic is unit-testable
        /// (see <see cref="RiverGeometry"/>). Tile size, separation and the rect dimensions
        /// are all drawn from here, so the numbers the UI lays out with and the numbers the
        /// tests assert on cannot drift apart.
        /// </summary>
        public static RiverMetrics River => IsTouch ? RiverGeometry.Touch : RiverGeometry.Desktop;

        public static Vector2I RiverTile => new(River.TileWidth, River.TileHeight);

        public static int RiverSeparation => River.Separation;

        /// <summary>River centres as offsets from the table centre, in visual seat order.</summary>
        private static Vector2[] RiverCentres => IsTouch
            ? new Vector2[] { new(0, 78),  new(207, 0), new(0, -78),  new(-207, 0) }
            : new Vector2[] { new(0, 239), new(471, 0), new(0, -261), new(-471, 0) };

        /// <summary>
        /// River rects as (l,t,r,b) offsets from the table centre, in visual seat order.
        /// Each is the seat's centre expanded to the Core rect size, so the rect the UI
        /// draws stays locked to the dimensions the capacity test checks.
        /// </summary>
        public static (float l, float t, float r, float b)[] RiverRects
        {
            get
            {
                float hw = River.RectWidth * 0.5f;
                float hh = River.RectHeight * 0.5f;
                var centres = RiverCentres;
                var rects = new (float, float, float, float)[centres.Length];
                for (int i = 0; i < centres.Length; i++)
                    rects[i] = (centres[i].X - hw, centres[i].Y - hh,
                                centres[i].X + hw, centres[i].Y + hh);
                return rects;
            }
        }

        // =====================================================================
        // Nameplates
        // =====================================================================

        /// <summary>
        /// Desktop plates carry wind, name, score, riichi stick and the furiten badge.
        /// Phone chips carry name, wind and score only - the riichi stick becomes a 4px
        /// underline and furiten a corner dot.
        /// </summary>
        public static (float l, float t, float r, float b)[] NameplateRects => IsTouch
            ? new (float, float, float, float)[]
            {
                ( 322,  145,  418,  185),   // self  - beside the hand, bottom right
                ( 322,  -20,  418,   20),   // right
                ( -48, -190,   48, -150),   // top
                (-418,  -20, -322,   20),   // left
            }
            : new (float, float, float, float)[]
            {
                (-320,  250, -160,  345),
                ( 610, -240,  770, -150),
                (-320, -365, -160, -275),
                (-770, -240, -610, -150),
            };

        public static Vector2I NameplateSize => IsTouch ? new Vector2I(96, 40) : new Vector2I(160, 90);

        // =====================================================================
        // Centre plaque
        // =====================================================================

        public static Vector2I PlaqueSize => IsTouch ? new Vector2I(150, 52) : new Vector2I(260, 136);

        // =====================================================================
        // Props
        // =====================================================================

        public static int PropSize => IsTouch ? 48 : 118;

        /// <summary>
        /// Prop pocket positions as offsets from the table centre. Each sits inside its
        /// own wedge and clear of every hand, river and plate at maximum size.
        /// </summary>
        public static Vector2[] PropOffsets => IsTouch
            ? new Vector2[]
            {
                new(-120,   70),
                new( 322,  -80),
                new(-180, -118),
                new(-370,  -80),
            }
            : new Vector2[]
            {
                new( 200,  250),
                new( 630,   80),
                new( 200, -365),
                new(-748,   80),
            };

        // =====================================================================
        // Controls
        // =====================================================================

        /// <summary>The DISCARD button on the touch info card.</summary>
        public static Vector2I DiscardButton => new(96, 44);

        /// <summary>Call buttons and other primary actions.</summary>
        public static Vector2I ActionButton => IsTouch ? new Vector2I(120, 56) : new Vector2I(150, 48);

        /// <summary>
        /// The touch info card that replaces hover: 56px tall, sits above the hand and
        /// holds what the desktop hover line said plus a DISCARD button.
        /// </summary>
        public const int InfoCardHeight = 56;
    }
}
