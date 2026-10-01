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
    }
}
