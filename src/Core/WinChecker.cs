// =============================================================================
// WinChecker.cs
// Determines whether a 14-tile hand is a winning hand, and if so, returns
// ALL valid decompositions (ways of splitting it into sets + pair).
//
// A winning hand in Riichi Mahjong is one of:
//   1. Standard:        4 sets (chi/pon/kan) + 1 pair
//   2. Seven Pairs:     7 pairs (all different)
//   3. Thirteen Orphans: one each of 13 terminals/honours + 1 duplicate
//
// Why find ALL decompositions?
//   A hand can sometimes be split in multiple valid ways, each giving a
//   different yaku or score. The YakuChecker and ScoreCalculator will
//   evaluate each decomposition and choose the highest-scoring one.
//
// "Open" melds (chi/pon/kan) are already committed — they count as complete
// sets and are not re-decomposed. Only the closed tiles need decomposition.
// =============================================================================

using System.Collections.Generic;
using System.Linq;

namespace RiichiMahjong.Core
{
    // -------------------------------------------------------------------------
    // Result types
    // -------------------------------------------------------------------------

    /// <summary>
    /// One complete decomposition of a winning hand.
    /// Contains the closed-tile sets + pair, plus the open melds (passed through unchanged).
    /// </summary>
    public class HandDecomposition
    {
        /// <summary>The four sets found in the closed tiles + open melds combined.</summary>
        public List<Meld> Sets { get; }

        /// <summary>The pair (jantai).</summary>
        public Meld Pair { get; }

        /// <summary>True if this is a Seven Pairs hand (no sets, 7 pairs).</summary>
        public bool IsSevenPairs { get; }

        /// <summary>True if this is a Thirteen Orphans hand.</summary>
        public bool IsThirteenOrphans { get; }

        public HandDecomposition(List<Meld> sets, Meld pair,
                                  bool isSevenPairs = false, bool isThirteenOrphans = false)
        {
            Sets               = sets;
            Pair               = pair;
            IsSevenPairs       = isSevenPairs;
            IsThirteenOrphans  = isThirteenOrphans;
        }
    }

    /// <summary>
    /// The result of checking whether a hand wins.
    /// </summary>
    public class WinCheckResult
    {
        /// <summary>True if the hand is a valid winning hand.</summary>
        public bool IsWin { get; }

        /// <summary>
        /// All valid decompositions of the winning hand.
        /// There may be multiple if the tiles can be split different ways.
        /// Empty if IsWin is false.
        /// </summary>
        public List<HandDecomposition> Decompositions { get; }

        public WinCheckResult(bool isWin, List<HandDecomposition> decompositions)
        {
            IsWin          = isWin;
            Decompositions = decompositions;
        }

        public static WinCheckResult NoWin() => new(false, new List<HandDecomposition>());
    }

    // -------------------------------------------------------------------------
    // WinChecker
    // -------------------------------------------------------------------------

    public static class WinChecker
    {
        // The 13 required tile indices for Thirteen Orphans (by TileId)
        private static readonly int[] OrphanIds =
        {
            0, 8,       // 1m, 9m
            9, 17,      // 1p, 9p
            18, 26,     // 1s, 9s
            27, 28, 29, 30,  // East, South, West, North
            31, 32, 33       // White, Green, Red Dragon
        };

        /// <summary>
        /// Main entry point. Checks a complete 14-tile hand for a win.
        ///
        /// <param name="closedTiles">The player's concealed tiles (typically 14 for pure self-draw,
        ///   or 13 closed + winning tile added before calling this).</param>
        /// <param name="openMelds">Melds already committed (chi/pon/kan). Each counts as one set.</param>
        /// </summary>
        public static WinCheckResult Check(List<Tile> closedTiles, List<Meld> openMelds)
        {
            var allDecomps = new List<HandDecomposition>();

            // --- Try standard decomposition (4 sets + 1 pair) ---
            allDecomps.AddRange(FindStandardDecompositions(closedTiles, openMelds));

            // --- Special hands (only valid when fully concealed) ---
            if (!openMelds.Any(m => m.IsOpen))
            {
                var sevenPairs = CheckSevenPairs(closedTiles);
                if (sevenPairs != null) allDecomps.Add(sevenPairs);

                var thirteenOrphans = CheckThirteenOrphans(closedTiles);
                if (thirteenOrphans != null) allDecomps.Add(thirteenOrphans);
            }

            return allDecomps.Count > 0
                ? new WinCheckResult(true, allDecomps)
                : WinCheckResult.NoWin();
        }

        // ---- Standard decomposition -----------------------------------------

        /// <summary>
        /// Find all ways to decompose the closed tiles into sets (chi/pon/kan-ankou) + pair,
        /// combined with the already-committed open melds.
        /// </summary>
        private static List<HandDecomposition> FindStandardDecompositions(
            List<Tile> closedTiles, List<Meld> openMelds)
        {
            var results = new List<HandDecomposition>();

            // How many sets do we still need from the closed tiles?
            int openSetCount = openMelds.Count;  // Each open meld is one complete set
            int neededFromClosed = 4 - openSetCount;

            // Convert to count array for fast processing
            int[] counts = Hand.TilesToCounts(closedTiles);

            // Try each tile type as the pair candidate
            for (int pairId = 0; pairId < 34; pairId++)
            {
                if (counts[pairId] < 2) continue;

                // Reserve 2 tiles for the pair
                counts[pairId] -= 2;

                // Recursively find neededFromClosed sets from remaining tiles
                var closedSets = new List<Meld>();
                FindSetsRecursive(counts, neededFromClosed, closedSets, results,
                                  openMelds, TileIdToTile(pairId));

                // Restore
                counts[pairId] += 2;
            }

            return results;
        }

        /// <summary>
        /// Recursive helper: try to form <paramref name="needed"/> sets from <paramref name="counts"/>.
        /// When successful (needed == 0), record the complete decomposition.
        /// </summary>
        private static void FindSetsRecursive(
            int[] counts, int needed, List<Meld> currentSets,
            List<HandDecomposition> results, List<Meld> openMelds, Tile pair)
        {
            if (needed == 0)
            {
                // Check counts are fully depleted (no leftover tiles)
                if (counts.All(c => c == 0))
                {
                    var allSets = new List<Meld>(openMelds);
                    allSets.AddRange(currentSets);
                    results.Add(new HandDecomposition(allSets, Meld.Pair(pair)));
                }
                return;
            }

            // Find the first tile with remaining count (skip forward to avoid duplicates)
            int first = -1;
            for (int i = 0; i < 34; i++)
            {
                if (counts[i] > 0) { first = i; break; }
            }
            if (first < 0) return;  // No tiles left but still need sets — dead end

            // Option 1: Triplet (pon/ankou) — 3 of the same tile
            if (counts[first] >= 3)
            {
                counts[first] -= 3;
                currentSets.Add(MakeConcealedPon(TileIdToTile(first)));
                FindSetsRecursive(counts, needed - 1, currentSets, results, openMelds, pair);
                currentSets.RemoveAt(currentSets.Count - 1);
                counts[first] += 3;
            }

            // Option 2: Sequence (chi) — only for number suits (TileId 0–26)
            int suit = first / 9;
            if (suit < 3)  // Man=0, Pin=1, Sou=2
            {
                int pos = first % 9;
                if (pos <= 6 && counts[first + 1] > 0 && counts[first + 2] > 0)
                {
                    counts[first]--;
                    counts[first + 1]--;
                    counts[first + 2]--;
                    currentSets.Add(MakeConcealedChi(
                        TileIdToTile(first),
                        TileIdToTile(first + 1),
                        TileIdToTile(first + 2)));
                    FindSetsRecursive(counts, needed - 1, currentSets, results, openMelds, pair);
                    currentSets.RemoveAt(currentSets.Count - 1);
                    counts[first]++;
                    counts[first + 1]++;
                    counts[first + 2]++;
                }
            }
        }

        // ---- Seven Pairs ----------------------------------------------------

        /// <summary>
        /// Check for Seven Pairs (Chiitoitsu):
        /// 7 different pairs. All four copies of the same tile count as only ONE pair.
        /// Returns a decomposition if valid, null otherwise.
        /// </summary>
        private static HandDecomposition? CheckSevenPairs(List<Tile> tiles)
        {
            if (tiles.Count != 14) return null;

            int[] counts = Hand.TilesToCounts(tiles);

            // Must have exactly 7 tile types with count >= 2, and no type with count == 3 or 1
            var pairs   = new List<int>();
            foreach (int id in Enumerable.Range(0, 34))
            {
                if (counts[id] == 1 || counts[id] == 3) return null;  // Invalid count
                if (counts[id] >= 2) pairs.Add(id);
            }
            if (pairs.Count != 7) return null;

            // Build decomposition — treat as 7 pairs with the first pair as "the pair"
            // and the remaining 6 as sets (conceptually; YakuChecker handles scoring)
            var pairMelds = pairs.Select(id => Meld.Pair(TileIdToTile(id))).ToList();
            return new HandDecomposition(
                pairMelds.Skip(1).ToList(),
                pairMelds[0],
                isSevenPairs: true);
        }

        // ---- Thirteen Orphans -----------------------------------------------

        /// <summary>
        /// Check for Thirteen Orphans (Kokushi Musou):
        /// One each of the 13 terminal/honour types, plus one duplicate of any of them.
        /// Returns a decomposition if valid, null otherwise.
        /// </summary>
        private static HandDecomposition? CheckThirteenOrphans(List<Tile> tiles)
        {
            if (tiles.Count != 14) return null;

            int[] counts = Hand.TilesToCounts(tiles);

            // Every orphan tile must appear at least once
            bool allPresent = OrphanIds.All(id => counts[id] >= 1);
            if (!allPresent) return null;

            // Exactly one orphan must appear twice (the duplicate)
            int duplicates = OrphanIds.Count(id => counts[id] >= 2);
            if (duplicates != 1) return null;

            // No non-orphan tiles allowed
            for (int i = 0; i < 34; i++)
            {
                if (!OrphanIds.Contains(i) && counts[i] > 0) return null;
            }

            // Use the duplicate as the "pair" for decomposition purposes
            int pairId = OrphanIds.First(id => counts[id] >= 2);
            var pair   = Meld.Pair(TileIdToTile(pairId));

            return new HandDecomposition(
                new List<Meld>(),  // No sets — special hand
                pair,
                isThirteenOrphans: true);
        }

        // ---- Helpers --------------------------------------------------------

        /// <summary>Convert a TileId (0–33) back to a Tile instance.</summary>
        public static Tile TileIdToTile(int id)
        {
            if (id < 9)  return Tile.Man(id + 1);
            if (id < 18) return Tile.Pin(id - 9 + 1);
            if (id < 27) return Tile.Sou(id - 18 + 1);
            if (id < 31) return Tile.Wind((WindDirection)(id - 27 + 1));
            return Tile.Dragon((DragonType)(id - 31 + 1));
        }

        /// <summary>Create a conceptual "concealed" Chi set for decomposition purposes.</summary>
        private static Meld MakeConcealedChi(Tile t1, Tile t2, Tile t3) =>
            Meld.Chi(t1, t2, t3, t1, ClaimSource.None);

        /// <summary>Create a concealed Pon set for decomposition purposes.</summary>
        private static Meld MakeConcealedPon(Tile tile) =>
            Meld.Pon(tile, tile, ClaimSource.None);
    }
}
