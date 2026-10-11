using System;
using System.Collections.Generic;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Streaming true-peak estimate of one channel: 4× oversampling with a 48-tap windowed-sinc interpolator (12 taps per phase,
    /// each phase normalized to unity gain). Returns the largest absolute value among the sample and the three points between it and
    /// the previous one, about six samples late. An approximation of ITU-R BS.1770 true peak (not its exact filter). No allocation.
    /// </summary>
    public sealed class MusicTruePeak
    {
        const int Taps = 12, Phases = 4;
        static readonly double[] Coefficients = Design();
        readonly double[] history = new double[Taps];
        int at;

        static double[] Design()
        {
            var c = new double[Phases * Taps];
            double centre = (Phases * Taps - 1) / 2.0;
            for (int j = 0; j < c.Length; j++)
            {
                double t = (j - centre) / Phases, sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double window = .5 - .5 * Math.Cos(2 * Math.PI * (j + .5) / c.Length); // Hann
                c[j] = sinc * window;
            }
            for (int p = 0; p < Phases; p++)
            {
                double sum = 0; for (int k = 0; k < Taps; k++) sum += c[p + Phases * k];
                for (int k = 0; k < Taps; k++) c[p + Phases * k] /= sum;
            }
            return c;
        }

        public double Process(double x)
        {
            history[at] = x; at = at + 1 == Taps ? 0 : at + 1;
            double peak = 0;
            for (int p = 0; p < Phases; p++)
            {
                double y = 0; int i = at;
                for (int k = 0; k < Taps; k++) { i = i == 0 ? Taps - 1 : i - 1; y += Coefficients[p + Phases * k] * history[i]; }
                peak = Math.Max(peak, Math.Abs(y));
            }
            return peak;
        }

        public void Reset() { Array.Clear(history, 0, Taps); at = 0; }
    }

    /// <summary>
    /// Look-ahead brick-wall limiter for the master (playback V3, MusicMixerSettings.Limiter). Gain into it (LimiterInputDb), then the
    /// gain each sample needs to keep its true-peak estimate under the ceiling; the minimum over the look-ahead window, averaged over
    /// the window, is reached exactly when the peak leaves the 2 ms delay line, so no sample passes the ceiling (a final clamp catches
    /// the residual inter-sample overshoot). Releases exponentially. Below the ceiling it is a pure 2 ms delay at unity gain.
    /// Buffers are allocated in the constructor; Process allocates nothing.
    /// </summary>
    internal sealed class MusicLimiter
    {
        public const double LookaheadSeconds = MusicMixerSettings.LimiterLookaheadSeconds;
        readonly int delay;
        readonly float[] lineL, lineR;
        readonly double[] required, averaged; readonly long[] stamp; // sliding minimum (monotonic queue) and running average
        readonly MusicTruePeak peakL = new MusicTruePeak(), peakR = new MusicTruePeak();
        readonly int rate;
        long n; int head, tail, write; double sum, gain = 1;

        public MusicLimiter(int sampleRate)
        {
            rate = sampleRate; delay = Math.Max(8, (int)Math.Round(LookaheadSeconds * sampleRate));
            lineL = new float[delay]; lineR = new float[delay];
            required = new double[delay + 2]; stamp = new long[delay + 2]; averaged = new double[delay];
            sum = delay; for (int i = 0; i < delay; i++) averaged[i] = 1;
        }

        public int LatencyFrames => delay;

        public void Process(ref double left, ref double right, MusicMixerSnapshot s)
        {
            double input = s.LimiterInput, ceiling = s.LimiterCeiling;
            double l = left * input, r = right * input;
            double tp = Math.Max(Math.Max(Math.Abs(l), Math.Abs(r)), Math.Max(peakL.Process(l), peakR.Process(r)));
            double need = tp > ceiling ? ceiling / tp : 1;
            // Sliding minimum over the last delay + 1 requirements (ring-buffered monotonic queue; one spare slot so full != empty).
            int capacity = required.Length, window = delay + 1;
            while (head != tail && stamp[head] <= n - window) head = (head + 1) % capacity;
            while (head != tail && required[(tail - 1 + capacity) % capacity] >= need) tail = (tail - 1 + capacity) % capacity;
            required[tail] = need; stamp[tail] = n; tail = (tail + 1) % capacity;
            double minimum = required[head];
            // Average of the minimum over the delay: it ramps down across the look-ahead and reaches each peak's gain on time.
            sum += minimum - averaged[write]; averaged[write] = minimum;
            double target = sum / delay;
            if ((n & 65535) == 65535) { sum = 0; foreach (double v in averaged) sum += v; target = sum / delay; } // drift guard
            gain = target < gain ? target : target + (gain - target) * s.LimiterReleaseCoefficient(rate);
            double outL = lineL[write] * gain, outR = lineR[write] * gain;
            lineL[write] = (float)l; lineR[write] = (float)r;
            write = write + 1 == delay ? 0 : write + 1; n++;
            left = Math.Max(-ceiling, Math.Min(ceiling, outL)); right = Math.Max(-ceiling, Math.Min(ceiling, outR));
        }

        /// <summary>Gain reduction now (dB, 0 or negative) for meters.</summary>
        public double ReductionDb => 20 * Math.Log10(Math.Max(1e-9, gain));
    }

    /// <summary>Integrated loudness of a render and matching a song to a loudness target with the master limiter (playback V3).</summary>
    public static class MusicLoudness
    {
        /// <summary>BS.1770 gated integrated loudness (LUFS) and approximate true peak (dBTP) of interleaved stereo PCM.</summary>
        public static double Measure(float[] stereo, int sampleRate, out double truePeakDb)
        {
            if (stereo == null || stereo.Length < 2 || stereo.Length % 2 != 0) throw new ArgumentException("Need stereo PCM.");
            var meter = new MusicLevelMeter(sampleRate, 2, Math.Max(1, Math.Min(21600, stereo.Length / 2 / sampleRate + 1)));
            meter.ProcessStereo(stereo);
            meter.TryRead(out var levels);
            truePeakDb = MusicLevelMeter.AmplitudeDb(Math.Max(levels.TruePeakLeft, levels.TruePeakRight));
            return meter.ReadIntegratedLufs();
        }

        /// <summary>
        /// Sets the song's master <see cref="MusicMixerSettings.LimiterInputDb"/> so its render reaches <paramref name="targetLufs"/>
        /// (turns the limiter on). Renders up to <paramref name="passes"/> times because limiting changes loudness; returns the final
        /// integrated loudness. <paramref name="render"/> renders the song (stereo, <paramref name="sampleRate"/>).
        /// </summary>
        public static double Match(MusicCueDefinition song, double targetLufs, Func<MusicCueDefinition, float[]> render, int sampleRate = 48000, int passes = 6)
        {
            if (song == null || render == null) throw new ArgumentNullException(nameof(song));
            if (!MusicTrack.Finite(targetLufs) || targetLufs < -40 || targetLufs > 0) throw new ArgumentOutOfRangeException(nameof(targetLufs));
            song.Mixer = song.Mixer ?? new MusicMixerSettings();
            song.Mixer.Limiter = true;
            double lufs = double.NegativeInfinity, previousGain = double.NaN, previousLufs = double.NaN;
            for (int pass = 0; pass < passes; pass++)
            {
                lufs = Measure(render(song), sampleRate, out _);
                if (double.IsInfinity(lufs) || double.IsNaN(lufs)) throw new InvalidOperationException("The song is silent; nothing to match.");
                double error = targetLufs - lufs, gain = song.Mixer.LimiterInputDb;
                if (Math.Abs(error) < .1) break;
                // Secant step: under heavy limiting a dB of input adds less than a dB of loudness, so use the measured slope.
                double slope = double.IsNaN(previousGain) || Math.Abs(gain - previousGain) < 1e-3 ? 1 : Math.Max(.15, Math.Min(1.5, (lufs - previousLufs) / (gain - previousGain)));
                previousGain = gain; previousLufs = lufs;
                double next = Math.Max(-24, Math.Min(MusicMixerSettings.MaxLimiterInputDb, gain + error / slope));
                if (Math.Abs(next - gain) < 1e-3) break; // at the input range limit
                song.Mixer.LimiterInputDb = (float)next;
            }
            return lufs;
        }

        /// <summary>Renders the whole song once (stereo, playOnce) for loudness work.</summary>
        public static float[] RenderSong(MusicCueDefinition song, int sampleRate = 48000)
        {
            var cue = song.Compile();
            var pcm = new float[Math.Max(1, (long)Math.Round(cue.Tempo.SecondsAt(cue.LoopBeats) * sampleRate)) * 2];
            new AdaptiveMusicEngine(cue, sampleRate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        /// <summary>Matches the song's own <see cref="MusicCueDefinition.TargetLufs"/> (song.json "targetLufs"); null when it has none.</summary>
        public static double? MatchTarget(MusicCueDefinition song, int sampleRate = 48000)
        {
            if (song == null || song.TargetLufs == 0) return null;
            return Match(song, song.TargetLufs, s => RenderSong(s, sampleRate), sampleRate);
        }
    }
}
