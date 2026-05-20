// =============================================================================
// ServerMessage.cs — Messages sent FROM the server TO Godot clients.
//
// A single flat class with optional fields avoids the complexity of
// polymorphic JSON deserialization on the Godot side.  The "type" field
// tells the client which fields to read.
// =============================================================================

namespace RiichiServer.Messages
{
    public class ServerMessage
    {
        public string Type { get; set; } = "";

        // ---- Lobby -------------------------------------------------------
        public string?           Code       { get; set; }
        public int               YourSeat   { get; set; }
        public List<PlayerInfoDto>? Players  { get; set; }
        public string?           Error      { get; set; }

        // ---- Game start --------------------------------------------------
        public int               DealerSeat   { get; set; }
        public string?           RoundWind    { get; set; }
        public int               Counters     { get; set; }
        public List<TileDto>?    YourTiles    { get; set; }
        public int[]?            TileCounts   { get; set; }  // face-down counts per seat
        public int[]?            Scores       { get; set; }  // per seat
        public string[]?         Names        { get; set; }  // per seat

        // ---- Per-turn events --------------------------------------------
        public int               Seat         { get; set; }
        public TileDto?          Tile         { get; set; }
        public bool              IsRiichiDiscard { get; set; }
        public MeldDto?          Meld         { get; set; }

        // ---- Claim window -----------------------------------------------
        public int               DiscarderSeat { get; set; }
        public bool              CanRon        { get; set; }
        public bool              CanPon        { get; set; }
        public bool              CanChi        { get; set; }
        public bool              CanKan        { get; set; }

        // ---- Hand end ---------------------------------------------------
        public string?           Reason        { get; set; }
        public int[]?            Winners       { get; set; }
        public List<ScoreEntryDto>? ScoreBoard { get; set; }

        // ---- Score result (shown in scoring panel) -----------------------
        public int               Han           { get; set; }
        public int               Fu            { get; set; }
        public int               BasePoints    { get; set; }
        public string[]?         YakuNames     { get; set; }
        public int               DoraCount     { get; set; }
        public int               WinnerSeat    { get; set; }
        public int               PayerSeat     { get; set; }  // -1 for tsumo

        // ---- Reconnection state snapshot --------------------------------
        // Sent as "gameStateSnapshot" when a player rejoins mid-game.
        // Carries the full current board state so the client can resync.
        public List<List<TileDto>>?  Discards     { get; set; }  // [seat][tile]
        public List<List<MeldDto>>?  Melds        { get; set; }  // [seat][meld]
        public int[]?                RiichiSeats  { get; set; }  // seats currently in riichi
        public int                   CurrentTurn  { get; set; }  // seat whose turn it is
    }

    /// <summary>Well-known Type string constants.</summary>
    public static class ServerMessageType
    {
        public const string RoomCreated         = "roomCreated";
        public const string RoomJoined          = "roomJoined";
        public const string PlayerJoined        = "playerJoined";
        public const string PlayerLeft          = "playerLeft";
        public const string Error               = "error";
        public const string GameStarted         = "gameStarted";
        public const string HandDealt           = "handDealt";
        public const string TileDrawn           = "tileDrawn";
        public const string TileDiscarded       = "tileDiscarded";
        public const string MeldDeclared        = "meldDeclared";
        public const string RiichiDeclared      = "riichiDeclared";
        public const string ClaimWindowOpened   = "claimWindowOpened";
        public const string HandEnded           = "handEnded";
        public const string NewHand             = "newHand";
        public const string GameOver            = "gameOver";
        public const string GameStateSnapshot   = "gameStateSnapshot";  // full resync on rejoin
    }
}
