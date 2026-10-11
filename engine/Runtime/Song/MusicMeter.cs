using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>A time-signature change at a beat (quarter notes from the loop start). A bar of n/d lasts n × 4 / d beats.</summary>
    [Serializable]
    public struct MusicMeterChange
    {
        [Tooltip("Beat where the new meter starts; must be on a bar line of the meter before it (0 replaces the song's Beats Per Bar).")]
        public double Beat;
        [Range(1, 64)] public int Numerator;
        [Tooltip("Note value of one count: 1, 2, 4, 8, 16, 32 or 64 (7/8 = bars of 3.5 beats).")]
        public int Denominator;
        [Tooltip("Optional count grouping, e.g. \"2+2+3\" for 7/8 or \"4+4+3+3\" for 14/16 (must add up to the numerator). Empty = the usual grouping. Composition assistance accents it; the sound does not change.")]
        public string Grouping;
        public MusicMeterChange(double beat, int numerator, int denominator, string grouping = null)
        { Beat = beat; Numerator = numerator; Denominator = denominator; Grouping = grouping; }
    }

    /// <summary>
    /// Bar grid of a cue: the song's Beats Per Bar plus optional meter changes, repeating with the loop (the loop end is always a bar line).
    /// Positions are cue-local beats that may run past the loop (a playing deck's unwrapped beat). Without changes every method
    /// computes exactly the arithmetic the engine used with a fixed Beats Per Bar, so existing transitions are bit-identical.
    /// Immutable; safe on the audio thread (no allocation).
    /// </summary>
    public sealed class MusicMeter
    {
        public const int MaxChanges = 1024;
        readonly double fixedBar, loop;
        readonly double[] starts, lengths, firstBar; // per segment: start beat, bar length, bar number of its first bar inside a loop
        readonly int[] numerators, denominators;
        readonly int[][] groupings; // per segment; null = default
        readonly double barsPerLoop;
        public bool Uniform => starts == null;
        public int ChangeCount { get; }

        public MusicMeter(int beatsPerBar, double loopBeats, MusicMeterChange[] changes = null)
        {
            if (beatsPerBar < 1 || beatsPerBar > 64 || !MusicTrack.Finite(loopBeats) || loopBeats <= 0) throw new ArgumentException("Invalid meter.");
            fixedBar = beatsPerBar; loop = loopBeats;
            if (changes == null || changes.Length == 0) return;
            if (changes.Length > MaxChanges) throw new ArgumentException($"At most {MaxChanges} meter changes.");
            ChangeCount = changes.Length;
            int count = changes[0].Beat == 0 ? changes.Length : changes.Length + 1;
            starts = new double[count]; lengths = new double[count]; firstBar = new double[count]; numerators = new int[count]; denominators = new int[count];
            groupings = new int[count][];
            int s = 0;
            if (changes[0].Beat != 0) { starts[0] = 0; lengths[0] = beatsPerBar; numerators[0] = beatsPerBar; denominators[0] = 4; s = 1; }
            for (int i = 0; i < changes.Length; i++, s++)
            {
                var c = changes[i];
                if (!MusicTrack.Finite(c.Beat) || c.Beat < 0 || c.Beat >= loopBeats) throw new ArgumentException($"Meter change {i + 1}: beat must be 0..{loopBeats} (inside the loop).");
                if (c.Numerator < 1 || c.Numerator > 64 || Array.IndexOf(Denominators, c.Denominator) < 0)
                    throw new ArgumentException($"Meter change {i + 1}: numerator 1–64, denominator 1, 2, 4, 8, 16, 32 or 64.");
                if (s > 0)
                {
                    if (c.Beat <= starts[s - 1]) throw new ArgumentException($"Meter change {i + 1}: changes must be in ascending beat order.");
                    double bars = (c.Beat - starts[s - 1]) / lengths[s - 1];
                    if (Math.Abs(bars - Math.Round(bars)) > 1e-6)
                        throw new ArgumentException($"Meter change {i + 1} at beat {c.Beat:0.###} is not on a bar line of the {numerators[s - 1]}/{denominators[s - 1]} meter before it.");
                }
                starts[s] = c.Beat; lengths[s] = c.Numerator * 4.0 / c.Denominator; numerators[s] = c.Numerator; denominators[s] = c.Denominator;
                if (!string.IsNullOrWhiteSpace(c.Grouping))
                {
                    if (!TryParseGrouping(c.Grouping, c.Numerator, out groupings[s]))
                        throw new ArgumentException($"Meter change {i + 1}: grouping \"{c.Grouping}\" must be counts joined by + that add up to {c.Numerator}.");
                }
            }
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                firstBar[i] = total;
                double end = i + 1 < count ? starts[i + 1] : loop;
                total += Math.Ceiling((end - starts[i]) / lengths[i] - 1e-9); // a bar cut short by the next change or the loop end still counts
            }
            barsPerLoop = total;
        }

        static readonly int[] Denominators = { 1, 2, 4, 8, 16, 32, 64 };

        int Segment(double r)
        {
            int lo = 0, hi = starts.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (starts[mid] <= r + 1e-9) lo = mid; else hi = mid - 1; }
            return lo;
        }

        // Splits an unwrapped beat into whole loops and the position inside the loop.
        void Split(double beat, out double loops, out double r)
        {
            loops = Math.Floor(beat / loop); r = beat - loops * loop;
            if (r >= loop - 1e-9) { loops += 1; r = 0; }
            if (r < 0) r = 0;
        }

        /// <summary>Last bar line at or before the beat.</summary>
        public double BarFloor(double beat)
        {
            if (Uniform) return Math.Floor(beat / fixedBar) * fixedBar;
            Split(beat, out double loops, out double r);
            int i = Segment(r);
            return loops * loop + starts[i] + Math.Floor((r - starts[i]) / lengths[i] + 1e-9) * lengths[i];
        }

        /// <summary>First bar line at or after the beat.</summary>
        public double BarCeil(double beat)
        {
            if (Uniform) return Math.Ceiling(beat / fixedBar) * fixedBar;
            double floor = BarFloor(beat);
            return floor >= beat - 1e-9 ? floor : After(floor);
        }

        /// <summary>The bar line after the bar line <paramref name="line"/>.</summary>
        double After(double line)
        {
            Split(line, out double loops, out double r);
            int i = Segment(r);
            double end = i + 1 < starts.Length ? starts[i + 1] : loop, next = r + lengths[i];
            return loops * loop + (next > end - 1e-9 ? end : next);
        }

        /// <summary>First bar line strictly after the beat (beyond a 1e-8 tolerance), as the engine plans its next bar.</summary>
        public double NextBarLine(double beat)
        {
            if (Uniform) return (Math.Floor((beat + 1e-8) / fixedBar) + 1) * fixedBar;
            return After(BarFloor(beat + 1e-8));
        }

        /// <summary>The bar line <paramref name="bars"/> bars after the bar line <paramref name="line"/>.</summary>
        public double Advance(double line, int bars)
        {
            if (Uniform) return line + bars * fixedBar;
            for (int i = 0; i < bars; i++) line = After(line);
            return line;
        }

        /// <summary>Length in beats of the bar containing the beat.</summary>
        public double BarLengthAt(double beat)
        {
            if (Uniform) return fixedBar;
            double floor = BarFloor(beat + 1e-9);
            return After(floor) - floor;
        }

        /// <summary>Nearest bar line (halfway rounds up).</summary>
        public double Nearest(double beat)
        {
            if (Uniform) return Math.Floor(beat / fixedBar + .5) * fixedBar;
            double floor = BarFloor(beat), next = After(floor);
            return beat - floor < (next - floor) * .5 ? floor : next;
        }

        /// <summary>Whole bars from the loop start to the bar containing the beat (bar 0 = the first bar).</summary>
        public double BarNumber(double beat)
        {
            if (Uniform) return Math.Floor(beat / fixedBar);
            Split(beat, out double loops, out double r);
            int i = Segment(r);
            return loops * barsPerLoop + firstBar[i] + Math.Floor((r - starts[i]) / lengths[i] + 1e-9);
        }

        public bool OnBarLine(double beat) => Uniform ? Math.Abs(beat - Math.Round(beat / fixedBar) * fixedBar) < 1e-9 : Math.Abs(beat - Nearest(beat)) < 1e-9;

        /// <summary>
        /// Count grouping of the bar containing the beat (counts of 1/denominator note, adding up to the numerator) and whether the
        /// song wrote it explicitly. Default: n/4 up to 4 counts one per count; otherwise 3s for n divisible by 3 (6/8, 9/8) or twos /
        /// fours with threes at the end (7/8 = 2+2+3, 14/16 = 4+4+3+3, 5/4 = 2+3). Not for the audio thread (allocates).
        /// </summary>
        public int[] GroupingAt(double beat, out bool authored)
        {
            SignatureAt(beat, out int n, out int d);
            int[] g = null;
            if (!Uniform) { Split(beat, out _, out double r); g = groupings[Segment(r)]; }
            authored = g != null;
            return g != null ? (int[])g.Clone() : DefaultGrouping(n, d);
        }

        public static int[] DefaultGrouping(int numerator, int denominator)
        {
            if (numerator < 1) throw new ArgumentOutOfRangeException(nameof(numerator));
            var groups = new System.Collections.Generic.List<int>();
            if (denominator <= 4 && numerator <= 4) { for (int i = 0; i < numerator; i++) groups.Add(1); return groups.ToArray(); }
            int big = denominator >= 16 ? 4 : 2; // sixteenths group in fours and threes, eighths and quarters in twos and threes
            if (big == 2 && numerator % 3 == 0) { for (int i = 0; i < numerator / 3; i++) groups.Add(3); return groups.ToArray(); }
            for (int threes = 0; threes * 3 <= numerator; threes++)
                if ((numerator - threes * 3) % big == 0)
                {
                    for (int i = 0; i < (numerator - threes * 3) / big; i++) groups.Add(big);
                    for (int i = 0; i < threes; i++) groups.Add(3);
                    return groups.ToArray();
                }
            // Fives in fours/threes: 3 + 2 (and 1 + ... never happens for n >= 2).
            groups.Add(3); for (int left = numerator - 3; left > 0; left -= 2) groups.Add(Math.Min(2, left));
            return groups.ToArray();
        }

        public static bool TryParseGrouping(string text, int numerator, out int[] groups)
        {
            groups = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split('+'); var result = new int[parts.Length]; int sum = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out result[i]) || result[i] < 1 || result[i] > 64) return false;
                sum += result[i];
            }
            if (sum != numerator) return false;
            groups = result; return true;
        }

        /// <summary>Time signature at the beat (numerator over denominator; the song's Beats Per Bar reads as n/4).</summary>
        public void SignatureAt(double beat, out int numerator, out int denominator)
        {
            if (Uniform) { numerator = (int)fixedBar; denominator = 4; return; }
            Split(beat, out _, out double r);
            int i = Segment(r); numerator = numerators[i]; denominator = denominators[i];
        }
    }
}
