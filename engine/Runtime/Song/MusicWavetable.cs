using System;
using System.Collections.Generic;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// A wavetable for the synth's Wavetable oscillator: single-cycle frames of <see cref="FrameSize"/> samples the oscillator morphs
    /// through (position 0..1). Every frame is kept band-limited per octave (mip levels), so high notes do not alias. Built-in tables:
    /// Basic (sine → triangle → saw → square), Pulse (narrowing pulse), Harmonic (sine → bright), Vocal (A E I O U formants). Immutable.
    /// </summary>
    public sealed class MusicWavetable
    {
        public const int FrameSize = 2048, MaxFrames = 64, Levels = 11; // level l keeps harmonics up to 1024 >> l
        public static readonly string[] BuiltInNames = { "Basic", "Pulse", "Harmonic", "Vocal" };
        readonly float[][][] mips; // [frame][level][sample]
        public string Name { get; }
        public int FrameCount => mips.Length;

        MusicWavetable(string name, double[][] frames)
        {
            Name = name;
            mips = new float[frames.Length][][];
            var re = new double[FrameSize]; var im = new double[FrameSize];
            for (int f = 0; f < frames.Length; f++)
            {
                // Spectrum once, then each level keeps its harmonics (inverse FFT by conjugation); DC removed, peak-normalized table-wide below.
                for (int i = 0; i < FrameSize; i++) { re[i] = frames[f][i]; im[i] = 0; }
                MusicFft.Transform(re, im);
                var spectrumRe = (double[])re.Clone(); var spectrumIm = (double[])im.Clone();
                mips[f] = new float[Levels][];
                for (int level = 0; level < Levels; level++)
                {
                    int keep = 1024 >> level;
                    for (int k = 0; k < FrameSize; k++)
                    {
                        int h = k <= FrameSize / 2 ? k : FrameSize - k;
                        bool pass = h >= 1 && h <= keep && !(k == FrameSize / 2 && keep < FrameSize / 2);
                        re[k] = pass ? spectrumRe[k] : 0; im[k] = pass ? -spectrumIm[k] : 0; // conjugate for the inverse
                    }
                    MusicFft.Transform(re, im);
                    var table = new float[FrameSize];
                    for (int i = 0; i < FrameSize; i++) table[i] = (float)(re[i] / FrameSize);
                    mips[f][level] = table;
                }
            }
            double peak = 0;
            foreach (var frame in mips) foreach (float v in frame[0]) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0) foreach (var frame in mips) foreach (var table in frame) for (int i = 0; i < table.Length; i++) table[i] = (float)(table[i] / peak);
        }

        /// <summary>
        /// The oscillator sample at <paramref name="phase"/> (0..1) and <paramref name="position"/> (0..1 across the frames), band-limited for
        /// a phase step of <paramref name="dt"/> per sample. Allocates nothing.
        /// </summary>
        public double Read(double phase, double position, double dt)
        {
            double harmonics = dt > 0 ? .45 / dt : 1024; int level = 0;
            while (level < Levels - 1 && (1024 >> level) > harmonics) level++;
            double fp = Math.Max(0, Math.Min(1, position)) * (mips.Length - 1); int f0 = (int)fp, f1 = Math.Min(mips.Length - 1, f0 + 1); double ff = fp - f0;
            double x = phase * FrameSize; int i0 = (int)x & (FrameSize - 1), i1 = (i0 + 1) & (FrameSize - 1); double fx = x - Math.Floor(x);
            var a = mips[f0][level]; var b = mips[f1][level];
            double va = a[i0] + (a[i1] - a[i0]) * fx, vb = b[i0] + (b[i1] - b[i0]) * fx;
            return va + (vb - va) * ff;
        }

        static readonly Dictionary<string, MusicWavetable> builtIn = new Dictionary<string, MusicWavetable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A built-in table by name (built once, then shared).</summary>
        public static MusicWavetable BuiltIn(string name)
        {
            lock (builtIn)
            {
                if (builtIn.TryGetValue(name ?? "", out var table)) return table;
                double[][] frames;
                switch ((name ?? "").ToLowerInvariant())
                {
                    case "basic": frames = Additive(7, (f, h) => BasicShape(f / 6.0, h)); break; // frames on sine, triangle (2), saw (4), square (6)
                    case "pulse": frames = Pulse(8); break;
                    case "harmonic": frames = Additive(8, (f, h) => h <= Math.Pow(2, f + 1) ? 1.0 / Math.Sqrt(h) : 0); break;
                    case "vocal": frames = Vocal(); break;
                    default: throw new ArgumentException($"Unknown wavetable \"{name}\" (built-in: {string.Join(", ", BuiltInNames)}).");
                }
                return builtIn[name] = new MusicWavetable(name, frames);
            }
        }

        /// <summary>A table from PCM holding whole frames of <see cref="FrameSize"/> samples back to back (the common single-cycle wavetable format).</summary>
        public static MusicWavetable FromFrames(string name, float[] mono)
        {
            if (mono == null || mono.Length < FrameSize || mono.Length % FrameSize != 0)
                throw new ArgumentException($"A wavetable needs whole frames of {FrameSize} samples (got {mono?.Length ?? 0}).");
            int count = mono.Length / FrameSize;
            if (count > MaxFrames) throw new ArgumentException($"At most {MaxFrames} wavetable frames.");
            var frames = new double[count][];
            for (int f = 0; f < count; f++) { frames[f] = new double[FrameSize]; for (int i = 0; i < FrameSize; i++) frames[f][i] = mono[f * FrameSize + i]; }
            return new MusicWavetable(name, frames);
        }

        // Sine series Σ a_h sin(2π h n / N) per frame, synthesized by an inverse FFT (X[h] = −i N/2 a, X[N−h] = conj).
        static double[][] Additive(int count, Func<int, int, double> amplitude)
        {
            var frames = new double[count][]; var re = new double[FrameSize]; var im = new double[FrameSize];
            for (int f = 0; f < count; f++)
            {
                Array.Clear(re, 0, FrameSize); Array.Clear(im, 0, FrameSize);
                for (int h = 1; h <= 512; h++) { double a = amplitude(f, h) * FrameSize / 2; im[h] = a; im[FrameSize - h] = -a; } // conjugated for the inverse
                MusicFft.Transform(re, im);
                frames[f] = new double[FrameSize];
                for (int i = 0; i < FrameSize; i++) frames[f][i] = re[i] / FrameSize;
            }
            return frames;
        }

        // Harmonic amplitudes morphing sine (0) → triangle (1/3) → saw (2/3) → square (1).
        static double BasicShape(double t, int h)
        {
            double sine = h == 1 ? 1 : 0, triangle = h % 2 == 1 ? ((h / 2) % 2 == 0 ? 1 : -1) / (double)(h * h) : 0, saw = 1.0 / h, square = h % 2 == 1 ? 1.0 / h : 0;
            if (t < 1 / 3.0) { double u = t * 3; return sine * (1 - u) + triangle * u; }
            if (t < 2 / 3.0) { double u = t * 3 - 1; return triangle * (1 - u) + saw * u; }
            { double u = t * 3 - 2; return saw * (1 - u) + square * u; }
        }

        static double[][] Pulse(int count)
        {
            var frames = new double[count][];
            for (int f = 0; f < count; f++)
            {
                double width = .5 - .45 * f / (count - 1); frames[f] = new double[FrameSize];
                for (int i = 0; i < FrameSize; i++) frames[f][i] = i < width * FrameSize ? 1 : -1;
            }
            return frames;
        }

        // Five vowels as harmonic spectra of a 130 Hz voice shaped by two formants each.
        static double[][] Vocal()
        {
            double[][] formants = { new[] { 800.0, 1200 }, new[] { 400.0, 2000 }, new[] { 280.0, 2300 }, new[] { 450.0, 800 }, new[] { 325.0, 700 } };
            return Additive(5, (f, h) =>
            {
                double hz = 130 * h, a = 0;
                foreach (double formant in formants[f]) { double d = (hz - formant) / (formant * .15); a += Math.Exp(-.5 * d * d); }
                return h > 60 ? 0 : (a + .02) / h * 4;
            });
        }
    }
}
