// =============================================================================
// GameSettings.cs
// Static container for settings that persist across scene changes.
// =============================================================================

namespace RiichiMahjong.UI
{
    public static class GameSettings
    {
        /// <summary>When true, tiles use the black/dark art set; otherwise the regular (white) set.</summary>
        public static bool UseBlackTiles { get; set; } = false;

        public static string TileThemeFolder =>
            UseBlackTiles ? "Black" : "Regular";

        /// <summary>Background music volume (0 = silent, 1 = full). Persists across scene changes.</summary>
        public static float MusicVolume { get; set; } = 0.6f;

        /// <summary>Sound-effects volume (0 = silent, 1 = full). Persists across scene changes.</summary>
        public static float SfxVolume { get; set; } = 1.0f;

        /// <summary>Display name used in multiplayer lobbies.</summary>
        public static string PlayerName { get; set; } = "";

        /// <summary>WebSocket server URL for multiplayer (e.g. ws://localhost:5000/ws).</summary>
        public static string ServerUrl { get; set; } = "ws://localhost:5000/ws";

        /// <summary>Convert a linear 0–1 volume to decibels, clamping near-zero to −80 dB.</summary>
        public static float LinearToDb(float linear)
            => linear < 0.01f ? -80f : Godot.Mathf.LinearToDb(linear);
    }
}
