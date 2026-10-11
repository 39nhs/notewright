using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Graze.Presentation.Audio
{
    /// <summary>Helpers for building multi-zone samplers from a folder of recordings (song.json generators).</summary>
    public static class MusicSampleZones
    {
        static readonly Regex Note = new Regex(@"(?<![A-Za-z])([A-Ga-g])([#b]?)(-?\d)(?!\d)"), Number = new Regex(@"(?<!\d)(\d{1,3})(?!\d)");
        static readonly int[] PitchClass = { 9, 11, 0, 2, 4, 5, 7 }; // A B C D E F G

        /// <summary>
        /// Root key from a sample file name: a note name with octave (C4 = 60, F#3, Db5, A-1) or, failing that, the last number 0–127
        /// ("piano-60"). Null when the name has neither.
        /// </summary>
        public static int? RootFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var notes = Note.Matches(name);
            if (notes.Count > 0)
            {
                var m = notes[notes.Count - 1];
                int pc = PitchClass[char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A'] + (m.Groups[2].Value == "#" ? 1 : m.Groups[2].Value == "b" ? -1 : 0);
                int key = (int.Parse(m.Groups[3].Value) + 1) * 12 + pc;
                if (key >= 0 && key <= 127) return key;
            }
            var numbers = Number.Matches(name).Cast<Match>().Select(x => int.Parse(x.Value)).Where(v => v <= 127).ToArray();
            return numbers.Length > 0 ? numbers[numbers.Length - 1] : (int?)null;
        }

        /// <summary>
        /// Key ranges for recordings at <paramref name="roots"/> (any order): each zone reaches halfway to its neighbours, the lowest
        /// down to key 0 and the highest up to 127. Returned in the order of <paramref name="roots"/>.
        /// </summary>
        public static (int low, int high)[] Ranges(int[] roots)
        {
            if (roots == null || roots.Length == 0) throw new ArgumentException("Need at least one root key.");
            if (roots.Any(r => r < 0 || r > 127)) throw new ArgumentOutOfRangeException(nameof(roots));
            var order = Enumerable.Range(0, roots.Length).OrderBy(i => roots[i]).ToArray();
            var result = new (int, int)[roots.Length];
            for (int k = 0; k < order.Length; k++)
            {
                int r = roots[order[k]];
                int low = k == 0 ? 0 : (roots[order[k - 1]] + r) / 2 + 1, high = k == order.Length - 1 ? 127 : (r + roots[order[k + 1]]) / 2;
                result[order[k]] = (Math.Min(low, r), Math.Max(high, r));
            }
            return result;
        }
    }
}
