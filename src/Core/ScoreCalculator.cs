// =============================================================================
// ScoreCalculator.cs
// Converts a winning hand's yaku + dora into a final point payment.
//
// Riichi Mahjong scoring works in two steps:
//   Step 1 — Count Fan (doubles) from yaku + dora
//   Step 2 — Calculate Fu (minipoints) from hand structure
//   Step 3 — Apply limit hands (mangan and above) if fan >= 5
//   Step 4 — Calculate base points: fu × 2^(fan+2)
//   Step 5 — Determine payments (tsumo splits, ron flat, east multiplier)
//   Step 6 — Add counter bonuses and riichi bet collection
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
    // -------------------------------------------------------------------------
    // Result types
    // -------------------------------------------------------------------------

    /// <summary>Minipoint (fu) breakdown for display and verification.</summary>
    public class FuBreakdown
    {
        public int Base        { get; set; }  // 20 or 30 depending on win method / hand type
        public int MeldsFu     { get; set; }  // Fu from pons/kans
        public int PairFu      { get; set; }  // Fu from the pair (value tiles)
        public int WaitFu      { get; set; }  // Fu from wait type (edge/closed/single)
        public int TsumoFu     { get; set; }  // 2 fu for non-pinfu tsumo
        public int Total       => RoundUpToTen(Base + MeldsFu + PairFu + WaitFu + TsumoFu);

        private static int RoundUpToTen(int value) => (int)Math.Ceiling(value / 10.0) * 10;
    }

    /// <summary>The limit hand classification based on fan count.</summary>
    public enum HandLimit
    {
        None,           // Under 5 fan — use fu-based calculation
        Mangan,         // 5 fan
        Haneman,        // 6–7 fan
        Baiman,         // 8–10 fan
        Sanbaiman,      // 11–12 fan
        Yakuman,        // 13 fan (or yakuman)
        DoubleYakuman,  // 26 fan (double yakuman)
    }

    /// <summary>Complete score calculation result for a winning hand.</summary>
    public class ScoreResult
    {
        public int          TotalFan          { get; set; }
        public FuBreakdown  Fu                { get; set; } = new();
        public HandLimit    Limit             { get; set; }
        public bool         IsDealer          { get; set; }
        public WinMethod    WinMethod         { get; set; }

        // Payment amounts
        public int          RonPayment        { get; set; }  // Discarder pays this (ron)
        public int          TsumoPaymentEast  { get; set; }  // East pays this (tsumo)
        public int          TsumoPaymentOther { get; set; }  // Each non-East pays this (tsumo)

        // Bonuses collected on top of base payment
        public int          CounterBonus      { get; set; }  // +300 per counter (ron) / +100 per counter per player (tsumo)
        public int          RiichiBetsWon     { get; set; }  // Total riichi bets collected (1000 each)

        /// <summary>Total points won by the winner (sum of all payments received).</summary>
        public int TotalPointsWon =>
            WinMethod == WinMethod.Ron
                ? RonPayment + CounterBonus + RiichiBetsWon
                : TsumoPaymentEast + (TsumoPaymentOther * 2) + CounterBonus + RiichiBetsWon;
    }

    // -------------------------------------------------------------------------
    // ScoreCalculator
    // -------------------------------------------------------------------------

    public static class ScoreCalculator
    {
        // ---- Base point values for limit hands ----
        // "Basic points" = base × 4 for dealer ron, base × 6 for non-dealer ron
        private const int ManganBase    = 2000;
        private const int HanemanBase   = 3000;
        private const int BaimanBase    = 4000;
        private const int SanbaimanBase = 6000;
        private const int YakumanBase        = 8000;
        private const int DoubleYakumanBase  = 16000;

        /// <summary>
        /// Full scoring calculation for a winning hand.
        /// </summary>
        /// <param name="decomp">The winning hand decomposition.</param>
        /// <param name="yakuResult">Yaku detected by YakuChecker.</param>
        /// <param name="ctx">Game context (win method, seat, round, dora counts etc.).</param>
        /// <param name="counters">Number of counters (honba sticks) on the table.</param>
        /// <param name="riichiBetsOnTable">Number of 1,000-point riichi sticks on the table.</param>
        public static ScoreResult Calculate(
            HandDecomposition decomp,
            YakuCheckResult   yakuResult,
            YakuContext       ctx,
            int               counters        = 0,
            int               riichiBetsOnTable = 0)
        {
            var result = new ScoreResult
            {
                IsDealer  = ctx.IsDealer,
                WinMethod = ctx.WinMethod,
            };

            // ---- Step 1: Total fan ----
            int yakuFan  = yakuResult.YakuFan;
            int dorFan   = ctx.DoraCount + ctx.KanDoraCount + (ctx.IsRiichi ? ctx.UraDoraCount : 0);
            result.TotalFan = yakuFan + dorFan;

            // ---- Step 2: Fu ----
            result.Fu = CalcFu(decomp, ctx);
            int fu    = result.Fu.Total;

            // ---- Step 3: Limit classification ----
            result.Limit = ClassifyLimit(result.TotalFan, yakuResult.IsYakuman, yakuResult.IsDoubleYakuman);

            // ---- Step 4 & 5: Payments ----
            if (result.Limit == HandLimit.None)
                CalcNormalPayments(result, fu);
            else
                CalcLimitPayments(result);

            // ---- Step 6: Counters and riichi bets ----
            if (ctx.WinMethod == WinMethod.Ron)
                result.CounterBonus = counters * 300;
            else
                result.CounterBonus = counters * 100 * 3;  // 100 per player × 3 opponents

            result.RiichiBetsWon = riichiBetsOnTable * 1000;

            return result;
        }

        // =====================================================================
        // Fu (minipoints) calculation
        // =====================================================================

        private static FuBreakdown CalcFu(HandDecomposition decomp, YakuContext ctx)
        {
            var fu = new FuBreakdown();

            // Seven Pairs: fixed 25 fu (no other minipoints apply)
            if (decomp.IsSevenPairs) { fu.Base = 25; return fu; }

            // Thirteen Orphans: no fu calculation (yakuman)
            if (decomp.IsThirteenOrphans) { fu.Base = 30; return fu; }

            // ---- Base fu ----
            bool isOpen = decomp.Sets.Any(s => s.IsOpen);
            if (ctx.WinMethod == WinMethod.Ron && !isOpen)
                fu.Base = 30;  // Concealed ron
            else
                fu.Base = 20;  // Open hand or tsumo

            // ---- Meld fu ----
            foreach (var meld in decomp.Sets)
            {
                fu.MeldsFu += MeldFu(meld);
            }

            // ---- Pair fu ----
            var pair = decomp.Pair.Lead;
            if (pair.IsHonour)
            {
                bool isSeatWind  = pair == Tile.Wind(ctx.SeatWind);
                bool isRoundWind = pair == Tile.Wind(ctx.RoundWind);
                if (pair.Suit == TileSuit.Dragon || isSeatWind || isRoundWind)
                    fu.PairFu = 2;
            }

            // ---- Wait fu ----
            fu.WaitFu = WaitFu(decomp, ctx.WinningTile);

            // ---- Tsumo fu ----
            // 2 fu for tsumo, EXCEPT pinfu tsumo (which gives 20 fu base with no tsumo fu)
            bool isPinfu = ctx.WinMethod == WinMethod.Tsumo
                && fu.WaitFu == 0 && fu.PairFu == 0 && fu.MeldsFu == 0;
            if (ctx.WinMethod == WinMethod.Tsumo && !isPinfu)
                fu.TsumoFu = 2;

            // Pinfu tsumo special case: hand scores 20 fu
            if (isPinfu && ctx.WinMethod == WinMethod.Tsumo)
                fu.Base = 20;

            return fu;
        }

        /// <summary>Fu value for a single meld (pon or kan).</summary>
        private static int MeldFu(Meld meld)
        {
            if (meld.Type == MeldType.Chi)  return 0;  // Sequences give no fu
            if (meld.Type == MeldType.Pair) return 0;  // Pair fu handled separately

            bool th     = meld.Lead.IsTerminalOrHonour;   // Terminal or honour?
            bool isOpen = meld.Source != ClaimSource.None; // Has a discard source = open

            return meld.Type switch
            {
                // Pon: open = 2/4, concealed = 4/8
                MeldType.Pon         => isOpen ? (th ? 4 : 2) : (th ? 8 : 4),
                // Open Kan (called or extended): 8/16
                MeldType.KanOpen     => th ? 16 : 8,
                MeldType.KanExtended => th ? 16 : 8,
                // Concealed Kan: 16/32
                MeldType.KanClosed   => th ? 32 : 16,
                _                    => 0,
            };
        }

        /// <summary>
        /// Fu for the wait type. Checks how the winning tile completed the hand.
        /// </summary>
        private static int WaitFu(HandDecomposition decomp, Tile? winTile)
        {
            if (winTile == null) return 0;

            // Check each set — find which one the winning tile completed
            foreach (var meld in decomp.Sets)
            {
                if (meld.Type != MeldType.Chi) continue;
                var tiles = meld.Tiles;

                // Only check if winning tile is in this chi
                if (!tiles.Contains(winTile)) continue;

                int low = tiles.Min(t => t.Value);
                int high = tiles.Max(t => t.Value);

                // Edge wait (penchan): winning tile is 3 in 1-2-3, or 7 in 7-8-9
                if ((winTile.Value == 3 && low == 1) || (winTile.Value == 7 && high == 9))
                    return 2;

                // Closed wait (kanchan): winning tile is middle of sequence (e.g. 4 in 3-4-5 wait)
                if (winTile.Value != low && winTile.Value != high)
                    return 2;

                // Two-sided wait = 0 fu
                return 0;
            }

            // Pair wait (single tile completing the pair = shanpon or tanki)
            // Tanki: winning tile IS the pair
            if (winTile == decomp.Pair.Lead)
                return 2;

            // Shanpon (dual-pon wait): 0 fu
            return 0;
        }

        // =====================================================================
        // Payment calculations
        // =====================================================================

        private static HandLimit ClassifyLimit(int totalFan, bool isYakuman, bool isDoubleYakuman = false)
        {
            if (isDoubleYakuman || totalFan >= 26) return HandLimit.DoubleYakuman;
            if (isYakuman       || totalFan >= 13) return HandLimit.Yakuman;
            return totalFan switch
            {
                >= 11 => HandLimit.Sanbaiman,
                >= 8  => HandLimit.Baiman,
                >= 6  => HandLimit.Haneman,
                >= 5  => HandLimit.Mangan,
                _     => HandLimit.None,
            };
        }

        /// <summary>Calculate payments for non-limit hands using fu × 2^(fan+2) formula.</summary>
        private static void CalcNormalPayments(ScoreResult result, int fu)
        {
            int fan = result.TotalFan;

            // Basic points = fu × 2^(fan+2)
            // Cap at mangan if the result would exceed mangan
            int basic = Math.Min(fu * (int)Math.Pow(2, fan + 2), ManganBase);

            if (result.IsDealer)
            {
                // Dealer ron: discarder pays basic × 6
                result.RonPayment = RoundUp100(basic * 6);
                // Dealer tsumo: all three pay basic × 2
                result.TsumoPaymentEast  = 0;  // Not applicable
                result.TsumoPaymentOther = RoundUp100(basic * 2);
            }
            else
            {
                // Non-dealer ron: discarder pays basic × 4
                result.RonPayment = RoundUp100(basic * 4);
                // Non-dealer tsumo: East pays basic × 2, others pay basic × 1
                result.TsumoPaymentEast  = RoundUp100(basic * 2);
                result.TsumoPaymentOther = RoundUp100(basic * 1);
            }
        }

        /// <summary>Calculate payments for limit hands (mangan and above).</summary>
        private static void CalcLimitPayments(ScoreResult result)
        {
            int basic = result.Limit switch
            {
                HandLimit.Mangan        => ManganBase,
                HandLimit.Haneman       => HanemanBase,
                HandLimit.Baiman        => BaimanBase,
                HandLimit.Sanbaiman     => SanbaimanBase,
                HandLimit.Yakuman       => YakumanBase,
                HandLimit.DoubleYakuman => DoubleYakumanBase,
                _ => ManganBase,
            };

            if (result.IsDealer)
            {
                result.RonPayment        = basic * 6;
                result.TsumoPaymentOther = basic * 2;
                result.TsumoPaymentEast  = 0;
            }
            else
            {
                result.RonPayment        = basic * 4;
                result.TsumoPaymentEast  = basic * 2;
                result.TsumoPaymentOther = basic * 1;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>Round up to the nearest 100 (as required by the payment rules).</summary>
        private static int RoundUp100(int value) => (int)Math.Ceiling(value / 100.0) * 100;

        /// <summary>Human-readable limit name for display.</summary>
        public static string LimitName(HandLimit limit) => limit switch
        {
            HandLimit.Mangan        => "Mangan",
            HandLimit.Haneman       => "Haneman",
            HandLimit.Baiman        => "Baiman",
            HandLimit.Sanbaiman     => "Sanbaiman",
            HandLimit.Yakuman       => "Yakuman",
            HandLimit.DoubleYakuman => "Double Yakuman",
            _                       => "",
        };
    }
}
