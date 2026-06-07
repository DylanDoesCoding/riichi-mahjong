// =============================================================================
// GameSettings.cs
// Static container for settings that persist across scene changes.
// Call Load() once at startup (NetworkManager._Ready) and Save() whenever
// the player changes a preference.
// =============================================================================

namespace RiichiMahjong.UI
{
    public static class GameSettings
    {
        private const string ConfigPath = "user://settings.cfg";

        // ---- Display ---------------------------------------------------------

        /// <summary>When true, tiles use the black/dark art set; otherwise the regular (white) set.</summary>
        public static bool UseBlackTiles { get; set; } = false;

        public static string TileThemeFolder =>
            UseBlackTiles ? "Black" : "Regular";

        // ---- Audio -----------------------------------------------------------

        /// <summary>Background music volume (0 = silent, 1 = full). Persists across scene changes.</summary>
        public static float MusicVolume { get; set; } = 0.6f;

        /// <summary>Sound-effects volume (0 = silent, 1 = full). Persists across scene changes.</summary>
        public static float SfxVolume { get; set; } = 1.0f;

        // ---- Multiplayer -----------------------------------------------------

        /// <summary>
        /// Persistent UUID sent to the server on connect so a disconnected player
        /// can reclaim their seat even after restarting the game client.
        /// Generated once and saved to disk; the same UUID is reused across sessions
        /// so a cold-start reconnect still works as long as the server room is alive.
        /// </summary>
        public static string PlayerUuid { get; private set; } = "";

        /// <summary>Display name used in multiplayer lobbies. Saved to disk.</summary>
        public static string PlayerName { get; set; } = "";

        /// <summary>WebSocket server URL for multiplayer. Defaults to the live Render server.</summary>
        public static string ServerUrl { get; set; } = "wss://riichi-mahjong-server.onrender.com/ws";

        // ---- Persistence -----------------------------------------------------

        /// <summary>
        /// Load saved settings from disk. Call once at startup before any scene accesses
        /// GameSettings. Safe to call even if no save file exists yet.
        /// </summary>
        public static void Load()
        {
            var cfg = new Godot.ConfigFile();
            bool configExists = cfg.Load(ConfigPath) == Godot.Error.Ok;

            PlayerName    = configExists ? (string)cfg.GetValue("player",  "name",       "") : "";
            ServerUrl     = configExists ? (string)cfg.GetValue("player",  "serverUrl",  "wss://riichi-mahjong-server.onrender.com/ws") : "wss://riichi-mahjong-server.onrender.com/ws";
            UseBlackTiles = configExists ? (bool)  cfg.GetValue("display", "blackTiles", false) : false;
            MusicVolume   = configExists ? (float) cfg.GetValue("audio",   "music",      0.6f) : 0.6f;
            SfxVolume     = configExists ? (float) cfg.GetValue("audio",   "sfx",        1.0f) : 1.0f;

            // UUID: load from disk so the player can rejoin after a game client restart.
            // If no UUID is saved yet, generate one and immediately persist it.
            string savedUuid = configExists ? (string)cfg.GetValue("player", "uuid", "") : "";
            if (string.IsNullOrEmpty(savedUuid))
            {
                savedUuid = System.Guid.NewGuid().ToString("N");
                cfg.SetValue("player", "uuid", savedUuid);
                cfg.Save(ConfigPath);
            }
            PlayerUuid = savedUuid;
        }

        /// <summary>
        /// Persist the current settings to disk. Call after the player changes any preference.
        /// </summary>
        public static void Save()
        {
            var cfg = new Godot.ConfigFile();
            cfg.SetValue("player",  "name",       PlayerName);
            cfg.SetValue("player",  "serverUrl",  ServerUrl);
            cfg.SetValue("player",  "uuid",       PlayerUuid);
            cfg.SetValue("display", "blackTiles", UseBlackTiles);
            cfg.SetValue("audio",   "music",      MusicVolume);
            cfg.SetValue("audio",   "sfx",        SfxVolume);
            cfg.Save(ConfigPath);
        }

        // ---- Helpers ---------------------------------------------------------

        /// <summary>Convert a linear 0–1 volume to decibels, clamping near-zero to −80 dB.</summary>
        public static float LinearToDb(float linear)
            => linear < 0.01f ? -80f : Godot.Mathf.LinearToDb(linear);
    }
}
