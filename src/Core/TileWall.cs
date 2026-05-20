// =============================================================================
// TileWall.cs
// Manages the full 136-tile set for one hand of Riichi Mahjong.
//
// Responsibilities:
//   - Build the complete set (4 copies × 34 tile types)
//   - Shuffle it (Fisher-Yates)
//   - Split into live wall (122 tiles) and dead wall (14 tiles)
//   - Handle the initial deal (East=14 tiles, others=13)
//   - Provide normal draws from the live wall
//   - Provide kan replacement draws from the dead wall (rinshan)
//   - Reveal dora indicators (and track ura-dora for riichi winners)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RiichiMahjong.Core
{
    public class TileWall
    {
        // ---- Constants -------------------------------------------------------

        private const int TotalTiles   = 136;  // 34 types × 4 copies
        private const int DeadWallSize = 14;   // 7 stacks × 2 tiers, never drawn normally
        private const int MaxKans      = 4;    // Maximum kans allowed in one hand

        // ---- Dead wall layout (14 slots, 0-indexed) --------------------------
        //
        //  [0]  [1]  [2]  [3]  — Rinshan tiles (kan replacements, 1 per kan)
        //  [4]  [5]  [6]  [7]  [8]  — Dora indicators (slot 4 revealed at start,
        //                              slots 5-8 revealed after each kan)
        //  [9]  [10] [11] [12] [13] — Ura-dora indicators (paired with dora slots,
        //                              revealed only to riichi winners at hand end)

        private const int RinshanStart     = 0;
        private const int DoraStart        = 4;
        private const int UraDoraStart     = 9;

        // ---- Internal state --------------------------------------------------

        private readonly List<Tile> _liveTiles;        // 122 tiles drawn during normal play
        private readonly List<Tile> _deadWall;         // 14 reserved tiles
        private int _drawIndex;                        // Next live wall draw position
        private int _kanCount;                         // Kans declared this hand (0–4)

        // Tracks revealed indicators separately so UI can display them easily
        private readonly List<Tile> _doraIndicators;
        private readonly List<Tile> _uraIndicators;

        // ---- Public properties -----------------------------------------------

        /// <summary>Tiles still available to draw from the live wall.</summary>
        public int TilesRemaining => (_liveTiles.Count - _drawIndex) - _kanCount;

        /// <summary>True when no tiles remain — triggers an exhaustive draw (ryuukyoku).</summary>
        public bool IsEmpty => TilesRemaining <= 0;

        /// <summary>Number of kans declared this hand.</summary>
        public int KanCount => _kanCount;

        /// <summary>
        /// Currently revealed dora indicator tiles.
        /// The ACTUAL dora tile is one step ahead — use <see cref="GetDoraTile"/> to convert.
        /// Starts with 1 indicator; +1 revealed after each kan (up to 5 total).
        /// </summary>
        public ReadOnlyCollection<Tile> DoraIndicators => _doraIndicators.AsReadOnly();

        /// <summary>
        /// Ura-dora indicator tiles — only shown to riichi winners at hand end.
        /// Also starts at 1 and grows with each kan.
        /// </summary>
        public ReadOnlyCollection<Tile> UraIndicators => _uraIndicators.AsReadOnly();

        // ---- Constructor -----------------------------------------------------

        /// <param name="rng">
        /// Optional Random instance. Pass a seeded Random for reproducible tests;
        /// leave null for a random game.
        /// </param>
        public TileWall(Random? rng = null)
        {
            rng ??= new Random();

            var allTiles = BuildFullSet();
            Shuffle(allTiles, rng);

            // Split: last 14 = dead wall, rest = live wall
            int splitAt = TotalTiles - DeadWallSize;
            _liveTiles = allTiles.GetRange(0, splitAt);               // 122 tiles
            _deadWall  = allTiles.GetRange(splitAt, DeadWallSize);    // 14 tiles

            _drawIndex = 0;
            _kanCount  = 0;

            // Reveal the first dora indicator (dead wall slot 4)
            // and prepare the matching ura-dora indicator (slot 9)
            _doraIndicators = new List<Tile> { _deadWall[DoraStart] };
            _uraIndicators  = new List<Tile> { _deadWall[UraDoraStart] };
        }

        // ---- Initial deal ----------------------------------------------------

        /// <summary>
        /// Deals opening hands to all four players following EMA rules:
        ///   - Three rounds of 4 tiles each (players 0–3 = East–North)
        ///   - East (player 0) then draws from positions 1 and 3 of the next group
        ///   - South, West, North draw one each
        ///   Result: East = 14 tiles, South/West/North = 13 tiles each.
        ///
        /// Returns an array of 4 hand lists: [East, South, West, North].
        /// </summary>
        public List<Tile>[] DealInitialHands()
        {
            var hands = new List<Tile>[4];
            for (int i = 0; i < 4; i++)
                hands[i] = new List<Tile>(14);

            // Three rounds — 4 tiles to each player per round
            for (int round = 0; round < 3; round++)
                for (int player = 0; player < 4; player++)
                    for (int draw = 0; draw < 4; draw++)
                        hands[player].Add(DrawFromLive());

            // Final group: East takes slots 1 and 3, South slot 2, West slot 4
            hands[0].Add(DrawFromLive());  // East   — slot 1 of final group
            hands[1].Add(DrawFromLive());  // South  — slot 2
            hands[0].Add(DrawFromLive());  // East   — slot 3
            hands[2].Add(DrawFromLive());  // West   — slot 4
            hands[3].Add(DrawFromLive());  // North  — next draw (slot 5)

            // Verify tile counts (useful during development)
            System.Diagnostics.Debug.Assert(hands[0].Count == 14, "East should have 14 tiles");
            System.Diagnostics.Debug.Assert(hands[1].Count == 13, "South should have 13 tiles");
            System.Diagnostics.Debug.Assert(hands[2].Count == 13, "West should have 13 tiles");
            System.Diagnostics.Debug.Assert(hands[3].Count == 13, "North should have 13 tiles");

            return hands;
        }

        // ---- Normal draw -----------------------------------------------------

        /// <summary>
        /// Draw the next tile from the live wall for a player's normal turn.
        /// Throws if the wall is empty (caller should check <see cref="IsEmpty"/> first).
        /// </summary>
        public Tile DrawTile()
        {
            if (IsEmpty)
                throw new InvalidOperationException("Wall is exhausted — no tiles to draw.");
            return DrawFromLive();
        }

        // ---- Kan replacement (rinshan) ---------------------------------------

        /// <summary>
        /// Called after a player declares any kind of kan.
        /// Draws the replacement tile from the dead wall (rinshan area).
        /// Also reveals the next kan-dora indicator.
        ///
        /// Note: per the rules, when a kan is declared the last live wall tile
        /// conceptually transfers to the dead wall, which is why TilesRemaining
        /// subtracts _kanCount — that tile is no longer available for normal draws.
        /// </summary>
        /// <returns>The replacement tile the player draws after their kan.</returns>
        public Tile DrawKanReplacement()
        {
            if (_kanCount >= MaxKans)
                throw new InvalidOperationException("Maximum of 4 kans per hand already reached.");

            // Rinshan tile for this kan
            Tile replacement = _deadWall[RinshanStart + _kanCount];
            _kanCount++;

            // Reveal the next dora indicator (if we haven't hit the 5-indicator max)
            if (_kanCount <= MaxKans)  // slots 5,6,7,8 for kans 1,2,3,4
            {
                _doraIndicators.Add(_deadWall[DoraStart    + _kanCount]);
                _uraIndicators .Add(_deadWall[UraDoraStart + _kanCount]);
            }

            return replacement;
        }

        // ---- Dora helpers ----------------------------------------------------

        /// <summary>
        /// Converts a dora INDICATOR tile into the actual DORA tile.
        ///
        /// Rule (EMA 2016):
        ///   - Number suits: next number, wrapping 9 → 1
        ///   - Winds:   East → South → West → North → East
        ///   - Dragons: White → Green → Red → White
        /// </summary>
        public static Tile GetDoraTile(Tile indicator) => indicator.Suit switch
        {
            TileSuit.Man =>
                Tile.Man(indicator.Value == 9 ? 1 : indicator.Value + 1),

            TileSuit.Pin =>
                Tile.Pin(indicator.Value == 9 ? 1 : indicator.Value + 1),

            TileSuit.Sou =>
                Tile.Sou(indicator.Value == 9 ? 1 : indicator.Value + 1),

            TileSuit.Wind =>
                Tile.Wind(indicator.Value == 4
                    ? WindDirection.East
                    : (WindDirection)(indicator.Value + 1)),

            TileSuit.Dragon =>
                Tile.Dragon(indicator.Value == 3
                    ? DragonType.White
                    : (DragonType)(indicator.Value + 1)),

            _ => throw new ArgumentException($"Unknown tile suit on indicator: {indicator}"),
        };

        /// <summary>
        /// Returns the list of all currently active dora tiles (not indicators).
        /// Count a tile in a hand against this list to find dora bonuses.
        /// </summary>
        public List<Tile> GetActiveDoraTiles()
        {
            var doras = new List<Tile>(_doraIndicators.Count);
            foreach (var indicator in _doraIndicators)
                doras.Add(GetDoraTile(indicator));
            return doras;
        }

        /// <summary>
        /// Returns the list of ura-dora tiles (converted from ura indicators).
        /// Only relevant for players who won with riichi declared.
        /// </summary>
        public List<Tile> GetUradDoraTiles()
        {
            var uras = new List<Tile>(_uraIndicators.Count);
            foreach (var indicator in _uraIndicators)
                uras.Add(GetDoraTile(indicator));
            return uras;
        }

        // ---- Private helpers -------------------------------------------------

        private Tile DrawFromLive()
        {
            if (_drawIndex >= _liveTiles.Count)
                throw new InvalidOperationException("Live wall has no more tiles.");
            return _liveTiles[_drawIndex++];
        }

        /// <summary>
        /// Creates the full 136-tile set: 4 copies of each of the 34 tile types.
        /// </summary>
        private static List<Tile> BuildFullSet()
        {
            var tiles = new List<Tile>(TotalTiles);

            for (int copy = 0; copy < 4; copy++)
            {
                // Number suits: 1–9 each.
                // Copy 0 of the 5-tiles are red dora (akadora) — one per suit.
                bool isRedCopy = copy == 0;
                for (int v = 1; v <= 9; v++)
                {
                    tiles.Add(Tile.Man(v, isRedDora: isRedCopy && v == 5));
                    tiles.Add(Tile.Pin(v, isRedDora: isRedCopy && v == 5));
                    tiles.Add(Tile.Sou(v, isRedDora: isRedCopy && v == 5));
                }

                // Winds
                tiles.Add(Tile.Wind(WindDirection.East));
                tiles.Add(Tile.Wind(WindDirection.South));
                tiles.Add(Tile.Wind(WindDirection.West));
                tiles.Add(Tile.Wind(WindDirection.North));

                // Dragons
                tiles.Add(Tile.Dragon(DragonType.White));
                tiles.Add(Tile.Dragon(DragonType.Green));
                tiles.Add(Tile.Dragon(DragonType.Red));
            }

            System.Diagnostics.Debug.Assert(tiles.Count == TotalTiles);
            return tiles;
        }

        /// <summary>Fisher-Yates shuffle — gives a fair, unbiased random order.</summary>
        private static void Shuffle(List<Tile> tiles, Random rng)
        {
            for (int i = tiles.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
            }
        }
    }
}
