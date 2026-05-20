// =============================================================================
// TileDto.cs — Wire format for a single tile.
// =============================================================================

using RiichiMahjong.Core;

namespace RiichiServer.Messages
{
    public class TileDto
    {
        public TileSuit Suit      { get; set; }
        public int      Value     { get; set; }
        public bool     IsRedDora { get; set; }

        public static TileDto From(Tile t) => new() { Suit = t.Suit, Value = t.Value, IsRedDora = t.IsRedDora };

        public Tile ToTile() => new(Suit, Value, IsRedDora);

        /// <summary>Find the matching Tile instance inside a hand (preserves reference equality for removal).</summary>
        public Tile? FindIn(IEnumerable<Tile> tiles)
            => tiles.FirstOrDefault(t => t.Suit == Suit && t.Value == Value);
    }

    public class MeldDto
    {
        public string       Type  { get; set; } = "";   // "Chi","Pon","KanOpen","KanClosed","Kakan"
        public List<TileDto> Tiles { get; set; } = new();

        public static MeldDto From(Meld m) => new()
        {
            Type  = m.Type.ToString(),
            Tiles = m.Tiles.Select(TileDto.From).ToList()
        };
    }

    public class PlayerInfoDto
    {
        public int    Seat   { get; set; }
        public string Name   { get; set; } = "";
        public bool   IsCpu  { get; set; }
    }

    public class ScoreEntryDto
    {
        public int    Seat   { get; set; }
        public string Name   { get; set; } = "";
        public int    Points { get; set; }
    }
}
