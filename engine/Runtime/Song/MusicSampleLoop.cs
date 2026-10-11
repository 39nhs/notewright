using System;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Sustain loops for recorded samples (playback V3+): a held note keeps playing [start, end) of its recording instead of stopping at the
    /// sample's end, so a short recording can sustain any length. The jump back is smoothed by an equal-power crossfade baked into a copy of
    /// the sample (the original stays for V1/V2). <see cref="Find"/> moves rough loop points to where the waveform matches best.
    /// </summary>
    public static class MusicSampleLoop
    {
        public const double MaxCrossfadeSeconds = 1;

        /// <summary>
        /// A copy of <paramref name="samples"/> whose last <paramref name="crossfade"/> frames before <paramref name="end"/> fade into the frames
        /// before <paramref name="start"/>, so playing on from start after reaching end is seamless. The crossfade is limited to the frames
        /// before start and half the loop.
        /// </summary>
        public static float[] Bake(float[] samples, int start, int end, int crossfade, out int used)
        {
            Check(samples, start, end);
            used = Math.Max(0, Math.Min(crossfade, Math.Min(start, (end - start) / 2)));
            var result = (float[])samples.Clone();
            for (int i = 0; i < used; i++)
            {
                double t = (i + 1) / (double)(used + 1), fadeOut = Math.Cos(t * Math.PI / 2), fadeIn = Math.Sin(t * Math.PI / 2);
                int a = end - used + i, b = start - used + i;
                result[a] = (float)(samples[a] * fadeOut + samples[b] * fadeIn);
            }
            return result;
        }

        /// <summary>
        /// Loop points near (start, end) in frames where a window of the waveform after <c>start</c> best matches the window after <c>end</c>
        /// (normalized correlation over 10 ms windows, searching ±<paramref name="searchSeconds"/> around end and rising zero crossings near start).
        /// </summary>
        public static (int start, int end) Find(float[] samples, int sampleRate, int start, int end, double searchSeconds = .01)
        {
            Check(samples, start, end);
            int window = Math.Max(32, sampleRate / 100), search = Math.Max(1, (int)(searchSeconds * sampleRate));
            // Start on a rising zero crossing near the requested start.
            int bestStart = start;
            for (int d = 0; d < search; d++)
            {
                if (Rising(samples, start + d)) { bestStart = start + d; break; }
                if (Rising(samples, start - d)) { bestStart = start - d; break; }
            }
            int bestEnd = end; double bestScore = double.NegativeInfinity;
            for (int e = Math.Max(bestStart + window, end - search); e <= Math.Min(samples.Length - window - 1, end + search); e++)
            {
                double dot = 0, na = 0, nb = 0;
                for (int i = 0; i < window; i++) { double x = samples[bestStart + i], y = samples[e + i]; dot += x * y; na += x * x; nb += y * y; }
                double score = na > 0 && nb > 0 ? dot / Math.Sqrt(na * nb) : -1;
                if (score > bestScore + 1e-12) { bestScore = score; bestEnd = e; }
            }
            return (bestStart, bestEnd);
        }

        static bool Rising(float[] s, int i) => i > 0 && i < s.Length && s[i - 1] < 0 && s[i] >= 0;

        static void Check(float[] samples, int start, int end)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (start < 0 || end > samples.Length - 1 || end - start < 2) throw new ArgumentOutOfRangeException(nameof(start), "Loop must lie inside the sample with end > start + 1.");
        }
    }
}
