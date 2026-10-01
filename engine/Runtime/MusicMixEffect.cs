using System;

namespace Graze.Presentation.Audio
{
    /// <summary>Immutable whole-mix effect configuration; no generated sound or voice.</summary>
    public sealed class MusicMixEffect
    {
        public readonly double Start, Duration, Wet;
        public readonly NoisePlayback Playback;
        public readonly uint Seed;
        public MusicMixEffect(MusicNoiseRegion region, double loopBeats)
        {
            if ((int)region.Kind < 4 || !Enum.IsDefined(typeof(MusicNoiseKind), region.Kind) ||
                !MusicTrack.Finite(region.StartBeat) || region.StartBeat < 0 ||
                !MusicTrack.Finite(region.DurationBeats) || region.DurationBeats <= 0 || region.StartBeat + region.DurationBeats > loopBeats ||
                !MusicTrack.Finite(region.Wet) || region.Wet < 0 || region.Wet > 1 ||
                !MusicTrack.Finite(region.GlitchRate) || region.GlitchRate < 1 || region.GlitchRate > 24 ||
                !MusicTrack.Finite(region.PitchDepth) || region.PitchDepth < 0 || region.PitchDepth > 24 ||
                !MusicTrack.Finite(region.FadeInBeats) || region.FadeInBeats < 0 || !MusicTrack.Finite(region.FadeOutBeats) || region.FadeOutBeats < 0 ||
                region.FadeInBeats + region.FadeOutBeats > region.DurationBeats)
                throw new ArgumentException("Invalid master glitch: " + region.Name);
            Start = region.StartBeat; Duration = region.DurationBeats; Wet = region.Wet; Seed = unchecked((uint)region.Seed);
            Playback = new NoisePlayback(region.FadeInBeats, region.FadeOutBeats, true, region.RepeatWithCue, region.Kind, region.GlitchRate, region.PitchDepth);
        }
    }

    /// <summary>Preallocated delay tape processes the actual mix including drums.</summary>
    internal sealed class MusicMixProcessor
    {
        readonly float[] tape;
        readonly int rate;
        long written;
        double read;
        MusicMixEffect active;
        double previousBeat, held;
        int holdCounter;
        public MusicMixProcessor(int sampleRate) { rate = sampleRate; tape = new float[sampleRate * 4]; }
        double Read(double position)
        {
            position = Math.Max(Math.Max(0, written - tape.Length + 2), Math.Min(written, position));
            long index = (long)position; double fraction = position - index;
            return tape[index % tape.Length] * (1 - fraction) + tape[(index + 1) % tape.Length] * fraction;
        }
        public float Process(float dry, MusicCue cue, double beat, double bpm, bool repeated, bool playOnce)
        {
            tape[written % tape.Length] = dry;
            MusicMixEffect selected = null;
            double local = beat % cue.LoopBeats;
            if (beat >= 0 && (!playOnce || beat < cue.LoopBeats))
                foreach (var fx in cue.MixEffects)
                    if (local >= fx.Start && local < fx.Start + fx.Duration &&
                        (fx.Playback.RepeatWithCue || (!repeated && beat < cue.LoopBeats))) selected = fx;
            double result = dry;
            if (selected != null)
            {
                if (selected != active || beat < previousBeat) { read = written; holdCounter = 0; }
                double elapsed = local - selected.Start, p = elapsed / selected.Duration;
                var playback = selected.Playback;
                double wet;
                if (playback.Kind == MusicNoiseKind.BitcrushGlitch)
                {
                    // Rate = sample-and-hold factor, Depth = bits removed from 16.
                    if (holdCounter == 0)
                    {
                        double levels = Math.Pow(2, Math.Max(1, 16 - (int)Math.Round(playback.Depth)) - 1);
                        held = Math.Round(dry * levels) / levels;
                    }
                    holdCounter = (holdCounter + 1) % Math.Max(1, (int)Math.Round(playback.Rate));
                    wet = held;
                }
                else if (playback.Kind == MusicNoiseKind.GateGlitch)
                    wet = dry * StepGate(elapsed, playback, selected.Seed);
                else if (playback.Kind == MusicNoiseKind.StutterGlitch)
                {
                    double grain = rate * 60 / bpm / playback.Rate;
                    double age = elapsed * rate * 60 / bpm;
                    // Repeat a captured slice of the real mix, refreshing every beat.
                    wet = Read(written - (age % (rate * 60 / bpm)) + age % grain);
                }
                else
                {
                    wet = Read(read);
                    read += playback.Speed(p);
                    read = Math.Min(written + 1, Math.Max(written - tape.Length + 2, read));
                }
                wet *= playback.Gate(elapsed, selected.Duration);
                double fadeIn = Math.Max(playback.FadeInBeats, bpm / 60 * .01);
                double fadeOut = Math.Max(playback.FadeOutBeats, bpm / 60 * .01);
                double mix = selected.Wet * Math.Max(0, Math.Min(1, Math.Min(elapsed / fadeIn, (selected.Duration - elapsed) / fadeOut)));
                result = dry * (1 - mix) + wet * mix;
            }
            active = selected; previousBeat = beat; written++;
            return (float)result;
        }

        /// <summary>Seeded on/off chop, Rate steps per beat; the first step of every beat stays open to keep the pulse.</summary>
        internal static double StepGate(double elapsed, NoisePlayback playback, uint seed)
        {
            double position = elapsed * playback.Rate;
            long step = (long)Math.Floor(position);
            bool Open(long s) => s % Math.Max(1, (long)Math.Round(playback.Rate)) == 0 || Hash(seed, s) >= playback.Depth / 24;
            if (!Open(step)) return 0;
            double phase = position - step;
            return Math.Max(0, Math.Min(1, Math.Min(Open(step - 1) ? 1 : phase / .04, Open(step + 1) ? 1 : (1 - phase) / .04)));
        }

        static double Hash(uint seed, long step)
        {
            uint h = seed ^ unchecked((uint)step * 2654435761u);
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }
}
