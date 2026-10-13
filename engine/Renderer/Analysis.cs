using System;
using System.Collections.Generic;
using Graze.Presentation.Audio;

namespace GrazePlugin
{
    /// <summary>
    /// What the render sounds like, in numbers Claude can read: per second the level in five frequency bands (low 20-120 Hz, low-mid
    /// 120-500, mid 500-2k, high-mid 2-6k, high 6k+; dB of the mid signal, Hann-windowed 4096-point FFT) and the stereo width (side/mid RMS),
    /// plus the integrated loudness (BS.1770 LUFS) and true peak. It only reads the rendered audio.
    /// </summary>
    sealed class Analysis
    {
        public static readonly string[] BandNames = { "low", "lowMid", "mid", "highMid", "high" };
        static readonly double[] Edges = { 20, 120, 500, 2000, 6000, double.MaxValue };
        const int Size = 4096;
        readonly int rate;
        readonly double[] window = new double[Size], re = new double[Size], im = new double[Size];
        readonly int[] bandOf = new int[Size / 2];
        readonly double norm;
        readonly MusicLevelMeter meter;
        public readonly List<double[]> Bands = new List<double[]>();
        public readonly List<double> Width = new List<double>();

        public Analysis(int rate, double seconds)
        {
            this.rate = rate;
            double sum = 0;
            for (int i = 0; i < Size; i++) { window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / Size); sum += window[i] * window[i]; }
            norm = 2 / (Size * sum);
            for (int k = 0; k < Size / 2; k++)
            {
                double hz = k * (double)rate / Size; bandOf[k] = -1;
                for (int b = 0; b < 5; b++) if (hz >= Edges[b] && hz < Edges[b + 1]) bandOf[k] = b;
            }
            if (rate % 10 == 0) meter = new MusicLevelMeter(rate, 2, (int)Math.Max(1, Math.Min(21600, Math.Ceiling(seconds) + 1)));
        }

        /// <summary>One second (or the last part of one) of interleaved stereo.</summary>
        public void Add(float[] stereo, int frames)
        {
            var power = new double[5]; int blocks = 0;
            for (int at = 0; at < frames; at += Size, blocks++)
            {
                for (int i = 0; i < Size; i++)
                {
                    int f = at + i;
                    re[i] = f < frames ? (stereo[f * 2] + stereo[f * 2 + 1]) * .5 * window[i] : 0; im[i] = 0;
                }
                MusicFft.Transform(re, im);
                for (int k = 1; k < Size / 2; k++) if (bandOf[k] >= 0) power[bandOf[k]] += (re[k] * re[k] + im[k] * im[k]) * norm;
            }
            var db = new double[5];
            for (int b = 0; b < 5; b++) db[b] = Db(power[b] / Math.Max(1, blocks));
            Bands.Add(db);
            double mid = 0, side = 0;
            for (int f = 0; f < frames; f++) { double l = stereo[f * 2], r = stereo[f * 2 + 1]; mid += (l + r) * (l + r) * .25; side += (l - r) * (l - r) * .25; }
            Width.Add(mid > 1e-12 ? Math.Round(Math.Sqrt(side / mid), 2) : 0);
            meter?.ProcessStereo(stereo, 0, frames);
        }

        static double Db(double power) => power > 1e-18 ? Math.Round(10 * Math.Log10(power), 1) : -180;

        public double? Lufs { get { if (meter == null) return null; double v = meter.ReadIntegratedLufs(); return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : Math.Round(v, 2); } }
        public double? TruePeakDb { get { if (meter == null || !meter.TryRead(out var s)) return null; double v = s.TruePeakDb; return double.IsInfinity(v) ? (double?)null : Math.Round(v, 2); } }

        /// <summary>Average level per band over the whole render (power mean, dB).</summary>
        public Dictionary<string, object> Average()
        {
            var result = new Dictionary<string, object>();
            for (int b = 0; b < 5; b++)
            {
                double sum = 0; foreach (var s in Bands) sum += Math.Pow(10, s[b] / 10);
                result[BandNames[b]] = Db(Bands.Count > 0 ? sum / Bands.Count : 0);
            }
            return result;
        }

        public List<object> PerSecond()
        {
            var list = new List<object>();
            foreach (var s in Bands) list.Add(new List<object> { s[0], s[1], s[2], s[3], s[4] });
            return list;
        }
    }
}
