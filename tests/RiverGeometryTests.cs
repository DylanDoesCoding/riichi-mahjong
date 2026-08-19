// =============================================================================
// RiverGeometryTests.cs
// The river-capacity arithmetic that P0.1 was about.
//
// The headline redesign bug — a river silently dropping one to two discards by an
// exhaustive draw — was a layout-minimum interaction that only a played game
// surfaced. These assertions encode why the river is sized 6x4 and fail loudly if
// a future tile-size or separation change quietly regresses the capacity below a
// full hand. TEST_PLAN.md section C.
// =============================================================================

using System;
using RiichiMahjong.Core;

static class RiverGeometryTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n[ River geometry ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        // =====================================================================
        // The Fit helper — the largest N of (tile + sep) runs that fits an extent
        // =====================================================================

        // Six 28px tiles, 2px apart, occupy 6*28 + 5*2 = 178, so 178 fits exactly 6.
        Test("Fit: 178 fits exactly six 28px tiles at 2px", RiverGeometry.Fit(178, 28, 2) == 6);
        // One pixel short drops the sixth.
        Test("Fit: 177 fits only five", RiverGeometry.Fit(177, 28, 2) == 5);
        // One pixel of slack is not enough for a seventh.
        Test("Fit: 179 still fits six", RiverGeometry.Fit(179, 28, 2) == 6);
        Test("Fit: a zero extent fits nothing", RiverGeometry.Fit(0, 28, 2) == 0);
        Test("Fit: a degenerate zero pitch fits nothing (no divide-by-zero)",
            RiverGeometry.Fit(100, 0, 0) == 0);

        // =====================================================================
        // Desktop — the 6x4 = 24 the design chose, with headroom over a full hand
        // =====================================================================

        var (dCols, dRows, dCap) = RiverGeometry.Capacity(RiverGeometry.Desktop);
        Test("Desktop river is 6 columns", dCols == 6);
        Test("Desktop river is 4 rows", dRows == 4);
        Test("Desktop capacity is 24", dCap == 24);
        // A hand to exhaustion is 17-18 discards and a riichi rotation eats a column,
        // so the capacity must clear a full hand with room to spare.
        Test("Desktop capacity clears a full hand (>= 22)", dCap >= 22);

        // =====================================================================
        // Touch — smaller by design; lock its computed capacity so it can't slip
        // =====================================================================

        var (tCols, tRows, tCap) = RiverGeometry.Capacity(RiverGeometry.Touch);
        Test("Touch river is 8 columns", tCols == 8);
        Test("Touch river is 2 rows", tRows == 2);
        Test("Touch capacity is 16", tCap == 16);

        // =====================================================================
        // The P0.1 regression, encoded: a Button's 20px content margins inflated
        // each 28px tile's minimum width to 40px, and that is what dropped capacity
        // from 24 to 16 and clipped discards. Proving the inflated tile regresses
        // below a full hand is what makes this a guard rather than a restatement.
        // =====================================================================

        var inflated = RiverGeometry.Desktop with { TileWidth = 40 };
        var (iCols, _, iCap) = RiverGeometry.Capacity(inflated);
        Test("P0.1: a 40px-min tile drops the river to four columns", iCols == 4);
        Test("P0.1: a 40px-min tile regresses capacity below a full hand", iCap < 22);

        return (pass, fail);
    }
}
