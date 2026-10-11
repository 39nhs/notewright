using System;
using System.Threading;

namespace Graze.Presentation.Audio
{
    /// <summary>Sample-clock transport. No Unity calls, locks or per-buffer allocations in Render.</summary>
    public sealed class MusicSynth
    {
        private readonly MusicScore _score;
        private readonly int _sampleRate;
        private long _sample;
        private long _bar;
        private int _requested;
        private int _current;
        private int _previous;
        private double _transitionBeat = -100;
        private readonly double[] _frequencies = new double[128];
        public MusicMood CurrentMood => (MusicMood)Volatile.Read(ref _current);
        public long RenderedSamples => Interlocked.Read(ref _sample);

        public MusicSynth(MusicScore score, int sampleRate)
        {
            _score = score ?? throw new ArgumentNullException(nameof(score));
            if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _sampleRate = sampleRate;
            for (int i = 0; i < _frequencies.Length; i++) _frequencies[i] = 440 * Math.Pow(2, (i - 69) / 12.0);
        }

        // Main-thread safe; the latest request before the next bar wins.
        public void RequestMood(MusicMood mood)
        {
            if ((int)mood < 0 || (int)mood >= _score.Sections.Length) throw new ArgumentOutOfRangeException(nameof(mood));
            Volatile.Write(ref _requested, (int)mood);
        }

        // Single audio-thread consumer. Mono PCM; Unity routes it to output channels.
        public void Render(float[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            long sample = _sample;
            for (int i = 0; i < data.Length; i++, sample++)
            {
                double beat = sample * (_score.Bpm / (60.0 * _sampleRate));
                long bar = (long)Math.Floor(beat / _score.BeatsPerBar);
                if (bar != _bar)
                {
                    _bar = bar;
                    int requested = Volatile.Read(ref _requested);
                    if (requested != _current)
                    {
                        _previous = _current;
                        Volatile.Write(ref _current, requested);
                        _transitionBeat = bar * _score.BeatsPerBar;
                    }
                }
                double blend = Math.Min(1, Math.Max(0, (beat - _transitionBeat) / _score.FadeBeats));
                double value = Section(_current, beat);
                // Linear crossfade keeps shared tracks at a constant level.
                if (blend < 1) value = Section(_previous, beat) * (1 - blend) + value * blend;
                value *= Math.Min(1, sample / (_sampleRate * .02));
                data[i] = (float)Math.Tanh(value);
            }
            Interlocked.Exchange(ref _sample, sample);
        }

        private double Section(int section, double beat)
        {
            double sum = 0;
            foreach (var track in _score.Sections[section])
            {
                long step = (long)Math.Floor(beat / track.StepBeats);
                int note = track.Notes[step % track.Notes.Length];
                if (note < 0) continue;
                double seconds = (beat - step * track.StepBeats) * 60 / _score.Bpm;
                double duration = track.StepBeats * 60 / _score.Bpm;
                // Zero at both note edges, avoiding clicks at note and phrase boundaries.
                double envelope = Math.Min(1, seconds / .012) * Math.Min(1, Math.Max(0, duration - seconds) / .04);
                double phase = 2 * Math.PI * _frequencies[note] * seconds;
                double tone;
                switch (track.Voice)
                {
                    case MusicVoice.Bell:
                        tone = (Math.Sin(phase) + .25 * Math.Sin(phase * 2)) * Math.Exp(-seconds * 4);
                        break;
                    case MusicVoice.Bass:
                        tone = Math.Sin(phase) + .2 * Math.Sin(phase * 2);
                        break;
                    case MusicVoice.Kick:
                        tone = Math.Sin(2 * Math.PI * (45 * seconds + 8 * (1 - Math.Exp(-seconds * 20)))) * Math.Exp(-seconds * 16);
                        break;
                    default:
                        tone = (Math.Sin(phase) + Math.Sin(phase * 1.5)) * .5;
                        envelope *= Math.Min(1, seconds / .15);
                        break;
                }
                sum += tone * envelope * track.Gain;
            }
            return sum;
        }
    }
}
