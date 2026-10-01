using System;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>
    /// Fixed songs that exercise every sound path (oscillators, pitched and downmixed samples, envelopes, pan,
    /// additive noise, tempo ramps, loop wrap, master mixer, whole-mix glitches). Their fingerprints guard each
    /// playback version: never edit a scenario once fingerprints for a released version are recorded.
    /// </summary>
    internal static class MusicPlaybackScenarios
    {
        public const int SampleRate = 8000, Frames = 64000;
        public static readonly string[] Names = { "Oscillators", "Samples", "Master", "Dense", "Glide", "Drive", "TrackFx" };

        public sealed class Song : IDisposable
        {
            public MusicCueDefinition Definition;
            public AudioClip[] Clips = Array.Empty<AudioClip>();
            /// <summary>Notes beyond a version's voice limit are expected (Dense: V1 drops, V2 plays them).</summary>
            public bool AllowDrops;
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Definition);
                foreach (var clip in Clips) UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        public static Song Build(string name)
        {
            switch (name)
            {
                case "Oscillators": return new Song { Definition = Oscillators(name) };
                case "Samples": return Samples();
                case "Master": return new Song { Definition = Master() };
                case "Dense": return new Song { Definition = Dense(), AllowDrops = true };
                case "Glide": return Glide();
                case "Drive": return new Song { Definition = Drive() };
                case "TrackFx": return new Song { Definition = TrackFx() };
                default: throw new ArgumentException(name);
            }
        }

        /// <summary>Stereo PCM from the looping runtime engine (no repeat fade), as a game plays a cue.</summary>
        public static float[] Render(MusicCue cue, bool allowDrops = false)
        {
            var engine = new AdaptiveMusicEngine(cue, SampleRate);
            var pcm = new float[Frames * 2];
            var block = new float[1000];
            for (int offset = 0; offset < pcm.Length; offset += block.Length)
            {
                engine.RenderStereo(block);
                Array.Copy(block, 0, pcm, offset, block.Length);
            }
            if (engine.DroppedNotes > 0 && !allowDrops) throw new InvalidOperationException("Scenario exceeds the voice limit.");
            return pcm;
        }

        static MusicCueDefinition Oscillators(string name)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = name; song.Bpm = 132; song.LoopBeats = 8;
            song.TempoChanges = new[] { new MusicTempoChange { Beat = 4, Bpm = 150, RampBeats = 2 } };
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Bell", Layer = MusicLayer.Melody, FallbackVoice = MusicVoice.Bell, Gain = .14f, Pan = -.4f,
                    AttackSeconds = .004f, ReleaseSeconds = .2f, Notes = new[] {
                        new MusicNote(0, .5, 72, .9f), new MusicNote(.5, .5, 76, .7f), new MusicNote(1, 1, 79), new MusicNote(2, .25, 84, .6f),
                        new MusicNote(2.5, 1.5, 71, .8f), new MusicNote(4, .75, 74, .9f), new MusicNote(5, .5, 77, .5f), new MusicNote(6, 2, 69) } },
                new MusicCueDefinition.Part { Name = "Bass", Layer = MusicLayer.Bass, FallbackVoice = MusicVoice.Bass, Gain = .2f, Pan = .1f, Notes = new[] {
                    new MusicNote(0, 2, 36), new MusicNote(2, 2, 43, .9f), new MusicNote(4, 2, 41), new MusicNote(6, 2, 38, .8f) } },
                new MusicCueDefinition.Part { Name = "Pad", Layer = MusicLayer.Harmony, FallbackVoice = MusicVoice.Pad, Gain = .08f, Pan = .5f,
                    AttackSeconds = .3f, ReleaseSeconds = .5f, Notes = new[] {
                        new MusicNote(0, 4, 60, .7f), new MusicNote(0, 4, 64, .7f), new MusicNote(4, 4, 62, .7f), new MusicNote(4, 4, 65, .7f) } },
                new MusicCueDefinition.Part { Name = "Kick", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Gain = .25f, Notes = new[] {
                    new MusicNote(0, .5, 36), new MusicNote(1, .5, 36, .7f), new MusicNote(2, .5, 36), new MusicNote(3, .5, 36, .7f),
                    new MusicNote(4, .5, 36), new MusicNote(5, .5, 36, .7f), new MusicNote(6, .5, 36), new MusicNote(7, .5, 36, .7f) } },
            };
            return song;
        }

        static Song Samples()
        {
            // Mono 11.025 kHz pluck (root A3) and stereo 22.05 kHz organ (root A4, shorter than its notes).
            var pluck = AudioClip.Create("Pluck", 6615, 1, 11025, false);
            var pluckPcm = new float[6615];
            for (int i = 0; i < pluckPcm.Length; i++)
            {
                double t = i / 11025.0, phase = 220 * t + .002 * Math.Sin(2 * Math.PI * 5 * t);
                pluckPcm[i] = (float)(.8 * (2 * (phase - Math.Floor(phase)) - 1) * Math.Exp(-t * 6));
            }
            pluck.SetData(pluckPcm, 0);
            var organ = AudioClip.Create("Organ", 8820, 2, 22050, false);
            var organPcm = new float[8820 * 2];
            for (int i = 0; i < 8820; i++)
            {
                double t = i / 22050.0, a = 2 * Math.PI * 440 * t;
                organPcm[i * 2] = (float)(.5 * Math.Sin(a) + .2 * Math.Sin(2 * a));
                organPcm[i * 2 + 1] = (float)(.5 * Math.Sin(a) + .15 * Math.Sin(3 * a));
            }
            organ.SetData(organPcm, 0);

            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Samples"; song.Bpm = 110; song.LoopBeats = 8;
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Pluck", Layer = MusicLayer.Melody, Sample = pluck, SampleRootKey = 57, Gain = .3f, Pan = -1,
                    AttackSeconds = .002f, ReleaseSeconds = .05f, Notes = new[] {
                        new MusicNote(0, .5, 57), new MusicNote(.5, .5, 64, .8f), new MusicNote(1, .25, 69, .9f), new MusicNote(1.5, 1, 45),
                        new MusicNote(3, .5, 81, .7f), new MusicNote(4, 2, 52), new MusicNote(6, .75, 60, .6f), new MusicNote(7, 1, 57) } },
                new MusicCueDefinition.Part { Name = "Organ", Layer = MusicLayer.Harmony, Sample = organ, SampleRootKey = 69, Gain = .12f, Pan = .7f,
                    AttackSeconds = .05f, ReleaseSeconds = .3f, Notes = new[] {
                        new MusicNote(0, 3, 64), new MusicNote(0, 3, 69, .8f), new MusicNote(4, 1, 62), new MusicNote(4, 4, 67, .9f), new MusicNote(5, 3, 71, .7f) } },
            };
            song.NoiseRegions = new[]
            {
                new MusicNoiseRegion { Name = "Air", Kind = MusicNoiseKind.White, StartBeat = 2, DurationBeats = 3, FadeInBeats = 1, FadeOutBeats = .5, Gain = .05f, Seed = 99 },
                new MusicNoiseRegion { Name = "Rumble", Kind = MusicNoiseKind.Brown, StartBeat = 0, DurationBeats = 2, FadeInBeats = .25, FadeOutBeats = .25, Gain = .1f, RepeatWithCue = false },
                new MusicNoiseRegion { Name = "Wobble", Kind = MusicNoiseKind.PitchGlitch, StartBeat = 5, DurationBeats = 2, FadeInBeats = .1, FadeOutBeats = .1, Gain = .06f, GlitchRate = 6, PitchDepth = 7 },
                new MusicNoiseRegion { Name = "Loop", Kind = MusicNoiseKind.Sample, Sample = pluck, StartBeat = 6, DurationBeats = 2, FadeInBeats = 0, FadeOutBeats = .5, Gain = .08f },
            };
            return new Song { Definition = song, Clips = new[] { pluck, organ } };
        }

        /// <summary>
        /// Legato glide (V2+; V1 ignores GlideSeconds): an oscillator bass and a sampled 808 with overlapping notes slide,
        /// a note after a gap and a same-beat chord start new voices, a tempo ramp and the loop wrap run during slides.
        /// </summary>
        static Song Glide()
        {
            var boom = AudioClip.Create("Boom", 16000, 1, 8000, false);
            var boomPcm = new float[16000];
            for (int i = 0; i < boomPcm.Length; i++)
            {
                double t = i / 8000.0, phase = 55 * t + .6 * (1 - Math.Exp(-t * 30)) / 30 * 55;
                boomPcm[i] = (float)(.9 * Math.Tanh(2.5 * Math.Sin(2 * Math.PI * phase)) * Math.Exp(-t * .7));
            }
            boom.SetData(boomPcm, 0);
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Glide"; song.Bpm = 140; song.LoopBeats = 8;
            song.TempoChanges = new[] { new MusicTempoChange { Beat = 5, Bpm = 100, RampBeats = 2 } };
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "808", Layer = MusicLayer.Bass, Sample = boom, SampleRootKey = 33, Gain = .5f, Pan = -.2f,
                    AttackSeconds = .002f, ReleaseSeconds = .08f, GlideSeconds = .09f, Notes = new[] {
                        new MusicNote(0, 1.2, 33), new MusicNote(1, 1.2, 40, .9f), new MusicNote(2, 1, 28), new MusicNote(3.5, .5, 36, .8f),
                        new MusicNote(3.75, 1.5, 31), new MusicNote(5, 1.5, 43, .7f), new MusicNote(6.5, 1.75, 33) } },
                new MusicCueDefinition.Part { Name = "Sub", Layer = MusicLayer.Bass, FallbackVoice = MusicVoice.Bass, Gain = .15f, Pan = .3f,
                    GlideSeconds = .25f, Notes = new[] {
                        new MusicNote(0, 2.5, 45), new MusicNote(2, 2.5, 52), new MusicNote(4, 1, 45, .8f), new MusicNote(4, 1, 49, .8f),
                        new MusicNote(4.5, 2, 57, .9f), new MusicNote(6, 2.2, 40) } },
                new MusicCueDefinition.Part { Name = "Bell", Layer = MusicLayer.Melody, FallbackVoice = MusicVoice.Bell, Gain = .1f, Pan = .6f, Notes = new[] {
                    new MusicNote(0, 1, 76), new MusicNote(.5, 1, 79), new MusicNote(4, 1, 74), new MusicNote(4.5, 1, 81) } },
            };
            return new Song { Definition = song, Clips = new[] { boom } };
        }

        /// <summary>
        /// Drive (V2+; V1 ignores it): track drive in every shaper mode, output gain above the old +12 dB limit and a master drive
        /// with fuzz, partial mix, tone filter, clean-bass split and trim.
        /// </summary>
        static MusicCueDefinition Drive()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Drive"; song.Bpm = 128; song.LoopBeats = 8;
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Bass", Layer = MusicLayer.Bass, FallbackVoice = MusicVoice.Bass, Gain = .2f, DriveDb = 24, DriveMode = MusicDriveMode.Fold,
                    Notes = new[] { new MusicNote(0, 1.5, 33), new MusicNote(2, 1.5, 36, .8f), new MusicNote(4, 1.5, 31), new MusicNote(6, 1.5, 38, .9f) } },
                new MusicCueDefinition.Part { Name = "Bell", Layer = MusicLayer.Melody, FallbackVoice = MusicVoice.Bell, Gain = .08f, Pan = -.5f, DriveDb = 12, DriveMode = MusicDriveMode.Hard,
                    Notes = new[] { new MusicNote(0, .5, 72), new MusicNote(1, .5, 79, .7f), new MusicNote(3, 1, 76), new MusicNote(5, .5, 84, .6f) } },
                new MusicCueDefinition.Part { Name = "Pad", Layer = MusicLayer.Harmony, FallbackVoice = MusicVoice.Pad, Gain = .05f, Pan = .5f, DriveDb = 6, DriveMode = MusicDriveMode.Fuzz,
                    AttackSeconds = .2f, ReleaseSeconds = .4f, Notes = new[] { new MusicNote(0, 4, 60, .7f), new MusicNote(4, 4, 62, .7f) } },
                new MusicCueDefinition.Part { Name = "Kick", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Gain = .2f, DriveDb = 18, DriveMode = MusicDriveMode.Soft,
                    Notes = new[] { new MusicNote(0, .5, 36), new MusicNote(2, .5, 36), new MusicNote(4, .5, 36), new MusicNote(6, .5, 36) } },
            };
            song.Mixer = new MusicMixerSettings { OutputDb = 18, Drive = true, DriveMode = MusicDriveMode.Fuzz, DriveDb = 15, DriveMix = .7f,
                DriveToneHz = 6000, DriveKeepBassHz = 120, DriveTrimDb = 3 };
            return song;
        }

        /// <summary>
        /// Track effects (V2+; V1 ignores them): an echo whose tail rings past the last note, a crushed and filtered bass, a pad
        /// through a step gate, a compressed and driven kick, an untouched lead — and a master compressor on top of it all.
        /// </summary>
        static MusicCueDefinition TrackFx()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "TrackFx"; song.Bpm = 120; song.LoopBeats = 8;
            song.Parts = new[]
            {
                new MusicCueDefinition.Part { Name = "Bell", Layer = MusicLayer.Melody, FallbackVoice = MusicVoice.Bell, Gain = .12f, Pan = -.4f, TrackEffects = true,
                    Effects = new MusicMixerSettings { EchoWet = .45f, EchoSeconds = .375f, EchoFeedback = .55f, HighPassHz = 300 },
                    Notes = new[] { new MusicNote(0, .5, 76), new MusicNote(1, .5, 79, .8f), new MusicNote(2.5, .5, 84, .7f) } },
                new MusicCueDefinition.Part { Name = "Bass", Layer = MusicLayer.Bass, FallbackVoice = MusicVoice.Bass, Gain = .2f, TrackEffects = true,
                    Effects = new MusicMixerSettings { CrushBits = 4, CrushDownsample = 6, CrushMix = .8f, LowPassHz = 900 },
                    Notes = new[] { new MusicNote(0, 2, 33), new MusicNote(2, 2, 36, .9f), new MusicNote(4, 2, 31) } },
                new MusicCueDefinition.Part { Name = "Pad", Layer = MusicLayer.Harmony, FallbackVoice = MusicVoice.Pad, Gain = .07f, Pan = .5f, TrackEffects = true,
                    Effects = new MusicMixerSettings { Gate = true, GateBeats = .25f, GatePattern = "x-x- xx-x x--x xxx-" },
                    AttackSeconds = .1f, ReleaseSeconds = .3f, Notes = new[] { new MusicNote(0, 6, 60, .7f), new MusicNote(0, 6, 64, .7f) } },
                new MusicCueDefinition.Part { Name = "Kick", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Gain = .25f, TrackEffects = true,
                    Effects = new MusicMixerSettings { Compressor = true, ThresholdDb = -20, Ratio = 8, AttackMs = 2, ReleaseMs = 80, OutputDb = 6,
                        Drive = true, DriveMode = MusicDriveMode.Hard, DriveDb = 12 },
                    Notes = new[] { new MusicNote(0, .5, 36), new MusicNote(1.5, .5, 36), new MusicNote(3, .5, 36), new MusicNote(4.5, .5, 36) } },
                new MusicCueDefinition.Part { Name = "Lead", Layer = MusicLayer.Melody, FallbackVoice = MusicVoice.Bell, Gain = .08f, Pan = .3f,
                    Notes = new[] { new MusicNote(4, 1, 72), new MusicNote(5, 1, 74) } },
            };
            song.Mixer = new MusicMixerSettings { Compressor = true, ThresholdDb = -14, Ratio = 3 };
            return song;
        }

        /// <summary>300 overlapping pad notes: more than V1's 128 voices, within V2's 1024.</summary>
        static MusicCueDefinition Dense()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Dense"; song.Bpm = 120; song.LoopBeats = 8;
            var notes = new MusicNote[300];
            for (int i = 0; i < notes.Length; i++) notes[i] = new MusicNote(i * .02, 3, 36 + i % 60, .5f + (i % 5) * .1f);
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Cluster", Layer = MusicLayer.Harmony, FallbackVoice = MusicVoice.Pad,
                Gain = .004f, Pan = .2f, AttackSeconds = .05f, ReleaseSeconds = .3f, Notes = notes } };
            return song;
        }

        static MusicCueDefinition Master()
        {
            var song = Oscillators("Master");
            song.Mixer = new MusicMixerSettings
            {
                OutputDb = -2, LowPassHz = 3000, HighPassHz = 80, Compressor = true, ThresholdDb = -24, Ratio = 6, AttackMs = 5, ReleaseMs = 120,
                EchoWet = .25f, EchoSeconds = .18f, EchoFeedback = .4f, CrushBits = 10, CrushDownsample = 2, CrushMix = .5f,
                NoiseGate = true, NoiseGateThresholdDb = -45, NoiseGateRangeDb = -60, NoiseGateHoldMs = 10, NoiseGateReleaseMs = 40,
                Gate = true, GateBeats = .25f, GateDepth = .8f, GatePattern = "x-xx x-x- xxx- x---",
            };
            MusicNoiseRegion Glitch(MusicNoiseKind kind, double start, double duration) => new MusicNoiseRegion
            { Name = kind.ToString(), Kind = kind, MasterFilter = true, StartBeat = start, DurationBeats = duration, FadeInBeats = .1, FadeOutBeats = .1, Wet = .9f, Seed = 77 };
            song.NoiseRegions = new[]
            {
                Glitch(MusicNoiseKind.StutterGlitch, 1, 1), Glitch(MusicNoiseKind.SlowingGlitch, 2.5, 1.5), Glitch(MusicNoiseKind.BitcrushGlitch, 4, 1),
                Glitch(MusicNoiseKind.GateGlitch, 5.5, 1.5), Glitch(MusicNoiseKind.AcceleratingGlitch, 7, 1),
            };
            return song;
        }
    }
}
