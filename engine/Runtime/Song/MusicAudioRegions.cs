using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>How an audio region follows the song.</summary>
    public enum MusicRegionWarp
    {
        /// <summary>The recording plays at its own speed (Semitones changes speed and pitch together, like tape).</summary>
        Off,
        /// <summary>Time-stretched from <see cref="MusicAudioRegion.SourceBpm"/> to the song tempo (Semitones = pitch only).</summary>
        Tempo,
        /// <summary>Warp markers pin recording times to beats; the stretch between them follows (live takes, detected beats).</summary>
        Markers,
    }

    /// <summary>A warp marker: <see cref="SourceSeconds"/> of the recording lands on <see cref="Beat"/> (beats from the region start).</summary>
    [Serializable]
    public struct MusicWarpMarker
    {
        public double Beat;
        public float SourceSeconds;
        public MusicWarpMarker(double beat, float sourceSeconds) { Beat = beat; SourceSeconds = sourceSeconds; }
    }

    /// <summary>
    /// A piece of a recording placed on a track (playback V3+): from <see cref="SourceStartSeconds"/> of the recording, at
    /// <see cref="StartBeat"/>, for <see cref="LengthBeats"/>, with gain, fades and transposition; optionally warped to the song.
    /// </summary>
    [Serializable]
    public sealed class MusicAudioRegion
    {
        [Tooltip("Recording (null = the track's Sample).")]
        public AudioClip Sample;
        public double StartBeat, LengthBeats = 4;
        [Tooltip("Where in the recording the region starts (Warp Off / Tempo; markers carry their own times).")]
        public float SourceStartSeconds;
        [Range(-48, 12)] public float GainDb;
        public float FadeInBeats, FadeOutBeats;
        [Range(-24, 24)] public float Semitones;
        public MusicRegionWarp Warp;
        [Tooltip("Warp = Tempo: the recording's own BPM.")]
        public float SourceBpm = 120;
        public MusicWarpMarker[] Markers = Array.Empty<MusicWarpMarker>();
        public bool Mute;

        public MusicAudioRegion Clone() { var c = (MusicAudioRegion)MemberwiseClone(); c.Markers = (MusicWarpMarker[])(Markers ?? Array.Empty<MusicWarpMarker>()).Clone(); return c; }
    }

    /// <summary>Compiled region the engine plays as one voice.</summary>
    internal sealed class MusicRegionSegment
    {
        public MusicInstrument Instrument;
        public double RegionStart, RegionLength, FadeIn, FadeOut, Gain, SourceStartFrames, Pitch;
        /// <summary>Recording seconds per song beat (time stretch); 0 = the recording's own speed.</summary>
        public double SecondsPerBeat;
        /// <summary>Warp markers (beats from the region start → recording seconds), or null: the stretch follows them, one continuous voice.</summary>
        public double[] MarkerBeats, MarkerSeconds;

        /// <summary>Recording seconds at <paramref name="offset"/> beats into the region (markers; linear beyond the ends). Allocates nothing.</summary>
        public double SourceAt(double offset)
        {
            var b = MarkerBeats; int lo = 0, hi = b.Length - 2;
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (b[mid] <= offset) lo = mid; else hi = mid - 1; }
            return MarkerSeconds[lo] + (offset - b[lo]) * (MarkerSeconds[lo + 1] - MarkerSeconds[lo]) / (b[lo + 1] - b[lo]);
        }
    }

    /// <summary>Audio region compilation and undoable edits (split, trim, move, fades, gain, warp to detected beats).</summary>
    public static class MusicAudioRegions
    {
        public const int MaxRegionsPerTrack = 1024, MaxMarkers = 4096;

        /// <summary>
        /// The recording a region plays: its own Sample, else the track's. Uses Unity's == (an empty reference loaded from an asset is a
        /// "fake null" that C#'s ?? would keep).
        /// </summary>
        public static AudioClip RecordingOf(MusicAudioRegion region, AudioClip trackSample) => region.Sample != null ? region.Sample : trackSample;

        // ---------------- compile ----------------

        /// <summary>Validates a track's regions (ArgumentException names the first problem).</summary>
        public static void Validate(MusicCueDefinition.Part p, double loopBeats)
        {
            var regions = p.Regions ?? Array.Empty<MusicAudioRegion>();
            if (regions.Length > MaxRegionsPerTrack) throw new ArgumentException($"At most {MaxRegionsPerTrack} audio regions per track.");
            for (int i = 0; i < regions.Length; i++)
            {
                var r = regions[i]; string where = $"Region {i + 1}";
                if (r == null) throw new ArgumentException(where + " is null.");
                if (RecordingOf(r, p.Sample) == null) throw new ArgumentException(where + ": no recording (set the region's or the track's Sample).");
                if (!MusicTrack.Finite(r.StartBeat) || r.StartBeat < 0 || r.StartBeat >= loopBeats || !MusicTrack.Finite(r.LengthBeats) || r.LengthBeats <= 0)
                    throw new ArgumentException($"{where}: start inside the song (0 ≤ beat < {loopBeats}) and a positive length.");
                if (!MusicTrack.Finite(r.SourceStartSeconds) || r.SourceStartSeconds < 0 || !MusicTrack.Finite(r.GainDb) || r.GainDb < -48 || r.GainDb > 12 ||
                    !MusicTrack.Finite(r.FadeInBeats) || r.FadeInBeats < 0 || !MusicTrack.Finite(r.FadeOutBeats) || r.FadeOutBeats < 0 || r.FadeInBeats + r.FadeOutBeats > r.LengthBeats + 1e-9 ||
                    !MusicTrack.Finite(r.Semitones) || Math.Abs(r.Semitones) > 24 || !Enum.IsDefined(typeof(MusicRegionWarp), r.Warp))
                    throw new ArgumentException($"{where}: source start ≥ 0, gain −48..12 dB, fades within the length, ±24 semitones.");
                if (r.Warp == MusicRegionWarp.Tempo && (!MusicTrack.Finite(r.SourceBpm) || r.SourceBpm < 10 || r.SourceBpm > 1000)) throw new ArgumentException(where + ": source BPM 10..1000.");
                if (r.Warp == MusicRegionWarp.Markers)
                {
                    var m = Sorted(r);
                    if (m.Length < 2 || m.Length > MaxMarkers) throw new ArgumentException($"{where}: 2..{MaxMarkers} warp markers.");
                    for (int k = 1; k < m.Length; k++)
                        if (!(m[k].Beat > m[k - 1].Beat + 1e-9) || !(m[k].SourceSeconds > m[k - 1].SourceSeconds) || !MusicTrack.Finite(m[k].Beat) || m[0].SourceSeconds < 0)
                            throw new ArgumentException($"{where}: warp markers must move forward in both beats and recording time.");
                }
            }
        }

        static MusicWarpMarker[] Sorted(MusicAudioRegion r) => (r.Markers ?? Array.Empty<MusicWarpMarker>()).OrderBy(m => m.Beat).ToArray();

        /// <summary>
        /// The segments the engine plays for a track's regions (one per region) and their hidden notes (Articulation = −1 − segment index;
        /// key 60). Muted regions and parts past the loop end are left out. <paramref name="instrumentFor"/> gives the mono sample instrument of a clip.
        /// </summary>
        internal static MusicRegionSegment[] Compile(MusicCueDefinition.Part p, double loopBeats, Func<AudioClip, MusicInstrument> instrumentFor, out MusicNote[] notes)
        {
            var segments = new List<MusicRegionSegment>(); var hidden = new List<MusicNote>();
            foreach (var r in p.Regions ?? Array.Empty<MusicAudioRegion>())
            {
                if (r.Mute) continue;
                var instrument = instrumentFor(RecordingOf(r, p.Sample));
                double length = Math.Min(r.LengthBeats, loopBeats - r.StartBeat), gain = Math.Pow(10, r.GainDb / 20), pitch = Math.Pow(2, r.Semitones / 12.0);
                var segment = new MusicRegionSegment { Instrument = instrument, RegionStart = r.StartBeat, RegionLength = length, FadeIn = r.FadeInBeats, FadeOut = r.FadeOutBeats, Gain = gain, Pitch = pitch };
                switch (r.Warp)
                {
                    case MusicRegionWarp.Off: segment.SourceStartFrames = r.SourceStartSeconds * instrument.SampleRate; break;
                    case MusicRegionWarp.Tempo: segment.SourceStartFrames = r.SourceStartSeconds * instrument.SampleRate; segment.SecondsPerBeat = 60.0 / r.SourceBpm; break;
                    default:
                        var m = Sorted(r);
                        segment.MarkerBeats = m.Select(x => x.Beat).ToArray(); segment.MarkerSeconds = m.Select(x => (double)x.SourceSeconds).ToArray();
                        segment.SecondsPerBeat = 1; // stretched; the markers give the position
                        segment.SourceStartFrames = Math.Max(0, segment.SourceAt(0)) * instrument.SampleRate;
                        break;
                }
                segments.Add(segment);
                hidden.Add(new MusicNote(r.StartBeat, length, 60, 1) { Articulation = -segments.Count });
            }
            notes = hidden.ToArray();
            return segments.Count == 0 ? null : segments.ToArray();
        }

        // ---------------- source mapping ----------------

        /// <summary>Recording seconds heard <paramref name="offsetBeats"/> into a region (Warp Off follows the song's tempo map from the region start).</summary>
        public static double SourceSecondsAt(MusicCueDefinition song, MusicAudioRegion r, double offsetBeats)
        {
            switch (r.Warp)
            {
                case MusicRegionWarp.Off:
                    var map = new MusicTempoMap(song.Bpm, song.LoopBeats + offsetBeats + r.StartBeat + 1, song.TempoChanges);
                    double end = Math.Min(r.StartBeat + offsetBeats, song.LoopBeats + offsetBeats + r.StartBeat);
                    return r.SourceStartSeconds + (map.SecondsAt(end) - map.SecondsAt(r.StartBeat)) * Math.Pow(2, r.Semitones / 12.0);
                case MusicRegionWarp.Tempo: return r.SourceStartSeconds + offsetBeats * 60.0 / r.SourceBpm;
                default:
                    var m = Sorted(r);
                    int k = 0; while (k + 2 < m.Length && offsetBeats >= m[k + 1].Beat) k++;
                    return m[k].SourceSeconds + (offsetBeats - m[k].Beat) * (m[k + 1].SourceSeconds - m[k].SourceSeconds) / (m[k + 1].Beat - m[k].Beat);
            }
        }

        /// <summary>The part of a region from <paramref name="from"/> to <paramref name="to"/> beats into it, as a new region sounding the same.</summary>
        public static MusicAudioRegion Piece(MusicCueDefinition song, MusicAudioRegion r, double from, double to)
        {
            var c = r.Clone();
            c.StartBeat = r.StartBeat + from; c.LengthBeats = to - from;
            if (r.Warp == MusicRegionWarp.Markers)
            {
                var m = Sorted(r).Select(x => new MusicWarpMarker(x.Beat - from, x.SourceSeconds)).ToList();
                // Keep the stretch at both ends: markers at the new start and end if none are there.
                if (!m.Any(x => Math.Abs(x.Beat) < 1e-9)) m.Add(new MusicWarpMarker(0, (float)SourceSecondsAt(song, r, from)));
                if (!m.Any(x => Math.Abs(x.Beat - (to - from)) < 1e-9)) m.Add(new MusicWarpMarker(to - from, (float)SourceSecondsAt(song, r, to)));
                c.Markers = m.Where(x => x.Beat > -1e-9 && x.Beat < to - from + 1e-9).OrderBy(x => x.Beat).ToArray();
            }
            else c.SourceStartSeconds = (float)SourceSecondsAt(song, r, from);
            c.FadeInBeats = from > 1e-9 ? 0 : Math.Min(r.FadeInBeats, (float)(to - from));
            c.FadeOutBeats = to < r.LengthBeats - 1e-9 ? 0 : Math.Min(r.FadeOutBeats, (float)(to - from) - c.FadeInBeats);
            return c;
        }
    }
}
