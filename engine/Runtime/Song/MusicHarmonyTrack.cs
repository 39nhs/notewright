using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>A key change at a beat ("E minor", "Bb dorian"…). The song's Key is the key before the first change.</summary>
    [Serializable]
    public struct MusicKeyChange
    {
        public double Beat;
        public string Key;
        public MusicKeyChange(double beat, string key) { Beat = beat; Key = key; }
    }

    /// <summary>A chord-track entry: the chord in force from its beat until the next entry. Chord names (Am, F#m7, C/E) or Roman numerals in the key at that beat.</summary>
    [Serializable]
    public struct MusicChordMarker
    {
        public double Beat;
        public string Chord;
        public MusicChordMarker(double beat, string chord) { Beat = beat; Chord = chord; }
    }

    /// <summary>
    /// The song's key timeline and chord track (authoring and game data; the sound does not change): which key and chord are in force at a beat,
    /// and the chord track as progression steps for the compose-assist generators. Built by <see cref="MusicCue"/> from the song's
    /// Key, KeyChanges and Chords.
    /// </summary>
    public sealed class MusicHarmonyTrack
    {
        public const int MaxEntries = 4096;
        public static readonly MusicHarmonyTrack Empty = new MusicHarmonyTrack(1, null, null, null);
        readonly double[] keyBeats, chordBeats;
        readonly MusicKey[] keys;
        readonly bool[] keyKnown;
        readonly MusicChord[] chords;
        readonly string[] symbols;
        public double LoopBeats { get; }
        public int KeyChangeCount => keyBeats.Length;
        public int ChordCount => chordBeats.Length;

        public MusicHarmonyTrack(double loopBeats, string initialKey, MusicKeyChange[] keyChanges, MusicChordMarker[] chordTrack)
        {
            LoopBeats = loopBeats;
            var changes = (keyChanges ?? Array.Empty<MusicKeyChange>()).OrderBy(k => k.Beat).ToArray();
            var marks = (chordTrack ?? Array.Empty<MusicChordMarker>()).OrderBy(c => c.Beat).ToArray();
            if (changes.Length > MaxEntries || marks.Length > MaxEntries) throw new ArgumentException($"At most {MaxEntries} key changes and chords.");
            keyBeats = new double[changes.Length + 1]; keys = new MusicKey[changes.Length + 1]; keyKnown = new bool[changes.Length + 1];
            keyKnown[0] = MusicKey.TryParse(initialKey ?? "", out keys[0]);
            for (int i = 0; i < changes.Length; i++)
            {
                var c = changes[i];
                if (!MusicTrack.Finite(c.Beat) || c.Beat < 0 || c.Beat >= loopBeats - 1e-9) throw new ArgumentException($"Key change {c.Key} must be inside the song (0 ≤ beat < {loopBeats}).");
                if (!MusicKey.TryParse(c.Key ?? "", out keys[i + 1])) throw new ArgumentException($"Unknown key \"{c.Key}\" at beat {c.Beat}.");
                keyBeats[i + 1] = c.Beat; keyKnown[i + 1] = true;
            }
            keyBeats[0] = double.NegativeInfinity;
            chordBeats = new double[marks.Length]; chords = new MusicChord[marks.Length]; symbols = new string[marks.Length];
            for (int i = 0; i < marks.Length; i++)
            {
                var m = marks[i];
                if (!MusicTrack.Finite(m.Beat) || m.Beat < 0 || m.Beat >= loopBeats - 1e-9) throw new ArgumentException($"Chord {m.Chord} must be inside the song (0 ≤ beat < {loopBeats}).");
                if (i > 0 && m.Beat - marks[i - 1].Beat < 1e-6) throw new ArgumentException($"Two chords at beat {m.Beat}.");
                var key = KeyIndex(m.Beat) is int k && keyKnown[k] ? keys[k] : new MusicKey(0);
                chords[i] = Parse(m.Chord, key, m.Beat, out bool named);
                chordBeats[i] = m.Beat; symbols[i] = named ? m.Chord.Trim() : Spell(chords[i], key); // a typed name keeps its spelling
            }
            // A key change at beat 0 replaces the initial key.
        }

        static MusicChord Parse(string symbol, MusicKey key, double beat, out bool named)
        {
            named = false;
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException($"Empty chord at beat {beat}.");
            if (MusicChord.TryParseName(symbol.Trim(), out var chord)) { named = true; return chord; }
            try
            {
                var steps = MusicProgression.Parse(symbol.Trim(), key, 1);
                if (steps.Length != 1) throw new FormatException("one chord per entry");
                return steps[0].Chord;
            }
            catch (FormatException e) { throw new ArgumentException($"Chord \"{symbol}\" at beat {beat}: {e.Message}"); }
        }

        static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }, Flats = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

        /// <summary>A chord name spelled for the key (G#7 in C# minor, Db in F minor); keys without sharps or flats use the default names.</summary>
        public static string Spell(MusicChord chord, MusicKey key)
        {
            string plain = chord.ToString(), root = MusicKey.PitchName(chord.Root), bass = chord.Bass != chord.Root ? "/" + MusicKey.PitchName(chord.Bass) : "";
            string suffix = plain.Substring(root.Length, plain.Length - root.Length - bass.Length);
            int[] toMajor = { 0, 3, 10, 8, 7, 5, 1, 3, 3, 0, 3, 3 }; // MusicMode order: the relative major's tonic offset
            int sharps = new[] { 0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5 }[(key.Tonic + toMajor[(int)key.Mode]) % 12];
            if (sharps == 0) return plain;
            var names = sharps > 0 ? Sharps : Flats;
            return names[chord.Root] + suffix + (bass.Length > 0 ? "/" + names[chord.Bass] : "");
        }

        int? KeyIndex(double beat)
        {
            int found = 0;
            for (int i = 1; i < keyBeats.Length; i++) if (keyBeats[i] <= beat + 1e-9) found = i; else break;
            return found;
        }

        /// <summary>The key in force at a beat, or null when the song has no key there.</summary>
        public MusicKey? KeyAt(double beat) { int i = KeyIndex(beat).Value; return keyKnown[i] ? keys[i] : (MusicKey?)null; }
        public string KeyNameAt(double beat) => KeyAt(beat)?.ToString() ?? "";

        /// <summary>Index of the chord in force at a beat, or -1 before the first chord (or without a chord track).</summary>
        public int ChordIndexAt(double beat)
        {
            int lo = 0, hi = chordBeats.Length - 1, found = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (chordBeats[mid] <= beat + 1e-9) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found;
        }
        public MusicChord? ChordAt(double beat) { int i = ChordIndexAt(beat); return i < 0 ? (MusicChord?)null : chords[i]; }
        public string ChordNameAt(double beat) { int i = ChordIndexAt(beat); return i < 0 ? "" : symbols[i]; }
        public MusicChord Chord(int index) => chords[index];
        public double ChordStart(int index) => chordBeats[index];
        public double ChordEnd(int index) => index + 1 < chordBeats.Length ? chordBeats[index + 1] : LoopBeats;

        /// <summary>The chord track as progression steps (each lasting until the next chord) for MusicComposeAssist.Chords / Bass / Arpeggio.</summary>
        public MusicProgressionStep[] Steps(double from = 0, double to = double.MaxValue)
        {
            var result = new List<MusicProgressionStep>();
            for (int i = 0; i < chordBeats.Length; i++)
            {
                double start = Math.Max(from, chordBeats[i]), end = Math.Min(to, ChordEnd(i));
                if (end > start + 1e-9) result.Add(new MusicProgressionStep(chords[i], start, end - start));
            }
            return result.ToArray();
        }
    }

    /// <summary>Song-level groove: swing delays every second grid step by <c>swing × grid</c> (0.33 ≈ triplet feel at 0.5).</summary>
    public static class MusicGroove
    {
        public const double MaxSwing = .75;

        /// <summary>Notes with swing applied (a copy; notes off the grid stay where they are).</summary>
        public static MusicNote[] Apply(MusicNote[] notes, double swing, double gridBeats)
        {
            if (notes == null || swing == 0) return notes;
            if (!MusicTrack.Finite(swing) || swing < 0 || swing > MaxSwing || !MusicTrack.Finite(gridBeats) || gridBeats < 1 / 32.0 || gridBeats > 4)
                throw new ArgumentException($"Swing 0..{MaxSwing} on a grid of 1/32..4 beats.");
            var result = (MusicNote[])notes.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                double steps = result[i].Beat / gridBeats, nearest = Math.Round(steps);
                if (Math.Abs(steps - nearest) > 1e-4 || ((long)nearest & 1) == 0) continue;
                var n = result[i]; double shift = swing * gridBeats;
                n.Beat += shift; n.Duration = Math.Max(1e-3, n.Duration - Math.Min(shift, n.Duration * .5));
                result[i] = n;
            }
            return result;
        }
    }
}
