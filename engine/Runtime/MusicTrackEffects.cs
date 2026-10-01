namespace Graze.Presentation.Audio
{
    /// <summary>
    /// One track's effect bus (playback V2+): the track's voices sum here instead of into the master, run through the same
    /// chain as the master (filters, compressor, echo, crusher, noise gate, rhythm gate, output gain, drive) and then join the
    /// master sum. Created on the caller thread when a cue is handed to the engine; Process allocates nothing. The echo buffer
    /// is sized to the track's echo time, not the master's two seconds.
    /// </summary>
    internal sealed class MusicTrackEffects
    {
        public readonly MusicMixerSnapshot Settings;
        readonly MusicMasterMixer mixer;
        readonly MusicMasterDrive drive;
        public double Left, Right;

        public MusicTrackEffects(MusicMixerSnapshot settings, int sampleRate)
        {
            Settings = settings;
            mixer = new MusicMasterMixer(sampleRate, settings.Delay);
            drive = new MusicMasterDrive(sampleRate);
        }

        /// <summary>Processes the samples gathered since the last call and adds them to the master sum.</summary>
        public void Process(double localBeat, ref double sum, ref double sumRight)
        {
            double l = Left, r = Right;
            Left = Right = 0;
            mixer.Process(ref l, ref r, Settings, localBeat);
            if (Settings.Drive) drive.Process(ref l, ref r, Settings);
            sum += l; sumRight += r;
        }

        /// <summary>Buses for a cue's parts that have effects (index = part index), or null when none apply (V1 cues ignore track effects).</summary>
        public static MusicTrackEffects[] For(MusicCue cue, int sampleRate)
        {
            if (cue.PlaybackVersion < MusicPlaybackVersion.V2) return null;
            MusicTrackEffects[] buses = null;
            for (int i = 0; i < cue.PartCount; i++)
            {
                var part = cue.Parts[i];
                if (part.Effects == null || cue.PlaybackVersionOf(part) < MusicPlaybackVersion.V2) continue;
                if (buses == null) buses = new MusicTrackEffects[cue.PartCount];
                buses[i] = new MusicTrackEffects(part.Effects, sampleRate);
            }
            return buses;
        }
    }
}
