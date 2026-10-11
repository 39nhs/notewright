using System;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Tone and space stage (playback V2+): 3-band EQ → stereo chorus → room reverb, after drive and before the soft limiter
    /// (master) or before a track bus joins the master. Each part runs only when enabled in the snapshot; with all three off the
    /// stage is skipped entirely. All buffers are allocated in the constructor (caller thread); Process allocates nothing.
    /// Reverb: Schroeder–Moorer/Freeverb topology (8 damped combs + 4 all-passes per side, right side detuned for width).
    /// </summary>
    internal sealed class MusicSpace
    {
        static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        const int StereoSpread = 23;
        const double ReverbInput = .015, ReverbScale = 3;

        readonly int rate;
        // EQ: 3 biquads per channel, state x1 x2 y1 y2.
        readonly double[] eqState = new double[2 * 3 * 4];
        readonly double[] eqCoef = new double[3 * 5];
        MusicMixerSnapshot eqFor;
        // Chorus
        readonly float[][] chorusLine;
        int chorusCursor;
        double chorusPhase;
        // Reverb
        readonly float[][] combs, allpasses;
        readonly int[] combIndex, allpassIndex;
        readonly double[] combStore;
        readonly float[] preDelay;
        int preCursor;

        public MusicSpace(int sampleRate)
        {
            rate = sampleRate;
            double scale = sampleRate / 44100.0;
            int chorusLength = (int)(sampleRate * .052) + 4;
            chorusLine = new[] { new float[chorusLength], new float[chorusLength] };
            combs = new float[16][]; combStore = new double[16]; combIndex = new int[16];
            for (int i = 0; i < 16; i++) combs[i] = new float[Math.Max(1, (int)((CombTuning[i % 8] + (i >= 8 ? StereoSpread : 0)) * scale))];
            allpasses = new float[8][]; allpassIndex = new int[8];
            for (int i = 0; i < 8; i++) allpasses[i] = new float[Math.Max(1, (int)((AllpassTuning[i % 4] + (i >= 4 ? StereoSpread : 0)) * scale))];
            preDelay = new float[(int)(sampleRate * .2) + 2];
        }

        public void Process(ref double left, ref double right, MusicMixerSnapshot s)
            => Process(ref left, ref right, s, s.ChorusMix, s.ReverbMix);

        /// <summary>The stage with the chorus and reverb mix given (playback V3 automation); the snapshot values give the V2 sound.</summary>
        public void Process(ref double left, ref double right, MusicMixerSnapshot s, double chorusMix, double reverbMix)
        {
            if (s.Eq)
            {
                if (!ReferenceEquals(eqFor, s)) DesignEq(s);
                left = Eq(left, 0); right = Eq(right, 1);
            }
            if (s.Chorus) Chorus(ref left, ref right, s, chorusMix);
            if (s.Reverb) Reverb(ref left, ref right, s, reverbMix);
        }

        // ---- EQ (RBJ cookbook: low shelf S=1, peaking, high shelf S=1) ----
        void DesignEq(MusicMixerSnapshot s)
        {
            eqFor = s;
            Shelf(0, s.EqLowHz, s.EqLowDb, false);
            Peak(5, s.EqMidHz, s.EqMidDb, s.EqMidQ);
            Shelf(10, s.EqHighHz, s.EqHighDb, true);
        }

        void Shelf(int at, double hz, double db, bool high)
        {
            double a = Math.Pow(10, db / 40), w = 2 * Math.PI * Math.Min(hz, rate * .45) / rate, c = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), root = 2 * Math.Sqrt(a) * alpha, sign = high ? -1 : 1;
            double b0 = a * ((a + 1) - sign * (a - 1) * c + root), b1 = sign * 2 * a * ((a - 1) - sign * (a + 1) * c), b2 = a * ((a + 1) - sign * (a - 1) * c - root);
            double a0 = (a + 1) + sign * (a - 1) * c + root, a1 = -sign * 2 * ((a - 1) + sign * (a + 1) * c), a2 = (a + 1) + sign * (a - 1) * c - root;
            Store(at, b0, b1, b2, a0, a1, a2);
        }

        void Peak(int at, double hz, double db, double q)
        {
            double a = Math.Pow(10, db / 40), w = 2 * Math.PI * Math.Min(hz, rate * .45) / rate, alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            Store(at, 1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
        }

        void Store(int at, double b0, double b1, double b2, double a0, double a1, double a2)
        { eqCoef[at] = b0 / a0; eqCoef[at + 1] = b1 / a0; eqCoef[at + 2] = b2 / a0; eqCoef[at + 3] = a1 / a0; eqCoef[at + 4] = a2 / a0; }

        double Eq(double x, int ch)
        {
            for (int band = 0; band < 3; band++)
            {
                int c = band * 5, st = (ch * 3 + band) * 4;
                double y = eqCoef[c] * x + eqCoef[c + 1] * eqState[st] + eqCoef[c + 2] * eqState[st + 1] - eqCoef[c + 3] * eqState[st + 2] - eqCoef[c + 4] * eqState[st + 3];
                eqState[st + 1] = eqState[st]; eqState[st] = x; eqState[st + 3] = eqState[st + 2]; eqState[st + 2] = Flush(y);
                x = y;
            }
            return x;
        }

        // ---- Chorus ----
        void Chorus(ref double left, ref double right, MusicMixerSnapshot s, double mix)
        {
            int n = chorusLine[0].Length;
            chorusLine[0][chorusCursor] = (float)left; chorusLine[1][chorusCursor] = (float)right;
            double centre = s.ChorusDelayMs / 1000 * rate, swing = s.ChorusDepthMs / 1000 * rate;
            double wl = Tap(0, centre + swing * Math.Sin(chorusPhase), n), wr = Tap(1, centre + swing * Math.Sin(chorusPhase + Math.PI / 2), n);
            chorusPhase += 2 * Math.PI * s.ChorusRateHz / rate;
            if (chorusPhase > 2 * Math.PI) chorusPhase -= 2 * Math.PI;
            chorusCursor = (chorusCursor + 1) % n;
            left = left * (1 - mix) + wl * mix;
            right = right * (1 - mix) + wr * mix;
        }

        double Tap(int ch, double delay, int n)
        {
            double position = chorusCursor - Math.Min(Math.Max(1, delay), n - 2);
            if (position < 0) position += n;
            int i = (int)position; double f = position - i;
            return chorusLine[ch][i] * (1 - f) + chorusLine[ch][(i + 1) % n] * f;
        }

        // ---- Reverb ----
        void Reverb(ref double left, ref double right, MusicMixerSnapshot s, double mix)
        {
            double input = (left + right) * ReverbInput;
            if (s.ReverbPreDelayMs > 0)
            {
                int n = preDelay.Length, delay = Math.Min(n - 1, (int)(s.ReverbPreDelayMs / 1000 * rate));
                preDelay[preCursor] = (float)input;
                input = preDelay[(preCursor - delay + n) % n];
                preCursor = (preCursor + 1) % n;
            }
            double feedback = s.ReverbSize * .28 + .7, damp = s.ReverbDamping * .4;
            double outL = 0, outR = 0;
            for (int i = 0; i < 8; i++) { outL += Comb(i, input, feedback, damp); outR += Comb(i + 8, input, feedback, damp); }
            for (int i = 0; i < 4; i++) { outL = Allpass(i, outL); outR = Allpass(i + 4, outR); }
            double wet1 = s.ReverbWidth / 2 + .5, wet2 = (1 - s.ReverbWidth) / 2;
            double l = (outL * wet1 + outR * wet2) * ReverbScale, r = (outR * wet1 + outL * wet2) * ReverbScale;
            left = left * (1 - mix) + l * mix;
            right = right * (1 - mix) + r * mix;
        }

        double Comb(int i, double input, double feedback, double damp)
        {
            var buffer = combs[i]; int at = combIndex[i];
            double output = buffer[at];
            combStore[i] = Flush(output * (1 - damp) + combStore[i] * damp);
            buffer[at] = (float)(input + combStore[i] * feedback);
            combIndex[i] = at + 1 == buffer.Length ? 0 : at + 1;
            return output;
        }

        double Allpass(int i, double input)
        {
            var buffer = allpasses[i]; int at = allpassIndex[i];
            double stored = buffer[at];
            buffer[at] = (float)Flush(input + stored * .5);
            allpassIndex[i] = at + 1 == buffer.Length ? 0 : at + 1;
            return stored - input;
        }

        // Keeps decaying feedback out of the slow denormal range.
        static double Flush(double x) => x > -1e-25 && x < 1e-25 ? 0 : x;
    }
}
