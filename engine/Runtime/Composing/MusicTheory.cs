using System;
using System.Collections.Generic;
using System.Globalization;

namespace Graze.Presentation.Audio
{
    /// <summary>Scales a key can use. Pitch-class intervals from the tonic.</summary>
    public enum MusicMode { Major, Minor, Dorian, Phrygian, Lydian, Mixolydian, Locrian, HarmonicMinor, MelodicMinor, MajorPentatonic, MinorPentatonic, Blues }

    /// <summary>A tonic pitch class (0 = C … 11 = B) and a mode. Parses "A minor", "F# dorian", "Bb", "C#m". Authoring data only — never changes playback.</summary>
    public readonly struct MusicKey : IEquatable<MusicKey>
    {
        public readonly int Tonic;
        public readonly MusicMode Mode;
        static readonly int[][] Scales = {
            new[] { 0, 2, 4, 5, 7, 9, 11 }, new[] { 0, 2, 3, 5, 7, 8, 10 }, new[] { 0, 2, 3, 5, 7, 9, 10 }, new[] { 0, 1, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 6, 7, 9, 11 }, new[] { 0, 2, 4, 5, 7, 9, 10 }, new[] { 0, 1, 3, 5, 6, 8, 10 }, new[] { 0, 2, 3, 5, 7, 8, 11 },
            new[] { 0, 2, 3, 5, 7, 9, 11 }, new[] { 0, 2, 4, 7, 9 }, new[] { 0, 3, 5, 7, 10 }, new[] { 0, 3, 5, 6, 7, 10 } };
        static readonly string[] Names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

        public MusicKey(int tonic, MusicMode mode = MusicMode.Major)
        {
            if (!Enum.IsDefined(typeof(MusicMode), mode)) throw new ArgumentException("Unknown mode.");
            Tonic = ((tonic % 12) + 12) % 12; Mode = mode;
        }

        /// <summary>Scale degrees as semitones above the tonic (5–7 entries).</summary>
        public IReadOnlyList<int> Intervals => Scales[(int)Mode];
        public bool Contains(int midiKey) => Array.IndexOf(Scales[(int)Mode], (((midiKey - Tonic) % 12) + 12) % 12) >= 0;

        /// <summary>The nearest scale note (ties go down unless <paramref name="preferUp"/>).</summary>
        public int Snap(int midiKey, bool preferUp = false)
        {
            for (int d = 0; d < 12; d++)
            {
                int first = preferUp ? midiKey + d : midiKey - d, second = preferUp ? midiKey - d : midiKey + d;
                if (Contains(first)) return first;
                if (Contains(second)) return second;
            }
            return midiKey;
        }

        /// <summary>MIDI key of a scale degree (0-based, may be negative or beyond the scale; wraps octaves) above <paramref name="tonicMidi"/>.</summary>
        public int Degree(int degree, int tonicMidi)
        {
            var s = Scales[(int)Mode]; int n = s.Length;
            int octave = (int)Math.Floor(degree / (double)n), index = degree - octave * n;
            return tonicMidi + octave * 12 + s[index];
        }

        public static string PitchName(int pitchClass) => Names[((pitchClass % 12) + 12) % 12];

        public override string ToString() => PitchName(Tonic) + " " + Mode.ToString().ToLowerInvariant();
        public bool Equals(MusicKey other) => Tonic == other.Tonic && Mode == other.Mode;
        public override bool Equals(object obj) => obj is MusicKey k && Equals(k);
        public override int GetHashCode() => Tonic * 31 + (int)Mode;

        /// <summary>Parses a pitch name (C, C#, Db, …) followed by an optional mode ("minor", "m", "dorian", "harmonic minor", "blues", …). Default major.</summary>
        public static MusicKey Parse(string text)
        {
            if (!TryParse(text, out var key)) throw new FormatException("Unknown key: " + text);
            return key;
        }

        public static bool TryParse(string text, out MusicKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            int length = PitchLength(s);
            if (length == 0) return false;
            int tonic = PitchClass(s.Substring(0, length));
            string mode = s.Substring(length).Trim().ToLowerInvariant().Replace("-", " ").Replace("_", " ");
            MusicMode m;
            switch (mode)
            {
                case "": case "major": case "maj": case "ionian": m = MusicMode.Major; break;
                case "m": case "min": case "minor": case "aeolian": m = MusicMode.Minor; break;
                case "dorian": m = MusicMode.Dorian; break;
                case "phrygian": m = MusicMode.Phrygian; break;
                case "lydian": m = MusicMode.Lydian; break;
                case "mixolydian": m = MusicMode.Mixolydian; break;
                case "locrian": m = MusicMode.Locrian; break;
                case "harmonic minor": case "harmonicminor": m = MusicMode.HarmonicMinor; break;
                case "melodic minor": case "melodicminor": m = MusicMode.MelodicMinor; break;
                case "major pentatonic": case "pentatonic": case "majorpentatonic": m = MusicMode.MajorPentatonic; break;
                case "minor pentatonic": case "minorpentatonic": m = MusicMode.MinorPentatonic; break;
                case "blues": m = MusicMode.Blues; break;
                default: return false;
            }
            key = new MusicKey(tonic, m);
            return true;
        }

        internal static int PitchLength(string s)
        {
            if (s.Length == 0 || "ABCDEFGabcdefg".IndexOf(s[0]) < 0) return 0;
            return s.Length > 1 && (s[1] == '#' || s[1] == 'b' || s[1] == '♯' || s[1] == '♭') ? 2 : 1;
        }

        internal static int PitchClass(string name)
        {
            int[] natural = { 9, 11, 0, 2, 4, 5, 7 }; // A B C D E F G
            int pc = natural[char.ToUpperInvariant(name[0]) - 'A'];
            if (name.Length > 1) pc += name[1] == '#' || name[1] == '♯' ? 1 : -1;
            return ((pc % 12) + 12) % 12;
        }
    }

    public enum MusicChordQuality { Major, Minor, Diminished, Augmented, Sus2, Sus4, Power, Dominant7, Major7, Minor7, HalfDiminished7, Diminished7, Add9, Minor9, Dominant9 }

    /// <summary>A chord: root pitch class, quality and an optional bass pitch class (slash chord).</summary>
    public readonly struct MusicChord
    {
        public readonly int Root;
        public readonly MusicChordQuality Quality;
        public readonly int Bass;
        static readonly int[][] Shapes = {
            new[] { 0, 4, 7 }, new[] { 0, 3, 7 }, new[] { 0, 3, 6 }, new[] { 0, 4, 8 }, new[] { 0, 2, 7 }, new[] { 0, 5, 7 }, new[] { 0, 7 },
            new[] { 0, 4, 7, 10 }, new[] { 0, 4, 7, 11 }, new[] { 0, 3, 7, 10 }, new[] { 0, 3, 6, 10 }, new[] { 0, 3, 6, 9 },
            new[] { 0, 4, 7, 14 }, new[] { 0, 3, 7, 10, 14 }, new[] { 0, 4, 7, 10, 14 } };
        static readonly string[] Suffix = { "", "m", "dim", "aug", "sus2", "sus4", "5", "7", "maj7", "m7", "m7b5", "dim7", "add9", "m9", "9" };

        public MusicChord(int root, MusicChordQuality quality, int bass = -1)
        {
            if (!Enum.IsDefined(typeof(MusicChordQuality), quality)) throw new ArgumentException("Unknown chord quality.");
            Root = ((root % 12) + 12) % 12; Quality = quality; Bass = bass < 0 ? Root : ((bass % 12) + 12) % 12;
        }

        /// <summary>Chord tones as semitones above the root (may exceed 12 for 9ths).</summary>
        public IReadOnlyList<int> Intervals => Shapes[(int)Quality];
        public override string ToString() => MusicKey.PitchName(Root) + Suffix[(int)Quality] + (Bass != Root ? "/" + MusicKey.PitchName(Bass) : "");

        /// <summary>Chord name: root, optional quality suffix, optional "/bass" — e.g. C, Am, F#m7, Bbmaj7, G7, Dsus4, Edim, C/E, A5.</summary>
        public static bool TryParseName(string token, out MusicChord chord)
        {
            chord = default;
            int length = MusicKey.PitchLength(token);
            if (length == 0 || !char.IsUpper(token[0])) return false;
            int root = MusicKey.PitchClass(token.Substring(0, length)), bass = -1;
            string rest = token.Substring(length);
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                string b = rest.Substring(slash + 1);
                if (MusicKey.PitchLength(b) != b.Length || b.Length == 0) return false;
                bass = MusicKey.PitchClass(b); rest = rest.Substring(0, slash);
            }
            if (!TryQuality(rest, out var quality)) return false;
            chord = new MusicChord(root, quality, bass);
            return true;
        }

        internal static bool TryQuality(string suffix, out MusicChordQuality quality)
        {
            switch (suffix)
            {
                case "": case "maj": case "M": quality = MusicChordQuality.Major; return true;
                case "m": case "min": case "-": quality = MusicChordQuality.Minor; return true;
                case "dim": case "°": case "o": quality = MusicChordQuality.Diminished; return true;
                case "aug": case "+": quality = MusicChordQuality.Augmented; return true;
                case "sus2": quality = MusicChordQuality.Sus2; return true;
                case "sus4": case "sus": quality = MusicChordQuality.Sus4; return true;
                case "5": quality = MusicChordQuality.Power; return true;
                case "7": quality = MusicChordQuality.Dominant7; return true;
                case "maj7": case "M7": case "Δ": case "Δ7": quality = MusicChordQuality.Major7; return true;
                case "m7": case "min7": case "-7": quality = MusicChordQuality.Minor7; return true;
                case "m7b5": case "ø": case "ø7": quality = MusicChordQuality.HalfDiminished7; return true;
                case "dim7": case "°7": case "o7": quality = MusicChordQuality.Diminished7; return true;
                case "add9": quality = MusicChordQuality.Add9; return true;
                case "m9": quality = MusicChordQuality.Minor9; return true;
                case "9": quality = MusicChordQuality.Dominant9; return true;
                default: quality = default; return false;
            }
        }
    }

    /// <summary>One chord of a progression and its length in beats.</summary>
    public readonly struct MusicProgressionStep
    {
        public readonly MusicChord Chord;
        public readonly double Beat, Beats;
        public MusicProgressionStep(MusicChord chord, double beat, double beats) { Chord = chord; Beat = beat; Beats = beats; }
    }

    /// <summary>
    /// Chord progressions from text. Tokens are separated by spaces or "|": Roman numerals in the key (I ii iii IV V vi vii°, bVII, #iv°,
    /// V7, ii7, IVmaj7, Vsus4; case gives major/minor; a numeral without b/# is a degree of the key's own scale, with b/# it is relative to the
    /// tonic's major scale; secondary dominants like V/V are not supported) or chord names (C Am F G7, C/E). "-" extends the previous chord by one slot; a ":n"
    /// suffix gives a chord n slots (e.g. "I:2 V vi:2 IV"). Each slot lasts <c>beatsPerChord</c>.
    /// </summary>
    public static class MusicProgression
    {
        public static MusicProgressionStep[] Parse(string text, MusicKey key, double beatsPerChord = 4, double startBeat = 0)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty progression.");
            if (!MusicTrack.Finite(beatsPerChord) || beatsPerChord <= 0 || beatsPerChord > 64 || !MusicTrack.Finite(startBeat) || startBeat < 0)
                throw new ArgumentOutOfRangeException(nameof(beatsPerChord));
            var steps = new List<MusicProgressionStep>(); double beat = startBeat;
            foreach (string raw in text.Split(new[] { ' ', '|', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw; double slots = 1;
                if (token == "-")
                {
                    if (steps.Count == 0) throw new FormatException("A progression cannot start with \"-\".");
                    var last = steps[steps.Count - 1]; steps[steps.Count - 1] = new MusicProgressionStep(last.Chord, last.Beat, last.Beats + beatsPerChord);
                    beat += beatsPerChord; continue;
                }
                int colon = token.IndexOf(':');
                if (colon > 0)
                {
                    if (!double.TryParse(token.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out slots) || !(slots > 0) || slots > 64)
                        throw new FormatException("Bad chord length: " + raw);
                    token = token.Substring(0, colon);
                }
                if (!MusicChord.TryParseName(token, out var chord) && !TryRoman(token, key, out chord)) throw new FormatException("Unknown chord: " + raw);
                steps.Add(new MusicProgressionStep(chord, beat, slots * beatsPerChord)); beat += slots * beatsPerChord;
            }
            if (steps.Count > 4096) throw new FormatException("Progression is too long.");
            return steps.ToArray();
        }

        static readonly string[] Numerals = { "vii", "iii", "vi", "iv", "ii", "v", "i" };
        static readonly int[] NumeralDegree = { 6, 2, 5, 3, 1, 4, 0 };

        static bool TryRoman(string token, MusicKey key, out MusicChord chord)
        {
            chord = default; int i = 0, shift = 0;
            while (i < token.Length && (token[i] == 'b' || token[i] == '#' || token[i] == '♭' || token[i] == '♯')) { shift += token[i] == '#' || token[i] == '♯' ? 1 : -1; i++; }
            string rest = token.Substring(i);
            for (int n = 0; n < Numerals.Length; n++)
            {
                string numeral = Numerals[n];
                if (rest.Length < numeral.Length || string.Compare(rest, 0, numeral, 0, numeral.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                string written = rest.Substring(0, numeral.Length), suffix = rest.Substring(numeral.Length);
                bool upper = char.IsUpper(written[0]);
                foreach (char c in written) if (char.IsUpper(c) != upper) return false;
                // Without an accidental the numeral is a degree of the key's own scale ("i VI VII" in A minor = Am F G); with one it is
                // relative to the major scale of the tonic, the usual chromatic notation ("i bVI bVII" in A minor = Am F G as well).
                int[] major = { 0, 2, 4, 5, 7, 9, 11 };
                bool diatonic = key.Intervals.Count == 7 && shift == 0;
                int root = key.Tonic + (diatonic ? key.Intervals[NumeralDegree[n]] : major[NumeralDegree[n]] + shift);
                MusicChordQuality quality;
                if (suffix == "°" || suffix == "o" || suffix == "dim") { quality = MusicChordQuality.Diminished; suffix = ""; }
                else if (suffix == "°7" || suffix == "o7") { quality = MusicChordQuality.Diminished7; suffix = ""; }
                else if (suffix == "ø" || suffix == "ø7") { quality = MusicChordQuality.HalfDiminished7; suffix = ""; }
                else if (suffix == "+") { quality = MusicChordQuality.Augmented; suffix = ""; }
                else quality = upper ? MusicChordQuality.Major : MusicChordQuality.Minor;
                switch (suffix)
                {
                    case "": break;
                    case "7": quality = upper ? MusicChordQuality.Dominant7 : MusicChordQuality.Minor7; break;
                    case "maj7": quality = MusicChordQuality.Major7; break;
                    case "9": quality = upper ? MusicChordQuality.Dominant9 : MusicChordQuality.Minor9; break;
                    case "sus2": quality = MusicChordQuality.Sus2; break;
                    case "sus4": case "sus": quality = MusicChordQuality.Sus4; break;
                    case "5": quality = MusicChordQuality.Power; break;
                    case "add9": quality = MusicChordQuality.Add9; break;
                    default: return false;
                }
                chord = new MusicChord(root, quality);
                return true;
            }
            return false;
        }
    }

    /// <summary>Key estimate from notes (Krumhansl–Kessler key profiles, weighted by duration × velocity). Major and minor only.</summary>
    public static class MusicKeyDetector
    {
        // Krumhansl & Kessler (1982) probe-tone ratings, the standard published profiles.
        static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        /// <summary>Best key and its correlation (−1..1). Drum parts should be left out by the caller.</summary>
        public static MusicKey Detect(IEnumerable<MusicNote> notes, out double confidence)
        {
            var weight = new double[12]; bool any = false;
            foreach (var n in notes) { if (n.Velocity <= 0 || n.Duration <= 0) continue; weight[((n.Key % 12) + 12) % 12] += n.Duration * n.Velocity; any = true; }
            if (!any) throw new ArgumentException("No pitched notes to analyse.");
            var best = new MusicKey(0); confidence = double.NegativeInfinity;
            for (int tonic = 0; tonic < 12; tonic++)
                for (int m = 0; m < 2; m++)
                {
                    double r = Correlation(weight, m == 0 ? MajorProfile : MinorProfile, tonic);
                    if (r > confidence + 1e-12) { confidence = r; best = new MusicKey(tonic, m == 0 ? MusicMode.Major : MusicMode.Minor); }
                }
            return best;
        }

        static double Correlation(double[] w, double[] profile, int tonic)
        {
            double mw = 0, mp = 0; for (int i = 0; i < 12; i++) { mw += w[i]; mp += profile[i]; } mw /= 12; mp /= 12;
            double num = 0, dw = 0, dp = 0;
            for (int i = 0; i < 12; i++) { double a = w[(i + tonic) % 12] - mw, b = profile[i] - mp; num += a * b; dw += a * a; dp += b * b; }
            return dw <= 0 || dp <= 0 ? 0 : num / Math.Sqrt(dw * dp);
        }
    }
}
