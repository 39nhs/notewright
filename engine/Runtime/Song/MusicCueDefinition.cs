using System;
using System.Linq;
using UnityEngine;

namespace Graze.Presentation.Audio
{
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
            [Tooltip("Playback V2+ slice sampler: start times (seconds, ascending) of slices in Sample. Key SampleRootKey + i plays slice i at its original pitch until the next slice. Empty = normal pitched sample.")]
            public float[] SliceSeconds = Array.Empty<float>();
            [Tooltip("Playback V2+ velocity layers and round robin: more recordings of the same note as Sample (same root key). A note uses the takes of the highest Min Velocity it reaches (Sample counts as Min Velocity 0); takes with equal Min Velocity alternate.")]
            public SampleVariant[] Variants = Array.Empty<SampleVariant>();
            [Tooltip("Playback V3+ automation: VolumeDb (dB on top of Gain, -80 = silent) and Pan (-1..1, replaces Pan) over the cue's beats. One lane per target.")]
            public MusicAutomationLane[] Automation = Array.Empty<MusicAutomationLane>();
            [Tooltip("Playback V3+ routing: mix bus this track feeds instead of the master (bus name; empty = master).")]
            public string Output = "";
            [Tooltip("Playback V3+ sends: copies of this track (after its effects and level) into mix buses, e.g. a shared reverb.")]
            public MusicSendDefinition[] Sends = Array.Empty<MusicSendDefinition>();
            [Tooltip("Playback V3+ time stretch: BPM the sample was recorded at (0 = off). The sample then follows the song tempo and keeps its pitch; notes set the pitch (slices keep theirs).")]
            public float StretchBpm;
            [Tooltip("Playback V3+ time stretch grain length in ms (20–200). Shorter suits drums, longer suits sustained sounds.")]
            [Range(20, 200)] public float StretchGrainMs = 60;
            [Tooltip("Playback V3+ built-in synth. When enabled it replaces Sample / Fallback Voice in V3; V1/V2 play the track as before.")]
            public MusicSynthSettings Synth = new MusicSynthSettings();
            [Tooltip("Placements of the song's patterns on this track: each plays its pattern's notes at Start Beat (Repeats times, transposed) together with Notes.")]
            public MusicPatternPlacement[] Placements = Array.Empty<MusicPatternPlacement>();
            [Tooltip("Playback V3+ sustain loop of Sample, in seconds: a held note repeats Loop Start..Loop End instead of stopping at the end of the recording (End 0 = off).")]
            public float LoopStartSeconds;
            public float LoopEndSeconds;
            [Tooltip("Equal-power crossfade at the loop jump, seconds (limited to the time before Loop Start and half the loop).")]
            [Range(0, 1)] public float LoopCrossfadeSeconds = .01f;
            [Tooltip("Playback V3+ multi-zone sampler: recordings for key ranges (and velocity layers) of one instrument. A key outside every zone uses the nearest. V1/V2 play Sample only.")]
            public SampleZone[] Zones = Array.Empty<SampleZone>();
            [Tooltip("Playback V3+ modulators: LFOs and envelope followers (another track's level) moving this track's automation targets.")]
            public MusicModulator[] Modulators = Array.Empty<MusicModulator>();
            [Tooltip("This track plays straight even when the song swings.")]
            public bool IgnoreSwing;
            [Tooltip("Playback V3+ playing techniques (palm mute, staccato…): sample start, gain and length per articulation id; zones can be per articulation too.")]
            public MusicArticulation[] Articulations = Array.Empty<MusicArticulation>();
            [Tooltip("Notes on these keys play nothing and switch the articulation of the later notes.")]
            public MusicKeySwitch[] KeySwitches = Array.Empty<MusicKeySwitch>();
            [Tooltip("Per-note vibrato rate (Hz).")]
            public float VibratoRateHz = (float)MusicPart.DefaultVibratoRateHz;
            [Tooltip("Playback V3+ sample voices: decay / sustain and a resonant filter with its own envelope (the built-in synth has its own).")]
            public MusicSampleShapeSettings SampleShape = new MusicSampleShapeSettings();
            [Tooltip("Playback V3+ audio regions: pieces of recordings placed on this track with offset, gain, fades, transposition and warp (beats-synced stretch).")]
            public MusicAudioRegion[] Regions = Array.Empty<MusicAudioRegion>();
        }

        [Serializable]
        public sealed class SampleZone
        {
            public AudioClip Sample;
            [Tooltip("MIDI key the sample was recorded at.")]
            [Range(0, 127)] public int RootKey = 60;
            [Tooltip("Lowest and highest keys that play this zone.")]
            [Range(0, 127)] public int LowKey;
            [Range(0, 127)] public int HighKey = 127;
            [Tooltip("Notes at least this loud use this zone (zones over the same keys with higher values are velocity layers).")]
            [Range(0, 1)] public float MinVelocity;
            [Range(-24, 24)] public float GainDb;
            [Tooltip("Playback V3+ sustain loop of this recording, seconds (End 0 = off); the track's Loop Crossfade applies.")]
            public float LoopStartSeconds, LoopEndSeconds;
            [Tooltip("Articulation this zone plays (0 = normal; notes of an articulation without zones use the normal ones).")]
            [Range(0, MusicArticulations.MaxId)] public int Articulation;
            [Tooltip("Seconds into the recording where its notes start.")]
            public float StartSeconds;
        }

        [Serializable]
        public sealed class SampleVariant
        {
            public AudioClip Sample;
            [Tooltip("Notes with at least this velocity use this layer. Equal values = round-robin takes of one layer; 0 = alternates with the track's Sample.")]
            [Range(0, 1)] public float MinVelocity;
            [Tooltip("Balances this take against the others (dB).")]
            [Range(-24, 24)] public float GainDb;
        }

        /// <summary>Mono PCM of a preloaded clip (main thread).</summary>
        static float[] Mono(AudioClip sample, string owner)
        {
            if (sample.loadType != AudioClipLoadType.DecompressOnLoad)
                throw new InvalidOperationException(owner + ": sample must use Decompress On Load.");
            if (sample.loadState != AudioDataLoadState.Loaded)
                throw new InvalidOperationException(owner + ": preload the sample before compiling the cue.");
            var pcm = new float[sample.samples * sample.channels];
            if (!sample.GetData(pcm, 0)) throw new InvalidOperationException("Cannot read sample: " + sample.name);
            var mono = new float[sample.samples];
            for (int frame = 0; frame < mono.Length; frame++)
                for (int channel = 0; channel < sample.channels; channel++) mono[frame] += pcm[frame * sample.channels + channel] / sample.channels;
            return mono;
        }

        [Tooltip("Sound-rendering version this song was approved with. 0 = recorded before versioning (plays as V1). Upgrading can change the sound.")]
        public int PlaybackVersion;

        public double Bpm = 120;
        [Tooltip("Song key for composition assistance, e.g. \"A minor\", \"F# dorian\" (authoring metadata; empty = unknown, never affects playback).")]
        public string Key = "";
        public MusicTempoChange[] TempoChanges = Array.Empty<MusicTempoChange>();
        public int BeatsPerBar = 4;
        [Tooltip("Time-signature changes (beat, numerator/denominator), each on a bar line of the meter before it. Transitions and layers follow this bar grid; the sound does not change.")]
        public MusicMeterChange[] MeterChanges = Array.Empty<MusicMeterChange>();
        [Tooltip("Key changes after the song Key (beat, key). Authoring and game data (current key queries); the sound does not change.")]
        public MusicKeyChange[] KeyChanges = Array.Empty<MusicKeyChange>();
        [Tooltip("Chord track: chord names or Roman numerals, each in force until the next. Compose assist can write chords / bass / arpeggios from it; games can query the current chord.")]
        public MusicChordMarker[] Chords = Array.Empty<MusicChordMarker>();
        [Tooltip("Swing: every second grid step is delayed by Swing × grid (0 = straight, 0.33 ≈ triplet feel). Applies to every track without Ignore Swing.")]
        [Range(0, (float)MusicGroove.MaxSwing)] public float Swing;
        [Tooltip("Swing grid in beats (0.5 = eighths, 0.25 = sixteenths).")]
        public float SwingGrid = .5f;
        [Tooltip("Loudness target in LUFS for builders and Match loudness (0 = none). The song.json builder and the headless renderer match it with the master limiter.")]
        public float TargetLufs;
        [Tooltip("Seed of the note variation draws (notes' Skip Chance): another seed, another set of left-out notes per pass.")]
        public int VariationSeed;
        [Tooltip("Reusable patterns (clips) that tracks place by name: editing a pattern changes every placement.")]
        public MusicPattern[] Patterns = Array.Empty<MusicPattern>();
        [Tooltip("Named sections (Intro, A, B, Chorus…): each starts at its beat and lasts until the next. Arrange tools and game logic use them; the sound does not change.")]
        public MusicSectionMarker[] Sections = Array.Empty<MusicSectionMarker>();
        public double LoopBeats = 16;
        [TextArea] public string Source;
        [TextArea] public string ImportNotes;
        public Part[] Parts = Array.Empty<Part>();
        public MusicNoiseRegion[] NoiseRegions = Array.Empty<MusicNoiseRegion>();
        public MusicMixerSettings Mixer = new MusicMixerSettings();
        [Tooltip("Playback V3+ master automation: VolumeDb only (dB on the final output, after the limiter and glitches; -80 = silent).")]
        public MusicAutomationLane[] MasterAutomation = Array.Empty<MusicAutomationLane>();
        [Tooltip("Playback V3+ mix buses: shared effect chains that tracks route or send into (group bus, shared reverb). Each feeds the master or another bus.")]
        public MusicBusDefinition[] Buses = Array.Empty<MusicBusDefinition>();
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
            MusicBus[] buses; System.Collections.Generic.Dictionary<string, int> busIndex;
            try { buses = MusicRouting.CompileBuses(Buses, out busIndex, LoopBeats); }
            catch (ArgumentException e) { throw new InvalidOperationException(e.Message); }
            bool solo = Array.Exists(Parts, p => p != null && p.Solo && !p.Mute);
            try { MusicPatterns.Validate(this); }
            catch (ArgumentException e) { throw new InvalidOperationException(name + ": " + e.Message); }
            for (int i = 0; i < parts.Length; i++)
            {
                var p = Parts[i];
                var sample = p.Sample;
                int root = p.SampleRootKey;
                MusicInstrument instrument;
                MusicSynthPatch synth;
                try { synth = p.Synth?.Compile(); }
                catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
                var zones = synth == null && p.Zones != null && p.Zones.Length > 0 ? CompileZones(p) : null;
                if (synth != null) instrument = new MusicInstrument(synth, p.FallbackVoice, p.AttackSeconds, p.ReleaseSeconds);
                else if (zones != null)
                {
                    if (p.SliceSeconds != null && p.SliceSeconds.Length > 0 || p.Variants != null && p.Variants.Length > 0)
                        throw new InvalidOperationException(p.Name + ": zones cannot be combined with slices or velocity-layer variants (use zone Min Velocity).");
                    // V1/V2 play the track's own sample; without one, the zone nearest middle C stands in.
                    MusicSampleZone main = zones.OrderBy(z => Math.Abs(z.RootKey - 60)).First();
                    float[] mainPcm = sample != null ? Mono(sample, p.Name) : main.Samples; int mainRate = sample != null ? sample.frequency : main.SampleRate, mainRoot = sample != null ? root : main.RootKey;
                    try { instrument = new MusicInstrument(mainPcm, mainRate, mainRoot, p.AttackSeconds, p.ReleaseSeconds, zones); }
                    catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
                }
                else if (sample != null)
                {
                    var mono = Mono(sample, p.Name);
                    double[] slices = p.SliceSeconds != null && p.SliceSeconds.Length > 0 ? Array.ConvertAll(p.SliceSeconds, x => (double)x) : null;
                    MusicSampleVariant[] variants = null;
                    if (p.Variants != null && p.Variants.Length > 0)
                    {
                        variants = new MusicSampleVariant[p.Variants.Length];
                        for (int v = 0; v < variants.Length; v++)
                        {
                            var variant = p.Variants[v];
                            if (variant == null || variant.Sample == null) throw new InvalidOperationException($"{p.Name}: variant {v + 1} has no sample.");
                            variants[v] = new MusicSampleVariant(Mono(variant.Sample, p.Name + " variant " + (v + 1)), variant.Sample.frequency, variant.MinVelocity,
                                (float)Math.Pow(10, variant.GainDb / 20));
                        }
                    }
                    try { instrument = new MusicInstrument(mono, sample.frequency, root, p.AttackSeconds, p.ReleaseSeconds, null, slices, variants); }
                    catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
                    if (p.LoopEndSeconds > 0)
                    {
                        if (slices != null || variants != null || p.StretchBpm > 0)
                            throw new InvalidOperationException(p.Name + ": a sustain loop cannot be combined with slices, velocity-layer variants or time stretch.");
                        try { instrument = instrument.WithSustainLoop(p.LoopStartSeconds, p.LoopEndSeconds, p.LoopCrossfadeSeconds); }
                        catch (ArgumentOutOfRangeException e) { throw new InvalidOperationException(p.Name + ": " + e.Message.Split('\n')[0]); }
                    }
                }
                else instrument = new MusicInstrument(p.FallbackVoice, p.AttackSeconds, p.ReleaseSeconds);
                int output; MusicSend[] sends;
                try { output = MusicRouting.CompileTrack(p.Output, p.Sends, busIndex, p.Name, out sends, LoopBeats); }
                catch (ArgumentException e) { throw new InvalidOperationException(e.Message); }
                parts[i] = new MusicPart(p.Layer, instrument, p.Mute || (solo && !p.Solo) ? 0 : p.Gain, string.IsNullOrWhiteSpace(p.InstrumentId) ? p.Name : p.InstrumentId, WithRegions(PlayedNotes(p), p, version, out var regionSegments), p.Pan, p.PlaybackVersion, p.GlideSeconds, p.DriveDb, p.DriveMode,
                    p.TrackEffects ? (p.Effects ?? new MusicMixerSettings()).Compile() : null, CompileAutomation(Modulated(p, out var followers), p.Name, false), output, sends,
                    p.StretchBpm, (p.StretchGrainMs > 0 ? p.StretchGrainMs : 60) / 1000.0);
                parts[i].Followers = followers;
                parts[i].Regions = regionSegments;
                try { parts[i].SampleShape = synth == null ? p.SampleShape?.Compile() : null; }
                catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
                try { parts[i].SetArticulations(p.Articulations, p.VibratoRateHz > 0 ? p.VibratoRateHz : MusicPart.DefaultVibratoRateHz); }
                catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
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
            try { new MusicMeter(Math.Max(1, BeatsPerBar), Math.Max(1e-9, LoopBeats), MeterChanges); }
            catch (ArgumentException e) when (BeatsPerBar >= 1 && LoopBeats > 0) { throw new InvalidOperationException(name + ": " + e.Message); }
            try { MusicSections.Of(this); }
            catch (ArgumentException e) when (LoopBeats > 0) { throw new InvalidOperationException(name + ": " + e.Message); }
            var cue = new MusicCue(name, Bpm, BeatsPerBar, LoopBeats, compiled.ToArray(), filters.ToArray(), TempoChanges, (Mixer ?? new MusicMixerSettings()).Compile(), version,
                CompileAutomation(MasterAutomation, "Master", true), buses, MeterChanges, Sections, HarmonyTrack());
            cue.VariationSeed = VariationSeed;
            return cue;
        }

        static MusicSampleZone[] CompileZones(Part p)
        {
            var zones = new MusicSampleZone[p.Zones.Length];
            for (int i = 0; i < zones.Length; i++)
            {
                var z = p.Zones[i];
                if (z == null || z.Sample == null) throw new InvalidOperationException($"{p.Name}: zone {i + 1} has no sample.");
                zones[i] = new MusicSampleZone(Mono(z.Sample, $"{p.Name} zone {i + 1}"), z.Sample.frequency, z.RootKey, z.LowKey, z.HighKey, z.MinVelocity, (float)Math.Pow(10, z.GainDb / 20),
                    z.LoopStartSeconds, z.LoopEndSeconds, p.LoopCrossfadeSeconds, z.Articulation, z.StartSeconds);
            }
            return zones;
        }

        MusicAutomationLane[] Modulated(Part p, out MusicFollower[] followers)
        {
            try { return MusicModulators.Bake(this, p, out followers); }
            catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
        }

        /// <summary>What a track plays: its notes and pattern placements, with the song's swing (unless the track ignores it).</summary>
        public MusicNote[] PerformedNotes(Part p)
        {
            try { return MusicGroove.Apply(MusicPatterns.PlayedNotes(this, p), p.IgnoreSwing ? 0 : Swing, SwingGrid); }
            catch (ArgumentException e) { throw new InvalidOperationException(name + ": " + e.Message); }
        }

        /// <summary>What the engine plays for a track: <see cref="PerformedNotes"/> with its key switches resolved (switch notes removed), validated.</summary>
        public MusicNote[] PlayedNotes(Part p)
        {
            try
            {
                var notes = MusicArticulations.ResolveKeySwitches(PerformedNotes(p), p.KeySwitches);
                MusicArticulations.Validate(notes);
                return notes;
            }
            catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
        }

        // Playback V3+: the track's audio regions as hidden notes (one per warp segment) and the segments the engine plays for them.
        MusicNote[] WithRegions(MusicNote[] notes, Part p, int version, out MusicRegionSegment[] segments)
        {
            segments = null;
            if (p.Regions == null || p.Regions.Length == 0 || version < MusicPlaybackVersion.V3 || (p.PlaybackVersion > 0 && p.PlaybackVersion < MusicPlaybackVersion.V3)) return notes;
            try { MusicAudioRegions.Validate(p, LoopBeats); }
            catch (ArgumentException e) { throw new InvalidOperationException(p.Name + ": " + e.Message); }
            var cache = new System.Collections.Generic.Dictionary<AudioClip, MusicInstrument>();
            segments = MusicAudioRegions.Compile(p, LoopBeats, clip =>
            {
                if (!cache.TryGetValue(clip, out var instrument)) cache[clip] = instrument = new MusicInstrument(Mono(clip, p.Name + " region"), clip.frequency, 60, .0005, .01);
                return instrument;
            }, out var hidden);
            return hidden.Length == 0 ? notes : notes.Concat(hidden).ToArray();
        }

        /// <summary>The song's key timeline and chord track (validated).</summary>
        public MusicHarmonyTrack HarmonyTrack()
        {
            try { return new MusicHarmonyTrack(Math.Max(1e-9, LoopBeats), Key, KeyChanges, Chords); }
            catch (ArgumentException e) { throw new InvalidOperationException(name + ": " + e.Message); }
        }

        MusicAutomation[] CompileAutomation(MusicAutomationLane[] lanes, string owner, bool master)
        {
            try { return MusicAutomation.Compile(lanes, LoopBeats, owner, master); }
            catch (ArgumentException e) { throw new InvalidOperationException(e.Message); }
        }
    }
}
