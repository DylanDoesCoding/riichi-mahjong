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
        public string?   Code        { get; set; }   // JoinRoom, RejoinRoom
        public string?   Uuid        { get; set; }   // All lobby messages — client-generated identity

        // ---- Account fields -----------------------------------------------------
        public string?   Username    { get; set; }   // Register, Login, RequestReset, ResetPassword
        public string?   Password    { get; set; }   // Register, Login
        public string?   Token       { get; set; }   // Optional on all lobby messages — signed session token
        public string?   OldPassword { get; set; }   // ChangePassword
        public string?   NewPassword { get; set; }   // ChangePassword, ResetPassword
        public string?   Email       { get; set; }   // SetEmail
        public string?   ResetCode   { get; set; }   // ResetPassword — 6-digit emailed code

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
        public const string RejoinRoom = "rejoinRoom";  // uuid + code → reconnect mid-game
        public const string JoinQueue  = "joinQueue";   // enter matchmaking queue
        public const string LeaveQueue = "leaveQueue";  // cancel matchmaking
        public const string Kyuushu   = "kyuushu";     // declare Kyuushu Kyuuhai abortive draw
        public const string Register  = "register";    // create account (username + password)
        public const string Login     = "login";       // sign in, returns authOk with token
        public const string ChangePassword = "changePassword"; // token + old + new password
        public const string SetEmail       = "setEmail";       // token + email (for recovery)
        public const string RequestReset   = "requestReset";   // username → emails a reset code
        public const string ResetPassword  = "resetPassword";  // username + code + new password
        public const string GetLeaderboard = "getLeaderboard"; // top accounts by wins
    }
}
