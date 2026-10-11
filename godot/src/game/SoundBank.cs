using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

/// <summary>
/// Procedural sound effects: every clip is SYNTHESISED at runtime, so the game ships with audio and
/// zero audio assets to license, download or babysit.
///
/// Why synthesis rather than .wav/.ogg files:
///   * no asset pipeline, no import step, no missing-file failure mode in an exported build;
///   * the clips are described by parameters, so tuning a hit sound is a code review, not a DAW
///     round trip;
///   * nothing to attribute, which matters because this build is handed to other people.
///
/// Design: plain sine/square/noise voices with exponential amplitude decays, mixed into a float
/// buffer and handed to Godot as an AudioStreamWAV (PCM16). That runs on the engine's normal audio
/// path, so volume, bus routing and 3D/pitch all behave like any other asset.
///
/// The whole system is SAFE HEADLESS: building a stream touches no audio device, and Play only
/// constructs those it needs. A headless gate run therefore never depends on an audio server.
/// </summary>
public static class SoundBank
{
    public enum Cue
    {
        /// <summary>Object ball struck by the cue. Bright, short.</summary>
        BallHitSoft,
        /// <summary>Hard ball-ball impact.</summary>
        BallHitHard,
        /// <summary>Rail bounce; the cue's energy bonus.</summary>
        RailBounce,
        /// <summary>Ball drops into a pocket.</summary>
        Pocket,
        /// <summary>White hole energises a ball.</summary>
        WhiteHole,
        /// <summary>Entering rewind (time running backwards).</summary>
        RewindStart,
        /// <summary>Releasing the rewind: the cue is launched back into normal time.</summary>
        RewindRelease,
        /// <summary>Rewind contact with an object ball.</summary>
        RewindContact,
        /// <summary>Level cleared.</summary>
        Win,
        /// <summary>UI hover.</summary>
        UiHover,
        /// <summary>UI confirm.</summary>
        UiClick,
    }

    private const int SampleRate = 22050;

    private static readonly Dictionary<Cue, AudioStreamWav> Cache = new();

    /// <summary>The stream for a cue, synthesised on first use and cached.</summary>
    public static AudioStreamWav Get(Cue cue)
    {
        if (Cache.TryGetValue(cue, out var s)) return s;
        s = Build(cue);
        Cache[cue] = s;
        return s;
    }

    /// <summary>Warms every cue. Called once at start so the first hit does not synthesise inline.</summary>
    public static void Prewarm()
    {
        foreach (Cue c in Enum.GetValues(typeof(Cue))) Get(c);
    }

    // ---------------------------------------------------------------- synthesis

    private static AudioStreamWav Build(Cue cue)
    {
        // (duration, tone Hz, decay, noise amount, second tone for a two-part body)
        var (dur, f0, decay, noise, f1) = cue switch
        {
            // A struck ball is a short, bright click: high tone, fast decay, a little noise.
            Cue.BallHitSoft => (0.075, 1180.0, 42.0, 0.22, 0.0),
            Cue.BallHitHard => (0.110, 860.0, 30.0, 0.34, 0.0),
            // A rail is duller and lower than a ball, with a hint of wood.
            Cue.RailBounce => (0.130, 330.0, 26.0, 0.16, 660.0),
            // A pocket is a short descending thunk.
            Cue.Pocket => (0.220, 420.0, 16.0, 0.20, 150.0),
            // A white hole should sound like energy ARRIVING: rising, clean, no noise.
            Cue.WhiteHole => (0.340, 520.0, 7.5, 0.0, 1560.0),
            // Rewind: a low, slow, slightly detuned swell.
            Cue.RewindStart => (0.420, 150.0, 6.0, 0.10, 226.0),
            Cue.RewindRelease => (0.260, 300.0, 12.0, 0.14, 900.0),
            Cue.RewindContact => (0.140, 520.0, 22.0, 0.30, 0.0),
            // Win: a short rising arpeggio feel from three stacked tones.
            Cue.Win => (0.900, 523.0, 3.4, 0.0, 1046.0),
            Cue.UiHover => (0.055, 900.0, 55.0, 0.0, 0.0),
            Cue.UiClick => (0.090, 1320.0, 40.0, 0.0, 660.0),
            _ => (0.100, 800.0, 30.0, 0.2, 0.0),
        };

        var n = (int)(dur * SampleRate);
        var data = new byte[n * 2];              // 16-bit mono
        var rng = new Random(12345);             // deterministic noise: captures are reproducible

        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var env = Math.Exp(-decay * t);

            // Attack ramp so nothing starts with a click at full amplitude.
            var attack = Math.Min(1.0, t / 0.002);
            env *= attack;

            var tone = Math.Sin(2.0 * Math.PI * f0 * t);
            if (f1 > 0.0)
            {
                // A second voice at a fixed ratio gives the sound a body instead of a beep. Its
                // amplitude decays faster so the initial transient stays bright.
                tone += 0.5 * Math.Sin(2.0 * Math.PI * f1 * t) * Math.Exp(-decay * 1.7 * t);
            }

            if (noise > 0.0) tone += noise * (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-decay * 3.0 * t);

            // Slight overall soft clipping keeps the sum inside range without a limiter stage.
            var v = Math.Tanh(tone * env * 1.35) * 0.72;

            var s = (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data,
        };
    }
}
