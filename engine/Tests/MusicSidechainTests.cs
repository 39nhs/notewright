using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSidechainTests
    {
        static double Rms(float[] pcm, int fromFrame, int toFrame)
        { double e = 0; for (int i = fromFrame * 2; i < toFrame * 2; i++) e += pcm[i] * pcm[i]; return Math.Sqrt(e / ((toFrame - fromFrame) * 2)); }

        /// <summary>Bass (and pad) alone through a compressed bus, keyed by a kick track that is muted at the output so only the ducking is heard.</summary>
        static MusicCueDefinition Ducked(MusicPlaybackScenarios.Song song, string key, bool keyAudible = false)
        {
            var d = song.Definition;
            d.Buses = new[] { new MusicBusDefinition { Name = "Duck", Effects = new MusicMixerSettings { Compressor = true, ThresholdDb = -40, Ratio = 20, AttackMs = 1, ReleaseMs = 100, SidechainTrack = key } },
                new MusicBusDefinition { Name = "Silent", Effects = new MusicMixerSettings { OutputDb = -36 } } };
            d.Parts[1].Output = "Duck";
            d.Parts = new[] { d.Parts[1], d.Parts[3] }; // bass + kick
            if (!keyAudible) d.Parts[1].Output = "Silent";
            return d;
        }

        [Test]
        public void SidechainsAreValidated()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d = Ducked(song, "Nope");
                Assert.Throws<ArgumentException>(() => d.Compile(MusicPlaybackVersion.V3), "unknown key track");
                d.Buses[0].Effects.SidechainTrack = "Kick"; d.Buses[0].Effects.Compressor = false;
                Assert.Throws<ArgumentException>(() => d.Compile(MusicPlaybackVersion.V3), "compressor off");
                d.Buses[0].Effects.Compressor = true; d.Parts[0].InstrumentId = "Kick";
                Assert.Throws<ArgumentException>(() => d.Compile(MusicPlaybackVersion.V3), "two tracks called Kick");
                d.Parts[0].InstrumentId = "Bass";
                Assert.DoesNotThrow(() => d.Compile(MusicPlaybackVersion.V3));
            }
        }

        [Test]
        public void TheKeyDucksTheBusAndASilentKeyDoesNothing()
        {
            using (var plain = MusicPlaybackScenarios.Build("Oscillators"))
            using (var ducked = MusicPlaybackScenarios.Build("Oscillators"))
            {
                var reference = Ducked(plain, "Kick"); reference.Buses[0].Effects.Compressor = false; reference.Buses[0].Effects.SidechainTrack = "";
                var d = Ducked(ducked, "Kick");
                var open = MusicPlaybackScenarios.Render(reference.Compile(MusicPlaybackVersion.V3));
                var duck = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                // The kick hits every beat; the bass under it must be quieter right after each hit than without the sidechain.
                int beat = (int)(MusicPlaybackScenarios.SampleRate * 60 / 132.0);
                Assert.Less(Rms(duck, 0, beat / 4), Rms(open, 0, beat / 4) * .5, "ducked under the first kick");
                // A muted key track (no notes sound) leaves the compressor idle: identical to the compressor switched off.
                d.Parts[1].Mute = true;
                var idle = MusicPlaybackScenarios.Render(d.Compile(MusicPlaybackVersion.V3));
                reference.Parts[1].Mute = true;
                CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(reference.Compile(MusicPlaybackVersion.V3)), idle);
            }
        }

        [Test]
        public void OlderVersionsIgnoreTheSidechain()
        {
            using (var a = MusicPlaybackScenarios.Build("Oscillators"))
            using (var b = MusicPlaybackScenarios.Build("Oscillators"))
            {
                a.Definition.Mixer.Compressor = true; b.Definition.Mixer.Compressor = true; b.Definition.Mixer.SidechainTrack = "Kick";
                for (int version = MusicPlaybackVersion.V1; version <= MusicPlaybackVersion.V2; version++)
                    CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(a.Definition.Compile(version)), MusicPlaybackScenarios.Render(b.Definition.Compile(version)), "V" + version);
                double difference = 0; var x = MusicPlaybackScenarios.Render(a.Definition.Compile(MusicPlaybackVersion.V3)); var y = MusicPlaybackScenarios.Render(b.Definition.Compile(MusicPlaybackVersion.V3));
                for (int i = 0; i < x.Length; i++) difference += Math.Abs(x[i] - y[i]);
                Assert.Greater(difference, .1, "the V3 master compressor follows the kick");
            }
        }

        [Test]
        public void MashupClearsKeysThatAreMissingOrAmbiguous()
        {
            var a = ScriptableObject.CreateInstance<MusicCueDefinition>(); var b = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                foreach (var s in new[] { a, b })
                {
                    s.Bpm = 120; s.LoopBeats = 4; s.PlaybackVersion = MusicPlaybackVersion.V3;
                    s.Parts = new[] { new MusicCueDefinition.Part { Name = "Kick", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Kick, Notes = new[] { new MusicNote(0, .5, 36) } } };
                }
                a.name = "A"; b.name = "B"; a.Mixer.Compressor = true; a.Mixer.SidechainTrack = "Kick";
                var result = MusicMashup.Combine(new[] { new MusicMashupLayer { Song = a }, new MusicMashupLayer { Song = b } });
                try
                {
                    Assert.AreEqual("", result.Song.Mixer.SidechainTrack, "both songs have a Kick: ambiguous");
                    Assert.IsTrue(result.Warnings.Any(w => w.Contains("sidechain")));
                    Assert.DoesNotThrow(() => result.Song.Compile());
                }
                finally { UnityEngine.Object.DestroyImmediate(result.Song); }
            }
            finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }
    }
}
