namespace Graze.Presentation.Audio
{
    /// <summary>
    /// One track's effect bus (playback V2+): the track's voices sum here instead of into the master, run through the same
    /// chain as the master (filters, compressor, echo, crusher, noise gate, rhythm gate, output gain, drive, amp, cabinet, EQ, chorus, reverb) and then join the
    /// master sum. Created on the caller thread when a cue is handed to the engine; Process allocates nothing. The echo buffer
    /// is sized to the track's echo time, not the master's two seconds.
    /// Playback V3 routing uses the same class: a routed track without effects gets a pass-through bus (null settings), and each
    /// mix bus of the cue is one more instance after the track buses (see <see cref="For"/>).
    /// </summary>
    internal sealed class MusicTrackEffects
    {
        public readonly MusicMixerSnapshot Settings;
        /// <summary>Playback V3 track with effect automation: the engine passes the automated values each sample.</summary>
        public bool Automated;
        readonly MusicMasterMixer mixer;
        readonly MusicMasterDrive drive;
        readonly MusicAmp amp; // only when the track uses the amp or a cabinet
        readonly MusicSpace space; // only when the track uses EQ, chorus or reverb (its buffers are the largest)
        readonly MusicConvolution convolution;
        readonly MusicColor color; // V3 chains with a color stage
        readonly MusicStutter stutter; // V3 chains with Stutter on
        readonly int rate;
        // V3 performance lanes (SetLanes): stutter engaged, slice seconds (from the tempo) and width (negative = none).
        bool stutterOn; double stutterSeconds = .25, width = -1;
        public bool HasStutter => stutter != null;
        public double Left, Right;
        /// <summary>Playback V3 tempo-synced echo (EchoBeats on a V3 chain): the engine sets the delay from the current tempo each sample.</summary>
        public readonly bool SyncedEcho;
        public void SetEchoTempo(double bpm) => mixer.DelayOverride = Settings.EchoDelayAt(bpm);

        public MusicTrackEffects(MusicMixerSnapshot settings, int sampleRate, bool useConvolution = false)
        {
            Settings = settings; rate = sampleRate;
            if (settings == null) return; // pass-through (routed track without its own effects)
            // useConvolution marks a V3 chain (see For): only V3 follows EchoBeats, and its buffer then holds the longest synced delay.
            SyncedEcho = useConvolution && settings.EchoBeats > 0;
            mixer = new MusicMasterMixer(sampleRate, SyncedEcho ? MusicMixerSnapshot.MaxEchoSeconds : settings.Delay);
            drive = new MusicMasterDrive(sampleRate);
            if (settings.AmpStage) amp = new MusicAmp(sampleRate);
            if (useConvolution) convolution = MusicConvolution.For(settings,MusicPlaybackVersion.V3,sampleRate);
            if (settings.Space) space = new MusicSpace(sampleRate);
            if (useConvolution && settings.Color != null) color = new MusicColor(sampleRate); // V3 chains only
            if (useConvolution && settings.Stutter) stutter = new MusicStutter(sampleRate);
        }

        /// <summary>
        /// Playback V3 performance lanes for the next Render: gate depth, crush mix, drive gain (linear) and mix (negative = authored),
        /// stutter engaged and its slice length, stereo width (negative = none).
        /// </summary>
        public void SetLanes(double gateDepth, double crushMix, double driveGain, double driveMix, bool stutterEngaged, double sliceSeconds, double stereoWidth, double rotarySpeed = -1)
        {
            if (Settings == null) return;
            if (color != null) color.RotaryOverride = rotarySpeed;
            mixer.GateDepthOverride = gateDepth; mixer.CrushMixOverride = crushMix; drive.GainOverride = driveGain; drive.MixOverride = driveMix;
            stutterOn = stutterEngaged; stutterSeconds = sliceSeconds; width = stereoWidth;
        }

        // Stutter and width after the rest of the chain (V3 lanes only; identity otherwise).
        void Performance(ref double l, ref double r)
        {
            stutter?.Process(ref l, ref r, stutterOn, stutterSeconds, rate);
            if (width >= 0) { double mid = (l + r) * .5, side = (l - r) * .5 * width; l = mid + side; r = mid - side; }
        }

        /// <summary>Processes the samples gathered since the last call and adds them to the master sum.</summary>
        public void Process(double localBeat, ref double sum, ref double sumRight)
        {
            Render(localBeat, out double l, out double r);
            sum += l; sumRight += r;
        }

        /// <summary>The chain's output for the samples gathered since the last call (pass-through without settings).</summary>
        public void Render(double localBeat, out double l, out double r)
        {
            l = Left; r = Right;
            Left = Right = 0;
            if (Settings == null) return;
            mixer.Process(ref l, ref r, Settings, localBeat);
            if (Settings.Drive) drive.Process(ref l, ref r, Settings);
            if (amp != null) amp.Process(ref l, ref r, Settings);
            convolution?.Process(ref l, ref r);
            if (space != null) space.Process(ref l, ref r, Settings);
            color?.Process(ref l, ref r, Settings.Color, localBeat);
        }

        /// <summary>Same chain with automated filter cutoffs, echo wet and chorus/reverb mix (playback V3).</summary>
        public void Render(double localBeat, out double l, out double r, double lowPass, double highPass, double echoWet, double chorusMix, double reverbMix, double keyPeak = -1)
        {
            l = Left; r = Right;
            Left = Right = 0;
            if (Settings == null) return;
            mixer.Process(ref l, ref r, Settings, localBeat, lowPass, highPass, echoWet, keyPeak);
            if (Settings.Drive) drive.Process(ref l, ref r, Settings);
            if (amp != null) amp.Process(ref l, ref r, Settings);
            convolution?.Process(ref l, ref r);
            if (space != null) space.Process(ref l, ref r, Settings, chorusMix, reverbMix);
            color?.Process(ref l, ref r, Settings.Color, localBeat);
            Performance(ref l, ref r);
        }

        /// <summary>
        /// Buses for a cue's parts that have effects (index = part index), or null when none apply (V1 cues ignore track effects).
        /// A V3 cue with mix buses gets them after the part buses (index PartCount + bus), and its routed parts always get a bus.
        /// </summary>
        public static MusicTrackEffects[] For(MusicCue cue, int sampleRate, bool preparation = false)
        {
            if (cue.PlaybackVersion < MusicPlaybackVersion.V2) return null;
            if (cue.HasPluginInserts && !preparation) throw new System.InvalidOperationException("Prepare VST3 inserts with MusicPluginInsertRenderer before playback; use its prepared PCM reader.");
            bool routing = cue.PlaybackVersion >= MusicPlaybackVersion.V3 && cue.Buses.Length > 0;
            MusicTrackEffects[] buses = routing ? new MusicTrackEffects[cue.PartCount + cue.Buses.Length] : null;
            if (routing) for (int b = 0; b < cue.Buses.Length; b++) buses[cue.PartCount + b] = new MusicTrackEffects(cue.Buses[b].Effects, sampleRate,true);
            for (int i = 0; i < cue.PartCount; i++)
            {
                var part = cue.Parts[i];
                int version = cue.PlaybackVersionOf(part);
                // V3: routed tracks and sidechain keys always get a bus (pass-through without effects); the key level is read from its input.
                if (version >= MusicPlaybackVersion.V2 && (preparation || (version >= MusicPlaybackVersion.V3 && cue.PlaybackVersion >= MusicPlaybackVersion.V3 && ((routing && part.Routed) || cue.SidechainKey[i]))))
                {
                    if (buses == null) buses = new MusicTrackEffects[cue.PartCount];
                    buses[i] = new MusicTrackEffects(part.Effects, sampleRate,version>=MusicPlaybackVersion.V3&&cue.PlaybackVersion>=MusicPlaybackVersion.V3) { Automated = part.HasEffectAutomation }; continue;
                }
                if (part.Effects == null || version < MusicPlaybackVersion.V2) continue;
                if (buses == null) buses = new MusicTrackEffects[cue.PartCount];
                buses[i] = new MusicTrackEffects(part.Effects, sampleRate,version>=MusicPlaybackVersion.V3&&cue.PlaybackVersion>=MusicPlaybackVersion.V3) { Automated = part.HasEffectAutomation && cue.PlaybackVersionOf(part) >= MusicPlaybackVersion.V3 };
            }
            return buses;
        }
    }
}
