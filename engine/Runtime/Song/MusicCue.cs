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
        /// <summary>
        /// Per-note pitch expression (playback V3+; older versions ignore it): the pitch moves from BendFrom to BendTo semitones between
        /// BendStart and BendEnd beats after the note starts (smooth; End ≤ Start = a jump at Start). Bend up: 0 → 2; release: 2 → 0; dive: 0 → −12.
        /// </summary>
        public float BendFrom, BendTo, BendStart, BendEnd;
        /// <summary>Vibrato depth in semitones (playback V3+), at the track's Vibrato Rate, fading in over its first 0.15 s.</summary>
        public float Vibrato;
        /// <summary>Playing technique (0 = normal): picks the track's articulation settings and zones (playback V3+). Key switches set it too.</summary>
        public int Articulation;
        /// <summary>
        /// Variation (playback V3+; older versions play every note): the chance 0..1 that the note is left out on a pass of the loop
        /// (decided again each pass, the same way for the same song and seed), and RepeatEvery / RepeatOffset = play only on passes
        /// where pass % RepeatEvery == RepeatOffset (0 or 1 = every pass; pass 0 is the first time through).
        /// </summary>
        public float SkipChance;
        public int RepeatEvery, RepeatOffset;
        public bool HasExpression => BendFrom != 0 || BendTo != 0 || Vibrato != 0;
        public bool HasVariation => SkipChance > 0 || RepeatEvery > 1;
        public MusicNote(double beat, double duration, int key, float velocity = 1)
        { Beat = beat; Duration = duration; Key = key; Velocity = velocity; BendFrom = BendTo = BendStart = BendEnd = Vibrato = 0; Articulation = 0; SkipChance = 0; RepeatEvery = RepeatOffset = 0; }
    }

    /// <summary>
    /// Another recording of an instrument's note (playback V2+): a velocity layer (a note with velocity at or above MinVelocity uses
    /// the takes of the highest such layer) or, with the same MinVelocity as other takes, a round-robin alternative. It plays with the
    /// instrument's root key, attack and release; Gain (linear) balances it against the other takes.
    /// </summary>
    public readonly struct MusicSampleVariant
    {
        public readonly float[] Samples;
        public readonly int SampleRate;
        public readonly float MinVelocity, Gain;
        public MusicSampleVariant(float[] samples, int sampleRate, float minVelocity, float gain = 1)
        { Samples = samples; SampleRate = sampleRate; MinVelocity = minVelocity; Gain = gain; }
    }

    /// <summary>
    /// One key zone of a multi-zone sampler (playback V3+): a recording of <see cref="RootKey"/> used for keys LowKey..HighKey and
    /// velocities from MinVelocity up (zones over the same keys with higher MinVelocity are velocity layers). Gain is linear.
    /// </summary>
    public readonly struct MusicSampleZone
    {
        public readonly float[] Samples;
        public readonly int SampleRate, RootKey, LowKey, HighKey;
        public readonly float MinVelocity, Gain;
        /// <summary>Sustain loop of this recording in seconds (end 0 = none; playback V3+).</summary>
        public readonly double LoopStart, LoopEnd, LoopCrossfade;
        /// <summary>Articulation this zone plays (0 = normal) and where in the recording its notes start, seconds.</summary>
        public readonly int Articulation;
        public readonly double StartSeconds;
        public MusicSampleZone(float[] samples, int sampleRate, int rootKey, int lowKey, int highKey, float minVelocity = 0, float gain = 1,
            double loopStart = 0, double loopEnd = 0, double loopCrossfade = .01, int articulation = 0, double startSeconds = 0)
        {
            Samples = samples; SampleRate = sampleRate; RootKey = rootKey; LowKey = lowKey; HighKey = highKey; MinVelocity = minVelocity; Gain = gain;
            LoopStart = loopStart; LoopEnd = loopEnd; LoopCrossfade = loopCrossfade; Articulation = articulation; StartSeconds = startSeconds;
        }
    }

    /// <summary>Preloaded mono sample or oscillator. No Unity objects on the audio thread.</summary>
    public sealed class MusicInstrument
    {
        internal readonly float[] Samples;
        public readonly int SampleRate, RootKey;
        public readonly MusicVoice Voice;
        public readonly double Attack, Release;
        public readonly NoisePlayback Noise;
        /// <summary>Slice sampler (playback V2+): first frame of each slice, ascending; key RootKey + i plays slice i at its original
        /// pitch until the next slice starts. Null = a normal pitched sample.</summary>
        internal readonly int[] SliceStarts;
        public int SliceCount => SliceStarts?.Length ?? 0;
        public const int MaxSlices = 128;
        /// <summary>
        /// Velocity layers and round robin (playback V2+): every recording of this instrument, this one first, grouped by layer in
        /// ascending MinVelocity (takes of a layer in the given order). Layer l holds Takes[LayerStart[l]] to Takes[LayerStart[l + 1] - 1].
        /// Null = this sample only.
        /// </summary>
        internal readonly MusicInstrument[] Takes;
        internal readonly float[] TakeGain, LayerMinVelocity;
        internal readonly int[] LayerStart;
        public int VariantCount => Takes == null ? 0 : Takes.Length - 1;
        public int VelocityLayerCount => LayerMinVelocity?.Length ?? 1;
        public const int MaxVariants = 64, MaxVelocityLayers = 16;
        public const float MaxVariantGain = 16;
        /// <summary>Built-in synth (playback V3+): V3 voices render this patch; older versions play <see cref="Voice"/> with Attack/Release. Null = none.</summary>
        public readonly MusicSynthPatch Synth;
        /// <summary>Multi-zone sampler (playback V3+): each zone is an instrument of its own recording; null = this sample for every key.</summary>
        internal readonly MusicInstrument[] Zones;
        internal readonly int[] ZoneLow, ZoneHigh;
        internal readonly float[] ZoneMinVelocity, ZoneGain;
        internal readonly int[] ZoneArticulation;
        /// <summary>Frame where notes start in this recording (a zone's Start Seconds), 0 = the beginning.</summary>
        internal int StartFrame;
        public int ZoneCount => Zones?.Length ?? 0;
        public const int MaxZones = 128;
        /// <summary>Sustain loop (playback V3+): frames [LoopStart, LoopEnd) repeat while the voice sounds; LoopSamples has the crossfade baked in.</summary>
        internal int LoopStart, LoopEnd;
        internal float[] LoopSamples;
        public bool HasSustainLoop => LoopEnd > 0;
        public double LoopStartSeconds => LoopStart / (double)SampleRate;
        public double LoopEndSeconds => LoopEnd / (double)SampleRate;

        /// <summary>
        /// This sample instrument with a sustain loop over [startSeconds, endSeconds) and an equal-power crossfade (playback V3+; V1/V2 play
        /// the sample as before). Not for noise, slices, variants or zones (give each zone its own loop).
        /// </summary>
        public MusicInstrument WithSustainLoop(double startSeconds, double endSeconds, double crossfadeSeconds = .01)
        {
            if (Samples == null || Noise != null || SliceStarts != null || Takes != null || Zones != null || Synth != null)
                throw new InvalidOperationException("A sustain loop needs a plain sample instrument (no noise, slices, variants, zones or synth).");
            if (!MusicTrack.Finite(startSeconds) || !MusicTrack.Finite(endSeconds) || !MusicTrack.Finite(crossfadeSeconds) || crossfadeSeconds < 0 || crossfadeSeconds > MusicSampleLoop.MaxCrossfadeSeconds)
                throw new ArgumentOutOfRangeException(nameof(startSeconds));
            int start = (int)Math.Round(startSeconds * SampleRate), end = (int)Math.Round(endSeconds * SampleRate);
            if (start < 0 || end > Samples.Length - 1 || end - start < 2)
                throw new ArgumentOutOfRangeException(nameof(startSeconds), $"Loop {startSeconds:0.###}–{endSeconds:0.###} s must lie inside the {(Samples.Length - 1) / (double)SampleRate:0.###} s sample.");
            var copy = (MusicInstrument)MemberwiseClone();
            copy.LoopStart = start; copy.LoopEnd = end;
            copy.LoopSamples = MusicSampleLoop.Bake(Samples, start, end, (int)Math.Round(crossfadeSeconds * SampleRate), out _);
            return copy;
        }

        /// <summary>
        /// A multi-zone sampler: playback V3+ picks, per note, the zone covering its key with the highest MinVelocity at or under the
        /// note's velocity (a key outside every zone uses the nearest zone); V1/V2 play this instrument's own sample for every key.
        /// Not with noise, slices or velocity-layer variants.
        /// </summary>
        public MusicInstrument(float[] samples, int sampleRate, int rootKey, double attack, double release, MusicSampleZone[] zones)
            : this(samples, sampleRate, rootKey, attack, release)
        {
            if (zones == null || zones.Length == 0) return;
            if (zones.Length > MaxZones) throw new ArgumentException($"At most {MaxZones} zones.");
            Zones = new MusicInstrument[zones.Length]; ZoneLow = new int[zones.Length]; ZoneHigh = new int[zones.Length];
            ZoneMinVelocity = new float[zones.Length]; ZoneGain = new float[zones.Length]; ZoneArticulation = new int[zones.Length];
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                if (z.LowKey < 0 || z.HighKey > 127 || z.LowKey > z.HighKey || !MusicTrack.Finite(z.MinVelocity) || z.MinVelocity < 0 || z.MinVelocity > 1 ||
                    !MusicTrack.Finite(z.Gain) || z.Gain < 0 || z.Gain > MaxVariantGain || z.Articulation < 0 || z.Articulation > MusicArticulations.MaxId ||
                    !MusicTrack.Finite(z.StartSeconds) || z.StartSeconds < 0)
                    throw new ArgumentException($"Zone {i + 1}: keys 0–127 with low ≤ high, minimum velocity 0–1, gain 0–{MaxVariantGain}, articulation 0–{MusicArticulations.MaxId}, start ≥ 0.");
                try
                {
                    Zones[i] = new MusicInstrument(z.Samples, z.SampleRate, z.RootKey, attack, release);
                    if (z.LoopEnd > 0) Zones[i] = Zones[i].WithSustainLoop(z.LoopStart, z.LoopEnd, z.LoopCrossfade);
                    int startFrame = (int)Math.Round(z.StartSeconds * z.SampleRate);
                    if (startFrame >= z.Samples.Length - 1) throw new ArgumentException($"start {z.StartSeconds:0.###} s is past the end of the recording.");
                    Zones[i].StartFrame = startFrame;
                }
                catch (ArgumentException e) { throw new ArgumentException($"Zone {i + 1}: {e.Message}"); }
                ZoneLow[i] = z.LowKey; ZoneHigh[i] = z.HighKey; ZoneMinVelocity[i] = z.MinVelocity; ZoneGain[i] = z.Gain; ZoneArticulation[i] = z.Articulation;
            }
        }

        /// <summary>
        /// The zone a note plays (index), or -1 without zones: among the zones of the note's articulation (else the normal ones, else all),
        /// the covering zone with the highest minimum velocity reached, else the nearest by key.
        /// </summary>
        public int ZoneFor(int key, float velocity, int articulation = 0)
        {
            if (Zones == null) return -1;
            int set = -1; // articulation the candidate zones share (-1 = every zone)
            for (int i = 0; i < Zones.Length; i++) if (ZoneArticulation[i] == articulation) { set = articulation; break; }
            if (set < 0) for (int i = 0; i < Zones.Length; i++) if (ZoneArticulation[i] == 0) { set = 0; break; }
            // The zones at the smallest key distance (0 = covering the key), then by velocity layer.
            int distance = int.MaxValue;
            for (int i = 0; i < Zones.Length; i++) if (set < 0 || ZoneArticulation[i] == set) distance = Math.Min(distance, key < ZoneLow[i] ? ZoneLow[i] - key : key > ZoneHigh[i] ? key - ZoneHigh[i] : 0);
            int best = -1, softest = -1;
            for (int i = 0; i < Zones.Length; i++)
            {
                if (set >= 0 && ZoneArticulation[i] != set) continue;
                int d = key < ZoneLow[i] ? ZoneLow[i] - key : key > ZoneHigh[i] ? key - ZoneHigh[i] : 0;
                if (d != distance) continue;
                if (softest < 0 || ZoneMinVelocity[i] < ZoneMinVelocity[softest]) softest = i;
                if (velocity >= ZoneMinVelocity[i] - 1e-6f && (best < 0 || ZoneMinVelocity[i] > ZoneMinVelocity[best])) best = i;
            }
            return best >= 0 ? best : softest; // too soft for every layer: the softest one
        }

        public MusicInstrument(MusicVoice voice, double attack = .008, double release = .12)
        {
            if (!Enum.IsDefined(typeof(MusicVoice), voice) || !MusicTrack.Finite(attack) || attack <= 0 ||
                !MusicTrack.Finite(release) || release <= 0) throw new ArgumentException("Invalid instrument.");
            Voice = voice; Attack = attack; Release = release;
        }

        /// <summary>A synth instrument: playback V3+ renders <paramref name="synth"/>; V1/V2 play the oscillator <paramref name="fallback"/>.</summary>
        public MusicInstrument(MusicSynthPatch synth, MusicVoice fallback = MusicVoice.Bell, double attack = .008, double release = .12)
            : this(fallback, attack, release)
        {
            Synth = synth ?? throw new ArgumentNullException(nameof(synth));
        }

        public MusicInstrument(float[] samples, int sampleRate, int rootKey, double attack = .008, double release = .12, NoisePlayback noise = null)
            : this(samples, sampleRate, rootKey, attack, release, noise, null) { }

        /// <param name="sliceSeconds">Slice start times in seconds (ascending, inside the sample, at most 128); null or empty = no slicing.</param>
        public MusicInstrument(float[] samples, int sampleRate, int rootKey, double attack, double release, NoisePlayback noise, double[] sliceSeconds)
            : this(samples, sampleRate, rootKey, attack, release, noise, sliceSeconds, null) { }

        /// <param name="variants">Velocity layers / round-robin takes besides this sample (which is the first take of velocity 0);
        /// null or empty = none. Not with noise or slices; at most 64 variants in 16 layers.</param>
        public MusicInstrument(float[] samples, int sampleRate, int rootKey, double attack, double release, NoisePlayback noise, double[] sliceSeconds,
            MusicSampleVariant[] variants)
            : this(MusicVoice.Bell, attack, release)
        {
            if (samples == null || samples.Length < 2 || sampleRate < 8000 || sampleRate > 192000 || rootKey < 0 || rootKey > 127)
                throw new ArgumentException("Invalid instrument sample.");
            Samples = (float[])samples.Clone();
            foreach (float value in Samples)
                if (!MusicTrack.Finite(value) || Math.Abs(value) > 1) throw new ArgumentException("Invalid PCM sample.");
            SampleRate = sampleRate; RootKey = rootKey;
            Noise = noise;
            if (sliceSeconds != null && sliceSeconds.Length > 0)
            {
                if (noise != null || sliceSeconds.Length > MaxSlices) throw new ArgumentException("Invalid slices: at most 128, not on noise.");
                SliceStarts = new int[sliceSeconds.Length];
                for (int i = 0; i < sliceSeconds.Length; i++)
                {
                    double t = sliceSeconds[i];
                    int frame = MusicTrack.Finite(t) ? (int)Math.Round(t * sampleRate) : -1;
                    if (frame < 0 || frame >= Samples.Length - 1 || (i > 0 && frame <= SliceStarts[i - 1]))
                        throw new ArgumentException($"Slice {i + 1} at {t:0.###} s: slices must be ascending and inside the sample.");
                    SliceStarts[i] = frame;
                }
            }
            if (variants != null && variants.Length > 0)
            {
                if (noise != null || SliceStarts != null) throw new ArgumentException("Invalid variants: not on noise or a sliced sample.");
                if (variants.Length > MaxVariants) throw new ArgumentException($"Invalid variants: at most {MaxVariants}.");
                var order = new int[variants.Length + 1]; // -1 = this sample (velocity 0, gain 1)
                order[0] = -1;
                for (int i = 0; i < variants.Length; i++)
                {
                    var v = variants[i];
                    if (!MusicTrack.Finite(v.MinVelocity) || v.MinVelocity < 0 || v.MinVelocity > 1 || !MusicTrack.Finite(v.Gain) || v.Gain < 0 || v.Gain > MaxVariantGain)
                        throw new ArgumentException($"Variant {i + 1}: minimum velocity must be 0–1 and gain 0–{MaxVariantGain}.");
                    order[i + 1] = i;
                }
                float Min(int k) => k < 0 ? 0 : variants[k].MinVelocity;
                // Stable insertion sort by layer: equal velocities keep their order (this sample first).
                for (int i = 1; i < order.Length; i++)
                    for (int j = i; j > 0 && Min(order[j - 1]) > Min(order[j]); j--) { int t = order[j]; order[j] = order[j - 1]; order[j - 1] = t; }
                Takes = new MusicInstrument[order.Length]; TakeGain = new float[order.Length];
                var mins = new System.Collections.Generic.List<float>(); var starts = new System.Collections.Generic.List<int>();
                for (int i = 0; i < order.Length; i++)
                {
                    int k = order[i];
                    try { Takes[i] = k < 0 ? this : new MusicInstrument(variants[k].Samples, variants[k].SampleRate, rootKey, attack, release); }
                    catch (ArgumentException e) { throw new ArgumentException($"Variant {k + 1}: {e.Message}"); }
                    TakeGain[i] = k < 0 ? 1 : variants[k].Gain;
                    if (mins.Count == 0 || Min(k) != mins[mins.Count - 1]) { mins.Add(Min(k)); starts.Add(i); }
                }
                if (mins.Count > MaxVelocityLayers) throw new ArgumentException($"Invalid variants: at most {MaxVelocityLayers} velocity layers.");
                starts.Add(order.Length);
                LayerMinVelocity = mins.ToArray(); LayerStart = starts.ToArray();
            }
        }

        /// <summary>Velocity layer a note plays from: the highest layer whose minimum velocity it reaches (layer 0 starts at 0).</summary>
        internal int LayerOf(float velocity)
        {
            int layer = 0;
            while (layer + 1 < LayerMinVelocity.Length && velocity >= LayerMinVelocity[layer + 1]) layer++;
            return layer;
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
        /// <summary>Playback V3+ automation of this track's volume (dB on top of <see cref="Gain"/>) and pan, or null. Older versions ignore it.</summary>
        public readonly MusicAutomation VolumeAutomation, PanAutomation;
        /// <summary>Playback V3+ lanes indexed by <see cref="MusicAutomationTarget"/>, or null. Effect lanes drive this track's effect chain.</summary>
        internal readonly MusicAutomation[] Automation;
        /// <summary>Playback V3+ routing: index of the mix bus this track feeds instead of the master (-1 = master), and its sends (or null).</summary>
        public readonly int OutputBus;
        internal readonly MusicSend[] Sends;
        internal bool Routed => OutputBus >= 0 || Sends != null;
        /// <summary>
        /// Playback V3+ time stretch: the tempo (BPM) the sample was recorded at, or 0 = off. When set, the sample's time follows the song's
        /// beats (tempo map included) while its pitch follows the note (slices keep their recorded pitch), with overlapping grains of
        /// <see cref="StretchGrainSeconds"/>. Older versions ignore it.
        /// </summary>
        public readonly double StretchBpm, StretchGrainSeconds;
        public const double DefaultStretchGrainSeconds = .06, MinStretchGrainSeconds = .02, MaxStretchGrainSeconds = .2;
        internal readonly bool HasEffectAutomation;
        /// <summary>Playback V3+ expression lanes (null = none): pitch bend, synth modulation depth, synth brightness.</summary>
        public readonly MusicAutomation PitchAutomation, ModulationAutomation, BrightnessAutomation;
        public MusicAutomation GetAutomation(MusicAutomationTarget target) => Automation?[(int)target];
        internal readonly MusicNote[] Notes;
        /// <summary>Playback V3+ envelope followers on this part's lanes (set by MusicCueDefinition.Compile), or null.</summary>
        internal MusicFollower[] Followers;
        /// <summary>Playback V3+ articulation settings by id (null = none; set by MusicCueDefinition.Compile or <see cref="SetArticulations"/>).</summary>
        internal MusicArticulationSettings[] Articulations;
        /// <summary>Playback V3+ audio region segments (hidden notes with Articulation = −1 − index), or null.</summary>
        internal MusicRegionSegment[] Regions;
        /// <summary>Playback V3+ per-voice envelope and filter of this track's sample notes, or null (set by MusicCueDefinition.Compile).</summary>
        public MusicSampleShape SampleShape { get; internal set; }
        /// <summary>Per-note vibrato rate in Hz (playback V3+).</summary>
        public double VibratoRateHz { get; private set; } = DefaultVibratoRateHz;
        public const double DefaultVibratoRateHz = 5.5, MinVibratoRateHz = .1, MaxVibratoRateHz = 20;
        /// <summary>Sets this part's articulations and vibrato rate (before playback; throws ArgumentException on bad values).</summary>
        public void SetArticulations(MusicArticulation[] articulations, double vibratoRateHz = DefaultVibratoRateHz)
        {
            if (!MusicTrack.Finite(vibratoRateHz) || vibratoRateHz < MinVibratoRateHz || vibratoRateHz > MaxVibratoRateHz)
                throw new ArgumentException($"Vibrato rate {MinVibratoRateHz}–{MaxVibratoRateHz} Hz.");
            Articulations = MusicArticulations.Compile(articulations); VibratoRateHz = vibratoRateHz;
        }
        public int NoteCount => Notes.Length;
        public MusicNote GetNote(int index) => Notes[index];

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, params MusicNote[] notes)
            : this(layer, instrument, gain, instrument == null ? "" : instrument.Samples == null ? instrument.Voice.ToString() : "", notes) { }

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, string instrumentId, params MusicNote[] notes)
            : this(layer, instrument, gain, instrumentId, notes, 0) { }

        public MusicPart(MusicLayer layer, MusicInstrument instrument, double gain, string instrumentId, MusicNote[] notes, double pan, int playbackVersion = 0, double glideSeconds = 0,
            double driveDb = 0, MusicDriveMode driveMode = MusicDriveMode.Soft, MusicMixerSnapshot effects = null, MusicAutomation[] automation = null,
            int outputBus = -1, MusicSend[] sends = null, double stretchBpm = 0, double stretchGrainSeconds = DefaultStretchGrainSeconds)
        {
            Effects = effects;
            if (!MusicTrack.Finite(stretchBpm) || (stretchBpm != 0 && (stretchBpm < MusicTempoMap.MinBpm || stretchBpm > MusicTempoMap.MaxBpm)) ||
                !MusicTrack.Finite(stretchGrainSeconds) || stretchGrainSeconds < MinStretchGrainSeconds || stretchGrainSeconds > MaxStretchGrainSeconds)
                throw new ArgumentException("Invalid time stretch.");
            if (stretchBpm > 0 && instrument != null && instrument.Samples == null) throw new ArgumentException("Time stretch needs a sample instrument.");
            StretchBpm = stretchBpm; StretchGrainSeconds = stretchGrainSeconds;
            if (outputBus < -1) throw new ArgumentException("Invalid output bus.");
            OutputBus = outputBus; Sends = sends != null && sends.Length > 0 ? (MusicSend[])sends.Clone() : null;
            Automation = MusicAutomation.ByTarget(automation, effects, "Music part " + instrumentId, false);
            VolumeAutomation = GetAutomation(MusicAutomationTarget.VolumeDb); PanAutomation = GetAutomation(MusicAutomationTarget.Pan);
            for (int t = 0; Automation != null && t < MusicAutomation.TargetCount; t++) HasEffectAutomation |= Automation[t] != null && MusicAutomation.IsEffect((MusicAutomationTarget)t);
            PitchAutomation = GetAutomation(MusicAutomationTarget.PitchSemitones); ModulationAutomation = GetAutomation(MusicAutomationTarget.Modulation);
            BrightnessAutomation = GetAutomation(MusicAutomationTarget.Brightness);
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
        /// <summary>Bar grid: Beats Per Bar plus meter changes (transitions and layers align to it). Never affects in-cue sound.</summary>
        public readonly MusicMeter Meter;
        /// <summary>Named sections (Intro, A, Chorus…) for arrange tools and game logic. Never affects the sound.</summary>
        public readonly MusicSections Sections;
        /// <summary>Key timeline and chord track (authoring and game data). Never affects the sound.</summary>
        public readonly MusicHarmonyTrack HarmonyTrack;
        /// <summary>Seed of the note variation draws (<see cref="MusicNote.SkipChance"/>): another seed, another set of left-out notes.</summary>
        public int VariationSeed { get; internal set; }
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
        /// <summary>Playback V3+ master output volume automation (dB, after the limiter and glitches), or null.</summary>
        public readonly MusicAutomation MasterVolume;
        /// <summary>Playback V3+ master lanes indexed by <see cref="MusicAutomationTarget"/>, or null. Effect lanes drive the master mixer.</summary>
        internal readonly MusicAutomation[] MasterAutomation;
        public MusicAutomation GetMasterAutomation(MusicAutomationTarget target) => MasterAutomation?[(int)target];
        internal readonly bool HasPartAutomation;
        /// <summary>Playback V3+ mix buses in processing order (each feeds a later bus or the master). Empty when the song has none.</summary>
        internal readonly MusicBus[] Buses;
        public int BusCount => Buses.Length;
        // Playback V3+ sidechain: key part index for each part's chain, each bus and the master (-1 = none); parts that are keys.
        internal readonly int[] PartSidechain, BusSidechain;
        internal readonly int MasterSidechain;
        internal readonly bool[] SidechainKey;
        public MusicBus GetBus(int index) => Buses[index];

        public MusicCue(string name, double bpm, int beatsPerBar, double loopBeats, params MusicPart[] parts)
            : this(name, bpm, beatsPerBar, loopBeats, parts, Array.Empty<MusicMixEffect>()) { }

        /// <param name="playbackVersion">0 (default) = V1, the behavior before versioning; code-built cues stay stable across updates.</param>
        public MusicCue(string name, double bpm, int beatsPerBar, double loopBeats, MusicPart[] parts, MusicMixEffect[] effects, MusicTempoChange[] tempoChanges = null, MusicMixerSnapshot mixer = null, int playbackVersion = 0, MusicAutomation[] masterAutomation = null, MusicBus[] buses = null,
            MusicMeterChange[] meterChanges = null, MusicSectionMarker[] sections = null, MusicHarmonyTrack harmony = null)
        {
            if (string.IsNullOrWhiteSpace(name) || !MusicTrack.Finite(bpm) || bpm < MusicTempoMap.MinBpm || bpm > MusicTempoMap.MaxBpm ||
                beatsPerBar < 1 || beatsPerBar > 12 || !MusicTrack.Finite(loopBeats) || loopBeats < beatsPerBar ||
                loopBeats > MaxLoopBeats || parts == null || parts.Length > MaxParts)
                throw new ArgumentException("Invalid music cue.");
            PlaybackVersion = MusicPlaybackVersion.Require(playbackVersion, "Music cue " + name);
            Name = name; Bpm = bpm; BeatsPerBar = beatsPerBar; LoopBeats = loopBeats;
            Meter = new MusicMeter(beatsPerBar, loopBeats, meterChanges);
            Sections = new MusicSections(loopBeats, sections);
            HarmonyTrack = harmony ?? MusicHarmonyTrack.Empty;
            Parts = (MusicPart[])parts.Clone();
            MixEffects = (MusicMixEffect[])effects.Clone();
            Tempo = new MusicTempoMap(bpm, loopBeats, tempoChanges);
            Mixer = mixer ?? new MusicMixerSettings().Compile();
            foreach (var part in Parts)
            {
                if (part == null) throw new ArgumentException("Null music part.");
                foreach (var note in part.Notes)
                    if (note.Beat >= loopBeats) throw new ArgumentException("Note starts outside loop.");
                if (part.Automation != null)
                    foreach (var lane in part.Automation)
                        if (lane != null && lane.LastBeat > loopBeats + 1e-9) throw new ArgumentException("Automation point outside loop.");
                HasPartAutomation |= part.Automation != null;
            }
            Buses = buses != null ? (MusicBus[])buses.Clone() : Array.Empty<MusicBus>();
            if (Buses.Length > MusicRouting.MaxBuses) throw new ArgumentException($"At most {MusicRouting.MaxBuses} buses.");
            for (int b = 0; b < Buses.Length; b++)
            {
                if (Buses[b] == null || Buses[b].Output != -1 && (Buses[b].Output <= b || Buses[b].Output >= Buses.Length))
                    throw new ArgumentException("A bus must feed the master or a later bus.");
                if (Buses[b].Automation != null) foreach (var lane in Buses[b].Automation)
                    if (lane != null && lane.LastBeat > loopBeats + 1e-9) throw new ArgumentException("Bus automation point outside loop.");
            }
            foreach (var part in Parts)
            {
                if (part.OutputBus >= Buses.Length) throw new ArgumentException("Output bus does not exist.");
                if (part.Sends != null) foreach (var send in part.Sends)
                {
                    if (send.Bus >= Buses.Length) throw new ArgumentException("Send bus does not exist.");
                    if (send.LevelAutomation != null && send.LevelAutomation.LastBeat > loopBeats + 1e-9) throw new ArgumentException("Send automation point outside loop.");
                }
            }
            SidechainKey = new bool[Parts.Length]; PartSidechain = new int[Parts.Length]; BusSidechain = new int[Buses.Length];
            for (int i = 0; i < Parts.Length; i++) PartSidechain[i] = Sidechain(Parts[i].Effects, "Track " + Parts[i].InstrumentId);
            // Envelope followers listen to another track's level like a sidechain key.
            foreach (var part in Parts)
                if (part.Followers != null)
                    foreach (var f in part.Followers)
                    {
                        f.Source = Array.FindIndex(Parts, x => x.InstrumentId == f.SourceId);
                        if (f.Source < 0) throw new ArgumentException($"Track {part.InstrumentId}: no track called {f.SourceId} for the follower.");
                        SidechainKey[f.Source] = true;
                    }
            for (int b = 0; b < Buses.Length; b++) BusSidechain[b] = Sidechain(Buses[b].Effects, "Bus " + Buses[b].Name);
            MasterSidechain = Sidechain(Mixer, "Master");
            MasterAutomation = MusicAutomation.ByTarget(masterAutomation, Mixer, "Music cue " + name, true, true);
            if (MasterAutomation != null)
                foreach (var lane in MasterAutomation)
                    if (lane != null && lane.LastBeat > loopBeats + 1e-9) throw new ArgumentException("Master automation point outside loop.");
            MasterVolume = GetMasterAutomation(MusicAutomationTarget.VolumeDb);
            // V3 tape speed: the tempo map follows it, so durations, seeking and exports include the slowed stretch.
            if (PlaybackVersion >= MusicPlaybackVersion.V3 && MasterAutomation?[(int)MusicAutomationTarget.Speed] != null)
                Tempo = MusicTempoMap.WithSpeed(bpm, loopBeats, tempoChanges, MasterAutomation[(int)MusicAutomationTarget.Speed]);
            Harmony = MusicHarmony.Analyze(this);
            foreach (var part in Parts)
                if (part.Layer == MusicLayer.Drums && part.Gain > 0)
                    foreach (var note in part.Notes) if (note.Velocity > 0) { HasDrums = true; break; }
        }

        int Sidechain(MusicMixerSnapshot chain, string owner)
        {
            if (chain == null || string.IsNullOrEmpty(chain.SidechainTrack)) return -1;
            if (!chain.Compressor) throw new ArgumentException(owner + ": the sidechain needs the compressor on.");
            int found = -1;
            for (int i = 0; i < Parts.Length; i++)
                if (Parts[i].InstrumentId == chain.SidechainTrack)
                { if (found >= 0) throw new ArgumentException($"{owner}: more than one track is called {chain.SidechainTrack}."); found = i; }
            if (found < 0) throw new ArgumentException($"{owner}: no track called {chain.SidechainTrack} for the sidechain.");
            SidechainKey[found] = true;
            return found;
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
