using System;

namespace Graze.Presentation.Audio
{
    public enum MusicLayer { Melody, Bass, Harmony, Drums }

    [Serializable]
    public struct MusicNote
    {
        public double Beat;
        public double Duration;
        public int Key;
        public float Velocity;
        public MusicNote(double beat, double duration, int key, float velocity = 1)
        { Beat = beat; Duration = duration; Key = key; Velocity = velocity; }
    }

    /// <summary>Preloaded mono sample or oscillator. No Unity objects on the audio thread.</summary>
    public sealed class MusicInstrument
    {
        internal readonly float[] Samples;
        public readonly int SampleRate, RootKey;
        public readonly MusicVoice Voice;
        public readonly double Attack, Release;
        public readonly NoisePlayback Noise;

        public MusicInstrument(MusicVoice voice, double attack = .008, double release = .12)
        {
            if (!Enum.IsDefined(typeof(MusicVoice), voice) || !MusicTrack.Finite(attack) || attack <= 0 ||
                !MusicTrack.Finite(release) || release <= 0) throw new ArgumentException("Invalid instrument.");
            Voice = voice; Attack = attack; Release = release;
        }

        public MusicInstrument(float[] samples, int sampleRate, int rootKey, double attack = .008, double release = .12, NoisePlayback noise = null)
            : this(MusicVoice.Bell, attack, release)
        {
            if (samples == null || samples.Length < 2 || sampleRate < 8000 || sampleRate > 192000 || rootKey < 0 || rootKey > 127)
                throw new ArgumentException("Invalid instrument sample.");
            Samples = (float[])samples.Clone();
            foreach (float value in Samples)
                if (!MusicTrack.Finite(value) || Math.Abs(value) > 1) throw new ArgumentException("Invalid PCM sample.");
            SampleRate = sampleRate; RootKey = rootKey;
            Noise = noise;
        }
    }

    public sealed class MusicPart
    {
        public readonly MusicLayer Layer;
        public readonly MusicInstrument Instrument;
        public readonly double Gain;
        public readonly string InstrumentId;
        public readonly double Pan;
        /// <summary>Notes per part (24 bytes each in memory).</summary>
        public const int MaxNotes = 1000000;
        /// <summary>Pinned playback version for this instrument; 0 follows the cue.</summary>
        public readonly int PlaybackVersion;
        /// <summary>
        /// Legato glide time in seconds (0 = off, at most <see cref="MaxGlideSeconds"/>). Playback V2+: a note that starts while an
        /// earlier note of this part is still held takes over that voice and slides to the new pitch (808 slides). V1 ignores it.
        /// </summary>
        public readonly double GlideSeconds;
        public const double MaxGlideSeconds = 2;
        /// <summary>Playback V2+ track drive: gain in dB into <see cref="DriveMode"/> on each voice, before its envelope (0 = off). V1 ignores it.</summary>
        public readonly double DriveDb;
        public readonly MusicDriveMode DriveMode;
        internal readonly double DriveGain;
        /// <summary>Playback V2+ track effect chain (same stages as the master mixer), or null. V1 ignores it.</summary>
        public readonly MusicMixerSnapshot Effects;
        internal readonly MusicNote[] Notes;
        public int NoteCount => Notes.Length;
        public MusicNote GetNote(int index) => Notes[index];

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, params MusicNote[] notes)
            : this(layer, instrument, gain, instrument == null ? "" : instrument.Samples == null ? instrument.Voice.ToString() : "", notes) { }

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, string instrumentId, params MusicNote[] notes)
            : this(layer, instrument, gain, instrumentId, notes, 0) { }

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, string instrumentId, MusicNote[] notes, double pan, int playbackVersion = 0, double glideSeconds = 0,
            double driveDb = 0, MusicDriveMode driveMode = MusicDriveMode.Soft, MusicMixerSnapshot effects = null)
        {
            Effects = effects;
            if (!MusicTrack.Finite(driveDb) || driveDb < 0 || driveDb > MusicDrive.MaxDriveDb || !MusicDrive.IsDefined(driveMode)) throw new ArgumentException("Invalid drive.");
            DriveDb = driveDb; DriveMode = driveMode; DriveGain = Math.Pow(10, driveDb / 20);
            if (!MusicTrack.Finite(pan) || pan < -1 || pan > 1) throw new ArgumentException("Invalid pan.");
            if (!MusicTrack.Finite(glideSeconds) || glideSeconds < 0 || glideSeconds > MaxGlideSeconds) throw new ArgumentException("Invalid glide time.");
            if (playbackVersion != 0) MusicPlaybackVersion.Require(playbackVersion, "Music part " + instrumentId);
            Pan = pan; PlaybackVersion = playbackVersion; GlideSeconds = glideSeconds;
            if (instrument == null || notes == null || notes.Length > MaxNotes || !MusicTrack.Finite(gain) || gain < 0 || gain > 1 ||
                !Enum.IsDefined(typeof(MusicLayer), layer)) throw new ArgumentException("Invalid music part.");
            Layer = layer; Instrument = instrument; Gain = gain; Notes = (MusicNote[])notes.Clone();
            InstrumentId = instrumentId ?? "";
            foreach (var note in Notes)
                if (!MusicTrack.Finite(note.Beat) || note.Beat < 0 || !MusicTrack.Finite(note.Duration) || note.Duration <= 0 ||
                    note.Key < 0 || note.Key > 127 || !MusicTrack.Finite(note.Velocity) || note.Velocity < 0 || note.Velocity > 1)
                    throw new ArgumentException("Invalid note.");
            Array.Sort(Notes, (a, b) => a.Beat.CompareTo(b.Beat));
        }
    }

    /// <summary>A looping, authored cue. Tempo is a target, pitch is independent of the transport.</summary>
    public sealed class MusicCue
    {
        public readonly string Name;
        public readonly double Bpm, LoopBeats;
        public readonly int BeatsPerBar;
        /// <summary>
        /// Longest loop in beats (about 27.8 hours even at MaxBpm). Harmony is stored per note run, not per beat, so length costs
        /// no memory; beat clocks stay sub-microsecond precise at this size.
        /// </summary>
        public const double MaxLoopBeats = 100000000;
        /// <summary>Parts plus additive noise regions per cue.</summary>
        public const int MaxParts = 32;
        internal readonly MusicPart[] Parts;
        internal readonly MusicHarmony.Map Harmony;
        public readonly bool HasDrums;
        public int PartCount => Parts.Length;
        public MusicPart GetPart(int index) => Parts[index];
        internal readonly MusicMixEffect[] MixEffects;
        public readonly MusicTempoMap Tempo;
        public readonly MusicMixerSnapshot Mixer;
        /// <summary>Resolved playback version (never 0). Voices of unpinned parts and the master chain use it.</summary>
        public readonly int PlaybackVersion;
        /// <summary>Version a part's voices render with: its pin, else the cue's.</summary>
        public int PlaybackVersionOf(MusicPart part) => part.PlaybackVersion != 0 ? part.PlaybackVersion : PlaybackVersion;

        public MusicCue(string name, double bpm, int beatsPerBar, double loopBeats, params MusicPart[] parts)
            : this(name, bpm, beatsPerBar, loopBeats, parts, Array.Empty<MusicMixEffect>()) { }

        /// <param name="playbackVersion">0 (default) = V1, the behavior before versioning; code-built cues stay stable across updates.</param>
        public MusicCue(string name, double bpm, int beatsPerBar, double loopBeats, MusicPart[] parts, MusicMixEffect[] effects, MusicTempoChange[] tempoChanges = null, MusicMixerSnapshot mixer = null, int playbackVersion = 0)
        {
            if (string.IsNullOrWhiteSpace(name) || !MusicTrack.Finite(bpm) || bpm < MusicTempoMap.MinBpm || bpm > MusicTempoMap.MaxBpm ||
                beatsPerBar < 1 || beatsPerBar > 12 || !MusicTrack.Finite(loopBeats) || loopBeats < beatsPerBar ||
                loopBeats > MaxLoopBeats || parts == null || parts.Length > MaxParts)
                throw new ArgumentException("Invalid music cue.");
            PlaybackVersion = MusicPlaybackVersion.Require(playbackVersion, "Music cue " + name);
            Name = name; Bpm = bpm; BeatsPerBar = beatsPerBar; LoopBeats = loopBeats;
            Parts = (MusicPart[])parts.Clone();
            MixEffects = (MusicMixEffect[])effects.Clone();
            Tempo = new MusicTempoMap(bpm, loopBeats, tempoChanges);
            Mixer = mixer ?? new MusicMixerSettings().Compile();
            foreach (var part in Parts)
            {
                if (part == null) throw new ArgumentException("Null music part.");
                foreach (var note in part.Notes)
                    if (note.Beat >= loopBeats) throw new ArgumentException("Note starts outside loop.");
            }
            Harmony = MusicHarmony.Analyze(this);
            foreach (var part in Parts)
                if (part.Layer == MusicLayer.Drums && part.Gain > 0)
                    foreach (var note in part.Notes) if (note.Velocity > 0) { HasDrums = true; break; }
        }

        public static MusicCue FromScore(MusicScore score, MusicMood mood, string name, double bpm)
        {
            var tracks = score.Sections[(int)mood];
            var parts = new MusicPart[tracks.Length];
            // The demo has a four-bar harmonic cycle. Expand short repeating tracks over it.
            const int loopBeats = 16;
            for (int i = 0; i < tracks.Length; i++)
            {
                var track = tracks[i];
                var notes = new System.Collections.Generic.List<MusicNote>();
                for (int step = 0; step * track.StepBeats < loopBeats; step++)
                {
                    int key = track.Notes[step % track.Notes.Length];
                    if (key >= 0) notes.Add(new MusicNote(step * track.StepBeats, track.StepBeats * .85, key));
                }
                var layer = track.Voice == MusicVoice.Bell ? MusicLayer.Melody :
                    track.Voice == MusicVoice.Bass ? MusicLayer.Bass :
                    track.Voice == MusicVoice.Kick ? MusicLayer.Drums : MusicLayer.Harmony;
                parts[i] = new MusicPart(layer, new MusicInstrument(track.Voice), track.Gain, notes.ToArray());
            }
            return new MusicCue(name, bpm, 4, loopBeats, parts);
        }
    }
}
