using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPlaybackVersionTests
    {
        [TestCase("Oscillators")]
        [TestCase("Samples")]
        [TestCase("Master")]
        [TestCase("Dense")]
        [TestCase("Glide")]
        [TestCase("Drive")]
        [TestCase("TrackFx")]
        public void EveryReleasedPlaybackVersionKeepsItsSound(string scenario)
        {
            var missing = new StringBuilder();
            for (int version = MusicPlaybackVersion.Oldest; version <= MusicPlaybackVersion.Latest; version++)
                using (var song = MusicPlaybackScenarios.Build(scenario))
                {
                    var actual = Fingerprint(MusicPlaybackScenarios.Render(song.Definition.Compile(version), song.AllowDrops));
                    string key = MusicPlaybackFingerprints.Key(version, scenario);
                    if (!MusicPlaybackFingerprints.Values.TryGetValue(key, out var expected))
                    {
                        missing.AppendLine("            [\"" + key + "\"] = new[] { " + string.Join(", ", actual.Select(x => x.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))) + " },");
                        continue;
                    }
                    Assert.AreEqual(expected.Length, actual.Length, key);
                    for (int i = 0; i < expected.Length; i++)
                        Assert.IsTrue(Math.Abs(actual[i] - expected[i]) <= 2e-6 + 1e-5 * Math.Abs(expected[i]),
                            $"Playback V{version} changed its sound in scenario {scenario} (block {i / 4}, {(i % 2 == 0 ? "rms" : "roughness")} " +
                            $"{(i % 4 < 2 ? "L" : "R")}: recorded {expected[i]:G9}, now {actual[i]:G9}). Released versions are frozen: put the change " +
                            "behind a new MusicPlaybackVersion (engine/playback-versions.md) instead of re-recording this fingerprint.");
                }
            Assert.IsTrue(missing.Length == 0, "Record the new version's fingerprints in MusicPlaybackFingerprints.Values after listening to it:\n" + missing);
        }

        [Test]
        public void RecordedVersionsStayPlayable()
        {
            // Dropping a version would make every song that uses it fail to load.
            foreach (string key in MusicPlaybackFingerprints.Values.Keys)
                Assert.IsTrue(MusicPlaybackVersion.IsSupported(int.Parse(key.Substring(1, key.IndexOf('/') - 1))), key);
            Assert.AreEqual(MusicPlaybackVersion.V1, MusicPlaybackVersion.Oldest);
        }

        [Test]
        public void SongsSavedBeforeVersioningPlayAsV1()
        {
            using (var song = MusicPlaybackScenarios.Build("Samples"))
            {
                Assert.AreEqual(0, song.Definition.PlaybackVersion, "Unrecorded assets deserialize as 0");
                var unrecorded = song.Definition.Compile();
                Assert.AreEqual(MusicPlaybackVersion.V1, MusicPlaybackVersion.Unrecorded, "Unrecorded songs stay on the module current when versioning began");
                Assert.AreEqual(MusicPlaybackVersion.Unrecorded, unrecorded.PlaybackVersion);
                song.Definition.PlaybackVersion = MusicPlaybackVersion.V1;
                CollectionAssert.AreEqual(MusicPlaybackScenarios.Render(unrecorded), MusicPlaybackScenarios.Render(song.Definition.Compile()));
            }
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .1, "Bell", new MusicNote(0, 1, 60));
            Assert.AreEqual(MusicPlaybackVersion.V1, new MusicCue("Code-built", 120, 4, 4, part).PlaybackVersion, "Code-built cues default to V1, not Latest");
        }

        [Test]
        public void UnknownVersionsFailInsteadOfPlayingDifferently()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Future"; song.Parts = new[] { new MusicCueDefinition.Part { Name = "Bell", Notes = new[] { new MusicNote(0, 1, 60) } } };
                song.PlaybackVersion = MusicPlaybackVersion.Latest + 1;
                var error = Assert.Throws<ArgumentException>(() => song.Compile());
                StringAssertContains(error.Message, "com.graze.music");
                song.PlaybackVersion = -1; Assert.Throws<ArgumentException>(() => song.Compile());
                song.PlaybackVersion = 0; song.Parts[0].PlaybackVersion = MusicPlaybackVersion.Latest + 1;
                Assert.Throws<ArgumentException>(() => song.Compile());
                song.Parts[0].PlaybackVersion = 0;
                Assert.Throws<ArgumentException>(() => song.Compile(MusicPlaybackVersion.Latest + 1));
                Assert.DoesNotThrow(() => song.Compile());
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void TrackPinOverridesSongVersionAndAuditionLeavesAssetUnchanged()
        {
            using (var song = MusicPlaybackScenarios.Build("Oscillators"))
            {
                song.Definition.PlaybackVersion = MusicPlaybackVersion.Latest;
                song.Definition.Parts[1].PlaybackVersion = MusicPlaybackVersion.V1;
                var cue = song.Definition.Compile(MusicPlaybackVersion.V1);
                Assert.AreEqual(MusicPlaybackVersion.Latest, song.Definition.PlaybackVersion, "Audition compile does not edit the song");
                Assert.AreEqual(MusicPlaybackVersion.V1, cue.PlaybackVersion);
                Assert.AreEqual(0, cue.GetPart(0).PlaybackVersion, "Unpinned track follows the song");
                Assert.AreEqual(MusicPlaybackVersion.V1, cue.GetPart(1).PlaybackVersion);
                Assert.AreEqual(MusicPlaybackVersion.V1, cue.PlaybackVersionOf(cue.GetPart(0)));
                Assert.AreEqual(MusicPlaybackVersion.V1, cue.PlaybackVersionOf(cue.GetPart(1)));
                foreach (int v in Enumerable.Range(MusicPlaybackVersion.Oldest, MusicPlaybackVersion.Latest))
                    Assert.IsFalse(MusicPlaybackVersion.Describe(v).Contains("unknown"), "Every supported version is described for the Arranger");
            }
        }

        /// <summary>Per block and channel: RMS and mean absolute sample-to-sample change (catches timbre changes that keep loudness).</summary>
        internal static double[] Fingerprint(float[] stereo)
        {
            const int blocks = 16;
            int frames = stereo.Length / 2, size = frames / blocks;
            var result = new double[blocks * 4];
            for (int b = 0; b < blocks; b++)
                for (int channel = 0; channel < 2; channel++)
                {
                    double energy = 0, change = 0;
                    for (int f = b * size; f < (b + 1) * size; f++)
                    {
                        double x = stereo[f * 2 + channel];
                        energy += x * x;
                        if (f > 0) change += Math.Abs(x - stereo[(f - 1) * 2 + channel]);
                    }
                    result[b * 4 + channel * 2] = Math.Sqrt(energy / size);
                    result[b * 4 + channel * 2 + 1] = change / size;
                }
            return result;
        }

        static void StringAssertContains(string text, string part) => Assert.IsTrue(text.Contains(part), text);
    }
}
