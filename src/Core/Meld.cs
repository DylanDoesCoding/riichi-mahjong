// =============================================================================
// Meld.cs
// Represents a completed set of tiles that has been called or declared.
//
// In Riichi Mahjong, a meld is a group of tiles that form one of:
//   - Chi  (sequence / chow):  3 consecutive tiles of the same suit
//   - Pon  (triplet / pung):   3 identical tiles
//   - Kan  (quad / kong):      4 identical tiles (3 variants — see MeldType)
//   - Pair (jantai):           2 identical tiles (only used for the pair in a complete hand;
//                              pairs are NOT called from other players)
//
// Melds are either "open" (called from another player's discard, visible to all)
// or "concealed" (formed entirely from tiles in your own hand).
// A concealed kan is a special case — declared openly but counts as concealed
// for yaku purposes.
// =============================================================================

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RiichiMahjong.Core
{
    // -------------------------------------------------------------------------
    // Enums
    // -------------------------------------------------------------------------

    /// <summary>The type of meld.</summary>
    public enum MeldType
    {
        Chi,          // Sequence (chow): 3 consecutive tiles, called from LEFT player only
        Pon,          // Triplet (pung): 3 identical tiles, called from any player
        KanOpen,      // Open quad: 4 identical tiles, called from another's discard
        KanClosed,    // Concealed quad: 4 identical tiles from own hand (declared, middle 2 face-down)
        KanExtended,  // Extended quad: existing open Pon upgraded with a 4th tile from own draw
        Pair,         // The pair (jantai) in a completed hand — internal use only, never called
    }

    /// <summary>
    /// Which player's discard was claimed, relative to the caller.
    /// Used by the UI to rotate the claimed tile to indicate its source.
    /// </summary>
    public enum ClaimSource
    {
        None,         // Tile not claimed (concealed meld or pair)
        Left,         // Claimed from the player to the left (kamicha)
        Opposite,     // Claimed from the player across (toimen)
        Right,        // Claimed from the player to the right (shimocha)
    }

    // -------------------------------------------------------------------------
    // Meld class
    // -------------------------------------------------------------------------

    /// <summary>
    /// A completed set of tiles — chi, pon, kan, or pair.
    /// Immutable after construction.
    /// </summary>
    public class Meld
    {
        // ---- Core data -------------------------------------------------------

        /// <summary>What kind of set this is.</summary>
        public MeldType Type { get; }

        /// <summary>
        /// The tiles in this meld, in display order.
        /// For Chi: ascending order (e.g. 3m, 4m, 5m).
        /// For Pon/Kan: all the same tile.
        /// For Pair: two of the same tile.
        /// </summary>
        public ReadOnlyCollection<Tile> Tiles { get; }

        /// <summary>
        /// The tile that was claimed from another player's discard, or null if concealed.
        /// For a Chi/Pon/KanOpen this is the tile that triggered the call.
        /// </summary>
        public Tile? ClaimedTile { get; }

        /// <summary>Which relative position the discard came from.</summary>
        public ClaimSource Source { get; }

        // ---- Computed properties ---------------------------------------------

        /// <summary>
        /// True if this meld is considered "open" (breaks full concealment of the hand).
        /// KanClosed is declared but does NOT open the hand.
        /// </summary>
        public bool IsOpen => Type is MeldType.Chi or MeldType.Pon
                                    or MeldType.KanOpen or MeldType.KanExtended;

        /// <summary>True for any kind of Kan (open, closed, or extended).</summary>
        public bool IsKan => Type is MeldType.KanOpen or MeldType.KanClosed or MeldType.KanExtended;

        /// <summary>The tile that identifies this meld (first tile for chi, any tile for pon/kan/pair).</summary>
        public Tile Lead => Tiles[0];

        // ---- Constructors (use factory methods below) -----------------------

        private Meld(MeldType type, List<Tile> tiles, Tile? claimedTile, ClaimSource source)
        {
            Type        = type;
            Tiles       = tiles.AsReadOnly();
            ClaimedTile = claimedTile;
            Source      = source;
        }

        // ---- Factory methods ------------------------------------------------

        /// <summary>
        /// Create a Chi (sequence). Tiles must be 3 consecutive tiles of the same suit.
        /// <paramref name="claimedTile"/> is the tile taken from the discard.
        /// </summary>
        public static Meld Chi(Tile t1, Tile t2, Tile t3, Tile claimedTile, ClaimSource source = ClaimSource.Left)
        {
            // Always store in ascending order for consistency
            var sorted = new List<Tile> { t1, t2, t3 };
            sorted.Sort((a, b) => a.Value.CompareTo(b.Value));
            return new Meld(MeldType.Chi, sorted, claimedTile, source);
        }

        /// <summary>
        /// Create a Pon (triplet) from 3 identical tiles.
        /// <paramref name="claimedTile"/> is the tile taken from the discard.
        /// </summary>
        public static Meld Pon(Tile tile, Tile claimedTile, ClaimSource source)
        {
            var tiles = new List<Tile> { tile, tile, tile };
            return new Meld(MeldType.Pon, tiles, claimedTile, source);
        }

        /// <summary>Open Kan — called on another player's discard (4 identical tiles).</summary>
        public static Meld KanOpen(Tile tile, Tile claimedTile, ClaimSource source)
        {
            var tiles = new List<Tile> { tile, tile, tile, tile };
            return new Meld(MeldType.KanOpen, tiles, claimedTile, source);
        }

        /// <summary>
        /// Concealed Kan — declared with 4 tiles all from own hand.
        /// Counts as concealed for yaku purposes (hand remains "closed").
        /// </summary>
        public static Meld KanClosed(Tile tile)
        {
            var tiles = new List<Tile> { tile, tile, tile, tile };
            return new Meld(MeldType.KanClosed, tiles, null, ClaimSource.None);
        }

        /// <summary>
        /// Extended Kan — player adds a 4th tile to an existing open Pon.
        /// The existing Pon is replaced by this meld.
        /// </summary>
        public static Meld KanExtended(Tile tile)
        {
            var tiles = new List<Tile> { tile, tile, tile, tile };
            return new Meld(MeldType.KanExtended, tiles, null, ClaimSource.None);
        }

        /// <summary>
        /// A pair — two identical tiles. Used internally when analysing a complete hand.
        /// Never claimed from another player.
        /// </summary>
        public static Meld Pair(Tile tile)
        {
            var tiles = new List<Tile> { tile, tile };
            return new Meld(MeldType.Pair, tiles, null, ClaimSource.None);
        }

        // ---- Display --------------------------------------------------------

        public override string ToString()
        {
            string tileStr = string.Join("-", Tiles);
            return Type switch
            {
                MeldType.Chi         => $"Chi({tileStr})",
                MeldType.Pon         => $"Pon({tileStr})",
                MeldType.KanOpen     => $"KanOpen({tileStr})",
                MeldType.KanClosed   => $"KanClosed({tileStr})",
                MeldType.KanExtended => $"KanExt({tileStr})",
                MeldType.Pair        => $"Pair({tileStr})",
                _                   => $"Meld({tileStr})",
            };
        }
    }
}
