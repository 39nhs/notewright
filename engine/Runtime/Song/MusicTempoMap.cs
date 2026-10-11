using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    [Serializable]
    public struct MusicTempoChange
    {
        [Tooltip("Zero-based beat; fractional positions are allowed, e.g. 6.5.")]
        public double Beat;
        public double Bpm;
        [Tooltip("0 = instant; otherwise ramp linearly in beats from the preceding tempo.")]
        public double RampBeats;
    }

    public sealed class MusicTempoMap
    {
        /// <summary>
        /// 60000 BPM = 1 ms per beat: a beat still spans 8 samples at the lowest supported rate (8 kHz), so per-sample note
        /// scheduling, bar logic and half-beat harmony windows keep working. Raising it further turns notes into sub-sample clicks.
        /// </summary>
        public const double MinBpm = 10, MaxBpm = 60000;
        readonly MusicTempoChange[] changes;
        readonly double initial, length;
        public MusicTempoMap(double bpm, double loopBeats, MusicTempoChange[] source)
        {
            initial = bpm; length = loopBeats;
            changes = source == null ? Array.Empty<MusicTempoChange>() : (MusicTempoChange[])source.Clone();
            Array.Sort(changes, (a,b) => a.Beat.CompareTo(b.Beat));
            double end = -1, previous = -1;
            foreach(var change in changes)
            {
                if (!MusicTrack.Finite(change.Beat) || change.Beat < 0 || change.Beat >= length || change.Beat < end || change.Beat == previous ||
                    !MusicTrack.Finite(change.Bpm) || change.Bpm < MinBpm || change.Bpm > MaxBpm ||
                    !MusicTrack.Finite(change.RampBeats) || change.RampBeats < 0 || change.Beat + change.RampBeats > length)
                    throw new ArgumentException($"Tempo changes must be within the song, {MinBpm}–{MaxBpm} BPM, with no overlapping ramps or duplicate beats.");
                previous = change.Beat; end = change.Beat + change.RampBeats;
            }
        }
        public double At(double beat, bool loop = true)
        {
            beat = Math.Max(0,beat);
            if(loop) beat %= length;
            double bpm = initial;
            foreach(var change in changes)
            {
                if(beat < change.Beat) break;
                if(change.RampBeats > 0 && beat < change.Beat + change.RampBeats)
                    return bpm + (change.Bpm-bpm)*(beat-change.Beat)/change.RampBeats;
                bpm = change.Bpm;
            }
            return bpm;
        }
        // Exact integral of 60/BPM over linear-in-beat ramps, used for export and seeking.
        public double SecondsAt(double beat)
        {
            if(!MusicTrack.Finite(beat) || beat < 0 || beat > length) throw new ArgumentOutOfRangeException(nameof(beat));
            double seconds=0, cursor=0, bpm=initial;
            foreach(var change in changes)
            {
                if(beat <= change.Beat) break;
                seconds += (change.Beat-cursor)*60/bpm; cursor=change.Beat;
                double span = Math.Min(change.RampBeats,beat-cursor);
                if(span > 0)
                {
                    double slope=(change.Bpm-bpm)/change.RampBeats;
                    seconds += Math.Abs(slope)<1e-10 ? span*60/bpm : 60/slope*Math.Log((bpm+slope*span)/bpm);
                    cursor += span;
                    if(cursor >= beat) return seconds;
                }
                bpm=change.Bpm;
            }
            return seconds+(beat-cursor)*60/bpm;
        }

        public int ChangeCount => changes.Length;
        public const int MaxSpeedChanges = 2048;

        /// <summary>
        /// The tempo map with a V3 master Speed lane folded in (tempo × <see cref="MusicAutomation.SpeedTempo"/>, held at <see cref="MinBpm"/>), so durations, seeking and
        /// exports follow the tape speed. Constant stretches stay exact; varying ones become linear ramps of at most 1/8 beat.
        /// </summary>
        internal static MusicTempoMap WithSpeed(double bpm, double loopBeats, MusicTempoChange[] source, MusicAutomation speed)
        {
            var map = new MusicTempoMap(bpm, loopBeats, source);
            if (speed == null) return map;
            var breaks = new System.Collections.Generic.SortedSet<double> { 0 };
            foreach (var c in map.changes) { breaks.Add(c.Beat); if (c.RampBeats > 0) breaks.Add(c.Beat + c.RampBeats); }
            for (int i = 0; i < speed.PointCount; i++) breaks.Add(speed.BeatAt(i));
            var points = new System.Collections.Generic.List<double>();
            foreach (double b in breaks) if (b >= 0 && b < loopBeats - 1e-9) points.Add(b);
            points.Add(loopBeats);
            double Effective(double beat) => Math.Max(MinBpm, Math.Min(MaxBpm, map.At(Math.Min(beat, loopBeats - 1e-12), false) * MusicAutomation.SpeedTempo(speed.Evaluate(beat))));
            double step = .125, varying = 0;
            for (int i = 0; i + 1 < points.Count; i++) if (Varies(points[i], points[i + 1])) varying += points[i + 1] - points[i];
            if (varying / step > MaxSpeedChanges) step = varying / MaxSpeedChanges;
            bool Varies(double a, double c)
            {
                double first = Effective(a + 1e-9), mid = Effective((a + c) * .5), last = Effective(c - 1e-9);
                return Math.Abs(first - mid) > 1e-9 || Math.Abs(mid - last) > 1e-9;
            }
            var result = new System.Collections.Generic.List<MusicTempoChange>();
            double initial = Effective(1e-9), current = initial;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double a = points[i], c = points[i + 1];
                if (!Varies(a, c))
                {
                    double v = Effective(a + 1e-9);
                    if (Math.Abs(v - current) > 1e-9 && a > 0) result.Add(new MusicTempoChange { Beat = a, Bpm = v });
                    current = v; continue;
                }
                int n = Math.Max(1, (int)Math.Ceiling((c - a) / step - 1e-9));
                double h = (c - a) / n;
                for (int k = 0; k < n; k++)
                {
                    double x0 = a + k * h, start = Effective(x0 + 1e-9), end = Effective(x0 + h - 1e-9), ramp = h;
                    if (k == 0 && Math.Abs(start - current) > 1e-9 && a > 0)
                    {
                        result.Add(new MusicTempoChange { Beat = x0, Bpm = start }); // a jump at the stretch start, then the ramp just after it
                        x0 += 1e-7; ramp -= 1e-7;
                    }
                    result.Add(new MusicTempoChange { Beat = x0, Bpm = end, RampBeats = ramp });
                    current = end;
                }
            }
            return new MusicTempoMap(initial, loopBeats, result.ToArray());
        }
    }
}
