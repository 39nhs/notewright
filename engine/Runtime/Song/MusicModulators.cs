using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    public enum MusicModulatorKind
    {
        /// <summary>A periodic shape (Hz or tempo-synced) added to the target.</summary>
        Lfo,
        /// <summary>The level of another track (its own signal, like a sidechain key) added to the target.</summary>
        Follower,
    }

    /// <summary>
    /// A track modulator (playback V3+): moves one of the track's automation targets on top of its lane (or its authored value). Amount is in
    /// the target's units — dB (volume), -1..1 (pan), octaves (low/high-pass: the cutoff is multiplied by 2^(amount × value)), mix (echo,
    /// chorus, reverb), semitones (pitch), 0..1 (modulation), octaves (brightness). An LFO swings ±Amount; a follower adds Amount × level (0..1).
    /// </summary>
    [Serializable]
    public struct MusicModulator
    {
        public bool Enabled;
        public MusicModulatorKind Kind;
        public MusicAutomationTarget Target;
        public float Amount;
        [Tooltip("LFO shape.")]
        public MusicLfoWave Wave;
        [Range(.01f, 50)] public float RateHz;
        [Tooltip("When above 0: one LFO cycle per this many beats (follows the tempo) instead of Rate Hz.")]
        [Range(0, 64)] public float SyncBeats;
        [Tooltip("LFO start phase, 0..1 of a cycle.")]
        [Range(0, 1)] public float Phase;
        [Tooltip("Follower: InstrumentId of the track whose level drives the target.")]
        public string Source;
        [Range(1, 500)] public float AttackMs;
        [Range(10, 3000)] public float ReleaseMs;

        public static MusicModulator Lfo(MusicAutomationTarget target, float amount, float syncBeats = 0, float rateHz = 1, MusicLfoWave wave = MusicLfoWave.Sine, float phase = 0) =>
            new MusicModulator { Enabled = true, Kind = MusicModulatorKind.Lfo, Target = target, Amount = amount, SyncBeats = syncBeats, RateHz = rateHz, Wave = wave, Phase = phase, AttackMs = 10, ReleaseMs = 200 };
        public static MusicModulator Follower(MusicAutomationTarget target, float amount, string source, float attackMs = 10, float releaseMs = 200) =>
            new MusicModulator { Enabled = true, Kind = MusicModulatorKind.Follower, Target = target, Amount = amount, Source = source, AttackMs = attackMs, ReleaseMs = releaseMs, RateHz = 1 };
    }

    /// <summary>A compiled envelope follower of a part (source resolved by <see cref="MusicCue"/>).</summary>
    internal sealed class MusicFollower
    {
        public readonly MusicAutomationTarget Target; public readonly string SourceId; public readonly double Amount, AttackSeconds, ReleaseSeconds;
        public int Source = -1;
        public MusicFollower(MusicAutomationTarget target, string sourceId, double amount, double attackSeconds, double releaseSeconds)
        { Target = target; SourceId = sourceId; Amount = amount; AttackSeconds = attackSeconds; ReleaseSeconds = releaseSeconds; }
    }

    /// <summary>
    /// Compiles track modulators: LFOs are baked into the track's automation lanes (exact for tempo-synced and Hz LFOs, through the tempo
    /// map; at most <see cref="MusicAutomation.MaxPoints"/> points), followers become runtime followers on a lane of their target (a flat
    /// lane at the authored value when the track has none).
    /// </summary>
    public static class MusicModulators
    {
        public const int MaxPerTrack = 8;
        static readonly MusicAutomationTarget[] Allowed = { MusicAutomationTarget.VolumeDb, MusicAutomationTarget.Pan, MusicAutomationTarget.LowPassHz, MusicAutomationTarget.HighPassHz,
            MusicAutomationTarget.EchoWet, MusicAutomationTarget.ChorusMix, MusicAutomationTarget.ReverbMix, MusicAutomationTarget.PitchSemitones, MusicAutomationTarget.Modulation, MusicAutomationTarget.Brightness,
            MusicAutomationTarget.GateDepth, MusicAutomationTarget.CrushMix, MusicAutomationTarget.DriveDb, MusicAutomationTarget.DriveMix, MusicAutomationTarget.Width, MusicAutomationTarget.RotarySpeed };

        /// <summary>The value a target has without a lane (the track's authored setting).</summary>
        public static float Authored(MusicCueDefinition.Part p, MusicAutomationTarget target)
        {
            var fx = p.Effects ?? new MusicMixerSettings();
            switch (target)
            {
                case MusicAutomationTarget.Pan: return p.Pan;
                case MusicAutomationTarget.LowPassHz: return fx.LowPassHz;
                case MusicAutomationTarget.HighPassHz: return fx.HighPassHz;
                case MusicAutomationTarget.EchoWet: return fx.EchoWet;
                case MusicAutomationTarget.ChorusMix: return fx.ChorusMix;
                case MusicAutomationTarget.ReverbMix: return fx.ReverbMix;
                case MusicAutomationTarget.Modulation: return 1;
                case MusicAutomationTarget.GateDepth: return fx.GateDepth;
                case MusicAutomationTarget.CrushMix: return fx.CrushMix;
                case MusicAutomationTarget.DriveDb: return fx.DriveDb;
                case MusicAutomationTarget.DriveMix: return fx.DriveMix;
                case MusicAutomationTarget.Width: return 1;
                case MusicAutomationTarget.RotarySpeed: return fx.Color?.RotarySpeed ?? 0;
                default: return 0; // volume (dB on top of the gain), pitch, brightness
            }
        }

        internal static void Check(MusicCueDefinition.Part p)
        {
            var mods = p.Modulators ?? Array.Empty<MusicModulator>();
            if (mods.Length > MaxPerTrack) throw new ArgumentException($"At most {MaxPerTrack} modulators per track.");
            foreach (var m in mods)
            {
                if (!m.Enabled) continue;
                if (Array.IndexOf(Allowed, m.Target) < 0) throw new ArgumentException($"Modulator target {m.Target} is not a track target.");
                if (!MusicTrack.Finite(m.Amount) || Math.Abs(m.Amount) > 100) throw new ArgumentException("Modulator amount must be finite (|amount| ≤ 100).");
                if (m.Kind == MusicModulatorKind.Lfo)
                {
                    if (!Enum.IsDefined(typeof(MusicLfoWave), m.Wave) || !MusicTrack.Finite(m.RateHz) || m.RateHz < .01f || m.RateHz > 50 || !MusicTrack.Finite(m.SyncBeats) || m.SyncBeats < 0 || m.SyncBeats > 64 || m.Phase < 0 || m.Phase > 1)
                        throw new ArgumentException("LFO modulator: rate .01–50 Hz, sync 0–64 beats, phase 0–1.");
                }
                else if (m.Kind == MusicModulatorKind.Follower)
                {
                    if (string.IsNullOrWhiteSpace(m.Source)) throw new ArgumentException("A follower needs a source track.");
                    if (!MusicTrack.Finite(m.AttackMs) || m.AttackMs < 1 || m.AttackMs > 500 || !MusicTrack.Finite(m.ReleaseMs) || m.ReleaseMs < 10 || m.ReleaseMs > 3000) throw new ArgumentException("Follower attack 1–500 ms, release 10–3000 ms.");
                }
                else throw new ArgumentException("Unknown modulator kind.");
            }
        }

        /// <summary>The track's lanes with its LFO modulators baked in and flat lanes added for follower targets (the definition is not changed).</summary>
        internal static MusicAutomationLane[] Bake(MusicCueDefinition song, MusicCueDefinition.Part p, out MusicFollower[] followers)
        {
            followers = null;
            var mods = (p.Modulators ?? Array.Empty<MusicModulator>()).Where(m => m.Enabled).ToArray();
            var lanes = p.Automation ?? Array.Empty<MusicAutomationLane>();
            if (mods.Length == 0) return lanes;
            Check(p);
            var result = lanes.Where(l => l != null).Select(l => new MusicAutomationLane(l.Target, (MusicAutomationPoint[])l.Points.Clone()) { Bypass = l.Bypass }).ToList();
            var tempo = new MusicTempoMap(song.Bpm, song.LoopBeats, song.TempoChanges);
            foreach (var target in mods.Select(m => m.Target).Distinct())
            {
                var lfos = mods.Where(m => m.Target == target && m.Kind == MusicModulatorKind.Lfo).ToArray();
                int at = result.FindIndex(l => l.Target == target && !l.Bypass);
                var source = at >= 0 ? new MusicAutomation(target, result[at].Points, double.MaxValue) : null;
                float authored = Authored(p, target);
                if (lfos.Length == 0)
                {
                    if (at < 0) result.Add(new MusicAutomationLane(target, new MusicAutomationPoint(0, authored, MusicAutomationCurve.Step)));
                    continue;
                }
                // Resolution: 24 points per shortest cycle (at most 1/64 beat), coarser when the lane would exceed the point limit.
                double shortest = lfos.Min(m => m.SyncBeats > 0 ? m.SyncBeats : MinBeatsPerCycle(tempo, song.LoopBeats, m.RateHz));
                double step = Math.Min(1 / 64.0, shortest / 24);
                step = Math.Max(step, song.LoopBeats / (MusicAutomation.MaxPoints - 4));
                MusicAutomation.Range(target, out float low, out float high);
                bool octaves = target == MusicAutomationTarget.LowPassHz || target == MusicAutomationTarget.HighPassHz;
                var points = new List<MusicAutomationPoint>();
                for (double b = 0; b < song.LoopBeats + 1e-9; b += step)
                {
                    double beat = Math.Min(b, song.LoopBeats), value = source != null ? source.Evaluate(beat) : authored, sum = 0;
                    foreach (var m in lfos) sum += m.Amount * Shape(m, Cycles(tempo, m, beat));
                    if (octaves) { double baseHz = value > 0 ? value : 1000; value = baseHz * Math.Pow(2, sum); }
                    else value += sum;
                    points.Add(new MusicAutomationPoint(beat, (float)Math.Max(low, Math.Min(high, value)), lfos.Any(m => m.Wave == MusicLfoWave.Square || m.Wave == MusicLfoWave.SampleAndHold) ? MusicAutomationCurve.Step : MusicAutomationCurve.Linear));
                }
                var lane = new MusicAutomationLane(target, points.ToArray());
                if (at >= 0) result[at] = lane; else result.Add(lane);
            }
            foreach (var m in mods.Where(m => m.Kind == MusicModulatorKind.Follower))
                if (!(song.Parts ?? Array.Empty<MusicCueDefinition.Part>()).Any(x => x != null && (string.IsNullOrWhiteSpace(x.InstrumentId) ? x.Name : x.InstrumentId) == m.Source))
                    throw new ArgumentException($"no track called {m.Source} for the follower.");
            followers = mods.Where(m => m.Kind == MusicModulatorKind.Follower).Select(m => new MusicFollower(m.Target, m.Source, m.Amount, m.AttackMs / 1000.0, m.ReleaseMs / 1000.0)).ToArray();
            if (followers.Length == 0) followers = null;
            return result.ToArray();
        }

        static double MinBeatsPerCycle(MusicTempoMap tempo, double loopBeats, double hz)
        {
            double fastest = tempo.At(0, false);
            for (double b = 0; b < loopBeats; b += Math.Max(1, loopBeats / 256)) fastest = Math.Max(fastest, tempo.At(b, false));
            return fastest / 60 / hz; // beats in one cycle at the fastest tempo
        }

        static double Cycles(MusicTempoMap tempo, MusicModulator m, double beat) =>
            (m.SyncBeats > 0 ? beat / m.SyncBeats : tempo.SecondsAt(beat) * m.RateHz) + m.Phase;

        static double Shape(MusicModulator m, double cycles)
        {
            double p = cycles - Math.Floor(cycles);
            switch (m.Wave)
            {
                case MusicLfoWave.Triangle: return 1 - 4 * Math.Abs(p - .5);
                case MusicLfoWave.Square: return p < .5 ? 1 : -1;
                case MusicLfoWave.SawUp: return 2 * p - 1;
                case MusicLfoWave.SawDown: return 1 - 2 * p;
                case MusicLfoWave.SampleAndHold:
                    long n = (long)Math.Floor(cycles); uint x = unchecked((uint)(n * 2654435761L) ^ 0x9E3779B9u); x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
                    return x / 2147483647.5 - 1;
                default: return Math.Sin(2 * Math.PI * p);
            }
        }
    }
}
