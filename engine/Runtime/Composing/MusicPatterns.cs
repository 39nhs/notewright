using System;
using System.Collections.Generic;
using System.Linq;

namespace Graze.Presentation.Audio
{
    /// <summary>A reusable pattern (clip): notes relative to its start, <see cref="LengthBeats"/> long. Placed on tracks by name.</summary>
    [Serializable]
    public sealed class MusicPattern
    {
        public string Name = "Pattern";
        public double LengthBeats = 4;
        public MusicNote[] Notes = Array.Empty<MusicNote>();
    }

    /// <summary>A placement of a pattern on a track: it plays at <see cref="StartBeat"/>, <see cref="Repeats"/> times back to back, transposed.</summary>
    [Serializable]
    public struct MusicPatternPlacement
    {
        public string Pattern;
        public double StartBeat;
        public int Transpose;
        public int Repeats;
        public MusicPatternPlacement(string pattern, double startBeat, int transpose = 0, int repeats = 1) { Pattern = pattern; StartBeat = startBeat; Transpose = transpose; Repeats = repeats; }
        public override string ToString() => $"{Pattern}@{StartBeat:0.##}{(Transpose != 0 ? $"{Transpose:+0;-0}" : "")}{(Repeats > 1 ? $"x{Repeats}" : "")}";
    }

    /// <summary>
    /// Pattern reuse: a song keeps patterns (<c>MusicCueDefinition.Patterns</c>) and each track places them (<c>Part.Placements</c>). Compile
    /// plays a track's own Notes plus every placement expanded, so editing a pattern changes every place it is used. Undoable commands: make a
    /// pattern from selected notes, place, move, remove, unlink (turn a placement into plain notes), make unique (copy the pattern for one
    /// placement), rename, delete, set notes. Arrange commands move whole placements and unlink the ones a cut goes through.
    /// </summary>
    public static class MusicPatterns
    {
        public const int MaxPatterns = 1024, MaxPlacements = 4096, MaxRepeats = 1024;
        const double Eps = 1e-9;

        static MusicCueDefinition.Part Part(MusicCueDefinition song, int part)
        {
            if (song?.Parts == null || part < 0 || part >= song.Parts.Length || song.Parts[part] == null) throw new ArgumentOutOfRangeException(nameof(part), "Unknown part index.");
            var p = song.Parts[part]; p.Notes = p.Notes ?? Array.Empty<MusicNote>(); p.Placements = p.Placements ?? Array.Empty<MusicPatternPlacement>();
            return p;
        }

        public static MusicPattern Find(MusicCueDefinition song, string name) => (song.Patterns ?? Array.Empty<MusicPattern>()).FirstOrDefault(p => p != null && p.Name == name);

        static MusicPattern Require(MusicCueDefinition song, string name) => Find(song, name) ?? throw new ArgumentException($"No pattern named \"{name}\".");

        /// <summary>Beats a placement covers (pattern length × repeats).</summary>
        public static double Length(MusicCueDefinition song, MusicPatternPlacement placement) => Require(song, placement.Pattern).LengthBeats * Math.Max(1, placement.Repeats);

        /// <summary>The notes a placement plays, in song beats (inside the loop only).</summary>
        public static MusicNote[] Expand(MusicCueDefinition song, MusicPatternPlacement placement)
        {
            var pattern = Require(song, placement.Pattern);
            var result = new List<MusicNote>();
            for (int r = 0; r < Math.Max(1, placement.Repeats); r++)
                foreach (var n in pattern.Notes ?? Array.Empty<MusicNote>())
                {
                    var note = n; note.Beat = placement.StartBeat + r * pattern.LengthBeats + n.Beat; note.Key = Math.Max(0, Math.Min(127, n.Key + placement.Transpose));
                    if (note.Beat < song.LoopBeats - Eps) result.Add(note);
                }
            return result.ToArray();
        }

        /// <summary>Everything a track plays: its own notes plus its placements expanded, sorted by beat.</summary>
        public static MusicNote[] PlayedNotes(MusicCueDefinition song, MusicCueDefinition.Part part)
        {
            var placements = part.Placements ?? Array.Empty<MusicPatternPlacement>();
            if (placements.Length == 0) return part.Notes ?? Array.Empty<MusicNote>();
            return (part.Notes ?? Array.Empty<MusicNote>()).Concat(placements.SelectMany(p => Expand(song, p))).OrderBy(n => n.Beat).ToArray();
        }

        // ---------------- arrange support ----------------

        /// <summary>Unlinks every placement that a cut at <paramref name="beat"/> goes through (starts before it and ends after it).</summary>
        internal static void UnlinkAcross(MusicCueDefinition s, double beat)
        {
            foreach (var part in s.Parts ?? Array.Empty<MusicCueDefinition.Part>())
            {
                if (part?.Placements == null) continue;
                for (int i = part.Placements.Length - 1; i >= 0; i--)
                {
                    var p = part.Placements[i]; var pattern = Find(s, p.Pattern); if (pattern == null) continue;
                    double end = p.StartBeat + pattern.LengthBeats * Math.Max(1, p.Repeats);
                    if (p.StartBeat < beat - Eps && end > beat + Eps) Unlink(s, part, i);
                }
            }
        }

        /// <summary>Unlinks every placement that overlaps [from, to).</summary>
        internal static void UnlinkOverlapping(MusicCueDefinition s, double from, double to)
        {
            foreach (var part in s.Parts ?? Array.Empty<MusicCueDefinition.Part>())
            {
                if (part?.Placements == null) continue;
                for (int i = part.Placements.Length - 1; i >= 0; i--)
                {
                    var p = part.Placements[i]; var pattern = Find(s, p.Pattern); if (pattern == null) continue;
                    double end = p.StartBeat + pattern.LengthBeats * Math.Max(1, p.Repeats);
                    if (p.StartBeat < to - Eps && end > from + Eps) Unlink(s, part, i);
                }
            }
        }

        static void Unlink(MusicCueDefinition s, MusicCueDefinition.Part part, int index)
        {
            if (index < 0 || index >= part.Placements.Length) throw new ArgumentOutOfRangeException(nameof(index));
            var notes = Expand(s, part.Placements[index]);
            part.Notes = (part.Notes ?? Array.Empty<MusicNote>()).Concat(notes).OrderBy(n => n.Beat).ToArray();
            part.Placements = part.Placements.Where((x, i) => i != index).ToArray();
        }

        // ---------------- validation ----------------

        /// <summary>Throws ArgumentException for invalid patterns or placements (Compile reports it).</summary>
        public static void Validate(MusicCueDefinition song)
        {
            var patterns = song.Patterns ?? Array.Empty<MusicPattern>();
            if (patterns.Length > MaxPatterns) throw new ArgumentException($"At most {MaxPatterns} patterns.");
            var names = new HashSet<string>();
            foreach (var p in patterns)
            {
                if (p == null) throw new ArgumentException("A pattern is missing.");
                if (string.IsNullOrWhiteSpace(p.Name) || !names.Add(p.Name)) throw new ArgumentException($"Pattern names must be unique and not empty (\"{p.Name}\").");
                if (!MusicTrack.Finite(p.LengthBeats) || p.LengthBeats <= 0 || p.LengthBeats > MusicCue.MaxLoopBeats) throw new ArgumentException($"Pattern \"{p.Name}\" needs a positive length.");
                if ((p.Notes?.Length ?? 0) > MusicPart.MaxNotes) throw new ArgumentException($"Pattern \"{p.Name}\" has too many notes.");
                foreach (var n in p.Notes ?? Array.Empty<MusicNote>())
                    if (!MusicTrack.Finite(n.Beat) || n.Beat < 0 || n.Beat >= p.LengthBeats - Eps) throw new ArgumentException($"Pattern \"{p.Name}\": notes must start inside its {p.LengthBeats} beats.");
            }
            foreach (var part in song.Parts ?? Array.Empty<MusicCueDefinition.Part>())
            {
                var placements = part?.Placements ?? Array.Empty<MusicPatternPlacement>();
                if (placements.Length > MaxPlacements) throw new ArgumentException($"{part.Name}: at most {MaxPlacements} pattern placements.");
                foreach (var p in placements)
                {
                    if (!names.Contains(p.Pattern ?? "")) throw new ArgumentException($"{part.Name}: placement {p} uses a missing pattern.");
                    try { CheckPlacement(song, p); } catch (ArgumentException e) { throw new ArgumentException($"{part.Name}: {e.Message}"); }
                }
            }
        }

        static void CheckPlacement(MusicCueDefinition s, MusicPatternPlacement p)
        {
            if (!MusicTrack.Finite(p.StartBeat) || p.StartBeat < 0 || p.StartBeat >= s.LoopBeats - Eps) throw new ArgumentException($"Placement {p} must start inside the song.");
            if (p.Repeats < 1 || p.Repeats > MaxRepeats) throw new ArgumentException($"Placement {p}: repeats 1..{MaxRepeats}.");
            if (p.Transpose < -48 || p.Transpose > 48) throw new ArgumentException($"Placement {p}: transpose -48..48.");
        }

        static void Index(MusicCueDefinition.Part p, int index) { if (index < 0 || index >= p.Placements.Length) throw new ArgumentOutOfRangeException(nameof(index)); }

        static void AddPattern(MusicCueDefinition s, MusicPattern pattern)
        {
            var list = s.Patterns ?? Array.Empty<MusicPattern>();
            if (list.Length >= MaxPatterns) throw new ArgumentException($"At most {MaxPatterns} patterns.");
            s.Patterns = list.Concat(new[] { pattern }).ToArray();
        }

        static string UniqueName(MusicCueDefinition s, string name)
        {
            if (Find(s, name) == null) return name;
            for (int i = 2; ; i++) if (Find(s, $"{name} {i}") == null) return $"{name} {i}";
        }
    }
}
