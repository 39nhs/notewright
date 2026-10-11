using System;
using System.Collections.Generic;

namespace Graze.Presentation.Audio
{
    /// <summary>Slice points for the slice sampler (Part.SliceSeconds): even, per beat, or at transients. Main-thread helpers.</summary>
    public static class MusicSlices
    {
        /// <summary><paramref name="count"/> equal slices over the sample.</summary>
        public static float[] Even(double sampleSeconds, int count)
        {
            if (!MusicTrack.Finite(sampleSeconds) || sampleSeconds <= 0 || count < 1 || count > MusicInstrument.MaxSlices) throw new ArgumentException("Invalid even slicing.");
            var result = new float[count];
            for (int i = 0; i < count; i++) result[i] = (float)(sampleSeconds * i / count);
            return result;
        }

        /// <summary>One slice every <paramref name="beats"/> beats of a sample recorded at <paramref name="bpm"/>, from <paramref name="offsetSeconds"/>.</summary>
        public static float[] EveryBeats(double sampleSeconds, double bpm, double beats, double offsetSeconds = 0)
        {
            if (!MusicTrack.Finite(sampleSeconds) || sampleSeconds <= 0 || !MusicTrack.Finite(bpm) || bpm <= 0 || !MusicTrack.Finite(beats) || beats <= 0 ||
                !MusicTrack.Finite(offsetSeconds) || offsetSeconds < 0) throw new ArgumentException("Invalid beat slicing.");
            var result = new List<float>();
            for (double t = offsetSeconds; t < sampleSeconds - 1e-3 && result.Count < MusicInstrument.MaxSlices; t += beats * 60 / bpm) result.Add((float)t);
            return result.ToArray();
        }

        /// <summary>
        /// Slice starts at transients: 5 ms energy frames; an onset is a frame whose energy jumps above <paramref name="sensitivity"/>
        /// times the recent average (and above -50 dBFS), at least <paramref name="minGapSeconds"/> after the previous one.
        /// Lower sensitivity finds more slices. The first slice always starts at the first onset (or 0).
        /// </summary>
        public static float[] AtTransients(float[] mono, int sampleRate, double sensitivity = 3, double minGapSeconds = .08)
        {
            if (mono == null || mono.Length < 2 || sampleRate < 8000 || !MusicTrack.Finite(sensitivity) || sensitivity < 1.1 ||
                !MusicTrack.Finite(minGapSeconds) || minGapSeconds < .005) throw new ArgumentException("Invalid transient slicing.");
            int hop = Math.Max(1, sampleRate / 200);
            int frames = mono.Length / hop;
            var energy = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double e = 0;
                for (int i = f * hop; i < (f + 1) * hop; i++) e += mono[i] * (double)mono[i];
                energy[f] = e / hop;
            }
            var result = new List<float>();
            double average = 0, floor = Math.Pow(10, -50 / 10.0);
            int gap = (int)Math.Ceiling(minGapSeconds * sampleRate / hop), last = -gap;
            for (int f = 0; f < frames && result.Count < MusicInstrument.MaxSlices; f++)
            {
                if (energy[f] > floor && energy[f] > average * sensitivity && f - last >= gap)
                {
                    // Start a little before the frame's loudest rise: the previous frame boundary, so the attack is not cut.
                    float start = (float)(Math.Max(0, f - 1) * hop / (double)sampleRate);
                    if (result.Count == 0 || start > result[result.Count - 1]) result.Add(start);
                    last = f;
                }
                average = average * .8 + energy[f] * .2;
            }
            if (result.Count == 0) result.Add(0);
            return result.ToArray();
        }
    }
}
