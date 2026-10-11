using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    public struct MusicLevelSnapshot
    {
        public long Frames, InvalidSamples;
        public double PeakLeft, PeakRight, RmsLeft, RmsRight, MomentaryLufs, ShortTermLufs;
        /// <summary>Approximate true peak since the start (4× oversampled, <see cref="MusicTruePeak"/>).</summary>
        public double TruePeakLeft, TruePeakRight;
        public double TruePeakDb => MusicLevelMeter.AmplitudeDb(Math.Max(TruePeakLeft, TruePeakRight));
        public bool MomentaryReady, ShortTermReady, IntegratedCapacityExceeded;
        public double PeakLeftDb => MusicLevelMeter.AmplitudeDb(PeakLeft);
        public double PeakRightDb => MusicLevelMeter.AmplitudeDb(PeakRight);
        public double RmsLeftDb => MusicLevelMeter.AmplitudeDb(RmsLeft);
        public double RmsRightDb => MusicLevelMeter.AmplitudeDb(RmsRight);
    }

    /// <summary>
    /// Opt-in mono/stereo sample peak, approximate true peak (4× oversampled), 400 ms RMS, K-weighted 400 ms/3 s LUFS and BS.1770
    /// gated integrated LUFS. One audio writer. Capture snapshots with TryRead; integrated scanning belongs on the control thread.
    /// Fixed buffers; no callback allocations. No LRA. Reset only while all consumers are stopped.
    /// </summary>
    public sealed class MusicLevelMeter
    {
        public int SampleRate { get; }
        public int Channels { get; }
        readonly int binFrames;
        readonly double[] binsK = new double[30], binsL = new double[30], binsR = new double[30], integrated;
        readonly Filter[] shelf = new Filter[2], high = new Filter[2];
        long frames, invalid, bins;
        int inBin, binSlot, integratedCount, exceeded, sequence;
        double sumK, sumL, sumR, peakL, peakR, rmsL, rmsR, momentary = double.NegativeInfinity, shortTerm = double.NegativeInfinity, trueL, trueR;
        readonly MusicTruePeak truePeakL = new MusicTruePeak(), truePeakR = new MusicTruePeak();
        MusicLevelSnapshot published;
        public MusicLevelMeter(int sampleRate, int channels = 2, int integratedSeconds = 1800)
        {
            if (sampleRate < 8000 || sampleRate > 192000 || sampleRate % 10 != 0 || (channels != 1 && channels != 2) || integratedSeconds < 1 || integratedSeconds > 21600)
                throw new ArgumentException("Meter needs 8..192 kHz in multiples of 10 Hz, mono/stereo, and 1..21600 seconds history.");
            SampleRate = sampleRate; Channels = channels; binFrames = sampleRate / 10;
            integrated = new double[integratedSeconds * 10];
            // BS.1770's 48 kHz weighting response, bilinear-transformed for the supplied rate (De Man parameterization).
            double k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate), q = .7071752369554196;
            double vh = Math.Pow(10, 3.999843853973347 / 20), vb = Math.Pow(vh, .4996667741545416), a0 = 1 + k / q + k * k;
            for (int c = 0; c < 2; c++) shelf[c] = new Filter((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
            k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate); q = .5003270373238773; a0 = 1 + k / q + k * k;
            for (int c = 0; c < 2; c++) high[c] = new Filter(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
            Publish();
        }
        public static double AmplitudeDb(double value) => value > 0 ? 20 * Math.Log10(value) : double.NegativeInfinity;
        static double Lufs(double energy) => energy > 0 ? -.691 + 10 * Math.Log10(energy) : double.NegativeInfinity;
        public void ProcessStereo(float[] pcm, int offset = 0, int frames = -1)
        {
            if (pcm == null || offset < 0 || offset > pcm.Length || offset % 2 != 0 || Channels != 2) throw new ArgumentException("Stereo meter needs aligned stereo PCM.");
            if(frames < -1)throw new ArgumentException("Invalid frame count.");
            if (frames == -1) { if ((pcm.Length - offset) % 2 != 0) throw new ArgumentException("Incomplete stereo frame."); frames = (pcm.Length - offset) / 2; }
            if ((long)frames * 2 > pcm.Length - offset) throw new ArgumentException("PCM range outside buffer.");
            for (int i = 0; i < frames; i++) AddFrame(pcm[offset + i * 2], pcm[offset + i * 2 + 1]);
            Publish();
        }
        public void ProcessMono(float[] pcm)
        {
            if (pcm == null || Channels != 1) throw new ArgumentException("Mono meter needs mono PCM.");
            for (int i = 0; i < pcm.Length; i++) AddFrame(pcm[i], 0);
            Publish();
        }
        internal void AddFrame(double l, double r)
        {
            // Corrupt upstream samples are diagnosed and contribute silence; the meter never changes the audio stream.
            if (!MusicTrack.Finite(l) || Math.Abs(l) > 1e10) { l = 0; invalid++; }
            if (!MusicTrack.Finite(r) || Math.Abs(r) > 1e10) { r = 0; invalid++; }
            if (Channels == 1) r = 0;
            peakL = Math.Max(peakL, Math.Abs(l)); peakR = Math.Max(peakR, Math.Abs(r));
            trueL = Math.Max(trueL, Math.Max(Math.Abs(l), truePeakL.Process(l))); trueR = Math.Max(trueR, Math.Max(Math.Abs(r), truePeakR.Process(r)));
            sumL += l * l; sumR += r * r;
            double kl = high[0].Process(shelf[0].Process(l)), kr = high[1].Process(shelf[1].Process(r));
            sumK += kl * kl + kr * kr; frames++;
            if (++inBin != binFrames) return;
            binsL[binSlot] = sumL / binFrames; binsR[binSlot] = sumR / binFrames; binsK[binSlot] = sumK / binFrames; bins++;
            double fourK = 0, thirtyK = 0, fourL = 0, fourR = 0;
            int recent = (int)Math.Min(bins, 4);
            for (int j = 0; j < Math.Min(bins, 30); j++)
            {
                int slot = (binSlot - j + 30) % 30; thirtyK += binsK[slot];
                if (j < 4) { fourK += binsK[slot]; fourL += binsL[slot]; fourR += binsR[slot]; }
            }
            rmsL = Math.Sqrt(fourL / recent); rmsR = Math.Sqrt(fourR / recent);
            if (bins >= 4)
            {
                double energy = fourK / 4; momentary = Lufs(energy);
                if (integratedCount < integrated.Length) { integrated[integratedCount] = energy; Volatile.Write(ref integratedCount, integratedCount + 1); }
                else Volatile.Write(ref exceeded, 1);
            }
            if (bins >= 30) shortTerm = Lufs(thirtyK / 30);
            binSlot = (binSlot + 1) % 30; sumL = sumR = sumK = 0; inBin = 0;
            Publish();
        }
        internal void Publish()
        {
            Interlocked.Increment(ref sequence);
            published = new MusicLevelSnapshot { Frames = frames, InvalidSamples = invalid, PeakLeft = peakL, PeakRight = peakR, TruePeakLeft = trueL, TruePeakRight = trueR,
                RmsLeft = rmsL, RmsRight = rmsR, MomentaryLufs = momentary, ShortTermLufs = shortTerm,
                MomentaryReady = bins >= 4, ShortTermReady = bins >= 30, IntegratedCapacityExceeded = exceeded != 0 };
            Volatile.Write(ref sequence, sequence + 1);
        }
        public bool TryRead(out MusicLevelSnapshot snapshot)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int before = Volatile.Read(ref sequence); if ((before & 1) != 0) continue;
                var copy = published; Thread.MemoryBarrier();
                if (before == Volatile.Read(ref sequence)) { snapshot = copy; return true; }
            }
            snapshot = default; return false; // UI can retain its previous valid snapshot.
        }
        /// <summary>Exact two-stage gate over retained 400 ms blocks; O(history), control/offline thread only. NaN if capacity exceeded.</summary>
        public double ReadIntegratedLufs()
        {
            if (Volatile.Read(ref exceeded) != 0) return double.NaN;
            int count = Volatile.Read(ref integratedCount), accepted = 0; double sum = 0;
            double absolute = Math.Pow(10, (-70 + .691) / 10);
            for (int i = 0; i < count; i++) if (integrated[i] > absolute) { sum += integrated[i]; accepted++; }
            if (accepted == 0) return double.NegativeInfinity;
            double gate = Math.Max(absolute, sum / accepted * .1); sum = 0; accepted = 0;
            for (int i = 0; i < count; i++) if (integrated[i] > gate) { sum += integrated[i]; accepted++; }
            return accepted == 0 ? double.NegativeInfinity : Lufs(sum / accepted);
        }
        public void Reset()
        {
            frames = invalid = bins = 0; inBin = binSlot = integratedCount = exceeded = 0;
            sumK = sumL = sumR = peakL = peakR = rmsL = rmsR = trueL = trueR = 0; momentary = shortTerm = double.NegativeInfinity; truePeakL.Reset(); truePeakR.Reset();
            Array.Clear(binsK, 0, 30); Array.Clear(binsL, 0, 30); Array.Clear(binsR, 0, 30); Array.Clear(integrated, 0, integrated.Length);
            for (int c = 0; c < 2; c++) { shelf[c].Reset(); high[c].Reset(); } Publish();
        }
        sealed class Filter
        {
            readonly double b0, b1, b2, a1, a2; double z1, z2;
            internal Filter(double b0, double b1, double b2, double a1, double a2) { this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2; }
            internal double Process(double x) { double y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
            internal void Reset() { z1 = z2 = 0; }
        }
    }
}
