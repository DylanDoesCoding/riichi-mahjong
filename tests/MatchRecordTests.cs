// =============================================================================
// MatchRecordTests.cs
// Tests for placement scoring: uma, oka and the arithmetic the results screen
// shows underneath every placement figure.
//
// This is worth testing precisely because the results screen promises to show
// its working. If the sum printed under a number does not add up to that
// number, the screen is worse than one that showed nothing.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RiichiMahjong.Core;

static class MatchRecordTests
{
    public static (int pass, int fail) Run()
    {
        Console.WriteLine("\n[ Placement scoring ]\n");
        int pass = 0, fail = 0;

        void Test(string name, bool result)
        {
            Console.WriteLine($"  {(result ? "✓" : "✗")}  {name}");
            if (result) pass++; else fail++;
        }

        var names = new[] { "You", "CPU 1", "CPU 2", "CPU 3" };

        // =====================================================================
        // Rules
        // =====================================================================

        var rules = new MatchRules();
        Test("Rules: 25,000 start", rules.StartingPoints == 25000);
        Test("Rules: 30,000 return", rules.ReturnPoints == 30000);
        Test("Rules: oka pot is 20", Math.Abs(rules.Oka - 20f) < 0.001f);
        Test("Rules: uma is 20/10/-10/-20",
            rules.Uma.SequenceEqual(new[] { 20f, 10f, -10f, -20f }));

        // =====================================================================
        // A worked game
        // =====================================================================

        var record = new MatchRecord(names);
        var final  = new[] { 40000, 28000, 20000, 12000 };
        var results = record.Settle(final);

        Test("Settle: four seats returned", results.Count == 4);
        Test("Settle: ordered by score descending",
            results[0].Seat == 0 && results[1].Seat == 1
            && results[2].Seat == 2 && results[3].Seat == 3);
        Test("Settle: placements are 1..4",
            results.Select(r => r.Placement).SequenceEqual(new[] { 1, 2, 3, 4 }));

        // First: (40,000 - 30,000)/1000 = +10, uma +20, oka +20 = +50
        Test("First: raw is +10", Math.Abs(results[0].RawPoints - 10f) < 0.001f);
        Test("First: uma is +20", Math.Abs(results[0].Uma - 20f) < 0.001f);
        Test("First: oka is +20", Math.Abs(results[0].Oka - 20f) < 0.001f);
        Test("First: placement points are +50",
            Math.Abs(results[0].PlacementPoints - 50f) < 0.001f);

        // Second: -2 + 10 = +8. Third: -10 - 10 = -20. Fourth: -18 - 20 = -38.
        Test("Second: placement points are +8",
            Math.Abs(results[1].PlacementPoints - 8f) < 0.001f);
        Test("Third: placement points are -20",
            Math.Abs(results[2].PlacementPoints + 20f) < 0.001f);
        Test("Fourth: placement points are -38",
            Math.Abs(results[3].PlacementPoints + 38f) < 0.001f);

        // The whole table must balance, or points have been created or destroyed.
        float total = results.Sum(r => r.PlacementPoints);
        Test("Placement points sum to zero", Math.Abs(total) < 0.001f);

        // Only first place takes the oka pot.
        Test("Oka goes to first place only",
            results.Skip(1).All(r => Math.Abs(r.Oka) < 0.001f));

        // =====================================================================
        // The arithmetic must add up to the figure it sits under
        // =====================================================================

        foreach (var r in results)
        {
            float sum = r.RawPoints + r.Uma + r.Oka;
            Test($"Arithmetic reconciles for place {r.Placement}",
                Math.Abs(sum - r.PlacementPoints) < 0.001f);
        }

        Test("Arithmetic string shows uma and oka for first",
            results[0].Arithmetic.Contains("uma") && results[0].Arithmetic.Contains("oka"));
        Test("Arithmetic string omits oka for second",
            !results[1].Arithmetic.Contains("oka"));

        // =====================================================================
        // Ties break by seat order
        // =====================================================================

        var tied = new MatchRecord(names).Settle(new[] { 25000, 25000, 25000, 25000 });
        Test("Ties: broken by seat order",
            tied.Select(r => r.Seat).SequenceEqual(new[] { 0, 1, 2, 3 }));
        Test("Ties: still sum to zero",
            Math.Abs(tied.Sum(r => r.PlacementPoints)) < 0.001f);

        // =====================================================================
        // Accumulation
        // =====================================================================

        var running = new MatchRecord(names);
        Test("Trajectory starts at the starting score",
            running.Trajectory.Count == 1 && running.Trajectory[0].All(v => v == 25000));

        running.RecordRiichi(0);
        running.RecordRiichi(0);
        running.RecordRiichi(2);

        running.RecordHand(new HandLogEntry
        {
            Label      = "East 1",
            WinnerSeat = 0,
            LoserSeat  = 2,
            Han        = 3,
            Deltas     = new[] { 5800, 0, -5800, 0 },
            Totals     = new[] { 30800, 25000, 19200, 25000 },
        });
        running.RecordHand(new HandLogEntry
        {
            Label  = "East 2",
            IsDraw = true,
            Deltas = new[] { 1500, -1500, 1500, -1500 },
            Totals = new[] { 32300, 23500, 20700, 23500 },
        });

        Test("Hand log records both hands", running.HandCount == 2);
        Test("Trajectory has start plus one point per hand", running.Trajectory.Count == 3);
        Test("Trajectory tracks totals", running.Trajectory[2][0] == 32300);

        var accumulated = running.Settle(new[] { 32300, 23500, 20700, 23500 });
        var you = accumulated.First(r => r.Seat == 0);
        Test("Wins counted", you.Wins == 1);
        Test("Riichi declarations counted", you.Riichis == 2);
        Test("Deal-ins counted against the discarder",
            accumulated.First(r => r.Seat == 2).DealIns == 1);
        Test("A draw is not counted as a win",
            accumulated.Sum(r => r.Wins) == 1);
        Test("Record line reads as expected",
            you.RecordLine == "1 win · 0 deal-ins · riichi x2");

        return (pass, fail);
    }
}
