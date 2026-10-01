using System;

namespace Graze.Presentation.Audio
{
    /// <summary>Waveshaper curves for the playback-V2 drive stages (master and per track). All outputs stay within ±1.</summary>
    public enum MusicDriveMode
    {
        /// <summary>tanh: warm saturation that rounds peaks.</summary>
        Soft,
        /// <summary>Hard clip at ±1: square-ish, the harshest edge.</summary>
        Hard,
        /// <summary>Sine wave folder: loud parts fold back over themselves into bright, metallic harmonics.</summary>
        Fold,
        /// <summary>Asymmetric (biased) tanh: adds even harmonics, a broken-speaker fuzz.</summary>
        Fuzz,
    }

    public static class MusicDrive
    {
        /// <summary>Highest drive gain into a shaper, in dB.</summary>
        public const double MaxDriveDb = 48;
        const double FuzzBias = .35;
        static readonly double FuzzOffset = Math.Tanh(FuzzBias), FuzzScale = 1 / (1 + Math.Tanh(FuzzBias));

        /// <summary>Shapes an already gained sample (u = x · drive gain).</summary>
        public static double Shape(MusicDriveMode mode, double u)
        {
            switch (mode)
            {
                case MusicDriveMode.Hard: return u > 1 ? 1 : u < -1 ? -1 : u;
                case MusicDriveMode.Fold: return Math.Sin(Math.PI / 2 * u);
                case MusicDriveMode.Fuzz: return (Math.Tanh(u + FuzzBias) - FuzzOffset) * FuzzScale;
                default: return Math.Tanh(u);
            }
        }

        public static bool IsDefined(MusicDriveMode mode) => mode >= MusicDriveMode.Soft && mode <= MusicDriveMode.Fuzz;
    }

    /// <summary>
    /// Master drive (playback V2, after the master mixer's output gain, before the soft limiter). Per channel: a 4th-order
    /// high-pass (two Butterworth biquads) at KeepBass splits off the part that is driven; the rest (input minus that band) stays
    /// clean, so low + high is exactly the input while the shaper is linear. The driven band is gained into the shaper, optionally
    /// darkened by Tone, trimmed and blended with the dry signal. State is allocated up front; Process allocates nothing.
    /// </summary>
    internal sealed class MusicMasterDrive
    {
        // per channel, per stage: x1, x2, y1, y2
        readonly double[] hp = new double[2 * 2 * 4], tone = new double[2];
        readonly int rate;
        double coefHz = -1, b0, b1, b2, a1, a2;
        public MusicMasterDrive(int sampleRate) { rate = sampleRate; }

        public void Process(ref double left, ref double right, MusicMixerSnapshot s)
        {
            if (s.DriveKeepBassHz != coefHz) Design(s.DriveKeepBassHz);
            left = Channel(left, s, 0);
            right = Channel(right, s, 1);
        }

        void Design(double hz)
        {
            coefHz = hz;
            if (hz <= 0) return;
            double w = 2 * Math.PI * Math.Min(hz, rate * .45) / rate, alpha = Math.Sin(w) / (2 * Math.Sqrt(.5)), c = Math.Cos(w), a0 = 1 + alpha;
            b0 = (1 + c) / 2 / a0; b1 = -(1 + c) / a0; b2 = b0; a1 = -2 * c / a0; a2 = (1 - alpha) / a0;
        }

        double Biquad(double x, int at)
        {
            double y = b0 * x + b1 * hp[at] + b2 * hp[at + 1] - a1 * hp[at + 2] - a2 * hp[at + 3];
            hp[at + 1] = hp[at]; hp[at] = x; hp[at + 3] = hp[at + 2]; hp[at + 2] = y;
            return y;
        }

        double Channel(double x, MusicMixerSnapshot s, int ch)
        {
            double high = s.DriveKeepBassHz > 0 ? Biquad(Biquad(x, ch * 8), ch * 8 + 4) : x, low = x - high;
            double wet = MusicDrive.Shape(s.DriveMode, high * s.DriveGain);
            if (s.DriveToneHz > 0)
            {
                double a = 1 - Math.Exp(-2 * Math.PI * Math.Min(s.DriveToneHz, rate * .45) / rate);
                tone[ch] += a * (wet - tone[ch]); wet = tone[ch];
            }
            wet = low + wet * s.DriveTrim;
            return x * (1 - s.DriveMix) + wet * s.DriveMix;
        }
    }
}
