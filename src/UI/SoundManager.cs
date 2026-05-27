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

            // Build each sound directly as an AudioStreamWAV (no file I/O needed)
            _streams[(int)Sound.TileDiscard]    = BuildStream(() => BuildTileClack(0.95f));
            _streams[(int)Sound.TileDraw]       = BuildStream(() => BuildTileClack(0.55f, 0.85f));
            _streams[(int)Sound.Riichi]         = BuildStream(BuildRiichi);
            _streams[(int)Sound.WinTsumo]       = BuildStream(() => BuildWinFanfare(true));
            _streams[(int)Sound.WinRon]         = BuildStream(() => BuildWinFanfare(false));
            _streams[(int)Sound.ExhaustiveDraw] = BuildStream(BuildExhaustiveDraw);
            _streams[(int)Sound.GameOver]       = BuildStream(BuildGameOver);
            _streams[(int)Sound.ButtonClick]    = BuildStream(BuildButtonClick);

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
        // Stream construction
        // =====================================================================

        /// <summary>
        /// Generates samples via <paramref name="factory"/> and returns an
        /// AudioStreamWAV populated with the PCM data — no file I/O.
        /// AudioStreamWAV is not exported as a public C# type in GodotSharp 4.x,
        /// so we instantiate it via ClassDB and set properties through GodotObject.Set.
        /// GodotSharp resolves the runtime type to the nearest known C# base (AudioStream).
        /// </summary>
        private static AudioStream? BuildStream(Func<float[]> factory)
        {
            float[] samples = factory();

            var pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s16 = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32_767f);
                pcm[i * 2]     = (byte)( s16       & 0xFF);
                pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }

            var variant = ClassDB.Instantiate("AudioStreamWAV");
            if (variant.VariantType == Variant.Type.Nil) return null;
            var obj = variant.AsGodotObject();
            if (obj is not AudioStream audioStream) return null;

            obj.Set("mix_rate", SampleRate);
            obj.Set("format",   1);      // AudioStreamWAV.FORMAT_16_BITS
            obj.Set("stereo",   false);
            obj.Set("data",     pcm);
            return audioStream;
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
