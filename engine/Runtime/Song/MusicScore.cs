using System;

namespace Graze.Presentation.Audio
{
    public enum MusicMood { Explore, Battle, Boss }
    public enum MusicVoice { Bell, Bass, Pad, Kick }

    /// <summary>Immutable repeating MIDI steps; -1 is a rest.</summary>
    public sealed class MusicTrack
    {
        internal readonly int[] Notes;
        public readonly double StepBeats;
        public readonly double Gain;
        public readonly MusicVoice Voice;

        public MusicTrack(MusicVoice voice, double stepBeats, double gain, params int[] notes)
        {
            if (notes == null || notes.Length == 0 || !Finite(stepBeats) || stepBeats <= 0 ||
                !Finite(gain) || gain < 0 || gain > 1 || !Enum.IsDefined(typeof(MusicVoice), voice))
                throw new ArgumentException("Invalid music track.");
            foreach (int note in notes)
                if (note < -1 || note > 127) throw new ArgumentOutOfRangeException(nameof(notes));
            Notes = (int[])notes.Clone();
            Voice = voice;
            StepBeats = stepBeats;
            Gain = gain;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class MusicScore
    {
        public readonly double Bpm;
        public readonly int BeatsPerBar;
        public readonly double FadeBeats;
        internal readonly MusicTrack[][] Sections;

        public MusicScore(double bpm, int beatsPerBar, double fadeBeats, params MusicTrack[][] sections)
        {
            if (!MusicTrack.Finite(bpm) || bpm < 30 || bpm > MusicTempoMap.MaxBpm || beatsPerBar < 1 || beatsPerBar > 12 ||
                !MusicTrack.Finite(fadeBeats) || fadeBeats <= 0 || fadeBeats > beatsPerBar ||
                sections == null || sections.Length != 3) throw new ArgumentException("Invalid music score.");
            Bpm = bpm;
            BeatsPerBar = beatsPerBar;
            FadeBeats = fadeBeats;
            Sections = new MusicTrack[sections.Length][];
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i] == null) throw new ArgumentException("Missing section.");
                Sections[i] = (MusicTrack[])sections[i].Clone();
                foreach (var track in Sections[i])
                    if (track == null) throw new ArgumentException("Missing track.");
            }
        }

        // Original placeholder: A minor, Am / F / C / G. Edit notes here to compose.
        public static MusicScore CreateDemo()
        {
            var bass = new MusicTrack(MusicVoice.Bass, 4, .17, 33, 29, 36, 31);
            var pad = new MusicTrack(MusicVoice.Pad, 4, .10, 57, 53, 60, 55);
            var lead = new MusicTrack(MusicVoice.Bell, .5, .13,
                69, 72, 76, 72, 71, 72, 76, 79, 69, 72, 77, 72, 69, 67, 65, 64,
                67, 72, 76, 79, 76, 74, 72, 71, 67, 71, 74, 79, 77, 74, 71, 67);
            var high = new MusicTrack(MusicVoice.Bell, .25, .07,
                81, 76, 72, 76, 81, 76, 72, 76, 83, 76, 72, 76, 84, 76, 72, 76,
                81, 77, 72, 77, 81, 77, 72, 77, 79, 77, 72, 77, 77, 72, 69, 72,
                79, 76, 72, 76, 84, 79, 76, 79, 83, 79, 76, 79, 84, 79, 76, 79,
                79, 74, 71, 74, 83, 79, 74, 79, 86, 79, 74, 79, 83, 79, 74, 71);
            var kick = new MusicTrack(MusicVoice.Kick, 1, .18, 36);
            return new MusicScore(120, 4, 1,
                new[] { bass, pad }, new[] { bass, pad, lead, kick },
                new[] { bass, pad, lead, high, kick });
        }
    }
}
