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
        public int               DealerSeat      { get; set; }
        public string?           RoundWind       { get; set; }
        public int               Counters        { get; set; }
        public List<TileDto>?    YourTiles       { get; set; }
        public int[]?            TileCounts      { get; set; }  // face-down counts per seat
        public int[]?            Scores          { get; set; }  // per seat
        public string[]?         Names           { get; set; }  // per seat
        public List<TileDto>?    DoraIndicators  { get; set; }  // current revealed indicators

        // ---- Per-turn events --------------------------------------------
        public int               Seat         { get; set; }
        public TileDto?          Tile         { get; set; }
        public bool              IsRiichiDiscard { get; set; }
        public MeldDto?          Meld         { get; set; }

        // ---- Claim window -----------------------------------------------
        public int               DiscarderSeat      { get; set; }
        public bool              CanRon             { get; set; }
        public bool              CanPon             { get; set; }
        public bool              CanChi             { get; set; }
        public bool              CanKan             { get; set; }

        // ---- Furiten notification ----------------------------------------
        /// <summary>
        /// Sent via the "furitenChanged" message type to the specific human seat
        /// that just entered temporary furiten (missed a ron opportunity).
        /// True = now in temporary furiten; the flag clears automatically on the
        /// client when that player receives their next tileDrawn event.
        /// </summary>
        public bool              IsTemporaryFuriten { get; set; }

        // ---- Hand end ---------------------------------------------------
        public string?           Reason        { get; set; }
        public int[]?            Winners       { get; set; }
        public List<ScoreEntryDto>? ScoreBoard { get; set; }

        // ---- Score result (shown in scoring panel) -----------------------
        public int               Han           { get; set; }
        public int               Fu            { get; set; }
        public int               BasePoints    { get; set; }
        public string[]?         YakuNames     { get; set; }
        public int[]?            YakuFans      { get; set; }  // fan count per yaku (parallel to YakuNames)
        public bool[]?           YakuIsYakuman { get; set; }  // yakuman flag per yaku
        public int               DoraCount     { get; set; }
        public int               UraDoraCount  { get; set; }
        public int               WinnerSeat    { get; set; }
        public int               PayerSeat     { get; set; }  // -1 for tsumo

        // ---- Ryuukyoku (exhaustive draw) reveal --------------------------
        /// <summary>
        /// Closed tiles for all 4 seats at exhaustive draw.
        /// Empty inner list = noten (hand not revealed). Null = not an exhaustive draw.
        /// </summary>
        public List<List<TileDto>>? RevealedHands { get; set; }
        /// <summary>
        /// Waiting tiles for all 4 seats at exhaustive draw.
        /// Empty inner list = noten or no waits. Null = not an exhaustive draw.
        /// </summary>
        public List<List<TileDto>>? TenpaiWaits   { get; set; }

        // ---- Account / auth ----------------------------------------------
        // Sent as "authOk" after a successful register, login, or password reset.
        public string? Token       { get; set; }   // signed session token — client persists it
        public string? Username    { get; set; }   // canonical account name
        public int     GamesPlayed { get; set; }
        public int     GamesWon    { get; set; }
        // Sent as "accountOk" — confirmation text for account-management actions.
        public string? Message     { get; set; }

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
        public const string FuritenChanged     = "furitenChanged";     // human entered temporary furiten
        public const string QueueJoined        = "queueJoined";        // player entered matchmaking queue
        public const string AuthOk             = "authOk";             // register/login succeeded — carries token + stats
        public const string AccountOk          = "accountOk";          // account-management action succeeded — carries message
    }
}
