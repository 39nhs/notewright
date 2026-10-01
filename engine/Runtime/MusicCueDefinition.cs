using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    [CreateAssetMenu(menuName = "Graze/Audio/Music Cue")]
    public sealed class MusicCueDefinition : ScriptableObject
    {
        [Serializable]
        public sealed class Part
        {
            public string Name;
            [Tooltip("Same stable ID across cues enables instrument-matched transition notes. Defaults to Name.")]
            public string InstrumentId;
            public MusicLayer Layer;
            public MusicVoice FallbackVoice = MusicVoice.Bell;
            [Range(0, 1)] public float Gain = .12f;
            [Range(-1, 1)] public float Pan;
            public bool Mute, Solo;
            public AudioClip Sample;
            [Range(0, 127)] public int SampleRootKey = 60;
            public float AttackSeconds = .008f;
            public float ReleaseSeconds = .12f;
            public MusicNote[] Notes = Array.Empty<MusicNote>();
            public int MidiChannel, MidiProgram;
            [Tooltip("0 = follow the song. Pin this instrument to an older playback version when upgrading the song changes it.")]
            public int PlaybackVersion;
            [Tooltip("Playback V2+: seconds a legato note (starting while the previous one is held) slides from the previous pitch, like an 808 slide. 0 = off.")]
            [Range(0, 2)] public float GlideSeconds;
            [Tooltip("Playback V2+: gain in dB into the track's waveshaper (each voice, before its envelope). 0 = off.")]
            [Range(0, 48)] public float DriveDb;
            public MusicDriveMode DriveMode = MusicDriveMode.Soft;
            [Tooltip("Playback V2+: give this track its own effect chain (the same stages as the master mixer, applied to this track only).")]
            public bool TrackEffects;
            public MusicMixerSettings Effects = new MusicMixerSettings();
        }

        [Tooltip("Sound-rendering version this song was approved with. 0 = recorded before versioning (plays as V1). Upgrading can change the sound.")]
        public int PlaybackVersion;

        public double Bpm = 120;
        public MusicTempoChange[] TempoChanges = Array.Empty<MusicTempoChange>();
        public int BeatsPerBar = 4;
        public double LoopBeats = 16;
        [TextArea] public string Source;
        [TextArea] public string ImportNotes;
        public Part[] Parts = Array.Empty<Part>();
        public VirtualInstrumentBank InstrumentBank;
        public MusicNoiseRegion[] NoiseRegions = Array.Empty<MusicNoiseRegion>();
        public MusicMixerSettings Mixer = new MusicMixerSettings();
        [Tooltip("Visual reference only: these notes never play.")]
        public MusicNote[] ReferenceMelody = Array.Empty<MusicNote>();
        public string ReferenceSource;

        /// <summary>Resolved playback version a track renders with: its pin, else the song's.</summary>
        public int PlaybackVersionOfPart(int index) =>
            Parts[index] != null && Parts[index].PlaybackVersion != 0 ? Parts[index].PlaybackVersion : MusicPlaybackVersion.Resolve(PlaybackVersion);

        /// <summary>Call once on the main thread before playback; copies all PCM and notes.</summary>
        public MusicCue Compile() => Compile(PlaybackVersion);

        /// <summary>Compiles with another song-level playback version (A/B audition) without changing the asset. Track pins still apply.</summary>
        public MusicCue Compile(int playbackVersion)
        {
            int version = MusicPlaybackVersion.Require(playbackVersion, name);
            var parts = new MusicPart[Parts.Length];
            bool solo = Array.Exists(Parts, p => p != null && p.Solo && !p.Mute);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = Parts[i];
                string id = string.IsNullOrWhiteSpace(p.InstrumentId) ? p.Name : p.InstrumentId;
                var binding = InstrumentBank != null ? InstrumentBank.Find(id) : null;
                var sample = binding != null ? binding.Sample : p.Sample;
                int root = binding != null ? binding.RootKey : p.SampleRootKey;
                MusicInstrument instrument;
                if (sample != null)
                {
                    if (sample.loadType != AudioClipLoadType.DecompressOnLoad)
                        throw new InvalidOperationException(p.Name + ": sample must use Decompress On Load.");
                    if (sample.loadState != AudioDataLoadState.Loaded)
                        throw new InvalidOperationException(p.Name + ": preload the sample before compiling the cue.");
                    var pcm = new float[sample.samples * sample.channels];
                    if (!sample.GetData(pcm, 0)) throw new InvalidOperationException("Cannot read sample: " + sample.name);
                    var mono = new float[sample.samples];
                    for (int frame = 0; frame < mono.Length; frame++)
                        for (int channel = 0; channel < sample.channels; channel++) mono[frame] += pcm[frame * sample.channels + channel] / sample.channels;
                    instrument = new MusicInstrument(mono, sample.frequency, root, p.AttackSeconds, p.ReleaseSeconds);
                }
                else instrument = new MusicInstrument(p.FallbackVoice, p.AttackSeconds, p.ReleaseSeconds);
                parts[i] = new MusicPart(p.Layer, instrument, p.Mute || (solo && !p.Solo) ? 0 : p.Gain, string.IsNullOrWhiteSpace(p.InstrumentId) ? p.Name : p.InstrumentId, p.Notes, p.Pan, p.PlaybackVersion, p.GlideSeconds, p.DriveDb, p.DriveMode,
                    p.TrackEffects ? (p.Effects ?? new MusicMixerSettings()).Compile() : null);
            }
            var compiled = new System.Collections.Generic.List<MusicPart>(parts);
            var filters = new System.Collections.Generic.List<MusicMixEffect>();
            foreach (var region in NoiseRegions)
            {
                if (region == null) throw new InvalidOperationException("Null noise region.");
                if (region.Enabled && region.MasterFilter) filters.Add(new MusicMixEffect(region, LoopBeats));
                else if (region.Enabled) compiled.Add(region.Compile(LoopBeats));
            }
            if (compiled.Count > MusicCue.MaxParts) throw new InvalidOperationException($"Instruments plus enabled noise regions must not exceed {MusicCue.MaxParts}.");
            return new MusicCue(name, Bpm, BeatsPerBar, LoopBeats, compiled.ToArray(), filters.ToArray(), TempoChanges, (Mixer ?? new MusicMixerSettings()).Compile(), version);
        }
    }
}
