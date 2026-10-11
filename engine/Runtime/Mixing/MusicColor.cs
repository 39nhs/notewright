using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    public enum MusicEqBandType { Peak, LowShelf, HighShelf, LowCut, HighCut, Notch }
    public enum MusicLfoShape { Sine, Triangle, Square }

    /// <summary>One parametric EQ band (playback V3+).</summary>
    [Serializable]
    public struct MusicEqBand
    {
        public bool Enabled;
        public MusicEqBandType Type;
        [Range(20, 20000)] public float Hz;
        [Tooltip("Gain for Peak / shelves (cuts and notch ignore it).")]
        [Range(-24, 24)] public float Db;
        [Tooltip("Width: higher = narrower (cut slopes: 0.707 = Butterworth).")]
        [Range(.1f, 18)] public float Q;
        public MusicEqBand(MusicEqBandType type, float hz, float db = 0, float q = .707f) { Enabled = true; Type = type; Hz = hz; Db = db; Q = q; }
    }

    /// <summary>
    /// Playback V3+ color stage of an effect chain (master, track, bus), after EQ / chorus / reverb: parametric EQ → de-esser → multiband
    /// compressor → phaser → flanger → tremolo or auto-pan → stereo width. Everything off (the default) = the stage is skipped.
    /// </summary>
    [Serializable]
    public sealed class MusicColorSettings
    {
        public const int MaxEqBands = 8;
        [Tooltip("Up to 8 bands: peak, shelves, low/high cut, notch.")]
        public MusicEqBand[] EqBands = Array.Empty<MusicEqBand>();
        [Tooltip("Reduces sibilance: the band above De-esser Hz is turned down while it is louder than the threshold.")]
        public bool DeEsser;
        [Range(2000, 12000)] public float DeEsserHz = 6000;
        [Range(-60, 0)] public float DeEsserThresholdDb = -30;
        [Tooltip("Largest reduction in dB.")]
        [Range(0, 24)] public float DeEsserRangeDb = 8;
        [Tooltip("Three-band compressor (Linkwitz–Riley crossovers); ratio 1 leaves a band untouched.")]
        public bool Multiband;
        [Range(40, 1000)] public float MultibandLowHz = 200;
        [Range(1000, 12000)] public float MultibandHighHz = 3000;
        [Range(-48, 0)] public float MultibandLowThresholdDb = -18, MultibandMidThresholdDb = -18, MultibandHighThresholdDb = -18;
        [Range(1, 20)] public float MultibandLowRatio = 2.5f, MultibandMidRatio = 2.5f, MultibandHighRatio = 2.5f;
        [Range(1, 100)] public float MultibandAttackMs = 10;
        [Range(10, 1000)] public float MultibandReleaseMs = 150;
        [Range(-12, 12)] public float MultibandOutputDb;
        public bool Phaser;
        [Range(.02f, 10)] public float PhaserRateHz = .4f;
        [Tooltip("Sweep: ± two octaves at 1.")]
        [Range(0, 1)] public float PhaserDepth = .7f;
        [Range(100, 4000)] public float PhaserCenterHz = 900;
        [Range(-.95f, .95f)] public float PhaserFeedback = .4f;
        [Tooltip("All-pass stages (even): more = more notches.")]
        [Range(2, 12)] public int PhaserStages = 6;
        [Range(0, 1)] public float PhaserMix = .5f;
        public bool Flanger;
        [Range(.02f, 10)] public float FlangerRateHz = .2f;
        [Range(.1f, 10)] public float FlangerDelayMs = 1.5f;
        [Range(0, 10)] public float FlangerDepthMs = 2;
        [Range(-.95f, .95f)] public float FlangerFeedback = .6f;
        [Range(0, 1)] public float FlangerMix = .5f;
        [Tooltip("Volume (or with Tremolo Pan, left/right) modulation.")]
        public bool Tremolo;
        [Range(.05f, 30)] public float TremoloRateHz = 5;
        [Tooltip("When above 0: one cycle per this many beats (follows the tempo) instead of Rate Hz.")]
        [Range(0, 16)] public float TremoloBeats;
        [Range(0, 1)] public float TremoloDepth = .5f;
        public MusicLfoShape TremoloShape;
        [Tooltip("Auto-pan: move between left and right instead of changing the volume.")]
        public bool TremoloPan;
        [Tooltip("Stereo width (mid/side): 0 = mono, 1 = unchanged, 2 = twice the side signal.")]
        [Range(0, 2)] public float StereoWidth = 1;
        [Tooltip("Rotary speaker (Leslie): treble horn and bass drum spin past two mics (Doppler pitch wobble and level swirl), mono in, stereo out.")]
        public bool Rotary;
        [Tooltip("Rotor speed: 0 = slow (chorale), 1 = fast (tremolo). The rotors take time to speed up and slow down; a RotarySpeed lane switches it.")]
        [Range(0, 1)] public float RotarySpeed;
        [Tooltip("Doppler and level swirl depth.")]
        [Range(0, 1)] public float RotaryDepth = .7f;
        [Range(0, 1)] public float RotaryMix = 1;
        /// <summary>Rotor rates (Hz) at slow and fast speed and how long each rotor takes to change speed (time constant, seconds).</summary>
        public const double RotaryHornSlowHz = .8, RotaryHornFastHz = 6.7, RotaryDrumSlowHz = .67, RotaryDrumFastHz = 5.8, RotaryHornSeconds = .7, RotaryDrumSeconds = 3.5;

        public bool Active => (EqBands != null && Array.Exists(EqBands, b => b.Enabled)) || DeEsser || Multiband || Phaser || Flanger || Tremolo || Rotary || Math.Abs(StereoWidth - 1) > 1e-6;
    }

    /// <summary>Validated, immutable color settings (null on the mixer snapshot when nothing is on).</summary>
    public sealed class MusicColorSnapshot
    {
        public readonly MusicEqBand[] Bands;
        public readonly bool DeEsser, Multiband, Phaser, Flanger, Tremolo, TremoloPan;
        public readonly double DeEsserHz, DeEsserThreshold, DeEsserRangeDb;
        public readonly double LowHz, HighHz, AttackSeconds, ReleaseSeconds, Output;
        public readonly double[] Threshold = new double[3], Ratio = new double[3];
        public readonly double PhaserRateHz, PhaserDepth, PhaserCenterHz, PhaserFeedback, PhaserMix; public readonly int PhaserStages;
        public readonly double FlangerRateHz, FlangerDelayMs, FlangerDepthMs, FlangerFeedback, FlangerMix;
        public readonly double TremoloRateHz, TremoloBeats, TremoloDepth; public readonly MusicLfoShape TremoloShape;
        public readonly double Width;
        public readonly bool Rotary;
        public readonly double RotarySpeed, RotaryDepth, RotaryMix;

        internal MusicColorSnapshot(MusicColorSettings s)
        {
            var bands = s.EqBands ?? Array.Empty<MusicEqBand>();
            if (bands.Length > MusicColorSettings.MaxEqBands) throw new ArgumentException($"At most {MusicColorSettings.MaxEqBands} EQ bands.");
            foreach (var b in bands)
            {
                if (!Enum.IsDefined(typeof(MusicEqBandType), b.Type)) throw new ArgumentException("Invalid EQ band type.");
                if (b.Enabled) { Check(b.Hz, 20, 20000); Check(b.Db, -24, 24); Check(b.Q, .1, 18); }
            }
            Bands = Array.FindAll(bands, b => b.Enabled);
            Check(s.DeEsserHz, 2000, 12000); Check(s.DeEsserThresholdDb, -60, 0); Check(s.DeEsserRangeDb, 0, 24);
            Check(s.MultibandLowHz, 40, 1000); Check(s.MultibandHighHz, 1000, 12000); Check(s.MultibandAttackMs, 1, 100); Check(s.MultibandReleaseMs, 10, 1000); Check(s.MultibandOutputDb, -12, 12);
            foreach (var t in new[] { s.MultibandLowThresholdDb, s.MultibandMidThresholdDb, s.MultibandHighThresholdDb }) Check(t, -48, 0);
            foreach (var r in new[] { s.MultibandLowRatio, s.MultibandMidRatio, s.MultibandHighRatio }) Check(r, 1, 20);
            if (s.Multiband && s.MultibandHighHz <= s.MultibandLowHz * 1.5) throw new ArgumentException("Multiband: the high crossover must be at least 1.5 × the low one.");
            Check(s.PhaserRateHz, .02, 10); Check(s.PhaserDepth, 0, 1); Check(s.PhaserCenterHz, 100, 4000); Check(s.PhaserFeedback, -.95, .95); Check(s.PhaserStages, 2, 12); Check(s.PhaserMix, 0, 1);
            Check(s.FlangerRateHz, .02, 10); Check(s.FlangerDelayMs, .1, 10); Check(s.FlangerDepthMs, 0, 10); Check(s.FlangerFeedback, -.95, .95); Check(s.FlangerMix, 0, 1);
            Check(s.TremoloRateHz, .05, 30); Check(s.TremoloBeats, 0, 16); Check(s.TremoloDepth, 0, 1); Check(s.StereoWidth, 0, 2);
            if (!Enum.IsDefined(typeof(MusicLfoShape), s.TremoloShape)) throw new ArgumentException("Invalid tremolo shape.");
            DeEsser = s.DeEsser; DeEsserHz = s.DeEsserHz; DeEsserThreshold = Math.Pow(10, s.DeEsserThresholdDb / 20); DeEsserRangeDb = s.DeEsserRangeDb;
            Multiband = s.Multiband; LowHz = s.MultibandLowHz; HighHz = s.MultibandHighHz; AttackSeconds = s.MultibandAttackMs / 1000; ReleaseSeconds = s.MultibandReleaseMs / 1000; Output = Math.Pow(10, s.MultibandOutputDb / 20);
            Threshold[0] = Math.Pow(10, s.MultibandLowThresholdDb / 20); Threshold[1] = Math.Pow(10, s.MultibandMidThresholdDb / 20); Threshold[2] = Math.Pow(10, s.MultibandHighThresholdDb / 20);
            Ratio[0] = s.MultibandLowRatio; Ratio[1] = s.MultibandMidRatio; Ratio[2] = s.MultibandHighRatio;
            Phaser = s.Phaser; PhaserRateHz = s.PhaserRateHz; PhaserDepth = s.PhaserDepth; PhaserCenterHz = s.PhaserCenterHz; PhaserFeedback = s.PhaserFeedback; PhaserStages = s.PhaserStages / 2 * 2; PhaserMix = s.PhaserMix;
            Flanger = s.Flanger; FlangerRateHz = s.FlangerRateHz; FlangerDelayMs = s.FlangerDelayMs; FlangerDepthMs = s.FlangerDepthMs; FlangerFeedback = s.FlangerFeedback; FlangerMix = s.FlangerMix;
            Tremolo = s.Tremolo; TremoloPan = s.TremoloPan; TremoloRateHz = s.TremoloRateHz; TremoloBeats = s.TremoloBeats; TremoloDepth = s.TremoloDepth; TremoloShape = s.TremoloShape;
            Width = s.StereoWidth;
            Check(s.RotarySpeed, 0, 1); Check(s.RotaryDepth, 0, 1); Check(s.RotaryMix, 0, 1);
            Rotary = s.Rotary; RotarySpeed = s.RotarySpeed; RotaryDepth = s.RotaryDepth; RotaryMix = s.RotaryMix;
        }

        static void Check(double v, double min, double max) { if (!MusicTrack.Finite(v) || v < min - 1e-6 || v > max + 1e-6) throw new ArgumentException("Invalid color parameter."); }
    }

    /// <summary>Per-chain processor for <see cref="MusicColorSnapshot"/>. Buffers allocated in the constructor; Process allocates nothing.</summary>
    internal sealed class MusicColor
    {
        readonly int rate;
        // Parametric EQ: up to 8 biquads × 2 channels (state x1 x2 y1 y2).
        readonly double[] eqCoef = new double[MusicColorSettings.MaxEqBands * 5], eqState = new double[MusicColorSettings.MaxEqBands * 2 * 4];
        MusicColorSnapshot designedFor;
        // Crossovers: biquad sections [coef 5][state per channel 4]. LR4 = two identical Butterworth sections.
        readonly Biquad deLow = new Biquad(), deHigh = new Biquad(), lowLp = new Biquad(), lowHp = new Biquad(), highLp = new Biquad(), highHp = new Biquad(), lowApLp = new Biquad(), lowApHp = new Biquad();
        double deEnv; readonly double[] bandEnv = new double[3];
        // Phaser
        readonly double[] apState = new double[2 * 12]; readonly double[] phaserLast = new double[2]; double phaserPhase; double apCoefL, apCoefR; int phaserCounter;
        // Flanger
        readonly float[][] flangeLine; int flangeCursor; double flangePhase;
        double tremoloPhase;
        // Rotary: crossover state, horn/drum delay lines, rotor phases and spinning rates (Hz; start at the set speed).
        readonly float[] hornLine, drumLine; int rotaryCursor; double rotLow1, rotLow2, hornPhase, drumPhase, hornHz = -1, drumHz = -1;
        /// <summary>Playback V3 RotarySpeed lane: the speed to use instead of the snapshot's, negative = the snapshot's.</summary>
        public double RotaryOverride = -1;
        const double HornSlowHz = MusicColorSettings.RotaryHornSlowHz, HornFastHz = MusicColorSettings.RotaryHornFastHz, DrumSlowHz = MusicColorSettings.RotaryDrumSlowHz,
            DrumFastHz = MusicColorSettings.RotaryDrumFastHz, HornSeconds = MusicColorSettings.RotaryHornSeconds, DrumSeconds = MusicColorSettings.RotaryDrumSeconds;

        public MusicColor(int sampleRate)
        {
            rate = sampleRate;
            int length = (int)(sampleRate * .025) + 4;
            flangeLine = new[] { new float[length], new float[length] };
            hornLine = new float[(int)(sampleRate * .01) + 4]; drumLine = new float[hornLine.Length];
        }

        public void Process(ref double left, ref double right, MusicColorSnapshot c, double localBeat)
        {
            if (!ReferenceEquals(designedFor, c)) Design(c);
            if (c.Bands.Length > 0) { left = Eq(left, 0, c.Bands.Length); right = Eq(right, 1, c.Bands.Length); }
            if (c.DeEsser) DeEss(ref left, ref right, c);
            if (c.Multiband) Multi(ref left, ref right, c);
            if (c.Phaser) PhaserStep(ref left, ref right, c);
            if (c.Flanger) FlangerStep(ref left, ref right, c);
            if (c.Tremolo) TremoloStep(ref left, ref right, c, localBeat);
            if (c.Rotary) RotaryStep(ref left, ref right, c);
            if (Math.Abs(c.Width - 1) > 1e-9) { double mid = (left + right) * .5, side = (left - right) * .5 * c.Width; left = mid + side; right = mid - side; }
        }

        // Leslie: 800 Hz crossover; each rotor's mics (90° apart) hear a delay swept by the rotation (Doppler) and a level swirl.
        void RotaryStep(ref double left, ref double right, MusicColorSnapshot c)
        {
            double speed = RotaryOverride >= 0 ? RotaryOverride : c.RotarySpeed;
            double hornTarget = HornSlowHz + (HornFastHz - HornSlowHz) * speed, drumTarget = DrumSlowHz + (DrumFastHz - DrumSlowHz) * speed;
            if (hornHz < 0) { hornHz = hornTarget; drumHz = drumTarget; }
            hornHz += (hornTarget - hornHz) * (1 - Math.Exp(-1 / (rate * HornSeconds)));
            drumHz += (drumTarget - drumHz) * (1 - Math.Exp(-1 / (rate * DrumSeconds)));
            hornPhase += hornHz / rate; if (hornPhase >= 1) hornPhase -= 1;
            drumPhase += drumHz / rate; if (drumPhase >= 1) drumPhase -= 1;
            double x = (left + right) * .5, a = 1 - Math.Exp(-2 * Math.PI * 800.0 / rate);
            rotLow1 += a * (x - rotLow1); rotLow2 += a * (rotLow1 - rotLow2);
            double low = rotLow2, high = x - low;
            hornLine[rotaryCursor] = (float)high; drumLine[rotaryCursor] = (float)low;
            double d = c.RotaryDepth, ah = 2 * Math.PI * hornPhase, ad = 2 * Math.PI * drumPhase;
            double hl = Tap(hornLine, .0022 + .0008 * d * Math.Sin(ah)) * (1 - .5 * d * (.5 - .5 * Math.Cos(ah)));
            double hr = Tap(hornLine, .0022 + .0008 * d * Math.Sin(ah + Math.PI / 2)) * (1 - .5 * d * (.5 - .5 * Math.Cos(ah + Math.PI / 2)));
            double dl = Tap(drumLine, .0012 + .0003 * d * Math.Sin(ad)) * (1 - .3 * d * (.5 - .5 * Math.Cos(ad)));
            double dr = Tap(drumLine, .0012 + .0003 * d * Math.Sin(ad + Math.PI / 2)) * (1 - .3 * d * (.5 - .5 * Math.Cos(ad + Math.PI / 2)));
            rotaryCursor = (rotaryCursor + 1) % hornLine.Length;
            left = left * (1 - c.RotaryMix) + (hl + dl) * c.RotaryMix;
            right = right * (1 - c.RotaryMix) + (hr + dr) * c.RotaryMix;
        }

        // Linear-interpolated read `seconds` behind the newest rotary sample.
        double Tap(float[] line, double seconds)
        {
            double back = seconds * rate, position = rotaryCursor - back;
            while (position < 0) position += line.Length;
            int i = (int)position; double f = position - i; int j = (i + 1) % line.Length;
            return line[i] * (1 - f) + line[j] * f;
        }

        void Design(MusicColorSnapshot c)
        {
            designedFor = c;
            for (int i = 0; i < c.Bands.Length; i++)
            {
                var b = c.Bands[i]; double hz = Math.Min(b.Hz, rate * .45);
                double[] k = b.Type == MusicEqBandType.Peak ? Rbj.Peak(rate, hz, b.Db, b.Q) : b.Type == MusicEqBandType.LowShelf ? Rbj.Shelf(rate, hz, b.Db, false) :
                    b.Type == MusicEqBandType.HighShelf ? Rbj.Shelf(rate, hz, b.Db, true) : b.Type == MusicEqBandType.LowCut ? Rbj.HighPass(rate, hz, b.Q) :
                    b.Type == MusicEqBandType.HighCut ? Rbj.LowPass(rate, hz, b.Q) : Rbj.Notch(rate, hz, b.Q);
                Array.Copy(k, 0, eqCoef, i * 5, 5);
            }
            deLow.Set(Rbj.LowPass(rate, Math.Min(c.DeEsserHz, rate * .45), .7071)); deHigh.Set(Rbj.HighPass(rate, Math.Min(c.DeEsserHz, rate * .45), .7071));
            lowLp.Set(Rbj.LowPass(rate, c.LowHz, .7071)); lowHp.Set(Rbj.HighPass(rate, c.LowHz, .7071));
            highLp.Set(Rbj.LowPass(rate, Math.Min(c.HighHz, rate * .45), .7071)); highHp.Set(Rbj.HighPass(rate, Math.Min(c.HighHz, rate * .45), .7071));
            lowApLp.Set(Rbj.LowPass(rate, Math.Min(c.HighHz, rate * .45), .7071)); lowApHp.Set(Rbj.HighPass(rate, Math.Min(c.HighHz, rate * .45), .7071));
        }

        double Eq(double x, int ch, int bands)
        {
            for (int b = 0; b < bands; b++)
            {
                int k = b * 5, st = (b * 2 + ch) * 4;
                double y = eqCoef[k] * x + eqCoef[k + 1] * eqState[st] + eqCoef[k + 2] * eqState[st + 1] - eqCoef[k + 3] * eqState[st + 2] - eqCoef[k + 4] * eqState[st + 3];
                eqState[st + 1] = eqState[st]; eqState[st] = x; eqState[st + 3] = eqState[st + 2]; eqState[st + 2] = Flush(y);
                x = y;
            }
            return x;
        }

        // De-esser: LR4 split; the high band's level above the threshold turns it down (up to RangeDb).
        void DeEss(ref double left, ref double right, MusicColorSnapshot c)
        {
            double lLo = deLow.Lr4(left, 0), rLo = deLow.Lr4(right, 1), lHi = deHigh.Lr4(left, 0), rHi = deHigh.Lr4(right, 1);
            double level = Math.Max(Math.Abs(lHi), Math.Abs(rHi));
            deEnv = level > deEnv ? deEnv + (level - deEnv) * (1 - Math.Exp(-1 / (rate * .001))) : deEnv * Math.Exp(-1 / (rate * .06));
            double over = deEnv > c.DeEsserThreshold ? 20 * Math.Log10(deEnv / c.DeEsserThreshold) : 0;
            double gain = Math.Pow(10, -Math.Min(c.DeEsserRangeDb, over) / 20);
            left = lLo + lHi * gain; right = rLo + rHi * gain;
        }

        // Three bands from two LR4 crossovers (the low band passes the upper crossover's all-pass so the bands sum flat), compressed separately.
        void Multi(ref double left, ref double right, MusicColorSnapshot c)
        {
            double lLow = lowLp.Lr4(left, 0), rLow = lowLp.Lr4(right, 1), lRest = lowHp.Lr4(left, 0), rRest = lowHp.Lr4(right, 1);
            double lMid = highLp.Lr4(lRest, 0), rMid = highLp.Lr4(rRest, 1), lHigh = highHp.Lr4(lRest, 0), rHigh = highHp.Lr4(rRest, 1);
            double lLowAp = lowApLp.Lr4(lLow, 0) + lowApHp.Lr4(lLow, 0), rLowAp = lowApLp.Lr4(rLow, 1) + lowApHp.Lr4(rLow, 1);
            double attack = 1 - Math.Exp(-1 / (rate * c.AttackSeconds)), release = Math.Exp(-1 / (rate * c.ReleaseSeconds));
            double g0 = Band(0, lLowAp, rLowAp, c, attack, release), g1 = Band(1, lMid, rMid, c, attack, release), g2 = Band(2, lHigh, rHigh, c, attack, release);
            left = (lLowAp * g0 + lMid * g1 + lHigh * g2) * c.Output; right = (rLowAp * g0 + rMid * g1 + rHigh * g2) * c.Output;
        }

        double Band(int band, double l, double r, MusicColorSnapshot c, double attack, double release)
        {
            double level = Math.Max(Math.Abs(l), Math.Abs(r));
            bandEnv[band] = level > bandEnv[band] ? bandEnv[band] + (level - bandEnv[band]) * attack : bandEnv[band] * release;
            double env = bandEnv[band], th = c.Threshold[band], ratio = c.Ratio[band];
            return env <= th || ratio <= 1 ? 1 : Math.Pow(env / th, 1 / ratio - 1);
        }

        // Phaser: a chain of first-order all-passes swept by an LFO (right channel a quarter cycle later), mixed with the dry signal.
        void PhaserStep(ref double left, ref double right, MusicColorSnapshot c)
        {
            if (phaserCounter-- <= 0)
            {
                phaserCounter = 15;
                apCoefL = AllpassCoef(c, Math.Sin(2 * Math.PI * phaserPhase)); apCoefR = AllpassCoef(c, Math.Cos(2 * Math.PI * phaserPhase));
            }
            phaserPhase += c.PhaserRateHz / rate; if (phaserPhase >= 1) phaserPhase -= 1;
            left = Phase(left, 0, apCoefL, c); right = Phase(right, 1, apCoefR, c);
        }

        double AllpassCoef(MusicColorSnapshot c, double lfo)
        {
            double hz = Math.Min(rate * .45, c.PhaserCenterHz * Math.Pow(2, 2 * c.PhaserDepth * lfo)), t = Math.Tan(Math.PI * hz / rate);
            return (t - 1) / (t + 1);
        }

        double Phase(double x, int ch, double a, MusicColorSnapshot c)
        {
            double y = x + c.PhaserFeedback * phaserLast[ch];
            for (int s = 0; s < c.PhaserStages; s++)
            {
                int i = ch * 12 + s;
                double o = a * y + apState[i]; apState[i] = Flush(y - a * o); y = o;
            }
            phaserLast[ch] = Flush(y);
            return x * (1 - c.PhaserMix) + y * c.PhaserMix;
        }

        // Flanger: a short modulated delay with feedback (right a quarter cycle later), mixed with the dry signal.
        void FlangerStep(ref double left, ref double right, MusicColorSnapshot c)
        {
            int length = flangeLine[0].Length;
            double baseDelay = c.FlangerDelayMs / 1000 * rate, depth = c.FlangerDepthMs / 1000 * rate;
            double dl = Math.Min(length - 3, 1 + baseDelay + depth * (.5 + .5 * Math.Sin(2 * Math.PI * flangePhase)));
            double dr = Math.Min(length - 3, 1 + baseDelay + depth * (.5 + .5 * Math.Cos(2 * Math.PI * flangePhase)));
            double wl = Read(0, dl), wr = Read(1, dr);
            flangeLine[0][flangeCursor] = (float)Flush(left + c.FlangerFeedback * wl); flangeLine[1][flangeCursor] = (float)Flush(right + c.FlangerFeedback * wr);
            flangeCursor = (flangeCursor + 1) % length;
            flangePhase += c.FlangerRateHz / rate; if (flangePhase >= 1) flangePhase -= 1;
            left = left * (1 - c.FlangerMix) + wl * c.FlangerMix; right = right * (1 - c.FlangerMix) + wr * c.FlangerMix;
        }

        double Read(int ch, double delay)
        {
            var line = flangeLine[ch]; int length = line.Length;
            double pos = flangeCursor - delay; while (pos < 0) pos += length;
            int i = (int)pos; double f = pos - i;
            return line[i] * (1 - f) + line[(i + 1) % length] * f;
        }

        void TremoloStep(ref double left, ref double right, MusicColorSnapshot c, double localBeat)
        {
            double phase;
            if (c.TremoloBeats > 0) phase = localBeat / c.TremoloBeats - Math.Floor(localBeat / c.TremoloBeats);
            else { tremoloPhase += c.TremoloRateHz / rate; if (tremoloPhase >= 1) tremoloPhase -= 1; phase = tremoloPhase; }
            double lfo = c.TremoloShape == MusicLfoShape.Sine ? .5 + .5 * Math.Sin(2 * Math.PI * phase) : c.TremoloShape == MusicLfoShape.Triangle ? 1 - Math.Abs(2 * phase - 1) : phase < .5 ? 1 : 0;
            if (c.TremoloPan)
            {
                double pan = c.TremoloDepth * (2 * lfo - 1);
                left *= pan > 0 ? 1 - pan : 1; right *= pan < 0 ? 1 + pan : 1;
            }
            else { double gain = 1 - c.TremoloDepth * (1 - lfo); left *= gain; right *= gain; }
        }

        static double Flush(double v) => Math.Abs(v) < 1e-25 ? 0 : v;

        // A biquad with independent state per channel; Lr4 runs two identical sections (Linkwitz–Riley 4th order).
        sealed class Biquad
        {
            readonly double[] k = new double[5], s = new double[2 * 2 * 4];
            public void Set(double[] coef) => Array.Copy(coef, k, 5);
            public double Lr4(double x, int ch) => Step(Step(x, ch * 2), ch * 2 + 1);
            double Step(double x, int section)
            {
                int i = section * 4;
                double y = k[0] * x + k[1] * s[i] + k[2] * s[i + 1] - k[3] * s[i + 2] - k[4] * s[i + 3];
                s[i + 1] = s[i]; s[i] = x; s[i + 3] = s[i + 2]; s[i + 2] = Flush(y);
                return y;
            }
        }

        // RBJ cookbook biquads, normalized (b0 b1 b2 a1 a2).
        static class Rbj
        {
            static double[] N(double b0, double b1, double b2, double a0, double a1, double a2) => new[] { b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0 };
            public static double[] LowPass(int fs, double hz, double q) { double w = 2 * Math.PI * hz / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q); return N((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a); }
            public static double[] HighPass(int fs, double hz, double q) { double w = 2 * Math.PI * hz / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q); return N((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a); }
            public static double[] Notch(int fs, double hz, double q) { double w = 2 * Math.PI * hz / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q); return N(1, -2 * c, 1, 1 + a, -2 * c, 1 - a); }
            public static double[] Peak(int fs, double hz, double db, double q)
            { double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / fs, c = Math.Cos(w), a = Math.Sin(w) / (2 * q); return N(1 + a * A, -2 * c, 1 - a * A, 1 + a / A, -2 * c, 1 - a / A); }
            public static double[] Shelf(int fs, double hz, double db, bool high)
            {
                double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / fs, c = Math.Cos(w), alpha = Math.Sin(w) / 2 * Math.Sqrt(2), root = 2 * Math.Sqrt(A) * alpha, sg = high ? -1 : 1;
                return N(A * ((A + 1) - sg * (A - 1) * c + root), sg * 2 * A * ((A - 1) - sg * (A + 1) * c), A * ((A + 1) - sg * (A - 1) * c - root),
                         (A + 1) + sg * (A - 1) * c + root, -sg * 2 * ((A - 1) + sg * (A + 1) * c), (A + 1) + sg * (A - 1) * c - root);
            }
        }
    }
}
