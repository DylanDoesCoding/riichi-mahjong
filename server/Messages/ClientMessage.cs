// =============================================================================
// ClientMessage.cs — Messages sent FROM a Godot client TO the server.
// =============================================================================

namespace RiichiServer.Messages
{
    /// <summary>Every message the client can send. Deserialized from JSON.</summary>
    public class ClientMessage
    {
        /// <summary>Discriminator field — determines which action this message represents.</summary>
        public string Type { get; set; } = "";

        // ---- Lobby fields -------------------------------------------------------
        public string?   DisplayName { get; set; }   // CreateRoom, JoinRoom
        public string?   Code        { get; set; }   // JoinRoom

        // ---- Game action fields -------------------------------------------------
        public TileDto?  Tile        { get; set; }   // Discard, Riichi, Ankan
        public TileDto?  T1          { get; set; }   // Chi — first hand tile
        public TileDto?  T2          { get; set; }   // Chi — second hand tile
    }

    /// <summary>Well-known Type string constants so both sides agree on spelling.</summary>
    public static class ClientMessageType
    {
        public const string CreateRoom = "createRoom";
        public const string JoinRoom   = "joinRoom";
        public const string StartGame  = "startGame";
        public const string Discard    = "discard";
        public const string Riichi     = "riichi";
        public const string Tsumo      = "tsumo";
        public const string Pon        = "pon";
        public const string Chi        = "chi";
        public const string Ron        = "ron";
        public const string Kan        = "kan";       // Ankan or Kakan — server picks correct method
        public const string Pass       = "pass";
        public const string NextHand   = "nextHand";
    }
}
