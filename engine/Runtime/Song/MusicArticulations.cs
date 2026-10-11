using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// A playing technique of a track (palm mute, staccato, legato…), chosen per note by <see cref="MusicNote.Articulation"/> or a key
    /// switch. Playback V3+: zones with the same Articulation play it, and these settings shape its notes. 0 is the normal articulation.
    /// </summary>
    [Serializable]
    public struct MusicArticulation
    {
        [Range(1, MusicArticulations.MaxId)] public int Id;
        public string Name;
        [Tooltip("Seconds into the sample where these notes start (skips the attack of a legato note, for example).")]
        public float StartSeconds;
        [Range(-24, 12)] public float GainDb;
        [Tooltip("Note length multiplier (0 = unchanged; 0.3 = short, palm-mute/staccato style).")]
        public float LengthScale;
        public MusicArticulation(int id, string name, float startSeconds = 0, float gainDb = 0, float lengthScale = 0)
        { Id = id; Name = name; StartSeconds = startSeconds; GainDb = gainDb; LengthScale = lengthScale; }
    }

    /// <summary>A key switch: a note on <see cref="Key"/> plays nothing and sets the articulation of the track's later notes.</summary>
    [Serializable]
    public struct MusicKeySwitch
    {
        [Range(0, 127)] public int Key;
        [Range(0, MusicArticulations.MaxId)] public int Articulation;
        public MusicKeySwitch(int key, int articulation) { Key = key; Articulation = articulation; }
    }

    /// <summary>Compiled articulation settings (engine side).</summary>
    internal sealed class MusicArticulationSettings
    {
        public double StartSeconds, Gain = 1, LengthScale = 1;
    }

    /// <summary>Note variation: which notes play on a pass of the loop (<see cref="MusicNote.SkipChance"/>, RepeatEvery, RepeatOffset).</summary>
    public static class MusicVariation
    {
        public const int MaxRepeatEvery = 64;

        /// <summary>Whether a note plays on <paramref name="pass"/> (0 = first time through) of track <paramref name="part"/>. Deterministic; allocates nothing.</summary>
        public static bool Plays(in MusicNote note, long pass, int part, int seed)
        {
            if (note.RepeatEvery > 1 && pass % note.RepeatEvery != note.RepeatOffset) return false;
            if (note.SkipChance <= 0) return true;
            if (note.SkipChance >= 1) return false;
            ulong h = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)part * 0xBF58476D1CE4E5B9UL ^ (ulong)(long)Math.Round(note.Beat * 1e6) * 0x94D049BB133111EBUL
                      ^ (ulong)note.Key * 0x2545F4914F6CDD1DUL ^ (ulong)pass * 0xD6E8FEB86659FD93UL;
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31; // splitmix64 finalizer
            return (h >> 11) * (1.0 / (1UL << 53)) >= note.SkipChance;
        }

        /// <summary>The notes a track of a compiled cue plays on a pass (preview, export of one pass).</summary>
        public static MusicNote[] OnPass(MusicCue cue, int part, long pass)
        {
            var p = cue.GetPart(part); var result = new System.Collections.Generic.List<MusicNote>();
            for (int i = 0; i < p.NoteCount; i++) { var n = p.GetNote(i); if (Plays(n, pass, part, cue.VariationSeed)) result.Add(n); }
            return result.ToArray();
        }
    }

    /// <summary>Per-note expression and articulation helpers: key switch resolution, validation, compiled settings.</summary>
    public static class MusicArticulations
    {
        public const int MaxId = 127;
        public const float MaxBendSemitones = 24, MaxVibratoSemitones = 2, MaxStartSeconds = 10;

        /// <summary>
        /// Notes with key switches applied: key-switch notes are removed and every later note (by start; a switch at the same beat counts)
        /// whose Articulation is 0 takes the last switch's articulation. No switches = the same array.
        /// </summary>
        public static MusicNote[] ResolveKeySwitches(MusicNote[] notes, MusicKeySwitch[] switches)
        {
            if (notes == null || switches == null || switches.Length == 0) return notes;
            var map = new Dictionary<int, int>();
            foreach (var s in switches)
            {
                if (s.Key < 0 || s.Key > 127 || s.Articulation < 0 || s.Articulation > MaxId) throw new ArgumentException($"Key switch {s.Key}: key 0–127, articulation 0–{MaxId}.");
                map[s.Key] = s.Articulation;
            }
            var order = Enumerable.Range(0, notes.Length).OrderBy(i => notes[i].Beat).ThenBy(i => map.ContainsKey(notes[i].Key) ? 0 : 1).ToArray();
            var result = new List<MusicNote>(notes.Length);
            int current = 0;
            foreach (int i in order)
            {
                var n = notes[i];
                if (map.TryGetValue(n.Key, out int articulation)) { current = articulation; continue; }
                if (n.Articulation == 0) n.Articulation = current;
                result.Add(n);
            }
            return result.ToArray();
        }

        /// <summary>Checks every note's expression and articulation range (ArgumentException names the first bad note).</summary>
        public static void Validate(MusicNote[] notes)
        {
            foreach (var n in notes ?? Array.Empty<MusicNote>())
            {
                if (n.Articulation < 0 || n.Articulation > MaxId) throw new ArgumentException($"Note at beat {n.Beat}: articulation 0–{MaxId}.");
                if (!MusicTrack.Finite(n.SkipChance) || n.SkipChance < 0 || n.SkipChance > 1 || n.RepeatEvery < 0 || n.RepeatEvery > MusicVariation.MaxRepeatEvery ||
                    n.RepeatOffset < 0 || (n.RepeatEvery > 1 ? n.RepeatOffset >= n.RepeatEvery : n.RepeatOffset != 0))
                    throw new ArgumentException($"Note at beat {n.Beat}: skip chance 0–1, repeat every 0–{MusicVariation.MaxRepeatEvery} with offset below it.");
                if (!n.HasExpression) continue;
                if (!MusicTrack.Finite(n.BendFrom) || !MusicTrack.Finite(n.BendTo) || Math.Abs(n.BendFrom) > MaxBendSemitones || Math.Abs(n.BendTo) > MaxBendSemitones)
                    throw new ArgumentException($"Note at beat {n.Beat}: bend within ±{MaxBendSemitones} semitones.");
                if (!MusicTrack.Finite(n.BendStart) || !MusicTrack.Finite(n.BendEnd) || n.BendStart < 0 || n.BendEnd < 0)
                    throw new ArgumentException($"Note at beat {n.Beat}: bend start and end are beats after the note start (≥ 0).");
                if (!MusicTrack.Finite(n.Vibrato) || n.Vibrato < 0 || n.Vibrato > MaxVibratoSemitones)
                    throw new ArgumentException($"Note at beat {n.Beat}: vibrato depth 0–{MaxVibratoSemitones} semitones.");
            }
        }

        internal static MusicArticulationSettings[] Compile(MusicArticulation[] definitions)
        {
            if (definitions == null || definitions.Length == 0) return null;
            var table = new MusicArticulationSettings[MaxId + 1];
            foreach (var a in definitions)
            {
                if (a.Id < 1 || a.Id > MaxId) throw new ArgumentException($"Articulation {a.Name}: id 1–{MaxId} (0 is the normal articulation).");
                if (table[a.Id] != null) throw new ArgumentException($"Two articulations with id {a.Id}.");
                if (!MusicTrack.Finite(a.StartSeconds) || a.StartSeconds < 0 || a.StartSeconds > MaxStartSeconds || !MusicTrack.Finite(a.GainDb) || a.GainDb < -24 || a.GainDb > 12 ||
                    !MusicTrack.Finite(a.LengthScale) || a.LengthScale < 0 || a.LengthScale > 4)
                    throw new ArgumentException($"Articulation {a.Name}: start 0–{MaxStartSeconds} s, gain −24..12 dB, length scale 0–4.");
                table[a.Id] = new MusicArticulationSettings { StartSeconds = a.StartSeconds, Gain = Math.Pow(10, a.GainDb / 20), LengthScale = a.LengthScale > 0 ? a.LengthScale : 1 };
            }
            return table;
        }
    }
}
