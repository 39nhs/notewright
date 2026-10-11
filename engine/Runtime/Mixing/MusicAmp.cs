using System;

namespace Graze.Presentation.Audio
{
    /// <summary>Amp voicing (playback V2+): pre-emphasis and clipping stages before the tone stack.</summary>
    public enum MusicAmpModel
    {
        /// <summary>One gentle stage: stays clean at low gain, rounds peaks when pushed.</summary>
        Clean,
        /// <summary>One asymmetric tube-style stage with a mid push: classic rock crunch.</summary>
        Crunch,
        /// <summary>Two cascaded asymmetric stages with a tight low end and a mid push: modern high gain.</summary>
        HighGain,
        /// <summary>Soft clipping with the lows (below about 150 Hz) kept clean: bass amp.</summary>
        Bass,
    }

    /// <summary>Speaker cabinet and close microphone model (playback V2+), applied after the amp.</summary>
    public enum MusicCabinet
    {
        Off,
        /// <summary>Closed-back 4x12, close mic: tight lows, strong upper-mid bite, steep top roll-off.</summary>
        Closed4x12,
        /// <summary>Open-back 2x12: lighter lows, wider mids, airier top.</summary>
        Open2x12,
        /// <summary>1x12 combo: boxy mids, smaller low end.</summary>
        Combo1x12,
        /// <summary>Bass 4x10: deep lows, upper-mid grind, early top roll-off.</summary>
        Bass4x10,
    }

    /// <summary>
    /// Amp and cabinet stage (playback V2+), after drive and before EQ / chorus / reverb on the master or a track bus.
    /// Amp: tight high-pass → model pre-emphasis → gain → clipping at 4x oversampling (two cascaded 2x polyphase IIR half-band
    /// stages, flat to 20 kHz at 48 kHz, about 100 dB image/alias rejection, minimum phase) → tone stack (bass shelf, mid peak,
    /// treble shelf, presence peak) → level. An amp bus that has been silent for 50 ms skips its work until sound returns.
    /// Cabinet: a fixed filter model of speaker and close microphone per <see cref="MusicCabinet"/>, blended by CabinetMix.
    /// Buffers are allocated in the constructor (caller thread); Process allocates nothing. Coefficients are redesigned
    /// only when the snapshot changes.
    /// </summary>
    internal sealed class MusicAmp
    {
        // Half-band all-pass coefficients (Valenzuela–Constantinides design, as in hiir): stage 1 (base → 2x) 8 coefficients with
        // transition 0.04 (−99 dB stopband), stage 2 (2x → 4x) 4 coefficients with transition 0.1 (the signal is already band-limited).
        static readonly double[] Stage1 = { .04063346, .15050513, .30075706, .4607745, .60952431, .73850384, .84922381, .94974278 };
        static readonly double[] Stage2 = { .07986643, .28382934, .54532365, .83441189 };
        const int Factor = 4, IdleSamples = 2400;

        readonly int rate;
        // Biquads: per channel [tight, tight2, emphasis, bass, mid, treble, presence] and the cabinet chain (up to 10).
        const int AmpBands = 7, CabBands = 10;
        readonly double[] ampCoef = new double[AmpBands * 5], cabCoef = new double[CabBands * 5];
        readonly bool[] ampOn = new bool[AmpBands];
        int cabCount;
        readonly double[] ampState = new double[2 * AmpBands * 4], cabState = new double[2 * CabBands * 4];
        readonly double[] cleanLow = new double[2 * 4]; // Bass model: clean low band (2nd-order low-pass) per channel
        readonly double[] cleanCoef = new double[5];
        // Oversampling all-pass states per channel: [x, y] per coefficient for up1, up2, down2, down1.
        readonly double[][] up1 = { new double[16], new double[16] }, up2 = { new double[8], new double[8] };
        readonly double[][] down1 = { new double[16], new double[16] }, down2 = { new double[8], new double[8] };
        readonly double[] high = new double[Factor];
        int idle;
        // Inter-stage DC blocker and fizz low-pass at the high rate (HighGain), per channel.
        readonly double[] dcIn = new double[2], dcOut = new double[2], fizz = new double[2];
        double dcCoef, fizzCoef;
        MusicMixerSnapshot designedFor;

        public MusicAmp(int sampleRate) { rate = sampleRate; }

        public void Process(ref double left, ref double right, MusicMixerSnapshot s)
        {
            if (!ReferenceEquals(designedFor, s)) Design(s);
            // Silent input for 50 ms: the filters have rung out, so skip the stage (no CPU for idle guitar tracks).
            if (left == 0 && right == 0) { if (idle >= IdleSamples) return; idle++; }
            else idle = 0;
            if (s.Amp) { left = AmpChannel(left, 0, s); right = AmpChannel(right, 1, s); }
            if (s.Cabinet != MusicCabinet.Off)
            {
                double l = Run(cabCoef, cabState, cabCount, CabBands, left, 0), r = Run(cabCoef, cabState, cabCount, CabBands, right, 1);
                left += (l - left) * s.CabinetMix; right += (r - right) * s.CabinetMix;
            }
        }

        double AmpChannel(double x, int ch, MusicMixerSnapshot s)
        {
            double input = x;
            for (int b = 0; b < 3; b++) if (ampOn[b]) x = Band(ampCoef, ampState, AmpBands, b, x, ch);
            x *= s.AmpGain;
            // 4x oversampled clipping: 2x → 2x interpolation, the shaper on each high-rate sample, then 2x → 2x decimation.
            Up(Stage1, up1[ch], x, out double u0, out double u1);
            Up(Stage2, up2[ch], u0, out high[0], out high[1]);
            Up(Stage2, up2[ch], u1, out high[2], out high[3]);
            for (int i = 0; i < Factor; i++) high[i] = Shape(high[i], ch, s.AmpModel);
            double y = Down(Stage1, down1[ch], Down(Stage2, down2[ch], high[0], high[1]), Down(Stage2, down2[ch], high[2], high[3]));
            if (s.AmpModel == MusicAmpModel.Bass) y = y * .75 + Band(cleanCoef, cleanLow, 1, 0, input, ch) * Math.Min(4, s.AmpGain) * .5;
            for (int b = 3; b < AmpBands; b++) if (ampOn[b]) y = Band(ampCoef, ampState, AmpBands, b, y, ch);
            return y * s.AmpLevel;
        }

        // Asymmetric bias makes even harmonics like a tube stage; the offset keeps silence at zero.
        double Shape(double v, int ch, MusicAmpModel model)
        {
            switch (model)
            {
                case MusicAmpModel.Clean: return v / (1 + Math.Abs(v));
                case MusicAmpModel.Crunch: return (Math.Tanh(v + .25) - Math.Tanh(.25)) * 1.1;
                case MusicAmpModel.Bass: return v / Math.Sqrt(1 + v * v);
                default:
                {
                    double a = Math.Tanh(1.6 * v + .3) - Math.Tanh(.3);
                    double hp = dcCoef * (dcOut[ch] + a - dcIn[ch]); dcIn[ch] = a; dcOut[ch] = hp; // inter-stage coupling
                    fizz[ch] += fizzCoef * (hp - fizz[ch]);
                    return Math.Tanh(3 * fizz[ch]) * .9;
                }
            }
        }

        void Design(MusicMixerSnapshot s)
        {
            designedFor = s;
            Array.Clear(ampOn, 0, AmpBands);
            if (s.AmpTightHz > 0) { HighPass(ampCoef, 0, s.AmpTightHz, .7); ampOn[0] = true; if (s.AmpModel == MusicAmpModel.HighGain) { HighPass(ampCoef, 1, s.AmpTightHz * .8, .55); ampOn[1] = true; } }
            switch (s.AmpModel)
            {
                case MusicAmpModel.Crunch: Peak(ampCoef, 2, 900, 3, .8); ampOn[2] = true; break;
                case MusicAmpModel.HighGain: Peak(ampCoef, 2, 750, 6, .7); ampOn[2] = true; break;
                case MusicAmpModel.Bass: Peak(ampCoef, 2, 500, 2, .7); ampOn[2] = true; break;
            }
            Shelf(ampCoef, 3, 100, s.AmpBassDb, false); ampOn[3] = s.AmpBassDb != 0;
            Peak(ampCoef, 4, 650, s.AmpMidDb, .7); ampOn[4] = s.AmpMidDb != 0;
            Shelf(ampCoef, 5, 3200, s.AmpTrebleDb, true); ampOn[5] = s.AmpTrebleDb != 0;
            Peak(ampCoef, 6, 4500, s.AmpPresenceDb, .8); ampOn[6] = s.AmpPresenceDb != 0;
            LowPass(cleanCoef, 0, 150, .7);
            dcCoef = Math.Exp(-2 * Math.PI * 30 / (rate * Factor));
            fizzCoef = 1 - Math.Exp(-2 * Math.PI * 9000 / (rate * Factor));
            cabCount = 0;
            switch (s.Cabinet)
            {
                case MusicCabinet.Closed4x12:
                    CabHigh(75, .9); CabHigh(60, .6); CabPeak(110, 3, 1.2); CabPeak(400, -3, 1); CabPeak(1800, 2.5, 1.4);
                    CabPeak(2800, 4, 1.6); CabPeak(4200, -4, 2); CabLow(5200, .7); CabLow(5600, .55); break;
                case MusicCabinet.Open2x12:
                    CabHigh(90, .7); CabPeak(160, 1, 1); CabPeak(700, 1, .8); CabPeak(2200, 3, 1.2); CabPeak(3500, -2, 2);
                    CabLow(6000, .7); CabLow(6500, .55); break;
                case MusicCabinet.Combo1x12:
                    CabHigh(100, .7); CabPeak(200, 1.5, 1); CabPeak(1000, 2, .9); CabPeak(2500, 2, 1.3); CabLow(5000, .7); CabLow(5400, .55); break;
                case MusicCabinet.Bass4x10:
                    CabHigh(45, .7); CabPeak(80, 3, 1); CabPeak(600, -2, .8); CabPeak(2000, 3, 1.5); CabLow(4500, .7); CabLow(4800, .55); break;
            }
        }

        void CabHigh(double hz, double q) => HighPass(cabCoef, cabCount++, hz, q);
        void CabLow(double hz, double q) => LowPass(cabCoef, cabCount++, hz, q);
        void CabPeak(double hz, double db, double q) => Peak(cabCoef, cabCount++, hz, db, q);

        double Run(double[] coef, double[] state, int count, int stride, double x, int ch)
        {
            for (int b = 0; b < count; b++) x = Band(coef, state, stride, b, x, ch);
            return x;
        }

        static double Band(double[] coef, double[] state, int stride, int band, double x, int ch)
        {
            int c = band * 5, st = (ch * stride + band) * 4;
            double y = coef[c] * x + coef[c + 1] * state[st] + coef[c + 2] * state[st + 1] - coef[c + 3] * state[st + 2] - coef[c + 4] * state[st + 3];
            state[st + 1] = state[st]; state[st] = x; state[st + 3] = state[st + 2];
            state[st + 2] = Math.Abs(y) < 1e-25 ? 0 : y; // flush denormals
            return y;
        }

        // ---- RBJ cookbook biquads ----
        void HighPass(double[] coef, int band, double hz, double q)
        {
            double w = W(hz), c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            Store(coef, band, (1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        void LowPass(double[] coef, int band, double hz, double q)
        {
            double w = W(hz), c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            Store(coef, band, (1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        void Peak(double[] coef, int band, double hz, double db, double q)
        {
            double a = Math.Pow(10, db / 40), w = W(hz), alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            Store(coef, band, 1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
        }

        void Shelf(double[] coef, int band, double hz, double db, bool high)
        {
            double a = Math.Pow(10, db / 40), w = W(hz), c = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), root = 2 * Math.Sqrt(a) * alpha, sign = high ? -1 : 1;
            double b0 = a * ((a + 1) - sign * (a - 1) * c + root), b1 = sign * 2 * a * ((a - 1) - sign * (a + 1) * c), b2 = a * ((a + 1) - sign * (a - 1) * c - root);
            double a0 = (a + 1) + sign * (a - 1) * c + root, a1 = -sign * 2 * ((a - 1) + sign * (a + 1) * c), a2 = (a + 1) + sign * (a - 1) * c - root;
            Store(coef, band, b0, b1, b2, a0, a1, a2);
        }

        double W(double hz) => 2 * Math.PI * Math.Min(hz, rate * .45) / rate;

        static void Store(double[] coef, int band, double b0, double b1, double b2, double a0, double a1, double a2)
        { int at = band * 5; coef[at] = b0 / a0; coef[at + 1] = b1 / a0; coef[at + 2] = b2 / a0; coef[at + 3] = a1 / a0; coef[at + 4] = a2 / a0; }

        // Polyphase half-band all-pass pair: even coefficients form one path, odd the other; first-order sections at the low rate
        // (y = (x - y1) * a + x1). state = [x0, y0, x1, y1, ...].
        static void Up(double[] coef, double[] state, double input, out double first, out double second)
        {
            double even = input, odd = input;
            for (int i = 0; i < coef.Length; i += 2)
            {
                int s0 = i * 2, s1 = s0 + 2;
                double t0 = (even - state[s0 + 1]) * coef[i] + state[s0], t1 = (odd - state[s1 + 1]) * coef[i + 1] + state[s1];
                state[s0] = even; state[s0 + 1] = t0; state[s1] = odd; state[s1 + 1] = t1;
                even = t0; odd = t1;
            }
            first = even; second = odd;
        }

        static double Down(double[] coef, double[] state, double earlier, double later)
        {
            double p0 = later, p1 = earlier;
            for (int i = 0; i < coef.Length; i += 2)
            {
                int s0 = i * 2, s1 = s0 + 2;
                double t0 = (p0 - state[s0 + 1]) * coef[i] + state[s0], t1 = (p1 - state[s1 + 1]) * coef[i + 1] + state[s1];
                state[s0] = p0; state[s0 + 1] = Math.Abs(t0) < 1e-25 ? 0 : t0; state[s1] = p1; state[s1 + 1] = Math.Abs(t1) < 1e-25 ? 0 : t1;
                p0 = t0; p1 = t1;
            }
            return .5 * (p0 + p1);
        }
    }
}
