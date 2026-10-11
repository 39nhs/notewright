using System;
using System.Collections.Generic;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// MIDI expression → playback V3 track lanes: pitch bend → PitchSemitones (within the bend range), CC1 → Modulation (0..1),
    /// CC74 → Brightness (64 = 0, ±4 octaves). Points use Step curves, like the controller messages they come from.
    /// </summary>
    public static class MusicMidiExpression
    {
        public const double DefaultBendRange = 2, BrightnessOctaves = 4;
        public static readonly MusicAutomationTarget[] Targets = { MusicAutomationTarget.PitchSemitones, MusicAutomationTarget.Modulation, MusicAutomationTarget.Brightness };

        /// <summary>14-bit bend (0..16383, 8192 = centre) to semitones.</summary>
        public static float Bend(int value14, double rangeSemitones) =>
            (float)Math.Max(-24, Math.Min(24, (value14 - 8192) / 8192.0 * rangeSemitones));
        public static float Modulation(int cc) => cc / 127f;
        public static float Brightness(int cc) => (float)((cc - 64) / 64.0 * BrightnessOctaves);

        /// <summary>Index into <see cref="Targets"/> for a controller number (CC1, CC74), else -1.</summary>
        public static int ControllerTarget(int cc) => cc == 1 ? 1 : cc == 74 ? 2 : -1;

        /// <summary>Appends a Step point unless it repeats the previous value; false when the lane is full.</summary>
        internal static bool Add(List<MusicAutomationPoint> points, double beat, float value)
        {
            if (points.Count > 0 && points[points.Count - 1].Value == value) return true;
            if (points.Count >= MusicAutomation.MaxPoints - 2) return false;
            if (points.Count > 0 && points[points.Count - 1].Beat == beat) points[points.Count - 1] = new MusicAutomationPoint(beat, value, MusicAutomationCurve.Step);
            else points.Add(new MusicAutomationPoint(beat, value, MusicAutomationCurve.Step));
            return true;
        }

        /// <summary>
        /// Finished lane points for [start, end]: pitch and brightness start and end at 0 (centre) so the rest of the song is not bent;
        /// modulation holds what was played. Null when nothing was recorded.
        /// </summary>
        internal static MusicAutomationPoint[] Finish(List<MusicAutomationPoint> points, int target, double start, double end)
        {
            if (points.Count == 0) return null;
            var result = new List<MusicAutomationPoint>(points);
            if (target != 1)
            {
                if (result[0].Beat > start) result.Insert(0, new MusicAutomationPoint(start, 0, MusicAutomationCurve.Step));
                if (result[result.Count - 1].Value != 0 && end > result[result.Count - 1].Beat) result.Add(new MusicAutomationPoint(end, 0, MusicAutomationCurve.Step));
            }
            return result.ToArray();
        }

        /// <summary>Writes recorded points into a part's lane of that target: replaces the lane's points inside [start, end] (or the whole lane).</summary>
        internal static void Merge(MusicCueDefinition.Part part, MusicAutomationTarget target, MusicAutomationPoint[] recorded, double start, double end, bool replaceAll)
        {
            var lanes = new List<MusicAutomationLane>(part.Automation ?? Array.Empty<MusicAutomationLane>());
            var lane = lanes.Find(l => l != null && l.Target == target);
            if (lane == null) { lane = new MusicAutomationLane(target); lanes.Add(lane); }
            var points = new List<MusicAutomationPoint>();
            if (!replaceAll) foreach (var p in lane.Points ?? Array.Empty<MusicAutomationPoint>()) if (p.Beat < start - 1e-9 || p.Beat > end + 1e-9) points.Add(p);
            points.AddRange(recorded);
            points.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            if (points.Count > MusicAutomation.MaxPoints) throw new ArgumentException($"{target} automation would exceed {MusicAutomation.MaxPoints} points.");
            lane.Points = points.ToArray(); lane.Bypass = false;
            part.Automation = lanes.ToArray();
        }
    }
}
