using System;
using System.Collections.Generic;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// What an automation lane changes. Track lanes live on a part, master lanes on the song. The effect targets replace the value
    /// of the track's own effect chain (it must have Track effects) or of the master mixer while the lane exists.
    /// </summary>
    public enum MusicAutomationTarget
    {
        /// <summary>dB added to the authored track gain (master: to the final output). <see cref="MusicAutomation.MinVolumeDb"/> and below is silence.</summary>
        VolumeDb = 0,
        /// <summary>Track pan -1 (left) .. 1 (right), replacing the authored pan while the lane exists. Track lanes only.</summary>
        Pan = 1,
        /// <summary>Low-pass cutoff 0..18000 Hz (0 = filter off). Ramps between two cutoffs move in octaves.</summary>
        LowPassHz = 2,
        /// <summary>High-pass cutoff 0..2000 Hz (0 = filter off). Ramps between two cutoffs move in octaves.</summary>
        HighPassHz = 3,
        /// <summary>Echo wet 0..1 (at the chain's echo time).</summary>
        EchoWet = 4,
        /// <summary>Chorus mix 0..1 (needs Chorus on).</summary>
        ChorusMix = 5,
        /// <summary>Reverb mix 0..1 (needs Reverb on).</summary>
        ReverbMix = 6,
        /// <summary>
        /// A normalized (0..1) parameter of an external VST3 instrument. Not a song lane: it lives on the plugin mod target
        /// (<see cref="MusicPluginTarget.Automation"/>), keyed by the parameter's ID or name.
        /// </summary>
        PluginParameter = 7,
        /// <summary>Pitch bend in semitones -24..24 on every voice of the track (samples, oscillators, time stretch, synth). Track lanes only.</summary>
        PitchSemitones = 8,
        /// <summary>Modulation wheel 0..1: scales the built-in synth's LFO depths (absent = full depth). Track lanes only.</summary>
        Modulation = 9,
        /// <summary>Brightness: octaves -8..8 added to the built-in synth's filter cutoff. Track lanes only.</summary>
        Brightness = 10,
        /// <summary>Rhythm gate depth 0..1 (needs Gate on): 0 = open, 1 = the authored full chop.</summary>
        GateDepth = 11,
        /// <summary>Bit crusher dry/wet 0..1 (needs Crush Bits or Downsample).</summary>
        CrushMix = 12,
        /// <summary>Drive gain into the shaper, 0..48 dB (needs Drive on).</summary>
        DriveDb = 13,
        /// <summary>Drive dry/driven blend 0..1 (needs Drive on).</summary>
        DriveMix = 14,
        /// <summary>Beat repeat (needs Stutter on): at 0.5 and above the chain repeats the slice (Stutter Beats long) that started when it engaged.</summary>
        Stutter = 15,
        /// <summary>Stereo width 0..2 at the end of the chain (0 = mono, 1 = unchanged, 2 = twice the side signal).</summary>
        Width = 16,
        /// <summary>
        /// Tape speed 0..2 (cue master only): the tempo and every V3 voice's pitch scale together (0.5 = half speed, an octave down; the
        /// tempo is held at 0.05×). Below 0.15 the output fades out, so a ramp toward 0 is a tape stop. Exactly 0 = stopped: silent while
        /// the song's clock runs at its normal tempo, so the music comes back on time when the lane returns.
        /// </summary>
        Speed = 17,
        /// <summary>Rotary speaker speed 0..1 (0 = slow, 1 = fast; needs Color Rotary on). The rotors accelerate toward it.</summary>
        RotarySpeed = 18,
    }

    /// <summary>How the value moves from a point to the next one.</summary>
    public enum MusicAutomationCurve
    {
        /// <summary>Straight ramp (in dB for volume).</summary>
        Linear = 0,
        /// <summary>Hold the value and jump at the next point.</summary>
        Step = 1,
        /// <summary>Ease in and out (smoothstep).</summary>
        Smooth = 2,
    }

    [Serializable]
    public struct MusicAutomationPoint
    {
        [Tooltip("Beat inside the cue (0 = loop start, at most Loop Beats). Points at the same beat make an instant jump.")]
        public double Beat;
        [Tooltip("Volume: dB added to the authored level (-80 = silent, 0 = unchanged, up to +24). Pan: -1 left .. 1 right.")]
        public float Value;
        [Tooltip("Shape from this point to the next one.")]
        public MusicAutomationCurve Curve;

        public MusicAutomationPoint(double beat, float value, MusicAutomationCurve curve = MusicAutomationCurve.Linear)
        { Beat = beat; Value = value; Curve = curve; }
    }

    /// <summary>Serialized automation lane (song data). Playback V3+ plays it; older versions ignore it.</summary>
    [Serializable]
    public sealed class MusicAutomationLane
    {
        public MusicAutomationTarget Target;
        [Tooltip("On = keep the points but play the authored value.")]
        public bool Bypass;
        public MusicAutomationPoint[] Points = Array.Empty<MusicAutomationPoint>();

        public MusicAutomationLane() { }
        public MusicAutomationLane(MusicAutomationTarget target, params MusicAutomationPoint[] points)
        { Target = target; Points = points ?? Array.Empty<MusicAutomationPoint>(); }
    }

    /// <summary>
    /// Compiled, immutable automation lane (playback V3+). The value follows the cue's local beat, so it repeats with the loop.
    /// <see cref="Evaluate"/> allocates nothing and keeps no state, so the audio thread may call it for any deck.
    /// </summary>
    public sealed class MusicAutomation
    {
        public const float MinVolumeDb = -80, MaxVolumeDb = 24;
        public const int MaxPoints = 100000;
        /// <summary>Size of runtime lane arrays indexed by target (VolumeDb..Brightness). Index 7 (PluginParameter) is never a song lane.</summary>
        public const int TargetCount = 19;
        public const float MinSpeed = .05f, MaxSpeed = 2;
        /// <summary>Tempo factor of a Speed value: 0 (stopped) runs the clock normally, other values are held at <see cref="MinSpeed"/>.</summary>
        public static double SpeedTempo(double speed) => speed <= 0 ? 1 : Math.Max(MinSpeed, speed);
        public readonly MusicAutomationTarget Target;
        readonly double[] beats, values;
        readonly MusicAutomationCurve[] curves;
        readonly bool octaves;
        public int PointCount => beats.Length;
        public double LastBeat => beats[beats.Length - 1];
        public double BeatAt(int index) => beats[index];
        internal bool ValuesWithin(double minimum, double maximum)
        {
            foreach (double value in values) if (value < minimum || value > maximum) return false;
            return true;
        }

        public MusicAutomation(MusicAutomationTarget target, MusicAutomationPoint[] points, double loopBeats)
        {
            if (!Enum.IsDefined(typeof(MusicAutomationTarget), target)) throw new ArgumentException("Unknown automation target.");
            if (points == null || points.Length == 0 || points.Length > MaxPoints)
                throw new ArgumentException($"An automation lane needs 1..{MaxPoints} points.");
            Range(target, out float low, out float high);
            var sorted = (MusicAutomationPoint[])points.Clone();
            for (int i = 0; i < sorted.Length; i++)
            {
                var p = sorted[i];
                if (!MusicTrack.Finite(p.Beat) || p.Beat < 0 || p.Beat > loopBeats + 1e-9)
                    throw new ArgumentException($"Automation point {i + 1}: beat must be 0..{loopBeats} (the cue's loop).");
                if (!MusicTrack.Finite(p.Value) || p.Value < low || p.Value > high)
                    throw new ArgumentException($"Automation point {i + 1}: {target} value must be {low}..{high}.");
                if (!Enum.IsDefined(typeof(MusicAutomationCurve), p.Curve)) throw new ArgumentException($"Automation point {i + 1}: unknown curve.");
            }
            // Stable: points at the same beat keep their authored order (the later one wins from that beat on).
            var order = new int[sorted.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => sorted[a].Beat != sorted[b].Beat ? sorted[a].Beat.CompareTo(sorted[b].Beat) : a.CompareTo(b));
            Target = target;
            octaves = target == MusicAutomationTarget.LowPassHz || target == MusicAutomationTarget.HighPassHz;
            beats = new double[sorted.Length]; values = new double[sorted.Length]; curves = new MusicAutomationCurve[sorted.Length];
            for (int i = 0; i < order.Length; i++) { beats[i] = sorted[order[i]].Beat; values[i] = sorted[order[i]].Value; curves[i] = sorted[order[i]].Curve; }
        }

        /// <summary>Value at a cue-local beat: the first point's value before it, the last point's value after it.</summary>
        public double Evaluate(double beat)
        {
            if (beat < beats[0]) return values[0];
            int lo = 0, hi = beats.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (beats[mid] <= beat) lo = mid; else hi = mid - 1; }
            if (lo == beats.Length - 1) return values[lo];
            double span = beats[lo + 1] - beats[lo];
            if (curves[lo] == MusicAutomationCurve.Step || span <= 0) return values[lo];
            double t = (beat - beats[lo]) / span;
            if (curves[lo] == MusicAutomationCurve.Smooth) t = t * t * (3 - 2 * t);
            double a = values[lo], b = values[lo + 1];
            // Cutoff sweeps between two running filters move evenly in pitch; a ramp from or to 0 (off) is linear in Hz.
            if (octaves && a > 0 && b > 0) return a * Math.Pow(b / a, t);
            return a + (b - a) * t;
        }

        /// <summary>Valid values of a target.</summary>
        public static void Range(MusicAutomationTarget target, out float low, out float high)
        {
            switch (target)
            {
                case MusicAutomationTarget.VolumeDb: low = MinVolumeDb; high = MaxVolumeDb; return;
                case MusicAutomationTarget.Pan: low = -1; high = 1; return;
                case MusicAutomationTarget.LowPassHz: low = 0; high = 18000; return;
                case MusicAutomationTarget.HighPassHz: low = 0; high = 2000; return;
                case MusicAutomationTarget.PitchSemitones: low = -24; high = 24; return;
                case MusicAutomationTarget.Brightness: low = -8; high = 8; return;
                case MusicAutomationTarget.DriveDb: low = 0; high = (float)MusicDrive.MaxDriveDb; return;
                case MusicAutomationTarget.Width: low = 0; high = 2; return;
                case MusicAutomationTarget.Speed: low = 0; high = MaxSpeed; return;
                default: low = 0; high = 1; return;
            }
        }

        /// <summary>Targets that change an effect chain (track effects or the master mixer) rather than the voices.</summary>
        public static bool IsEffect(MusicAutomationTarget target) => (target >= MusicAutomationTarget.LowPassHz && target <= MusicAutomationTarget.ReverbMix) ||
            (target >= MusicAutomationTarget.GateDepth && target <= MusicAutomationTarget.Width) || target == MusicAutomationTarget.RotarySpeed;

        /// <summary>Targets that act on a track's voices (pan and expression); the master and mix buses have none of them.</summary>
        public static bool IsVoice(MusicAutomationTarget target) => target == MusicAutomationTarget.Pan || (target >= MusicAutomationTarget.PitchSemitones && target <= MusicAutomationTarget.Brightness);

        /// <summary>Targets added after the first effect lanes, passed to a chain through <see cref="MusicTrackEffects.SetLanes"/>.</summary>
        internal static bool IsPerformance(MusicAutomationTarget target) => (target >= MusicAutomationTarget.GateDepth && target <= MusicAutomationTarget.Width) || target == MusicAutomationTarget.RotarySpeed;

        /// <summary>song.json lane target names (Unity builder and headless renderer).</summary>
        public static readonly IReadOnlyDictionary<string, MusicAutomationTarget> JsonNames = new Dictionary<string, MusicAutomationTarget>
        {
            ["volume"] = MusicAutomationTarget.VolumeDb, ["pan"] = MusicAutomationTarget.Pan, ["lowpass"] = MusicAutomationTarget.LowPassHz,
            ["highpass"] = MusicAutomationTarget.HighPassHz, ["echo"] = MusicAutomationTarget.EchoWet, ["chorus"] = MusicAutomationTarget.ChorusMix,
            ["reverb"] = MusicAutomationTarget.ReverbMix, ["pitch"] = MusicAutomationTarget.PitchSemitones,
            ["modulation"] = MusicAutomationTarget.Modulation, ["brightness"] = MusicAutomationTarget.Brightness,
            ["gate"] = MusicAutomationTarget.GateDepth, ["crush"] = MusicAutomationTarget.CrushMix, ["drive"] = MusicAutomationTarget.DriveDb,
            ["driveMix"] = MusicAutomationTarget.DriveMix, ["stutter"] = MusicAutomationTarget.Stutter, ["width"] = MusicAutomationTarget.Width,
            ["speed"] = MusicAutomationTarget.Speed, ["rotary"] = MusicAutomationTarget.RotarySpeed,
        };

        /// <summary>Linear gain for a volume value in dB (silence at <see cref="MinVolumeDb"/>).</summary>
        public static double Gain(double db) => db <= MinVolumeDb ? 0 : Math.Pow(10, db / 20);

        /// <summary>
        /// Compiles the lanes of a part or of the master (bypassed lanes are skipped). One lane per target; the master takes every target except Pan.
        /// Returns null when nothing is enabled.
        /// </summary>
        public static MusicAutomation[] Compile(MusicAutomationLane[] lanes, double loopBeats, string owner, bool master)
        {
            if (lanes == null || lanes.Length == 0) return null;
            var compiled = new List<MusicAutomation>();
            foreach (var lane in lanes)
            {
                if (lane == null) throw new ArgumentException(owner + ": null automation lane.");
                if (lane.Bypass) continue;
                if (master && IsVoice(lane.Target)) throw new ArgumentException($"{owner}: {lane.Target} automation is for tracks only.");
                if (lane.Target == MusicAutomationTarget.PluginParameter) throw new ArgumentException(owner + ": plugin parameter automation belongs to the plugin mod target, not the song.");
                if (compiled.Exists(c => c.Target == lane.Target)) throw new ArgumentException($"{owner}: more than one enabled {lane.Target} automation lane.");
                try { compiled.Add(new MusicAutomation(lane.Target, lane.Points, loopBeats)); }
                catch (ArgumentException e) { throw new ArgumentException($"{owner} {lane.Target} automation: {e.Message}"); }
            }
            return compiled.Count == 0 ? null : compiled.ToArray();
        }

        /// <summary>
        /// Lanes indexed by target (null when there are none), validated against the effect chain they automate:
        /// effect targets need a chain, ChorusMix and ReverbMix their stage switched on.
        /// </summary>
        internal static MusicAutomation[] ByTarget(MusicAutomation[] lanes, MusicMixerSnapshot chain, string owner, bool master, bool cueMaster = false)
        {
            if (lanes == null || lanes.Length == 0) return null;
            var byTarget = new MusicAutomation[TargetCount];
            foreach (var lane in lanes)
            {
                if (lane == null) throw new ArgumentException(owner + ": null automation lane.");
                if (lane.Target == MusicAutomationTarget.PluginParameter || (int)lane.Target >= TargetCount) throw new ArgumentException(owner + ": plugin parameter automation belongs to the plugin mod target, not the song.");
                if (byTarget[(int)lane.Target] != null) throw new ArgumentException(owner + ": one automation lane per target.");
                if (master && IsVoice(lane.Target)) throw new ArgumentException($"{owner}: {lane.Target} automation is for tracks only.");
                if (lane.Target == MusicAutomationTarget.Speed && !cueMaster) throw new ArgumentException($"{owner}: Speed automation is for the master only.");
                if (IsEffect(lane.Target))
                {
                    if (chain == null) throw new ArgumentException($"{owner}: {lane.Target} automation needs Track effects on.");
                    if (lane.Target == MusicAutomationTarget.ChorusMix && !chain.Chorus) throw new ArgumentException($"{owner}: ChorusMix automation needs Chorus on.");
                    if (lane.Target == MusicAutomationTarget.ReverbMix && !chain.Reverb) throw new ArgumentException($"{owner}: ReverbMix automation needs Reverb on.");
                    if (lane.Target == MusicAutomationTarget.GateDepth && !chain.Gate) throw new ArgumentException($"{owner}: GateDepth automation needs Gate on.");
                    if (lane.Target == MusicAutomationTarget.CrushMix && !chain.Crusher) throw new ArgumentException($"{owner}: CrushMix automation needs Crush Bits or Downsample.");
                    if ((lane.Target == MusicAutomationTarget.DriveDb || lane.Target == MusicAutomationTarget.DriveMix) && !chain.Drive) throw new ArgumentException($"{owner}: {lane.Target} automation needs Drive on.");
                    if (lane.Target == MusicAutomationTarget.Stutter && !chain.Stutter) throw new ArgumentException($"{owner}: Stutter automation needs Stutter on.");
                    if (lane.Target == MusicAutomationTarget.RotarySpeed && !(chain.Color?.Rotary ?? false)) throw new ArgumentException($"{owner}: RotarySpeed automation needs Color Rotary on.");
                }
                byTarget[(int)lane.Target] = lane;
            }
            return byTarget;
        }
    }
}
