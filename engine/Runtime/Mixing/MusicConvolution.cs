using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>Immutable mono or independent L/R cabinet IR. Capture clips on the main thread before playback.</summary>
    public sealed class MusicConvolutionImpulse
    {
        public const double MaxSeconds = .25;
        readonly float[] left, right;
        internal readonly bool Normalized;
        public int SampleRate { get; }
        public int Frames => left.Length;
        public int Channels => right == null ? 1 : 2;
        public MusicConvolutionImpulse(float[] interleaved, int channels, int sampleRate, bool normalize = false)
        {
            if (interleaved == null || (channels != 1 && channels != 2) || sampleRate < 8000 || sampleRate > 192000 ||
                interleaved.Length == 0 || interleaved.Length % channels != 0 || interleaved.Length / channels > sampleRate * MaxSeconds)
                throw new ArgumentException("Cabinet IR needs mono/stereo PCM, 8..192 kHz, 1 frame..250 ms.");
            SampleRate = sampleRate; Normalized=normalize; left = new float[interleaved.Length / channels];
            if (channels == 2) right = new float[left.Length];
            double sumL = 0, sumR = 0;
            for (int i = 0; i < left.Length; i++)
            {
                float l = interleaved[i * channels], r = interleaved[i * channels + channels - 1];
                if (!MusicTrack.Finite(l) || !MusicTrack.Finite(r) || Math.Abs(l) > 16 || Math.Abs(r) > 16)
                    throw new ArgumentException("IR coefficients must be finite and at most 16 in magnitude.");
                left[i] = l; if (right != null) right[i] = r; sumL += Math.Abs(l); sumR += Math.Abs(r);
            }
            double sum = Math.Max(sumL, sumR);
            if (sum == 0 || sum > 10000) throw new ArgumentException("IR must contain a signal with bounded coefficient sum.");
            if (normalize) for (int i = 0; i < left.Length; i++) { left[i] /= (float)sum; if (right != null) right[i] /= (float)sum; }
        }
        public static MusicConvolutionImpulse FromClip(AudioClip clip, bool normalize = false)
        {
            if (clip == null) return null;
            if (clip.loadType != AudioClipLoadType.DecompressOnLoad || clip.loadState != AudioDataLoadState.Loaded)
                throw new InvalidOperationException("Cabinet IR must use Decompress On Load and be loaded before compiling.");
            if ((clip.channels != 1 && clip.channels != 2) || clip.samples < 1 || clip.samples > clip.frequency * MaxSeconds)
                throw new ArgumentException("Cabinet IR must be mono/stereo and at most 250 ms; trim the source before importing.");
            var pcm = new float[clip.samples * clip.channels];
            if (!clip.GetData(pcm, 0)) throw new InvalidOperationException("Cannot read cabinet IR: " + clip.name);
            return new MusicConvolutionImpulse(pcm, clip.channels, clip.frequency, normalize);
        }
        internal double[] AtRate(int channel, int rate)
        {
            var source = channel == 1 && right != null ? right : left;
            if (rate == SampleRate) return Array.ConvertAll(source, x => (double)x);
            int count = Math.Max(1, (int)Math.Ceiling(source.Length * rate / (double)SampleRate));
            var result = new double[count]; double scale = SampleRate / (double)rate, cutoff = Math.Min(1, 1 / scale);
            // Windowed sinc interpolation/anti-aliasing. IR coefficients are an impulse response: preserve its time integral.
            int radius = (int)Math.Ceiling(16 / cutoff);
            for (int i = 0; i < count; i++)
            {
                double position = i * scale; int center = (int)Math.Floor(position);
                double value = 0;
                for (int j = Math.Max(0, center - radius); j <= Math.Min(source.Length - 1, center + radius); j++)
                {
                    double x = (position - j) * cutoff;
                    if (Math.Abs(x) >= 16) continue;
                    double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    value += source[j] * cutoff * sinc * (.5 + .5 * Math.Cos(Math.PI * x / 16));
                }
                result[i] = value * scale;
            }
            return result;
        }
        public MusicConvolution CreateProcessor(int sampleRate, double mix = 1, double gainDb = 0)
            => new MusicConvolution(this, sampleRate, mix, gainDb);
    }

    /// <summary>Zero added latency: direct 128-tap head and partitioned FFT tail. One writer; construction outside audio callback.</summary>
    public sealed class MusicConvolution
    {
        const int Block = 128, Size = Block * 2;
        readonly Channel left, right;
        readonly double mix, gain;
        public MusicConvolution(MusicConvolutionImpulse impulse, int sampleRate, double mix = 1, double gainDb = 0)
        {
            if (impulse == null || sampleRate < 8000 || sampleRate > 192000 || !MusicTrack.Finite(mix) || mix < 0 || mix > 1 ||
                !MusicTrack.Finite(gainDb) || gainDb < -24 || gainDb > 24) throw new ArgumentException("Invalid convolution settings.");
            this.mix = mix; gain = Math.Pow(10, gainDb / 20);
            var leftTaps=impulse.AtRate(0,sampleRate);
            var rightTaps=impulse.Channels==1?leftTaps:impulse.AtRate(1,sampleRate);
            if(impulse.Normalized&&sampleRate!=impulse.SampleRate)
            {
                double sumL=0,sumR=0;foreach(double v in leftTaps)sumL+=Math.Abs(v);foreach(double v in rightTaps)sumR+=Math.Abs(v);
                double sum=Math.Max(sumL,sumR);
                if(sum>0){for(int i=0;i<leftTaps.Length;i++)leftTaps[i]/=sum;if(!ReferenceEquals(leftTaps,rightTaps))for(int i=0;i<rightTaps.Length;i++)rightTaps[i]/=sum;}
            }
            var l = new Kernel(leftTaps);
            var r = impulse.Channels == 1 ? l : new Kernel(rightTaps);
            left = new Channel(l); right = new Channel(r);
        }
        public void Process(ref double l, ref double r)
        {
            if (mix == 0) return;
            double wetL = left.Process(l), wetR = right.Process(r);
            l = l * (1 - mix) + wetL * gain * mix; r = r * (1 - mix) + wetR * gain * mix;
        }
        public void Reset() { left.Reset(); right.Reset(); }
        sealed class Kernel
        {
            internal readonly double[] Head;
            internal readonly double[][] Real, Imag;
            internal Kernel(double[] taps)
            {
                Head = new double[Math.Min(Block, taps.Length)]; Array.Copy(taps, Head, Head.Length);
                int count = Math.Max(0, (taps.Length - 1) / Block);
                Real = new double[count][]; Imag = new double[count][];
                for (int p = 0; p < count; p++)
                {
                    Real[p] = new double[Size]; Imag[p] = new double[Size];
                    int first = (p + 1) * Block; Array.Copy(taps, first, Real[p], 0, Math.Min(Block, taps.Length - first));
                    Fft(Real[p], Imag[p], false);
                }
            }
        }
        sealed class Channel
        {
            readonly Kernel kernel;
            readonly double[] history = new double[Block], block = new double[Block], tail = new double[Block], overlap = new double[Block];
            readonly double[][] real, imag;
            readonly double[] sumR = new double[Size], sumI = new double[Size];
            int position, slot;
            internal Channel(Kernel kernel)
            {
                this.kernel = kernel; real = new double[kernel.Real.Length][]; imag = new double[real.Length][];
                for (int p = 0; p < real.Length; p++) { real[p] = new double[Size]; imag[p] = new double[Size]; }
            }
            internal double Process(double sample)
            {
                history[position] = block[position] = sample; double value = tail[position];
                for (int j = 0; j < kernel.Head.Length; j++) value += history[(position - j + Block) & (Block - 1)] * kernel.Head[j];
                if (++position == Block)
                {
                    position = 0;
                    if (real.Length > 0)
                    {
                        var xr = real[slot]; var xi = imag[slot]; Array.Clear(xr, 0, Size); Array.Clear(xi, 0, Size);
                        Array.Copy(block, xr, Block); Fft(xr, xi, false);
                        Array.Clear(sumR, 0, Size); Array.Clear(sumI, 0, Size);
                        for (int p = 0; p < real.Length; p++)
                        {
                            int old = (slot - p + real.Length) % real.Length;
                            var ar = real[old]; var ai = imag[old]; var br = kernel.Real[p]; var bi = kernel.Imag[p];
                            for (int k = 0; k < Size; k++) { sumR[k] += ar[k] * br[k] - ai[k] * bi[k]; sumI[k] += ar[k] * bi[k] + ai[k] * br[k]; }
                        }
                        Fft(sumR, sumI, true);
                        for (int i = 0; i < Block; i++) { tail[i] = sumR[i] + overlap[i]; overlap[i] = sumR[i + Block]; }
                        slot = (slot + 1) % real.Length;
                    }
                }
                return value;
            }
            internal void Reset()
            {
                Array.Clear(history, 0, Block); Array.Clear(block, 0, Block); Array.Clear(tail, 0, Block); Array.Clear(overlap, 0, Block);
                for (int p = 0; p < real.Length; p++) { Array.Clear(real[p], 0, Size); Array.Clear(imag[p], 0, Size); }
                position = slot = 0;
            }
        }
        static void Fft(double[] real, double[] imag, bool inverse)
        {
            for (int i = 1, j = 0; i < Size; i++)
            {
                int bit = Size >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i < j) { double t = real[i]; real[i] = real[j]; real[j] = t; t = imag[i]; imag[i] = imag[j]; imag[j] = t; }
            }
            for (int length = 2; length <= Size; length <<= 1)
            {
                double angle = (inverse ? 2 : -2) * Math.PI / length, stepR = Math.Cos(angle), stepI = Math.Sin(angle);
                for (int first = 0; first < Size; first += length)
                {
                    double wr = 1, wi = 0;
                    for (int j = 0; j < length / 2; j++)
                    {
                        int a = first + j, b = a + length / 2;
                        double tr = wr * real[b] - wi * imag[b], ti = wr * imag[b] + wi * real[b];
                        real[b] = real[a] - tr; imag[b] = imag[a] - ti; real[a] += tr; imag[a] += ti;
                        double next = wr * stepR - wi * stepI; wi = wr * stepI + wi * stepR; wr = next;
                    }
                }
            }
            if (inverse) for (int i = 0; i < Size; i++) { real[i] /= Size; imag[i] /= Size; }
        }
        internal static MusicConvolution For(MusicMixerSnapshot settings, int version, int rate)
            => version >= MusicPlaybackVersion.V3 && settings?.CabinetImpulse != null && settings.CabinetImpulseMix > 0
                ? settings.CabinetImpulse.CreateProcessor(rate, settings.CabinetImpulseMix, settings.CabinetImpulseGainDb) : null;
    }
}
