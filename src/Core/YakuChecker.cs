// =============================================================================
// YakuChecker.cs
// Evaluates a winning hand decomposition for all applicable yaku (scoring patterns).
//
// Every winning hand MUST contain at least one yaku to be valid.
// Dora tiles are NOT yaku — they add fan but don't qualify a hand on their own.
//
// Usage:
//   var context = new YakuContext(...);
//   var result  = YakuChecker.Evaluate(decomposition, context);
//   int totalFan = result.TotalFan;
//
// The context carries everything the checker needs beyond the hand itself:
//   - Win type (tsumo/ron)
//   - Seat wind, round wind
//   - Riichi/ippatsu/double-riichi flags
//   - Special win type (rinshan, chankan, haitei, houtei)
//   - Dora counts
// =============================================================================

using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
    // -------------------------------------------------------------------------
    // Supporting types
    // -------------------------------------------------------------------------

    /// <summary>How the winning tile was obtained.</summary>
    public enum WinMethod
    {
        Tsumo,    // Self-draw from live wall
        Ron,      // Claimed from another player's discard
        Rinshan,  // Self-draw after a kan (replacement tile)
        Chankan,  // Robbed from an opponent extending a pon to kan
        Haitei,   // Self-draw on the very last tile of the live wall
        Houtei,   // Ron on the very last discard
    }

    /// <summary>
    /// Everything the yaku checker needs to know about the game state at the moment of winning.
    /// </summary>
    public class YakuContext
    {
        public WinMethod     WinMethod       { get; init; }
        public WindDirection SeatWind        { get; init; }   // The winning player's seat
        public WindDirection RoundWind       { get; init; }   // Current round wind (East/South/etc.)
        public bool          IsRiichi                { get; init; }
        public bool          IsDoubleRiichi          { get; init; }
        public bool          IsIppatsu               { get; init; }
        /// <summary>
        /// How many tiles the winning player has drawn from the live wall this hand.
        /// 0 = hasn't drawn yet (initial deal for dealer; pre-draw ron for non-dealer).
        /// </summary>
        public int           WinnerWallDrawCount     { get; init; }
        /// <summary>True if no pon/chi/kan claim has been made by any player this hand.</summary>
        public bool          FirstRoundUninterrupted { get; init; }
        public bool          IsDealer                { get; init; }   // Seat wind == Round wind (East round) AND player is East
        public int           DoraCount       { get; init; }   // Total dora in winning hand
        public int           UraDoraCount    { get; init; }   // Ura-dora (only if riichi)
        public int           KanDoraCount    { get; init; }   // Kan-dora in winning hand
        public Tile?         WinningTile     { get; init; }   // The tile that completed the hand
        public List<Meld>    OpenMelds       { get; init; } = new();
    }

    /// <summary>A single detected yaku with its fan value.</summary>
    public class YakuResult
    {
        public string Name       { get; }
        public string NameJP     { get; }
        public int    Fan        { get; }   // Fan value (1–13 for normal; 13 for yakuman)
        public bool   IsYakuman  { get; }

        public YakuResult(string name, string nameJP, int fan, bool isYakuman = false)
        {
            Name      = name;
            NameJP    = nameJP;
            Fan       = fan;
            IsYakuman = isYakuman;
        }
    }

    /// <summary>All yaku detected for one hand decomposition.</summary>
    public class YakuCheckResult
    {
        public List<YakuResult> Yaku             { get; } = new();
        public bool             IsYakuman        => Yaku.Any(y => y.IsYakuman);
        /// <summary>True when the hand qualifies for a double-yakuman variant (26-fan payout).</summary>
        public bool             IsDoubleYakuman  => Yaku.Any(y => y.IsYakuman && y.Fan >= 26);

        /// <summary>
        /// Total fan from yaku only (excludes dora).
        /// Returns 13 for regular yakuman, 26 for double yakuman.
        /// </summary>
        public int YakuFan => IsYakuman
            ? (IsDoubleYakuman ? 26 : 13)
            : Yaku.Sum(y => y.Fan);

        public bool HasYaku => Yaku.Count > 0;

        public void Add(string name, string jp, int fan, bool yakuman = false)
            => Yaku.Add(new YakuResult(name, jp, fan, yakuman));
    }

    // -------------------------------------------------------------------------
    // YakuChecker
    // -------------------------------------------------------------------------

    public static class YakuChecker
    {
        /// <summary>
        /// Evaluate all yaku for a given hand decomposition + game context.
        /// Returns a YakuCheckResult — call result.HasYaku to confirm the hand is valid.
        /// </summary>
        public static YakuCheckResult Evaluate(HandDecomposition decomp, YakuContext ctx)
        {
            var result = new YakuCheckResult();

            // ---- Yakuman checks (limit hands) — checked first ----
            // Yakuman are non-cumulative; if any yakuman applies, only yakuman count.
            CheckYakuman(decomp, ctx, result);
            if (result.IsYakuman) return result;

            // ---- Special hands ----
            if (decomp.IsSevenPairs)      { CheckSevenPairs(ctx, result);      return result; }
            if (decomp.IsThirteenOrphans) { CheckThirteenOrphans(ctx, result); return result; }

            // ---- Standard hand yaku ----
            bool open = decomp.Sets.Any(s => s.IsOpen);  // True if hand has any open melds

            CheckRiichi(ctx, result);
            CheckMenzenTsumo(ctx, open, result);
            CheckTanyao(decomp, result);
            CheckPinfu(decomp, ctx, open, result);
            CheckIipeikou(decomp, open, result);
            CheckSanshokuDoujun(decomp, open, result);
            CheckIttsu(decomp, open, result);
            CheckChanta(decomp, open, result);
            CheckFanpai(decomp, ctx, result);
            CheckSanshokuDoukou(decomp, result);
            CheckToitoi(decomp, result);
            CheckSanAnkou(decomp, ctx, result);
            CheckSanKantsu(decomp, result);
            CheckHonitsu(decomp, open, result);
            CheckChinitsu(decomp, open, result);
            CheckJunchan(decomp, open, result);
            CheckShouSangen(decomp, result);
            CheckHonroutou(decomp, result);
            CheckRyanPeikou(decomp, open, result);
            CheckSpecialWins(ctx, result);  // Rinshan, Chankan, Haitei, Houtei

            return result;
        }

        // =====================================================================
        // YAKUMAN (13 fan each)
        // =====================================================================

        private static void CheckYakuman(HandDecomposition d, YakuContext ctx, YakuCheckResult r)
        {
            // Blessing of Heaven (Tenhou) — dealer wins tsumo on initial 14-tile deal.
            // WallDrawCount == 0 means they have the dealt hand with no wall draw yet.
            if (ctx.IsDealer
                && ctx.WinnerWallDrawCount == 0
                && ctx.FirstRoundUninterrupted
                && ctx.WinMethod == WinMethod.Tsumo)
                r.Add("Blessing of Heaven", "Tenho", 13, yakuman: true);

            // Blessing of Earth (Chiihou) — non-dealer wins tsumo on their FIRST wall draw.
            if (!ctx.IsDealer
                && ctx.WinnerWallDrawCount == 1
                && ctx.FirstRoundUninterrupted
                && ctx.WinMethod == WinMethod.Tsumo)
                r.Add("Blessing of Earth", "Chiho", 13, yakuman: true);

            // Blessing of Man (Renhou) — non-dealer wins ron BEFORE their first wall draw.
            if (!ctx.IsDealer
                && ctx.WinnerWallDrawCount == 0
                && ctx.FirstRoundUninterrupted
                && ctx.WinMethod == WinMethod.Ron)
                r.Add("Blessing of Man", "Renho", 13, yakuman: true);

            if (r.IsYakuman) return;  // Tenho/Chiho/Renho found — stop here

            if (d.IsThirteenOrphans)
            {
                // 13-sided kokushi: the 13-tile waiting hand contained all 13 unique
                // terminal/honour types. Removing the winning tile leaves exactly
                // 13 distinct (suit, value) pairs — double yakuman.
                bool thirteenSided = false;
                if (ctx.WinningTile != null)
                {
                    var kokushiTiles = AllTiles(d).ToList();
                    int widx = kokushiTiles.FindIndex(
                        t => t.Suit == ctx.WinningTile.Suit && t.Value == ctx.WinningTile.Value);
                    if (widx >= 0)
                    {
                        kokushiTiles.RemoveAt(widx);
                        thirteenSided = kokushiTiles.Count == 13
                            && kokushiTiles.Select(t => (t.Suit, t.Value)).Distinct().Count() == 13;
                    }
                }
                r.Add("Thirteen Orphans", "Kokushi Musou", thirteenSided ? 26 : 13, yakuman: true);
                return;
            }

            var allMelds = AllMelds(d);
            var allTiles = AllTiles(d);

            // Four Concealed Pungs — four concealed triplets/quads
            // On ron: only valid if the pair is the single wait (the winning tile completes the pair)
            // Pair-wait (tanki) = double yakuman (Suu Ankou Tanki)
            bool fourAnkou = allMelds.Count(IsConcealedPon) == 4;
            if (fourAnkou)
            {
                bool pairWait = ctx.WinningTile != null && ctx.WinningTile == d.Pair.Lead;
                if (pairWait)
                    r.Add("Four Concealed Pungs — Pair Wait", "Suu Ankou Tanki", 26, yakuman: true);
                else if (ctx.WinMethod == WinMethod.Tsumo)
                    r.Add("Four Concealed Pungs", "Suu Ankou", 13, yakuman: true);
            }

            // Four Kongs
            if (allMelds.Count(m => m.IsKan) == 4)
                r.Add("Four Kongs", "Suu Kan Tsu", 13, yakuman: true);

            // Big Three Dragons — three dragon triplets/quads
            bool[] dragonsAsPon = { HasPon(allMelds, Tile.Dragon(DragonType.White)),
                                     HasPon(allMelds, Tile.Dragon(DragonType.Green)),
                                     HasPon(allMelds, Tile.Dragon(DragonType.Red)) };
            if (dragonsAsPon.All(x => x))
                r.Add("Big Three Dragons", "Dai Sangen", 13, yakuman: true);

            // Little Four Winds — three wind pons + wind pair
            var windPons = new[] { WindDirection.East, WindDirection.South,
                                    WindDirection.West, WindDirection.North }
                .Count(w => HasPon(allMelds, Tile.Wind(w)));
            bool windPair = new[] { WindDirection.East, WindDirection.South,
                                     WindDirection.West, WindDirection.North }
                .Any(w => d.Pair.Lead == Tile.Wind(w));
            if (windPons == 3 && windPair)
                r.Add("Little Four Winds", "Shou Suushii", 13, yakuman: true);

            // Big Four Winds — four wind pons
            if (windPons == 4)
                r.Add("Big Four Winds", "Dai Suushii", 13, yakuman: true);

            // All Green — only green tiles (2s,3s,4s,6s,8s + Green Dragon)
            if (allTiles.All(t => t.IsGreen))
                r.Add("All Green", "Ryuu Iisou", 13, yakuman: true);

            // All Terminals — only 1s and 9s
            if (allTiles.All(t => t.IsTerminal))
                r.Add("All Terminals", "Chinrouto", 13, yakuman: true);

            // All Honours — only winds and dragons
            if (allTiles.All(t => t.IsHonour))
                r.Add("All Honours", "Tsuu Iisou", 13, yakuman: true);

            // Nine Gates — 1112345678999 of one suit + one extra
            CheckNineGates(d, ctx, r);
        }

        private static void CheckNineGates(HandDecomposition d, YakuContext ctx, YakuCheckResult r)
        {
            // Must be fully concealed, all one suit (Man/Pin/Sou)
            if (d.Sets.Any(s => s.IsOpen)) return;
            var allTiles = AllTiles(d);
            if (allTiles.Count != 14) return;
            var suit = allTiles[0].Suit;
            if (suit is TileSuit.Wind or TileSuit.Dragon) return;
            if (allTiles.Any(t => t.Suit != suit)) return;

            var vals = allTiles.Select(t => t.Value).OrderBy(v => v).ToList();
            var required = new List<int> { 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9 };

            // Pure nine gates (double yakuman): the winning tile is the "extra" tile,
            // meaning the 13-tile waiting hand was exactly 1112345678999.
            if (ctx.WinningTile?.Suit == suit)
            {
                var withoutWin = new List<int>(vals);
                int widx = withoutWin.IndexOf(ctx.WinningTile.Value);
                if (widx >= 0)
                {
                    withoutWin.RemoveAt(widx);
                    if (withoutWin.SequenceEqual(required))
                    {
                        r.Add("Nine Gates — Pure", "Chuuren Pooto (Pure)", 26, yakuman: true);
                        return;
                    }
                }
            }

            // Regular nine gates: remove one tile (the extra) and see if remainder matches
            for (int skip = 0; skip < vals.Count; skip++)
            {
                var candidate = vals.Where((_, i) => i != skip).ToList();
                if (candidate.SequenceEqual(required))
                {
                    r.Add("Nine Gates", "Chuuren Pooto", 13, yakuman: true);
                    return;
                }
            }
        }

        // =====================================================================
        // SPECIAL FULL-HAND SHAPES
        // =====================================================================

        private static void CheckSevenPairs(YakuContext ctx, YakuCheckResult r)
        {
            r.Add("Seven Pairs", "Chiitoitsu", 2);
            CheckRiichi(ctx, r);
            CheckSpecialWins(ctx, r);
        }

        private static void CheckThirteenOrphans(YakuContext ctx, YakuCheckResult r)
        {
            // Already confirmed above as yakuman — this branch shouldn't be reached normally
            r.Add("Thirteen Orphans", "Kokushi Musou", 13, yakuman: true);
        }

        // =====================================================================
        // 1-FAN YAKU
        // =====================================================================

        private static void CheckRiichi(YakuContext ctx, YakuCheckResult r)
        {
            if (ctx.IsDoubleRiichi)
            {
                r.Add("Double Riichi", "Daburu Riichi", 2);
                if (ctx.IsIppatsu) r.Add("Ippatsu", "Ippatsu", 1);
            }
            else if (ctx.IsRiichi)
            {
                r.Add("Riichi", "Riichi", 1);
                if (ctx.IsIppatsu) r.Add("Ippatsu", "Ippatsu", 1);
            }
        }

        private static void CheckMenzenTsumo(YakuContext ctx, bool open, YakuCheckResult r)
        {
            // Menzen Tsumo: self-draw on a fully concealed hand.
            // Applies to standard Tsumo, Rinshan (after kan), and Haitei (last tile).
            bool isSelfDraw = ctx.WinMethod is WinMethod.Tsumo
                                           or WinMethod.Rinshan
                                           or WinMethod.Haitei;
            if (!open && isSelfDraw)
                r.Add("Menzen Tsumo", "Menzen Tsumo", 1);
        }

        private static void CheckTanyao(HandDecomposition d, YakuCheckResult r)
        {
            // All Simples: no terminals or honours in the entire hand
            bool open = d.Sets.Any(s => s.IsOpen);
            var allTiles = AllTiles(d);
            if (allTiles.All(t => t.IsSimple))
                r.Add("All Simples", "Tanyao Chuu", open ? 1 : 1);
            // Note: Tanyao is 1 fan whether open or closed under EMA rules
        }

        private static void CheckPinfu(HandDecomposition d, YakuContext ctx, bool open, YakuCheckResult r)
        {
            // Pinfu (closed only):
            //   - All sets are sequences (chi)
            //   - Pair is not a value tile (not seat wind, round wind, or dragon)
            //   - Two-sided wait (the winning tile completes a two-sided sequence wait)
            if (open) return;
            if (d.Sets.Any(s => s.Type != MeldType.Chi && s.Type != MeldType.KanClosed)) return;
            if (d.Sets.Any(s => s.IsKan)) return;  // Kan in concealed hand = no pinfu

            var pair = d.Pair.Lead;
            if (pair.IsHonour) return;  // Honour pair = no pinfu
            if (pair == Tile.Wind(ctx.SeatWind)) return;
            if (pair == Tile.Wind(ctx.RoundWind)) return;

            // Check two-sided wait on the winning tile
            if (ctx.WinningTile == null) return;
            bool twoSided = d.Sets
                .Where(s => s.Type == MeldType.Chi)
                .Any(chi => IsTwoSidedWait(chi, ctx.WinningTile));
            if (twoSided)
                r.Add("Pinfu", "Pinfu", 1);
        }

        private static bool IsTwoSidedWait(Meld chi, Tile winningTile)
        {
            // A two-sided wait: the winning tile is either end of the sequence,
            // and neither end is a terminal (1 or 9)
            var tiles  = chi.Tiles;
            var lowest = tiles.Min(t => t.Value);
            var highest = tiles.Max(t => t.Value);
            if (winningTile.Suit != tiles[0].Suit) return false;
            if (winningTile.Value == lowest  && lowest  != 1) return true;
            if (winningTile.Value == highest && highest != 9) return true;
            return false;
        }

        private static void CheckIipeikou(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Pure Double Sequence (closed only): two identical chi in the same suit
            if (open) return;
            var chis = d.Sets.Where(s => s.Type == MeldType.Chi).ToList();
            int dups = chis
                .GroupBy(c => (c.Tiles[0].Suit, c.Tiles[0].Value))
                .Count(g => g.Count() >= 2);
            if (dups >= 1) r.Add("Pure Double Sequence", "Iipeikou", 1);
        }

        private static void CheckSanshokuDoujun(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Mixed Triple Sequence: same number chi in all three suits
            var chis = d.Sets.Where(s => s.Type == MeldType.Chi).ToList();
            for (int start = 1; start <= 7; start++)
            {
                bool man = chis.Any(c => c.Lead.Suit == TileSuit.Man && c.Lead.Value == start);
                bool pin = chis.Any(c => c.Lead.Suit == TileSuit.Pin && c.Lead.Value == start);
                bool sou = chis.Any(c => c.Lead.Suit == TileSuit.Sou && c.Lead.Value == start);
                if (man && pin && sou)
                {
                    r.Add("Mixed Triple Sequence", "San Shoku Doujun", open ? 1 : 2);
                    return;
                }
            }
        }

        private static void CheckIttsu(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Pure Straight: 1-2-3, 4-5-6, 7-8-9 of the same suit
            var chis = d.Sets.Where(s => s.Type == MeldType.Chi).ToList();
            foreach (TileSuit suit in new[] { TileSuit.Man, TileSuit.Pin, TileSuit.Sou })
            {
                bool has123 = chis.Any(c => c.Lead.Suit == suit && c.Lead.Value == 1);
                bool has456 = chis.Any(c => c.Lead.Suit == suit && c.Lead.Value == 4);
                bool has789 = chis.Any(c => c.Lead.Suit == suit && c.Lead.Value == 7);
                if (has123 && has456 && has789)
                {
                    r.Add("Pure Straight", "Ittsu", open ? 1 : 2);
                    return;
                }
            }
        }

        private static void CheckChanta(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Outside Hand: every set and the pair contain a terminal or honour;
            // must have at least one sequence (chi).
            // Junchan (terminals in ALL sets, no honours required) supersedes Chanta —
            // suppress Chanta when every meld already contains a terminal.
            var allMelds = AllMelds(d);
            bool allContainTerminalOrHonour = allMelds.All(m =>
                m.Tiles.Any(t => t.IsTerminalOrHonour));
            bool hasChi    = d.Sets.Any(s => s.Type == MeldType.Chi);
            bool isJunchan = allMelds.All(m => m.Tiles.Any(t => t.IsTerminal));
            if (allContainTerminalOrHonour && hasChi && !isJunchan)
                r.Add("Outside Hand", "Chanta", open ? 1 : 2);
        }

        // =====================================================================
        // FANPAI (Value Tiles) — 1 fan each
        // =====================================================================

        private static void CheckFanpai(HandDecomposition d, YakuContext ctx, YakuCheckResult r)
        {
            var allMelds = AllMelds(d);

            // Dragon pons
            if (HasPon(allMelds, Tile.Dragon(DragonType.White)))
                r.Add("White Dragon", "Haku",   1);
            if (HasPon(allMelds, Tile.Dragon(DragonType.Green)))
                r.Add("Green Dragon", "Hatsu",  1);
            if (HasPon(allMelds, Tile.Dragon(DragonType.Red)))
                r.Add("Red Dragon",   "Chun",   1);

            // Seat wind pon
            if (HasPon(allMelds, Tile.Wind(ctx.SeatWind)))
                r.Add($"{ctx.SeatWind} (Seat Wind)", "Fanpai", 1);

            // Round wind pon (only counts separately if different from seat wind)
            if (ctx.RoundWind != ctx.SeatWind && HasPon(allMelds, Tile.Wind(ctx.RoundWind)))
                r.Add($"{ctx.RoundWind} (Round Wind)", "Fanpai", 1);
            // If seat wind == round wind, it's still only 1 fan total (per EMA rules)
            // — the check above handles this since we'd add seat wind once
        }

        // =====================================================================
        // 2-FAN YAKU
        // =====================================================================

        private static void CheckSanshokuDoukou(HandDecomposition d, YakuCheckResult r)
        {
            // Triple Pung: same number pon in all three suits
            var pons = d.Sets.Where(IsPon).ToList();
            for (int v = 1; v <= 9; v++)
            {
                bool man = pons.Any(p => p.Lead.Suit == TileSuit.Man && p.Lead.Value == v);
                bool pin = pons.Any(p => p.Lead.Suit == TileSuit.Pin && p.Lead.Value == v);
                bool sou = pons.Any(p => p.Lead.Suit == TileSuit.Sou && p.Lead.Value == v);
                if (man && pin && sou)
                {
                    r.Add("Triple Pung", "San Shoku Doukou", 2);
                    return;
                }
            }
        }

        private static void CheckToitoi(HandDecomposition d, YakuCheckResult r)
        {
            // All Pungs: all four sets are pons or kans (no chi)
            if (d.Sets.All(s => IsPon(s) || s.IsKan))
                r.Add("All Pungs", "Toitoi Hou", 2);
        }

        private static void CheckSanAnkou(HandDecomposition d, YakuContext ctx, YakuCheckResult r)
        {
            // Three Concealed Pungs: three pons/kans formed without calling a discard
            // (The winning tile may be a ron — that pon is NOT concealed)
            int count = d.Sets.Count(s =>
                IsConcealedPon(s) ||
                s.Type == MeldType.KanClosed);
            // If won by ron on a pon wait, that pon is open — deduct 1 if applicable
            // (This edge case is handled: open pon sets have IsOpen=true and won't pass IsConcealedPon)
            if (count >= 3)
                r.Add("Three Concealed Pungs", "San Ankou", 2);
        }

        private static void CheckSanKantsu(HandDecomposition d, YakuCheckResult r)
        {
            // Three Kongs: exactly three kans of any type
            if (d.Sets.Count(s => s.IsKan) >= 3)
                r.Add("Three Kongs", "San Kan Tsu", 2);
        }

        private static void CheckHonitsu(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Half Flush: one suit + honours (no mixing of suits)
            var allTiles = AllTiles(d);
            var suits = allTiles.Where(t => !t.IsHonour).Select(t => t.Suit).Distinct().ToList();
            bool hasHonour = allTiles.Any(t => t.IsHonour);
            if (suits.Count == 1 && hasHonour)
                r.Add("Half Flush", "Honitsu", open ? 2 : 3);
        }

        private static void CheckChinitsu(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Full Flush: all tiles of one suit, no honours
            var allTiles = AllTiles(d);
            if (allTiles.All(t => !t.IsHonour) &&
                allTiles.Select(t => t.Suit).Distinct().Count() == 1)
                r.Add("Full Flush", "Chinitsu", open ? 5 : 6);
        }

        private static void CheckJunchan(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Terminals in All Sets: every set and pair contain a TERMINAL (not just any honour)
            // Must have at least one chi
            var allMelds = AllMelds(d);
            bool allContainTerminal = allMelds.All(m => m.Tiles.Any(t => t.IsTerminal));
            bool hasChi = d.Sets.Any(s => s.Type == MeldType.Chi);
            // Junchan and Chanta are mutually exclusive — Junchan is the stronger one
            // (Chanta allows honours; Junchan requires pure terminals)
            if (allContainTerminal && hasChi)
                r.Add("Terminals in All Sets", "Junchan Taiyai", open ? 2 : 3);
        }

        private static void CheckShouSangen(HandDecomposition d, YakuCheckResult r)
        {
            // Little Three Dragons: two dragon pons + dragon pair
            var allMelds = AllMelds(d);
            int dragonPons = new[] { DragonType.White, DragonType.Green, DragonType.Red }
                .Count(dt => HasPon(allMelds, Tile.Dragon(dt)));
            bool dragonPair = new[] { DragonType.White, DragonType.Green, DragonType.Red }
                .Any(dt => d.Pair.Lead == Tile.Dragon(dt));
            if (dragonPons == 2 && dragonPair)
                r.Add("Little Three Dragons", "Shou Sangen", 2);
            // Note: the two dragon pons each also count as Fanpai (1 fan each) per EMA rules
        }

        private static void CheckHonroutou(HandDecomposition d, YakuCheckResult r)
        {
            // All Terminals and Honours: every tile is a terminal or honour
            var allTiles = AllTiles(d);
            if (allTiles.All(t => t.IsTerminalOrHonour))
                r.Add("All Terminals & Honours", "Honroutou", 2);
            // Honroutou combines with Toitoi (2 fan) or Seven Pairs (2 fan) for total of 4 fan
        }

        // =====================================================================
        // 3-FAN YAKU
        // =====================================================================

        private static void CheckRyanPeikou(HandDecomposition d, bool open, YakuCheckResult r)
        {
            // Twice Pure Double Sequence (closed only): two pairs of identical chi
            // This supersedes Iipeikou — don't double-count
            if (open) return;
            var chis = d.Sets.Where(s => s.Type == MeldType.Chi).ToList();
            var groups = chis
                .GroupBy(c => (c.Tiles[0].Suit, c.Tiles[0].Value))
                .Where(g => g.Count() >= 2)
                .Count();
            if (groups >= 2)
            {
                // Replace Iipeikou with RyanPeikou if it was already added
                var existing = r.Yaku.FirstOrDefault(y => y.NameJP == "Iipeikou");
                if (existing != null) r.Yaku.Remove(existing);
                r.Add("Twice Pure Double Sequence", "Ryan Peikou", 3);
            }
        }

        // =====================================================================
        // SPECIAL WIN CONDITIONS — 1 fan each
        // =====================================================================

        private static void CheckSpecialWins(YakuContext ctx, YakuCheckResult r)
        {
            switch (ctx.WinMethod)
            {
                case WinMethod.Rinshan:
                    r.Add("After a Kong", "Rinshan Kaihou", 1);
                    break;
                case WinMethod.Chankan:
                    r.Add("Robbing the Kong", "Chan Kan", 1);
                    break;
                case WinMethod.Haitei:
                    r.Add("Under the Sea", "Haitei Raoyue", 1);
                    break;
                case WinMethod.Houtei:
                    r.Add("Under the River", "Houtei Raoyui", 1);
                    break;
            }
        }

        // =====================================================================
        // Utility helpers
        // =====================================================================

        /// <summary>All melds in the decomposition including the pair.</summary>
        private static List<Meld> AllMelds(HandDecomposition d)
        {
            var all = new List<Meld>(d.Sets) { d.Pair };
            return all;
        }

        /// <summary>All individual tiles in the hand (flattened from all melds + pair).</summary>
        private static List<Tile> AllTiles(HandDecomposition d)
            => AllMelds(d).SelectMany(m => m.Tiles).ToList();

        /// <summary>True if the meld is a pon (triplet) or kan of any type.</summary>
        private static bool IsPon(Meld m)
            => m.Type is MeldType.Pon or MeldType.KanOpen or MeldType.KanClosed or MeldType.KanExtended;

        /// <summary>True if the meld is a concealed pon (not called from a discard).</summary>
        private static bool IsConcealedPon(Meld m)
            => m.Type is MeldType.Pon && m.Source == ClaimSource.None;

        /// <summary>True if there is a pon/kan of the specified tile in the meld list.</summary>
        private static bool HasPon(IEnumerable<Meld> melds, Tile tile)
            => melds.Any(m => IsPon(m) && m.Lead == tile);
    }
}
