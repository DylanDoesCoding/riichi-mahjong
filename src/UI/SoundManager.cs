// =============================================================================
// SoundManager.cs
// Autoload singleton that synthesises every game sound effect in code.
//
// Because GodotSharp's NuGet package does not export AudioStreamWAV as a C#
// type, we write valid RIFF/WAV files to user:// at startup, then load them
// back with GD.Load<AudioStream>().  The files are tiny (a few KB each) and
// are regenerated on every launch, so there is nothing to ship.
//
// To replace a sound with a real asset, drop a .wav/.ogg file in
// res://Assets/Sounds/ and change the corresponding Load*() call below.
//
// Usage (from anywhere in the scene tree):
//   SoundManager.Instance?.Play(Sound.TileDiscard);
// =============================================================================

using Godot;
using System;
using System.IO;

namespace RiichiMahjong.UI
{
    public enum Sound
    {
        TileDiscard,     // tile lands in discard pool
        TileDraw,        // tile drawn from wall (softer)
        Riichi,          // riichi declaration arpeggio
        WinTsumo,        // self-draw win fanfare
        WinRon,          // ron win fanfare
        ExhaustiveDraw,  // ryuukyoku descending phrase
        GameOver,        // final game-over cue
        ButtonClick,     // generic UI button press
    }

    /// <summary>
    /// Autoload node.  Synthesises all SFX as PCM audio on startup, writes
    /// them to user:// as WAV files, and loads them back as AudioStream
    /// resources ready for playback.
    /// </summary>
    public partial class SoundManager : Node
    {
        public static SoundManager? Instance { get; private set; }

        private const int SampleRate = 22_050;
        private const int PoolSize   = 8;   // polyphony — overlapping hits

        private AudioStream?[] _streams = null!;
        private AudioStreamPlayer[] _pool = null!;
        private int _poolNext = 0;

        // =====================================================================
        // Godot lifecycle
        // =====================================================================

        public override void _Ready()
        {
            Instance = this;

            int count = Enum.GetValues<Sound>().Length;
            _streams = new AudioStream?[count];

            // Generate and cache each sound to user://
            _streams[(int)Sound.TileDiscard]    = LoadBuilt("snd_clack_hi",  () => BuildTileClack(0.95f));
            _streams[(int)Sound.TileDraw]       = LoadBuilt("snd_clack_lo",  () => BuildTileClack(0.55f, 0.85f));
            _streams[(int)Sound.Riichi]         = LoadBuilt("snd_riichi",    BuildRiichi);
            _streams[(int)Sound.WinTsumo]       = LoadBuilt("snd_win_tsumo", () => BuildWinFanfare(true));
            _streams[(int)Sound.WinRon]         = LoadBuilt("snd_win_ron",   () => BuildWinFanfare(false));
            _streams[(int)Sound.ExhaustiveDraw] = LoadBuilt("snd_draw",      BuildExhaustiveDraw);
            _streams[(int)Sound.GameOver]       = LoadBuilt("snd_gameover",  BuildGameOver);
            _streams[(int)Sound.ButtonClick]    = LoadBuilt("snd_click",     BuildButtonClick);

            _pool = new AudioStreamPlayer[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                _pool[i] = new AudioStreamPlayer { Bus = "Master" };
                AddChild(_pool[i]);
            }
        }

        public override void _ExitTree()
        {
            if (Instance == this) Instance = null;
        }

        // =====================================================================
        // Public API
        // =====================================================================

        public void Play(Sound sound)
        {
            var stream = _streams[(int)sound];
            if (stream == null) return;

            // Round-robin through the pool so simultaneous hits overlap cleanly
            var player = _pool[_poolNext];
            _poolNext = (_poolNext + 1) % PoolSize;

            player.Stream   = stream;
            player.VolumeDb = GameSettings.LinearToDb(GameSettings.SfxVolume);
            player.Stop();
            player.Play();
        }

        // =====================================================================
        // WAV persistence helpers
        // =====================================================================

        /// <summary>
        /// Builds a sound via <paramref name="factory"/>, writes it as a WAV
        /// file to user://, and returns the loaded AudioStream.
        /// The build is skipped on subsequent launches (file already exists).
        /// </summary>
        private static AudioStream? LoadBuilt(string baseName, Func<float[]> factory)
        {
            string path = $"user://{baseName}.wav";

            string osPath = ProjectSettings.GlobalizePath(path);
            if (!File.Exists(osPath))
            {
                float[] samples = factory();
                WriteWav(path, samples);
            }

            return ResourceLoader.Load<AudioStream>(path);
        }

        /// <summary>
        /// Write a float[-1..1] sample array as a 16-bit mono WAV file.
        /// <paramref name="godotPath"/> is a Godot virtual path (e.g. "user://snd_x.wav");
        /// it is converted to a real OS path via ProjectSettings.GlobalizePath.
        /// </summary>
        private static void WriteWav(string godotPath, float[] samples)
        {
            int dataSize = samples.Length * 2;  // 16-bit = 2 bytes/sample

            // Build RIFF/WAV buffer in memory
            var buf = new byte[44 + dataSize];
            int p = 0;

            // RIFF chunk descriptor
            buf[p++] = (byte)'R'; buf[p++] = (byte)'I'; buf[p++] = (byte)'F'; buf[p++] = (byte)'F';
            WriteU32(buf, ref p, (uint)(36 + dataSize));
            buf[p++] = (byte)'W'; buf[p++] = (byte)'A'; buf[p++] = (byte)'V'; buf[p++] = (byte)'E';

            // "fmt " sub-chunk  (PCM, mono, 22050 Hz, 16-bit)
            buf[p++] = (byte)'f'; buf[p++] = (byte)'m'; buf[p++] = (byte)'t'; buf[p++] = (byte)' ';
            WriteU32(buf, ref p, 16);
            WriteU16(buf, ref p, 1);                          // AudioFormat = PCM
            WriteU16(buf, ref p, 1);                          // NumChannels = 1
            WriteU32(buf, ref p, (uint)SampleRate);
            WriteU32(buf, ref p, (uint)(SampleRate * 2));     // ByteRate
            WriteU16(buf, ref p, 2);                          // BlockAlign
            WriteU16(buf, ref p, 16);                         // BitsPerSample

            // "data" sub-chunk
            buf[p++] = (byte)'d'; buf[p++] = (byte)'a'; buf[p++] = (byte)'t'; buf[p++] = (byte)'a';
            WriteU32(buf, ref p, (uint)dataSize);

            // PCM samples (little-endian 16-bit signed)
            for (int i = 0; i < samples.Length; i++)
            {
                short s16 = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32_767f);
                buf[p++] = (byte)( s16        & 0xFF);
                buf[p++] = (byte)((s16 >> 8)  & 0xFF);
            }

            // Godot's "user://" maps to an OS-specific writable directory.
            // ProjectSettings.GlobalizePath converts it to an absolute OS path.
            string osPath = ProjectSettings.GlobalizePath(godotPath);
            try
            {
                File.WriteAllBytes(osPath, buf);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"SoundManager: cannot write {osPath} — {ex.Message}");
            }
        }

        private static void WriteU16(byte[] b, ref int p, ushort v)
        {
            b[p++] = (byte)( v       & 0xFF);
            b[p++] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] b, ref int p, uint v)
        {
            b[p++] = (byte)( v        & 0xFF);
            b[p++] = (byte)((v >>  8) & 0xFF);
            b[p++] = (byte)((v >> 16) & 0xFF);
            b[p++] = (byte)((v >> 24) & 0xFF);
        }

        // =====================================================================
        // Sound generators  (return float[-1..1] PCM sample arrays)
        // =====================================================================

        /// <summary>
        /// Percussive tile clack: white-noise impact burst blended with a
        /// short resonant sine body.  <paramref name="pitch"/> scales the
        /// resonance frequency (1 = ~1800 Hz; &lt;1 = softer / draw sound).
        /// </summary>
        private static float[] BuildTileClack(float amplitude, float pitch = 1.0f)
        {
            const float Duration  = 0.10f;
            float resonFreq = 1_800f * pitch;

            int n   = (int)(SampleRate * Duration);
            var rng = new Random(42);
            var s   = new float[n];

            for (int i = 0; i < n; i++)
            {
                float t    = (float)i / SampleRate;
                float envN = MathF.Exp(-t * 60f);          // sharp noise burst
                float envT = MathF.Exp(-t * 25f);          // slower resonance tail
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float tone  = MathF.Sin(2f * MathF.PI * resonFreq * t);
                s[i] = (noise * 0.6f * envN + tone * 0.4f * envT) * amplitude;
            }

            return s;
        }

        /// <summary>
        /// Riichi: ascending three-note arpeggio (A4 – C5 – E5).
        /// Each note has a quick exponential decay plus a second harmonic for brightness.
        /// </summary>
        private static float[] BuildRiichi()
        {
            float[] freqs   = { 440f, 523f, 659f };   // A4, C5, E5
            const float NoteDur  = 0.09f;
            const float Gap      = 0.02f;
            const float TailDur  = 0.20f;

            int total = (int)(SampleRate * (freqs.Length * (NoteDur + Gap) + TailDur));
            var s     = new float[total];

            for (int ni = 0; ni < freqs.Length; ni++)
            {
                int startI = (int)(ni * (NoteDur + Gap) * SampleRate);
                int noteN  = (int)(NoteDur * SampleRate);

                for (int i = 0; i < noteN && startI + i < s.Length; i++)
                {
                    float t   = (float)i / SampleRate;
                    float env = MathF.Exp(-t * 8f);
                    float f   = freqs[ni];
                    s[startI + i] +=
                        (MathF.Sin(2f * MathF.PI * f * t) * 0.70f +
                         MathF.Sin(4f * MathF.PI * f * t) * 0.20f) * env;
                }
            }

            Normalise(s, 0.85f);
            return s;
        }

        /// <summary>
        /// Win fanfare: ascending five-note arpeggio.
        /// Tsumo = bright C-major (C4 E4 G4 C5 E5).
        /// Ron  = slightly fuller G-major (G4 B4 D5 G5 B5).
        /// Each note has three harmonics for a bell-like timbre.
        /// </summary>
        private static float[] BuildWinFanfare(bool tsumo)
        {
            float[] freqs = tsumo
                ? new[] { 262f, 330f, 392f, 523f, 659f }   // C4 E4 G4 C5 E5
                : new[] { 392f, 494f, 587f, 784f, 988f };  // G4 B4 D5 G5 B5

            const float NoteDur  = 0.11f;
            const float Gap      = 0.01f;
            const float TailMult = 1.8f;
            const float TailDur  = 0.40f;

            int total = (int)(SampleRate * (freqs.Length * (NoteDur + Gap) + TailDur));
            var s     = new float[total];

            for (int ni = 0; ni < freqs.Length; ni++)
            {
                int   startI = (int)(ni * (NoteDur + Gap) * SampleRate);
                int   noteN  = (int)(NoteDur * TailMult * SampleRate);
                float f      = freqs[ni];

                for (int i = 0; i < noteN && startI + i < s.Length; i++)
                {
                    float t   = (float)i / SampleRate;
                    float env = MathF.Exp(-t * 5f);
                    s[startI + i] +=
                        (MathF.Sin(2f * MathF.PI * f * t) * 0.60f +
                         MathF.Sin(4f * MathF.PI * f * t) * 0.25f +
                         MathF.Sin(6f * MathF.PI * f * t) * 0.10f) * env;
                }
            }

            Normalise(s, 0.90f);
            return s;
        }

        /// <summary>
        /// Exhaustive draw (Ryuukyoku): descending three-note phrase (G4 → E4 → C4).
        /// </summary>
        private static float[] BuildExhaustiveDraw()
        {
            float[] freqs = { 392f, 330f, 262f };   // G4 E4 C4 (descending)

            const float NoteDur = 0.10f;
            const float Gap     = 0.02f;
            const float Tail    = 0.30f;

            int total = (int)(SampleRate * (freqs.Length * (NoteDur + Gap) + Tail));
            var s     = new float[total];

            for (int ni = 0; ni < freqs.Length; ni++)
            {
                int startI = (int)(ni * (NoteDur + Gap) * SampleRate);
                int noteN  = (int)((NoteDur + Tail * 0.35f) * SampleRate);

                for (int i = 0; i < noteN && startI + i < s.Length; i++)
                {
                    float t   = (float)i / SampleRate;
                    float env = MathF.Exp(-t * 6f);
                    s[startI + i] +=
                        MathF.Sin(2f * MathF.PI * freqs[ni] * t) * env * 0.55f;
                }
            }

            Normalise(s, 0.65f);
            return s;
        }

        /// <summary>Game over: solemn two-note descending phrase (E4 → C4).</summary>
        private static float[] BuildGameOver()
        {
            float[] freqs = { 330f, 262f };   // E4 → C4

            const float NoteDur = 0.20f;
            const float Gap     = 0.05f;
            const float Tail    = 0.60f;

            int total = (int)(SampleRate * (freqs.Length * (NoteDur + Gap) + Tail));
            var s     = new float[total];

            for (int ni = 0; ni < freqs.Length; ni++)
            {
                int startI = (int)(ni * (NoteDur + Gap) * SampleRate);
                int noteN  = (int)((NoteDur + Tail * 0.5f) * SampleRate);

                for (int i = 0; i < noteN && startI + i < s.Length; i++)
                {
                    float t   = (float)i / SampleRate;
                    float env = t < 0.01f
                        ? t / 0.01f
                        : MathF.Exp(-(t - 0.01f) * 3.5f);
                    float f = freqs[ni];
                    s[startI + i] +=
                        (MathF.Sin(2f * MathF.PI * f * t) * 0.70f +
                         MathF.Sin(4f * MathF.PI * f * t) * 0.20f) * env;
                }
            }

            Normalise(s, 0.75f);
            return s;
        }

        /// <summary>Brief sine-pop for UI button feedback.</summary>
        private static float[] BuildButtonClick()
        {
            const float Duration = 0.025f;
            const float Freq     = 700f;

            int n = (int)(SampleRate * Duration);
            var s = new float[n];

            for (int i = 0; i < n; i++)
            {
                float t   = (float)i / SampleRate;
                float env = 1f - t / Duration;
                s[i] = MathF.Sin(2f * MathF.PI * Freq * t) * env * 0.50f;
            }

            return s;
        }

        // =====================================================================
        // Utility
        // =====================================================================

        /// <summary>Scale samples so the peak magnitude equals <paramref name="target"/>.</summary>
        private static void Normalise(float[] s, float target)
        {
            float peak = 0f;
            foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
            if (peak < 1e-6f) return;
            float scale = target / peak;
            for (int i = 0; i < s.Length; i++) s[i] *= scale;
        }
    }
}
