using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSynthInstrumentTests
    {
        const int Rate = 48000;

        static MusicSynthSettings Plain(MusicSynthWave wave = MusicSynthWave.Sine) =>
            new MusicSynthSettings { Enabled = true, Osc1Wave = wave, Filter = MusicSynthFilter.Off, AmpAttack = .001f, AmpDecay = .1f, AmpSustain = 1, AmpRelease = .05f };

        static MusicCue Cue(MusicSynthSettings synth, int version, params MusicNote[] notes)
        {
            var instrument = synth != null ? new MusicInstrument(synth.Compile(), MusicVoice.Pad) : new MusicInstrument(MusicVoice.Pad);
            var part = new MusicPart(MusicLayer.Melody, instrument, .5, "Synth", notes, 0);
            return new MusicCue("Synth test", 120, 4, 8, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: version);
        }

        static float[] Render(MusicCue cue, double seconds, out AdaptiveMusicEngine engine)
        {
            engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            var pcm = new float[(int)(seconds * Rate) * 2];
            var block = new float[960];
            for (int at = 0; at < pcm.Length; at += block.Length) { engine.RenderStereo(block); Array.Copy(block, 0, pcm, at, Math.Min(block.Length, pcm.Length - at)); }
            return pcm;
        }

        static float[] Render(MusicCue cue, double seconds) => Render(cue, seconds, out _);
        static double[] Channel(float[] pcm, int channel, int from, int to) { var x = new double[to - from]; for (int i = from; i < to; i++) x[i - from] = pcm[i * 2 + channel]; return x; }
        static double Rms(double[] x) => Math.Sqrt(x.Sum(v => v * v) / x.Length);
        static double Frequency(double[] x)
        {
            int crossings = 0; double first = -1, last = 0;
            for (int i = 1; i < x.Length; i++)
                if (x[i - 1] < 0 && x[i] >= 0) { double t = i - 1 + x[i - 1] / (x[i - 1] - x[i]); if (first < 0) first = t; last = t; crossings++; }
            return (crossings - 1) / ((last - first) / Rate);
        }
        // High-frequency share: energy of the first difference relative to the signal.
        static double Brightness(double[] x) { double d = 0, e = 0; for (int i = 1; i < x.Length; i++) { d += (x[i] - x[i - 1]) * (x[i] - x[i - 1]); e += x[i] * x[i]; } return d / e; }

        [Test]
        public void OlderPlaybackVersionsAndDisabledSynthKeepTheirSound()
        {
            var note = new MusicNote(0, 2, 69, .8f);
            foreach (int version in new[] { MusicPlaybackVersion.V1, MusicPlaybackVersion.V2 })
                CollectionAssert.AreEqual(Render(Cue(null, version, note), 1.5), Render(Cue(MusicSynthPresets.Create("Saw lead"), version, note), 1.5), "V" + version + " ignores the synth");
            var off = MusicSynthPresets.Create("Saw lead"); off.Enabled = false;
            Assert.IsNull(off.Compile());
            Assert.IsFalse(Render(Cue(null, MusicPlaybackVersion.V3, note), 1.5).SequenceEqual(Render(Cue(MusicSynthPresets.Create("Saw lead"), MusicPlaybackVersion.V3, note), 1.5)), "V3 plays the synth");
        }

        [Test]
        public void OscillatorsPlayThePitchOfTheNoteAndOctaveSub()
        {
            var pcm = Render(Cue(Plain(), MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1);
            Assert.AreEqual(440, Frequency(Channel(pcm, 0, Rate / 10, Rate)), .5);
            var detuned = Plain(); detuned.Osc1Level = 0; detuned.Osc2Level = 1; detuned.Osc2Wave = MusicSynthWave.Sine; detuned.Osc2Semitones = 12; detuned.Osc2Cents = 0;
            Assert.AreEqual(880, Frequency(Channel(Render(Cue(detuned, MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1), 0, Rate / 10, Rate)), 1);
            var sub = Plain(); sub.Osc1Level = 0; sub.SubLevel = 1;
            Assert.AreEqual(220, Frequency(Channel(Render(Cue(sub, MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1), 0, Rate / 10, Rate)), .5);
            foreach (MusicSynthWave wave in Enum.GetValues(typeof(MusicSynthWave)))
            {
                var settings = Plain(wave); settings.Drawbars = "008000000"; settings.FmIndex = 0; // organ: the 8' drawbar alone; FM: index 0 = the carrier sine
                Assert.AreEqual(220, Frequency(Channel(Render(Cue(settings, MusicPlaybackVersion.V3, new MusicNote(0, 4, 57)), 1), 0, Rate / 10, Rate)), 1, wave.ToString());
            }
        }

        [Test]
        public void AmpEnvelopeAttacksSustainsAndReleasesThenFreesTheVoice()
        {
            var s = Plain(); s.AmpAttack = .1f; s.AmpDecay = .1f; s.AmpSustain = .5f; s.AmpRelease = .2f;
            // 120 BPM: the note ends at beat 2 = 1 s.
            var pcm = Render(Cue(s, MusicPlaybackVersion.V3, new MusicNote(0, 2, 69)), 1.5, out var engine);
            double early = Rms(Channel(pcm, 0, 0, Rate / 40)), peak = Rms(Channel(pcm, 0, Rate * 95 / 1000, Rate / 10)), sustain = Rms(Channel(pcm, 0, Rate * 6 / 10, Rate * 9 / 10));
            Assert.Less(early, peak * .3, "attack ramps up");
            Assert.AreEqual(.5, sustain / peak, .05, "decays to the sustain level");
            Assert.AreEqual(0, Rms(Channel(pcm, 0, Rate * 12 / 10 + 480, Rate * 3 / 2)), 1e-9, "silent after the release");
            Assert.AreEqual(0, engine.ActiveVoices, "voice freed");
            var pluck = Plain(); pluck.AmpSustain = 0; pluck.AmpDecay = .05f;
            Render(Cue(pluck, MusicPlaybackVersion.V3, new MusicNote(0, 8, 69)), 1, out var plucked);
            Assert.AreEqual(0, plucked.ActiveVoices, "a decayed pluck frees its voice before the note ends");
        }

        [Test]
        public void FilterDarkensFollowsItsEnvelopeAndStaysStableAtFullResonance()
        {
            MusicSynthSettings Saw(float cutoff) { var s = Plain(MusicSynthWave.Saw); s.Filter = MusicSynthFilter.LowPass; s.CutoffHz = cutoff; return s; }
            double Bright(MusicSynthSettings s) => Brightness(Channel(Render(Cue(s, MusicPlaybackVersion.V3, new MusicNote(0, 4, 57)), .5), 0, Rate / 10, Rate / 2));
            double open = Bright(Saw(18000)), closed = Bright(Saw(400));
            Assert.Less(closed, open * .2, "low-pass removes the highs");
            var high = Saw(2000); high.Filter = MusicSynthFilter.HighPass;
            Assert.Greater(Bright(high), open, "high-pass keeps the highs");
            var env = Saw(300); env.EnvelopeOctaves = 6; env.FilterAttack = .001f; env.FilterDecay = .15f; env.FilterSustain = 0;
            var swept = Channel(Render(Cue(env, MusicPlaybackVersion.V3, new MusicNote(0, 4, 57)), 1), 0, 0, Rate);
            Assert.Greater(Brightness(swept.Take(Rate / 20).ToArray()), Brightness(swept.Skip(Rate / 2).ToArray()) * 3, "the envelope opens then closes the filter");
            var wild = Saw(20000); wild.Resonance = 1; wild.EnvelopeOctaves = -8; wild.LfoFilterOctaves = 8; wild.LfoRateHz = 50; wild.Unison = 7;
            var pcm = Render(Cue(wild, MusicPlaybackVersion.V3, new MusicNote(0, 4, 100, 1)), 1);
            Assert.IsTrue(pcm.All(v => !float.IsNaN(v) && Math.Abs(v) <= 1), "bounded");
        }

        [Test]
        public void LfoVibratoTremoloAndTempoSync()
        {
            var vib = Plain(); vib.LfoPitchCents = 100; vib.LfoRateHz = 2;
            var pcm = Channel(Render(Cue(vib, MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1), 0, 0, Rate);
            // Sine LFO starts at 0 and peaks at 1/4 cycle (+1 semitone), troughs at 3/4 (-1).
            Assert.AreEqual(440 * Math.Pow(2, 1 / 12.0), Frequency(pcm.Skip(Rate / 8 - 1200).Take(2400).ToArray()), 6);
            Assert.AreEqual(440 / Math.Pow(2, 1 / 12.0), Frequency(pcm.Skip(Rate * 3 / 8 - 1200).Take(2400).ToArray()), 6);
            var trem = Plain(); trem.LfoLevel = 1; trem.LfoSyncBeats = 1; // 120 BPM: one cycle per 0.5 s
            var t = Channel(Render(Cue(trem, MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1), 0, 0, Rate);
            Assert.Greater(Rms(t.Skip(Rate / 8 - 240).Take(480).ToArray()), Rms(t.Skip(Rate * 3 / 8 - 240).Take(480).ToArray()) * 10, "tremolo follows the beat");
            var faded = Plain(); faded.LfoPitchCents = 100; faded.LfoRateHz = 2; faded.LfoFadeSeconds = 2;
            var f = Channel(Render(Cue(faded, MusicPlaybackVersion.V3, new MusicNote(0, 4, 69)), 1), 0, 0, Rate);
            Assert.AreEqual(440, Frequency(f.Skip(Rate / 8 - 1200).Take(2400).ToArray()), 2, "vibrato fades in");
        }

        [Test]
        public void UnisonSpreadIsStereoAndCentredUnisonIsMono()
        {
            var mono = Render(Cue(Plain(MusicSynthWave.Saw), MusicPlaybackVersion.V3, new MusicNote(0, 2, 60)), .5);
            for (int i = 0; i < mono.Length; i += 2) Assert.AreEqual(mono[i], mono[i + 1]);
            var wide = Plain(MusicSynthWave.Saw); wide.Unison = 5; wide.UnisonSpread = 1; wide.UnisonDetuneCents = 20;
            var pcm = Render(Cue(wide, MusicPlaybackVersion.V3, new MusicNote(0, 2, 60)), .5);
            double difference = 0; for (int i = Rate / 10; i < Rate / 2; i++) difference += Math.Abs(pcm[i * 2] - pcm[i * 2 + 1]);
            Assert.Greater(difference / (Rate * .4), .01);
            CollectionAssert.AreEqual(pcm, Render(Cue(wide, MusicPlaybackVersion.V3, new MusicNote(0, 2, 60)), .5), "deterministic");
            var noise = Plain(); noise.Osc1Level = 0; noise.NoiseLevel = 1;
            CollectionAssert.AreEqual(Render(Cue(noise, MusicPlaybackVersion.V3, new MusicNote(0, 1, 60)), .5), Render(Cue(noise, MusicPlaybackVersion.V3, new MusicNote(0, 1, 60)), .5), "seeded noise");
        }

        [Test]
        public void SettingsValidateAndCompileFromTheSongAsset()
        {
            Assert.Throws<ArgumentException>(() => new MusicSynthPatch(new MusicSynthSettings { Enabled = true, Unison = 9 }));
            Assert.Throws<ArgumentException>(() => new MusicSynthPatch(new MusicSynthSettings { Enabled = true, CutoffHz = float.NaN }));
            Assert.Throws<ArgumentException>(() => new MusicSynthPatch(new MusicSynthSettings { Enabled = true, Osc1Level = 0 }));
            foreach (string name in MusicSynthPresets.Names) Assert.IsNotNull(MusicSynthPresets.Create(name).Compile(), name);
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Synth song"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.LoopBeats = 4;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Lead", Notes = new[] { new MusicNote(0, 1, 60) }, Synth = MusicSynthPresets.Create("Pluck") } };
                Assert.IsNotNull(song.Compile().GetPart(0).Instrument.Synth);
                song.Parts[0].Synth.Unison = 0;
                var error = Assert.Throws<InvalidOperationException>(() => song.Compile());
                Assert.IsTrue(error.Message.Contains("Lead"), error.Message);
                song.Parts[0].Synth.Unison = 1; song.Parts[0].StretchBpm = 100;
                Assert.Throws<ArgumentException>(() => song.Compile(), "time stretch needs a sample");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void SynthVoicesFollowGlideDriveAndAutomation()
        {
            var s = Plain();
            var glide = new MusicPart(MusicLayer.Melody, new MusicInstrument(s.Compile()), .5, "Glide", new[] { new MusicNote(0, 2.5, 57), new MusicNote(2, 2, 69) }, 0, glideSeconds: .2);
            var cue = new MusicCue("Glide", 120, 4, 8, new[] { glide }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3);
            var pcm = Channel(Render(cue, 2), 0, 0, Rate * 2);
            Assert.AreEqual(220, Frequency(pcm.Skip(Rate / 4).Take(Rate / 2).ToArray()), 1);
            Assert.AreEqual(440, Frequency(pcm.Skip(Rate * 13 / 10).Take(Rate / 2).ToArray()), 1, "one voice slid to the new pitch");
            var driven = new MusicPart(MusicLayer.Melody, new MusicInstrument(s.Compile()), .5, "Drive", new[] { new MusicNote(0, 4, 57) }, 0, driveDb: 30, driveMode: MusicDriveMode.Hard);
            var clean = Channel(Render(Cue(s, MusicPlaybackVersion.V3, new MusicNote(0, 4, 57)), .5), 0, Rate / 10, Rate / 2);
            var hard = Channel(Render(new MusicCue("Drive", 120, 4, 8, new[] { driven }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3), .5), 0, Rate / 10, Rate / 2);
            Assert.Greater(Brightness(hard), Brightness(clean) * 2, "track drive shapes the synth");
        }
    }
}
