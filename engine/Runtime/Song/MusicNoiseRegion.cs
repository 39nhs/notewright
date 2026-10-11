using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    public enum MusicNoiseKind { White, Pink, Brown, Sample, SlowingGlitch, AcceleratingGlitch, PitchGlitch, StutterGlitch, BitcrushGlitch, GateGlitch }

    [Serializable]
    public sealed class MusicNoiseRegion
    {
        public string Name = "Noise";
        public bool Enabled = true;
        public MusicNoiseKind Kind = MusicNoiseKind.White;
        [Tooltip("Zero-based beats: bar 8 starts at beat 28 in 4/4.")]
        public double StartBeat;
        public double DurationBeats = 4;
        [Range(0, 1)] public float Gain = .08f;
        public double FadeInBeats = 3.5;
        public double FadeOutBeats = .5;
        public int Seed = 1234;
        public AudioClip Sample;
        public bool LoopSource = true;
        public bool RepeatWithCue = true;
        [Tooltip("Process the complete music mix instead of adding a sound layer. Glitch kinds only.")]
        public bool MasterFilter;
        [Range(0, 1)] public float Wet = 1;
        [Tooltip("Cuts per beat. BitcrushGlitch: sample-and-hold factor. GateGlitch: gate steps per beat.")]
        [Range(1, 24)] public float GlitchRate = 8;
        [Tooltip("Semitones. BitcrushGlitch: bits removed from 16 (12 = 4-bit). GateGlitch: closed-step probability x 24 (12 = half).")]
        [Range(0, 24)] public float PitchDepth = 12;

        /// <summary>Kinds that only exist as whole-mix filters.</summary>
        public static bool MasterOnly(MusicNoiseKind kind) => kind == MusicNoiseKind.BitcrushGlitch || kind == MusicNoiseKind.GateGlitch;

        public MusicPart Compile(double cueBeats)
        {
            if (!Enum.IsDefined(typeof(MusicNoiseKind), Kind) || !Finite(StartBeat) || StartBeat < 0 ||
                !Finite(DurationBeats) || DurationBeats <= 0 || StartBeat + DurationBeats > cueBeats + 1e-8 ||
                !Finite(FadeInBeats) || !Finite(FadeOutBeats) || FadeInBeats < 0 || FadeOutBeats < 0 ||
                FadeInBeats + FadeOutBeats > DurationBeats + 1e-8 || !Finite(Gain) || Gain < 0 || Gain > 1 ||
                !Finite(GlitchRate) || GlitchRate < 1 || GlitchRate > 24 || !Finite(PitchDepth) || PitchDepth < 0 || PitchDepth > 24)
                throw new ArgumentException("Invalid noise region: " + Name);
            if (MasterOnly(Kind)) throw new ArgumentException(Name + ": " + Kind + " requires MasterFilter.");
            float[] pcm; int rate;
            if (Kind == MusicNoiseKind.Sample || ((int)Kind >= 4 && Sample != null))
            {
                if (Sample == null || Sample.loadType != AudioClipLoadType.DecompressOnLoad || Sample.loadState != AudioDataLoadState.Loaded)
                    throw new InvalidOperationException(Name + ": noise sample must be loaded and Decompress On Load.");
                rate = Sample.frequency;
                var source = new float[Sample.samples * Sample.channels];
                if (!Sample.GetData(source, 0)) throw new InvalidOperationException("Cannot read noise sample: " + Name);
                pcm = new float[Sample.samples];
                for (int i = 0; i < pcm.Length; i++)
                    for (int c = 0; c < Sample.channels; c++) pcm[i] += source[i * Sample.channels + c] / Sample.channels;
            }
            else
            {
                rate = 48000; pcm = new float[rate * 2];
                uint state = unchecked((uint)Seed) | 1u;
                double b0 = 0, b1 = 0, b2 = 0, brown = 0, peak = .001;
                for (int i = 0; i < pcm.Length; i++)
                {
                    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    double white = state / (double)uint.MaxValue * 2 - 1;
                    b0 = .99765 * b0 + white * .099046;
                    b1 = .963 * b1 + white * .2965164;
                    b2 = .57 * b2 + white * 1.0526913;
                    brown = (brown + white * .02) / 1.02;
                    double value = Kind == MusicNoiseKind.Pink ? (b0 + b1 + b2 + white * .1848) * .05 :
                        Kind == MusicNoiseKind.Brown ? brown * 3.5 : white;
                    if ((int)Kind >= 4) value = .65 * Math.Sin(2 * Math.PI * 220 * i / rate) + .2 * Math.Sin(2 * Math.PI * 660 * i / rate) + .15 * white;
                    pcm[i] = (float)value; peak = Math.Max(peak, Math.Abs(value));
                }
                for (int i = 0; i < pcm.Length; i++) pcm[i] *= (float)(.8 / peak);
            }
            var instrument = new MusicInstrument(pcm, rate, 60, .005, .005,
                new NoisePlayback(FadeInBeats, FadeOutBeats, LoopSource, RepeatWithCue, Kind, GlitchRate, PitchDepth));
            return new MusicPart(MusicLayer.Harmony, instrument, Gain, "Noise:" + Name,
                new MusicNote(StartBeat, DurationBeats, 60));
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class NoisePlayback
    {
        public readonly double FadeInBeats, FadeOutBeats;
        public readonly bool LoopSource, RepeatWithCue;
        public readonly MusicNoiseKind Kind;
        public readonly double Rate, Depth;
        public NoisePlayback(double fadeIn, double fadeOut, bool loop, bool repeat, MusicNoiseKind kind = MusicNoiseKind.White, double rate = 8, double depth = 12)
        { FadeInBeats = fadeIn; FadeOutBeats = fadeOut; LoopSource = loop; RepeatWithCue = repeat; Kind = kind; Rate = rate; Depth = depth; }

        public double Speed(double progress)
        {
            if (Kind == MusicNoiseKind.SlowingGlitch) return Math.Pow(2, -Depth * progress / 12);
            if (Kind == MusicNoiseKind.AcceleratingGlitch) return Math.Pow(2, Depth * (progress - 1) / 12);
            if (Kind == MusicNoiseKind.PitchGlitch) return Math.Pow(2, Depth * Math.Sin(progress * Math.PI * 13) / 12);
            return 1;
        }

        public double Gate(double elapsed, double duration)
        {
            if ((int)Kind < 4 || MusicNoiseRegion.MasterOnly(Kind)) return 1;
            double p = Math.Max(0, Math.Min(1, elapsed / duration));
            double cycles = Kind == MusicNoiseKind.SlowingGlitch ? Rate * (elapsed - .45 * elapsed * p) :
                Kind == MusicNoiseKind.AcceleratingGlitch ? Rate * (.1 * elapsed + .45 * elapsed * p) : Rate * elapsed;
            double phase = cycles - Math.Floor(cycles);
            return Math.Max(0, Math.Min(1, Math.Min(phase / .04, (.68 - phase) / .04)));
        }
    }
}
