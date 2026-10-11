using System;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>A named song section starting at a beat (Intro, A, B, Chorus…). It lasts until the next marker or the loop end.</summary>
    [Serializable]
    public struct MusicSectionMarker
    {
        [Tooltip("Section name (Intro, Verse, Chorus…). The same name may appear more than once (a repeated chorus).")]
        public string Name;
        [Tooltip("Beat where the section starts (quarter notes from the song start, usually on a bar line).")]
        public double Beat;
        public MusicSectionMarker(string name, double beat) { Name = name; Beat = beat; }
        public override string ToString() => $"{Name}@{Beat:0.###}";
    }

    /// <summary>
    /// The section markers of a cue, sorted by beat: which section a beat is in and where each section starts and ends. Authoring and
    /// game logic only (arrange tools, loop a section, react to sections); never affects the sound. Notes before the first marker are in no section.
    /// </summary>
    public sealed class MusicSections
    {
        public const int MaxMarkers = 1024, MaxNameLength = 64;
        readonly MusicSectionMarker[] markers;
        public double LoopBeats { get; }
        public int Count => markers.Length;
        public MusicSectionMarker this[int index] => markers[index];

        public MusicSections(double loopBeats, MusicSectionMarker[] sections)
        {
            if (!MusicTrack.Finite(loopBeats) || loopBeats <= 0) throw new ArgumentException("Invalid loop length.");
            LoopBeats = loopBeats;
            sections = sections ?? Array.Empty<MusicSectionMarker>();
            if (sections.Length > MaxMarkers) throw new ArgumentException($"At most {MaxMarkers} section markers.");
            foreach (var s in sections)
            {
                if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > MaxNameLength) throw new ArgumentException($"Section names must be 1–{MaxNameLength} characters (marker at beat {s.Beat}).");
                if (!MusicTrack.Finite(s.Beat) || s.Beat < 0 || s.Beat >= loopBeats - 1e-9) throw new ArgumentException($"Section \"{s.Name}\" must start inside the song (0 ≤ beat < {loopBeats}).");
            }
            markers = sections.OrderBy(s => s.Beat).ToArray(); // stable
            for (int i = 1; i < markers.Length; i++)
                if (markers[i].Beat - markers[i - 1].Beat < 1e-6) throw new ArgumentException($"Sections \"{markers[i - 1].Name}\" and \"{markers[i].Name}\" start at the same beat {markers[i].Beat}.");
        }

        /// <summary>Validated markers of a song asset (throws ArgumentException when one is invalid).</summary>
        public static MusicSections Of(MusicCueDefinition song) => new MusicSections(Math.Max(1e-9, song.LoopBeats), song.Sections);

        /// <summary>Index of the section a beat is in (the last marker at or before it), or -1 before the first marker.</summary>
        public int IndexAt(double beat)
        {
            int lo = 0, hi = markers.Length - 1, found = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (markers[mid].Beat <= beat + 1e-9) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found;
        }

        /// <summary>Name of the section a beat is in, or "" before the first marker.</summary>
        public string NameAt(double beat) { int i = IndexAt(beat); return i < 0 ? "" : markers[i].Name; }

        /// <summary>Index of the <paramref name="occurrence"/>-th (0 = first) section with this name (ordinal match), or -1.</summary>
        public int IndexOf(string name, int occurrence = 0)
        {
            for (int i = 0; i < markers.Length; i++) if (string.Equals(markers[i].Name, name, StringComparison.Ordinal) && occurrence-- == 0) return i;
            return -1;
        }

        public double Start(int index) => markers[index].Beat;
        /// <summary>Beat where the section ends: the next marker or the loop end.</summary>
        public double End(int index) => index + 1 < markers.Length ? markers[index + 1].Beat : LoopBeats;
        public double Length(int index) => End(index) - Start(index);

        /// <summary>Start and end beat of the first (or <paramref name="occurrence"/>-th) section with this name.</summary>
        public bool TryRange(string name, out double start, out double end, int occurrence = 0)
        {
            int i = IndexOf(name, occurrence);
            start = i < 0 ? 0 : Start(i); end = i < 0 ? 0 : End(i);
            return i >= 0;
        }

        public MusicSectionMarker[] ToArray() => (MusicSectionMarker[])markers.Clone();
    }
}
