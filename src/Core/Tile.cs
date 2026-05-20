// =============================================================================
// Tile.cs
// Represents a single Riichi Mahjong tile — its suit, value, and properties.
//
// There are 34 distinct tile TYPES in Riichi Mahjong, and 4 physical copies
// of each, giving 136 tiles total. This class models the TYPE only — two Tile
// objects with the same Suit and Value are considered equal, just like two
// physical 5-Man tiles are the same kind of tile.
// =============================================================================

namespace RiichiMahjong.Core
{
    // -------------------------------------------------------------------------
    // Enums — the categories used to describe tiles
    // -------------------------------------------------------------------------

    /// <summary>
    /// The five suits in Riichi Mahjong.
    /// Man/Pin/Sou are number suits (1–9). Wind and Dragon are "honour" suits.
    /// </summary>
    public enum TileSuit
    {
        Man    = 0,  // Characters (Manzu)  — numbered 1 to 9
        Pin    = 1,  // Circles   (Pinzu)   — numbered 1 to 9
        Sou    = 2,  // Bamboo    (Souzu)   — numbered 1 to 9
        Wind   = 3,  // Winds: East(1), South(2), West(3), North(4)
        Dragon = 4,  // Dragons: White(1), Green(2), Red(3)
    }

    /// <summary>
    /// Wind directions. Used both as tile values and to track round/seat winds.
    /// The integer values match the Wind tile's Value field (1–4).
    /// </summary>
    public enum WindDirection
    {
        East  = 1,
        South = 2,
        West  = 3,
        North = 4,
    }

    /// <summary>
    /// Dragon types. The integer values match the Dragon tile's Value field (1–3).
    /// </summary>
    public enum DragonType
    {
        White = 1,  // Haku  (blank / white)
        Green = 2,  // Hatsu (green)
        Red   = 3,  // Chun  (red / centre)
    }

    // -------------------------------------------------------------------------
    // Tile class
    // -------------------------------------------------------------------------

    /// <summary>
    /// Represents one kind of Riichi Mahjong tile.
    ///
    /// Key design decisions:
    /// - Immutable: once created, a tile's suit and value never change.
    /// - Value equality: two tiles with the same Suit+Value are "==" (same type).
    /// - For number suits (Man/Pin/Sou): Value is 1–9.
    /// - For Wind tiles: Value is the WindDirection cast to int (1–4).
    /// - For Dragon tiles: Value is the DragonType cast to int (1–3).
    /// </summary>
    public sealed class Tile : System.IEquatable<Tile>
    {
        // ---- Core data -------------------------------------------------------

        public TileSuit Suit      { get; }
        public int      Value     { get; }  // 1–9 for suits; see WindDirection/DragonType for honours

        /// <summary>
        /// True if this is a red five (akadora). Affects display only — equality still
        /// matches any tile with the same Suit+Value, so game logic is unaffected.
        /// Only Man5, Pin5, and Sou5 can have this set.
        /// </summary>
        public bool IsRedDora { get; }

        // ---- Computed properties ---------------------------------------------

        /// <summary>True for Wind and Dragon tiles (not number suits).</summary>
        public bool IsHonour => Suit is TileSuit.Wind or TileSuit.Dragon;

        /// <summary>True for 1 or 9 of a number suit (Man/Pin/Sou only).</summary>
        public bool IsTerminal => !IsHonour && (Value == 1 || Value == 9);

        /// <summary>True if the tile cannot appear in a Chi (sequence) — terminals and all honours.</summary>
        public bool IsTerminalOrHonour => IsTerminal || IsHonour;

        /// <summary>True for numbered tiles 2–8 (not terminal, not honour). These give fewer points.</summary>
        public bool IsSimple => !IsTerminalOrHonour;

        /// <summary>
        /// True if this tile belongs to the "All Green" (Ryuuiisou) set:
        /// Green Dragon, or Sou tiles 2, 3, 4, 6, 8.
        /// </summary>
        public bool IsGreen =>
            (Suit == TileSuit.Dragon && Value == (int)DragonType.Green) ||
            (Suit == TileSuit.Sou   && Value is 2 or 3 or 4 or 6 or 8);

        /// <summary>
        /// A unique integer ID from 0 to 33, used to look up tile artwork.
        ///   Man 1–9  → 0–8
        ///   Pin 1–9  → 9–17
        ///   Sou 1–9  → 18–26
        ///   Winds    → 27–30  (East=27, South=28, West=29, North=30)
        ///   Dragons  → 31–33  (White=31, Green=32, Red=33)
        /// </summary>
        public int TileId => Suit switch
        {
            TileSuit.Man    => Value - 1,
            TileSuit.Pin    => 9  + Value - 1,
            TileSuit.Sou    => 18 + Value - 1,
            TileSuit.Wind   => 27 + Value - 1,
            TileSuit.Dragon => 31 + Value - 1,
            _               => -1,
        };

        // ---- Constructor -----------------------------------------------------

        public Tile(TileSuit suit, int value, bool isRedDora = false)
        {
            Suit      = suit;
            Value     = value;
            IsRedDora = isRedDora;
        }

        // ---- Factory helpers (make code more readable) -----------------------

        /// <summary>Create a Man (Character) tile, e.g. Tile.Man(5) = 5-Man.</summary>
        public static Tile Man(int value, bool isRedDora = false) => new(TileSuit.Man, value, isRedDora);

        /// <summary>Create a Pin (Circle) tile.</summary>
        public static Tile Pin(int value, bool isRedDora = false) => new(TileSuit.Pin, value, isRedDora);

        /// <summary>Create a Sou (Bamboo) tile.</summary>
        public static Tile Sou(int value, bool isRedDora = false) => new(TileSuit.Sou, value, isRedDora);

        /// <summary>Create a Wind tile, e.g. Tile.Wind(WindDirection.East).</summary>
        public static Tile Wind(WindDirection w)        => new(TileSuit.Wind,   (int)w);

        /// <summary>Create a Dragon tile, e.g. Tile.Dragon(DragonType.Red).</summary>
        public static Tile Dragon(DragonType d)         => new(TileSuit.Dragon, (int)d);

        // ---- Equality (by type, not physical identity) ----------------------

        public bool Equals(Tile? other) =>
            other is not null && Suit == other.Suit && Value == other.Value;

        public override bool Equals(object? obj) => Equals(obj as Tile);

        public override int GetHashCode() => System.HashCode.Combine(Suit, Value);

        public static bool operator ==(Tile? a, Tile? b) =>
            a is null ? b is null : a.Equals(b);

        public static bool operator !=(Tile? a, Tile? b) => !(a == b);

        // ---- Display --------------------------------------------------------

        /// <summary>
        /// Short debug code, e.g. "5m", "3p", "7s", "Ew" (East Wind), "Gd" (Green Dragon).
        /// </summary>
        public override string ToString() => Suit switch
        {
            TileSuit.Man    => $"{Value}m",
            TileSuit.Pin    => $"{Value}p",
            TileSuit.Sou    => $"{Value}s",
            TileSuit.Wind   => $"{(WindDirection)Value}"[..1] + "w",
            TileSuit.Dragon => $"{(DragonType)Value}"[..1]   + "d",
            _               => "?",
        };

        /// <summary>Full human-readable name, e.g. "5 Man", "East Wind", "Green Dragon".</summary>
        public string FullName() => Suit switch
        {
            TileSuit.Man    => $"{Value} Man",
            TileSuit.Pin    => $"{Value} Pin",
            TileSuit.Sou    => $"{Value} Sou",
            TileSuit.Wind   => $"{(WindDirection)Value} Wind",
            TileSuit.Dragon => $"{(DragonType)Value} Dragon",
            _               => "Unknown",
        };
    }
}
