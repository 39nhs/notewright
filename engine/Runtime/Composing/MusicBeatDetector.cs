using System;
using System.Collections.Generic;
using System.Linq;

namespace Graze.Presentation.Audio
{
    /// <summary>Tempo and beats found in a recording (<see cref="MusicBeatDetector.Detect"/>).</summary>
    public sealed class MusicBeatAnalysis
    {
        /// <summary>Tempo from the beats (least-squares fit; the average tempo of a drifting take).</summary>
        public double Bpm { get; internal set; }
        /// <summary>0..1: how clearly the onsets repeat at that tempo (below about 0.3 the result is a guess).</summary>
        public double Confidence { get; internal set; }
        /// <summary>Beat times in seconds from the start of the recording.</summary>
        public double[] BeatSeconds { get; internal set; } = Array.Empty<double>();
        /// <summary>Index into <see cref="BeatSeconds"/> of the first beat that looks like a bar start (strongest low end), -1 without beats.</summary>
        public int DownbeatIndex { get; internal set; } = -1;
        public int BeatsPerBar { get; internal set; }
        /// <summary>Seconds of the first downbeat (where bar 1 starts in the recording), or 0.</summary>
        public double DownbeatSeconds => DownbeatIndex >= 0 ? BeatSeconds[DownbeatIndex] : 0;
        /// <summary>Largest deviation of a beat interval from the median, as a share (0.02 = 2 %): a steady loop is near 0, a live take drifts.</summary>
        public double Drift { get; internal set; }
        /// <summary>How the tempo was chosen (candidate BPMs at each step), for diagnosis.</summary>
        public string Trace { get; internal set; } = "";

        /// <summary>An analysis from known beat times (tapped or placed by hand): tempo, drift and tempo map as from detection.</summary>
        public static MusicBeatAnalysis FromBeats(double[] beatSeconds, int downbeatIndex = 0, int beatsPerBar = 4)
        {
            if (beatSeconds == null || beatSeconds.Length < 2) throw new ArgumentException("At least two beats.");
            for (int i = 1; i < beatSeconds.Length; i++) if (!(beatSeconds[i] > beatSeconds[i - 1])) throw new ArgumentException("Beat times must increase.");
            if (downbeatIndex < 0 || downbeatIndex >= beatSeconds.Length || beatsPerBar < 1 || beatsPerBar > 16) throw new ArgumentOutOfRangeException(nameof(downbeatIndex));
            var intervals = beatSeconds.Skip(1).Select((t, i) => t - beatSeconds[i]).ToArray();
            var sorted = (double[])intervals.Clone(); Array.Sort(sorted); double median = sorted[sorted.Length / 2];
            return new MusicBeatAnalysis { BeatSeconds = (double[])beatSeconds.Clone(), DownbeatIndex = downbeatIndex, BeatsPerBar = beatsPerBar, Confidence = 1,
                Bpm = 60 * (beatSeconds.Length - 1) / (beatSeconds[beatSeconds.Length - 1] - beatSeconds[0]), Drift = intervals.Max(x => Math.Abs(x - median)) / median, Trace = "from beats" };
        }

        /// <summary>
        /// A tempo map that follows the beats from the first downbeat (beat 0 there): a change wherever a bar's tempo moves more than
        /// <paramref name="tolerance"/> (share) from the one in force. Initial BPM in <paramref name="bpm"/>.
        /// </summary>
        public MusicTempoChange[] ToTempoChanges(out double bpm, double tolerance = .01)
        {
            bpm = Bpm;
            var changes = new List<MusicTempoChange>();
            int start = Math.Max(0, DownbeatIndex), bar = Math.Max(1, BeatsPerBar);
            if (BeatSeconds.Length - start < bar + 1) return changes.ToArray();
            double current = -1;
            for (int b = start; b + bar < BeatSeconds.Length; b += bar)
            {
                double tempo = 60.0 * bar / (BeatSeconds[b + bar] - BeatSeconds[b]);
                if (current < 0) { bpm = current = tempo; continue; }
                if (Math.Abs(tempo - current) / current > tolerance) { changes.Add(new MusicTempoChange { Beat = b - start, Bpm = tempo }); current = tempo; }
            }
            return changes.ToArray();
        }
    }

    /// <summary>
    /// Finds the tempo and the beats of a recording (offline, any thread): spectral-flux onsets, an autocorrelation tempo with a
    /// preference around 120 BPM and an octave check, then dynamic-programming beat tracking (Ellis 2007); the bar start is the beat
    /// phase with the most low-end onset energy. Works best on music with a steady pulse (loops, band recordings).
    /// </summary>
    public static class MusicBeatDetector
    {
        public static MusicBeatAnalysis Detect(float[] mono, int sampleRate, double minBpm = 60, double maxBpm = 200, int beatsPerBar = 4)
        {
            if (mono == null || sampleRate < 8000 || sampleRate > 192000) throw new ArgumentException("Need mono PCM at 8–192 kHz.");
            if (!(minBpm >= 30 && maxBpm <= 400 && minBpm * 1.5 <= maxBpm)) throw new ArgumentOutOfRangeException(nameof(minBpm), "BPM range 30..400, max at least 1.5 × min.");
            if (beatsPerBar < 1 || beatsPerBar > 16) throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
            var result = new MusicBeatAnalysis { BeatsPerBar = beatsPerBar };
            int frame = sampleRate > 32000 ? 2048 : 1024, hop = frame / 4;
            if (mono.Length < frame * 8) return result;
            OnsetEnvelope(mono, sampleRate, frame, hop, out var onset, out var low, out var body);
            double fps = sampleRate / (double)hop;
            int minLag = (int)Math.Floor(fps * 60 / maxBpm), maxLag = (int)Math.Ceiling(fps * 60 / minBpm);
            if (onset.Length < maxLag * 3) return result;
            // Autocorrelation over the tempo lags, weighted toward 130 BPM (log-Gaussian, 0.8 octave wide).
            var ac = new double[maxLag + 2];
            for (int lag = 1; lag <= maxLag + 1; lag++) { double s = 0; for (int i = lag; i < onset.Length; i++) s += onset[i] * onset[i - lag]; ac[lag] = s / (onset.Length - lag); }
            double zero = 0; foreach (double v in onset) zero += v * v; zero /= onset.Length;
            double Weight(double lag) { double bpm = 60 * fps / lag; double o = Math.Log(bpm / 130, 2) / .8; return Math.Exp(-.5 * o * o); }
            double Comb(double lag) { double s = 0; for (int k = 1; k <= 4; k++) { double x = lag * k; int i = (int)x; if (i + 1 > maxLag + 1) break; s += (ac[i] + (ac[i + 1] - ac[i]) * (x - i)) / k; } return s; }
            int best = minLag; double bestScore = double.NegativeInfinity;
            for (int lag = minLag; lag <= maxLag; lag++) { double sc = Comb(lag) * Weight(lag); if (sc > bestScore) { bestScore = sc; best = lag; } }
            double period = best;
            if (best > minLag && best < maxLag) { double a = Comb(best - 1) * Weight(best - 1), b = bestScore, c = Comb(best + 1) * Weight(best + 1); double d = a - 2 * b + c; if (d < 0) period = best + .5 * (a - c) / d; }
            result.Trace = $"prior {60 * fps / period:0.#}";
            // Octave and 3:2 check: a related period that scores clearly better without the prior (triplet-heavy parts pull toward 2/3 tempo).
            double chosen = period;
            foreach (double alt in new[] { period / 2, period * 2, period * 2 / 3, period * 3 / 2 })
                if (alt >= minLag && alt <= maxLag && Comb(alt) > 1.3 * Comb(chosen)) chosen = alt;
            period = chosen; result.Trace += $" → related {60 * fps / period:0.#}";
            double peak = ac[(int)Math.Round(period)], mean = 0; for (int lag = minLag; lag <= maxLag; lag++) mean += ac[lag]; mean /= maxLag - minLag + 1;
            result.Confidence = zero > 0 ? Math.Max(0, Math.Min(1, (peak - mean) / zero * 2)) : 0;
            var beats = Track(onset, period);
            // Double-time check: when every other tracked beat is much weaker (hats between kick and snare), the beat is twice as long.
            if (beats.Count >= 8 && period * 2 <= maxLag)
            {
                double even = 0, odd = 0;
                for (int i = 0; i < beats.Count; i++) { double v = Peak(body, beats[i], 1); if (i % 2 == 0) even += v; else odd += v; } // kick/snare body, not hats
                even /= (beats.Count + 1) / 2; odd /= beats.Count / 2;
                // Not below 72 BPM: a very slow pulse is rarer than double time (kick every other beat at 140 stays 140).
                result.Trace += $" · alternation {Math.Min(even, odd) / Math.Max(even, odd):0.00}";
                if (Math.Min(even, odd) < .35 * Math.Max(even, odd) && 60 * fps / (period * 2) >= 72) { period *= 2; beats = Track(onset, period); result.Trace += $" → halved {60 * fps / period:0.#}"; }
            }
            // Half-time check: at a slow pulse, onsets halfway between the beats as strong as the beats (backbeat snares) mean twice the tempo.
            if (beats.Count >= 8 && 60 * fps / period < 100 && period / 2 >= minLag)
            {
                double on = 0, mid = 0; int pairs = 0;
                for (int i = 0; i + 1 < beats.Count; i++) { on += Peak(body, beats[i], 1); mid += Peak(body, (beats[i] + beats[i + 1]) / 2, 1); pairs++; }
                result.Trace += $" · midpoints {mid / Math.Max(1e-12, on):0.00}";
                if (mid >= .55 * on) { period /= 2; beats = Track(onset, period); result.Trace += $" → doubled {60 * fps / period:0.#}"; }
            }
            double offset = frame / 2.0 / sampleRate; // onset frame index → time of the frame centre
            result.BeatSeconds = beats.Select(f => Refine(onset, f) / fps + offset).ToArray();
            if (beats.Count >= 3)
            {
                var times = result.BeatSeconds;
                var intervals = new double[times.Length - 1];
                for (int i = 1; i < times.Length; i++) intervals[i - 1] = times[i] - times[i - 1];
                var sorted = (double[])intervals.Clone(); Array.Sort(sorted);
                double median = sorted[sorted.Length / 2];
                // Tempo: least-squares slope of beat time against beat number (sub-frame accurate; the average over a drifting take).
                double n = times.Length, sx = 0, sy = 0, sxx = 0, sxy = 0;
                for (int i = 0; i < times.Length; i++) { sx += i; sy += times[i]; sxx += (double)i * i; sxy += i * times[i]; }
                double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
                result.Bpm = 60 / slope;
                result.Drift = intervals.Max(x => Math.Abs(x - median)) / median;
                // Downbeat: the beat phase whose beats carry the most low-band onset energy.
                double bestPhase = double.NegativeInfinity;
                for (int phase = 0; phase < Math.Min(beatsPerBar, beats.Count); phase++)
                {
                    double s = 0; int count = 0;
                    for (int i = phase; i < beats.Count; i += beatsPerBar) { s += Peak(low, beats[i], 2); count++; }
                    s /= Math.Max(1, count);
                    if (s > bestPhase + 1e-12) { bestPhase = s; result.DownbeatIndex = phase; }
                }
            }
            else if (period > 0) result.Bpm = 60 * fps / period;
            return result;
        }

        // The onset peak nearest a beat frame, with parabolic interpolation (fractional frame).
        static double Refine(double[] env, int at)
        {
            int best = at;
            for (int i = Math.Max(1, at - 2); i <= Math.Min(env.Length - 2, at + 2); i++) if (env[i] > env[best]) best = i;
            if (best < 1 || best > env.Length - 2) return best;
            double a = env[best - 1], b = env[best], c = env[best + 1], d = a - 2 * b + c;
            return d < 0 ? best + .5 * (a - c) / d : best;
        }

        static double Peak(double[] env, int at, int radius) { double m = 0; for (int i = Math.Max(0, at - radius); i <= Math.Min(env.Length - 1, at + radius); i++) m = Math.Max(m, env[i]); return m; }

        // Log-magnitude spectral flux (positive changes, up to 8 kHz), its low band (< 200 Hz) and body band (< 3 kHz), local mean removed.
        static void OnsetEnvelope(float[] mono, int rate, int frame, int hop, out double[] onset, out double[] low, out double[] body)
        {
            int frames = (mono.Length - frame) / hop + 1, bins = frame / 2, top = Math.Min(bins, (int)(8000.0 * frame / rate)), lowTop = Math.Max(2, (int)(200.0 * frame / rate)),
                bodyTop = Math.Min(top, (int)(3000.0 * frame / rate));
            var window = new double[frame]; for (int i = 0; i < frame; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / frame);
            var re = new double[frame]; var im = new double[frame]; var previous = new double[bins]; var magnitude = new double[bins]; var previousLinear = new double[bins];
            var flux = new double[frames]; var lowFlux = new double[frames]; var bodyFlux = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int start = f * hop;
                for (int i = 0; i < frame; i++) { re[i] = mono[start + i] * window[i]; im[i] = 0; }
                MusicFft.Transform(re, im);
                double s = 0, l = 0, m = 0;
                for (int k = 1; k < top; k++)
                {
                    double linear = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    magnitude[k] = Math.Log(1 + 100 * linear);
                    double d = magnitude[k] - previous[k];
                    if (d > 0 && f > 0) { s += d; if (k < lowTop) l += d; if (k < bodyTop) m += d; } // log: kick and snare weigh alike, bright hats barely count
                    previous[k] = magnitude[k]; previousLinear[k] = linear;
                }
                flux[f] = s; lowFlux[f] = l; bodyFlux[f] = m;
            }
            int width = (int)Math.Max(3, .4 * rate / hop);
            onset = Detrend(flux, width); low = Detrend(lowFlux, width); body = Detrend(bodyFlux, width);
        }

        static double[] Detrend(double[] x, int width)
        {
            var result = new double[x.Length]; double sum = 0; int half = width / 2;
            var prefix = new double[x.Length + 1]; for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i];
            double max = 0;
            for (int i = 0; i < x.Length; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(x.Length, i + half + 1);
                double v = x[i] - (prefix[b] - prefix[a]) / (b - a); result[i] = v > 0 ? v : 0; max = Math.Max(max, result[i]); sum += result[i];
            }
            // Scale to unit standard deviation so the tracker's spacing penalty (tightness) weighs against typical onsets, not the loudest one.
            double mean = sum / Math.Max(1, x.Length), variance = 0;
            foreach (double v in result) variance += (v - mean) * (v - mean);
            double sd = Math.Sqrt(variance / Math.Max(1, x.Length));
            if (sd > 1e-12) for (int i = 0; i < result.Length; i++) result[i] /= sd;
            return result;
        }

        // Dynamic-programming beat tracker: each beat = onset strength + the best earlier beat about one period before, penalised by
        // the squared log of its spacing relative to the period.
        static List<int> Track(double[] onset, double period)
        {
            int n = onset.Length; var score = new double[n]; var back = new int[n];
            const double tightness = 100;
            int lo = (int)Math.Round(period / 2), hi = (int)Math.Round(period * 2);
            for (int t = 0; t < n; t++)
            {
                double bestPrev = double.NegativeInfinity; int arg = -1;
                for (int tau = t - hi; tau <= t - lo; tau++)
                {
                    if (tau < 0) continue;
                    double spacing = Math.Log((t - tau) / period), v = score[tau] - tightness * spacing * spacing;
                    if (v > bestPrev) { bestPrev = v; arg = tau; }
                }
                score[t] = onset[t] + (arg >= 0 ? Math.Max(0, bestPrev) : 0);
                back[t] = arg >= 0 && bestPrev > 0 ? arg : -1;
            }
            // The best-scoring beat in the last period, then back through the links.
            int end = n - 1; double top = double.NegativeInfinity;
            for (int t = Math.Max(0, n - (int)Math.Ceiling(period)); t < n; t++) if (score[t] > top) { top = score[t]; end = t; }
            var beats = new List<int>();
            for (int t = end; t >= 0; t = back[t]) { beats.Add(t); if (back[t] < 0) break; }
            beats.Reverse();
            return beats;
        }

        /// <summary>Mono PCM (the channel average) of interleaved audio, for <see cref="Detect"/>.</summary>
        public static float[] Mono(float[] interleaved, int channels)
        {
            if (interleaved == null || channels < 1) throw new ArgumentException("Need interleaved PCM.");
            if (channels == 1) return interleaved;
            var mono = new float[interleaved.Length / channels];
            for (int i = 0; i < mono.Length; i++) { double s = 0; for (int c = 0; c < channels; c++) s += interleaved[i * channels + c]; mono[i] = (float)(s / channels); }
            return mono;
        }
    }
}
