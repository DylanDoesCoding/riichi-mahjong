// =============================================================================
// RiverGeometry.cs
// How many discards a river can actually show.
//
// The headline bug of the redesign (P0.1) was a river silently dropping discards:
// a Button's stylebox margins inflated each tile's minimum size, so six columns
// became four and capacity fell from 24 to 16 with no indication. That was a
// layout-minimum interaction only a played game surfaced. This puts the capacity
// arithmetic in Core, without a Godot dependency, so it is a fast unit assertion
// instead: it encodes why the river is sized the way it is and fails loudly if a
// future tile-size or separation change regresses it below a full hand.
//
// A hand played to exhaustion produces 17-18 discards per seat, and a riichi
// declaration rotates a tile wider and eats a column in its row, so the desktop
// river wants real headroom over 18 — 6x4 = 24. DoloLayout reads these metrics so
// the numbers the UI lays out with and the numbers this asserts on are the same.
// =============================================================================

using System;

namespace RiichiMahjong.Core
{
    /// <summary>
    /// One river's measurements: the inner rect it occupies and the tile it tiles with.
    /// Tiles flow left-to-right, top-to-bottom with <see cref="Separation"/> between them.
    /// </summary>
    public readonly record struct RiverMetrics(
        int RectWidth, int RectHeight, int TileWidth, int TileHeight, int Separation);

    public static class RiverGeometry
    {
        /// <summary>Desktop (1920x1080 reference): 178x158 rect, 28x38 tiles, 2px apart → 6x4.</summary>
        public static readonly RiverMetrics Desktop = new(178, 158, 28, 38, 2);

        /// <summary>Touch (896x414 reference): 194x76 rect, 20x27 tiles, 4px apart.</summary>
        public static readonly RiverMetrics Touch = new(194, 76, 20, 27, 4);

        /// <summary>
        /// How many tiles of size <paramref name="tile"/> fit across <paramref name="extent"/>
        /// with <paramref name="separation"/> between them. N tiles occupy
        /// <c>N*tile + (N-1)*sep</c>, so the largest N that fits is
        /// <c>floor((extent + sep) / (tile + sep))</c>.
        /// </summary>
        public static int Fit(int extent, int tile, int separation)
        {
            int pitch = tile + separation;
            if (pitch <= 0) return 0;
            return Math.Max(0, (extent + separation) / pitch);
        }

        /// <summary>The columns, rows and total tiles a river of these metrics can show.</summary>
        public static (int columns, int rows, int capacity) Capacity(in RiverMetrics m)
        {
            int columns = Fit(m.RectWidth, m.TileWidth, m.Separation);
            int rows    = Fit(m.RectHeight, m.TileHeight, m.Separation);
            return (columns, rows, columns * rows);
        }
    }
}
