// =============================================================================
// MatchRecord.cs
// What happened over a whole game, as opposed to what is happening in this hand.
//
// The results screen needs facts nothing was keeping: how many hands each seat
// won, how often they dealt in, how often they declared riichi, and what every
// seat's score was after each hand. Recomputing that from the final totals is
// impossible - four players can reach the same score by very different routes -
// so it is accumulated as the game goes.
//
// This also owns the placement maths (uma and oka), because the results screen
// must be able to show the arithmetic rather than just the answer.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
    /// <summary>How one hand ended, for the hand log on the results screen.</summary>
    public class HandLogEntry
    {
        /// <summary>Round wind and dealer, e.g. "East 3".</summary>
        public string Label { get; init; } = "";

        public int  Honba      { get; init; }
        public int  WinnerSeat { get; init; } = -1;
        public int  LoserSeat  { get; init; } = -1;   // discarder on a ron; -1 otherwise
        public bool IsDraw     { get; init; }

        /// <summary>Yaku summary, e.g. "Riichi, Pinfu, Tsumo". Empty for a draw.</summary>
        public string Yaku { get; init; } = "";

        public int Han { get; init; }
        public int Fu  { get; init; }

        /// <summary>Points each seat gained or lost on this hand, indexed by seat.</summary>
        public int[] Deltas { get; init; } = new int[4];

        /// <summary>Every seat's total after this hand, indexed by seat.</summary>
        public int[] Totals { get; init; } = new int[4];
    }

    /// <summary>One seat's final standing, with the arithmetic that produced it.</summary>
    public class SeatResult
    {
        public int    Seat        { get; init; }
        public string Name        { get; init; } = "";
        public int    FinalScore  { get; init; }

        /// <summary>Points relative to the starting score.</summary>
        public int Delta { get; init; }

        /// <summary>1 = first place.</summary>
        public int Placement { get; init; }

        /// <summary>Raw score in thousands, measured against the return score.</summary>
        public float RawPoints { get; init; }

        public float Uma { get; init; }
        public float Oka { get; init; }

        /// <summary>Raw + uma + oka. What the player is actually ranked on.</summary>
        public float PlacementPoints { get; init; }

        public int Wins     { get; init; }
        public int DealIns  { get; init; }
        public int Riichis  { get; init; }

        /// <summary>One-line record, e.g. "2 wins · 0 deal-ins · riichi x2".</summary>
        public string RecordLine =>
            $"{Wins} win{(Wins == 1 ? "" : "s")} · " +
            $"{DealIns} deal-in{(DealIns == 1 ? "" : "s")} · " +
            $"riichi x{Riichis}";

        /// <summary>
        /// The placement sum written out, e.g. "+12.5 · uma 20 · oka 20". The results
        /// screen shows this under every placement figure: a player who has just lost
        /// 46.5 points should see where the number came from rather than trusting it.
        /// </summary>
        public string Arithmetic
        {
            get
            {
                var parts = new List<string> { Signed(RawPoints) };
                if (Math.Abs(Uma) > 0.001f) parts.Add($"uma {Signed(Uma)}");
                if (Math.Abs(Oka) > 0.001f) parts.Add($"oka {Signed(Oka)}");
                return string.Join(" · ", parts);
            }
        }

        private static string Signed(float value) =>
            value >= 0 ? $"+{value:0.#}" : $"{value:0.#}";
    }

    /// <summary>
    /// The scoring rules a match is settled under. Held as data so a change of ruleset
    /// is a change of values rather than a change of code.
    /// </summary>
    public class MatchRules
    {
        public int StartingPoints { get; init; } = GameState.StartingPoints;
        public int ReturnPoints   { get; init; } = GameState.ReturnPoints;

        /// <summary>Uma by placement, first to fourth, in thousands.</summary>
        public float[] Uma { get; init; } = { 20f, 10f, -10f, -20f };

        /// <summary>
        /// The oka pot, in thousands, awarded to first place. It is the gap between the
        /// return score and the starting score across all four seats, which is why a
        /// 25,000 start and a 30,000 return produce a pot of 20.
        /// </summary>
        public float Oka => (ReturnPoints - StartingPoints) * 4 / 1000f;
    }

    /// <summary>
    /// Accumulates the whole game. Fed by GameState as hands end, then read by the
    /// results screen.
    /// </summary>
    public class MatchRecord
    {
        public MatchRules Rules { get; }

        private readonly string[] _names = new string[4];
        private readonly int[]    _wins    = new int[4];
        private readonly int[]    _dealIns = new int[4];
        private readonly int[]    _riichis = new int[4];

        private readonly List<HandLogEntry> _hands = new();

        /// <summary>Every seat's score after each hand, with the starting score first.</summary>
        private readonly List<int[]> _trajectory = new();

        public MatchRecord(IReadOnlyList<string> names, MatchRules? rules = null)
        {
            Rules = rules ?? new MatchRules();

            for (int seat = 0; seat < 4; seat++)
                _names[seat] = seat < names.Count ? names[seat] : $"Player {seat + 1}";

            // The trajectory starts at the starting score, so a chart of it begins
            // level rather than at the first hand's outcome.
            _trajectory.Add(Enumerable.Repeat(Rules.StartingPoints, 4).ToArray());
        }

        public IReadOnlyList<HandLogEntry> Hands      => _hands;
        public IReadOnlyList<int[]>        Trajectory => _trajectory;
        public int                         HandCount  => _hands.Count;

        /// <summary>Note a riichi declaration. Counted whether or not the hand is won.</summary>
        public void RecordRiichi(int seat)
        {
            if (IsSeat(seat)) _riichis[seat]++;
        }

        /// <summary>Record one completed hand and the totals it left behind.</summary>
        public void RecordHand(HandLogEntry entry)
        {
            _hands.Add(entry);
            _trajectory.Add((int[])entry.Totals.Clone());

            if (IsSeat(entry.WinnerSeat)) _wins[entry.WinnerSeat]++;
            if (IsSeat(entry.LoserSeat))  _dealIns[entry.LoserSeat]++;
        }

        /// <summary>
        /// Settle the match: placement, uma and oka for every seat, ordered first to
        /// fourth.
        ///
        /// Ties are broken by seat order, which is the usual convention - the seat
        /// closest to the starting dealer takes the better placement.
        /// </summary>
        public List<SeatResult> Settle(IReadOnlyList<int> finalScores)
        {
            var order = Enumerable.Range(0, 4)
                .OrderByDescending(seat => finalScores[seat])
                .ThenBy(seat => seat)
                .ToList();

            var results = new List<SeatResult>();

            for (int place = 0; place < order.Count; place++)
            {
                int seat = order[place];

                float raw = (finalScores[seat] - Rules.ReturnPoints) / 1000f;
                float uma = place < Rules.Uma.Length ? Rules.Uma[place] : 0f;

                // Oka goes to first place only. It is already accounted for in every
                // seat's raw score, which is measured against the return rather than
                // the start - so awarding it again to anyone else would double-count.
                float oka = place == 0 ? Rules.Oka : 0f;

                results.Add(new SeatResult
                {
                    Seat            = seat,
                    Name            = _names[seat],
                    FinalScore      = finalScores[seat],
                    Delta           = finalScores[seat] - Rules.StartingPoints,
                    Placement       = place + 1,
                    RawPoints       = raw,
                    Uma             = uma,
                    Oka             = oka,
                    PlacementPoints = raw + uma + oka,
                    Wins            = _wins[seat],
                    DealIns         = _dealIns[seat],
                    Riichis         = _riichis[seat],
                });
            }

            return results;
        }

        private static bool IsSeat(int seat) => seat >= 0 && seat < 4;
    }
}
