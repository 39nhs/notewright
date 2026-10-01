using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicTransitionTests
    {
        private static MusicPart Chord(int root, double start = 0, double duration = 16) => new MusicPart(
            MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .06, "Strings",
            new MusicNote(start, duration, root), new MusicNote(start, duration, root + 4), new MusicNote(start, duration, root + 7));

        private static MusicPart Drums() => new MusicPart(MusicLayer.Drums, new MusicInstrument(MusicVoice.Kick), .1, "Kick",
            Enumerable.Range(0, 16).Select(i => new MusicNote(i * .5, .2, 60)).ToArray());

        [Test]
        public void CompatibleChordsGenerateNotesForSharedInstruments()
        {
            var source = new MusicCue("C", 120, 4, 16, Chord(60), Drums());
            var target = new MusicCue("C higher", 120, 4, 16, Chord(72), Drums());
            var engine = new AdaptiveMusicEngine(source, 8000);
            engine.TransitionTo(target);
            engine.Render(new float[32001]);
            Assert.AreSame(target, engine.CurrentCue);
            Assert.IsTrue(engine.UsesHarmonicBridge);
            Assert.Greater(engine.BridgeNotesScheduled, 0);
            Assert.AreEqual(0, engine.DroppedNotes);
        }

        [TestCase(60, true)]
        [TestCase(67, true)]
        [TestCase(61, false)]
        public void FiveSecondOverlapGeneratesOnlyCompatibleBridgeNotes(int root, bool expected)
        {
            var engine = new AdaptiveMusicEngine(new MusicCue("A", 120, 4, 32, Chord(60, 0, 32)), 8000);
            engine.TransitionTo(new MusicCue("B", 120, 4, 32, Chord(root, 0, 32)),
                MusicTransitionMode.MatchProgress, 2, 4, 2, false, 5, 0, 5);
            engine.Render(new float[20000]);
            Assert.AreEqual(5, engine.LastFadeOutSeconds);
            Assert.AreEqual(5, engine.LastFadeInSeconds);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(expected, engine.BridgeNotesScheduled > 0);
            engine.Render(new float[20001]);
            Assert.IsFalse(engine.IsTransitioning);
            Assert.AreEqual(0, engine.DroppedNotes);
        }

        [Test]
        public void RemoteChordsFadeWithoutInventingConnectingNotes()
        {
            var engine = new AdaptiveMusicEngine(new MusicCue("C", 120, 4, 16, Chord(60), Drums()), 8000);
            var target = new MusicCue("Db", 120, 4, 16, Chord(61), Drums());
            engine.TransitionTo(target);
            var audio = new float[32001]; engine.Render(audio);
            Assert.AreSame(target, engine.CurrentCue);
            Assert.IsFalse(engine.UsesHarmonicBridge);
            Assert.AreEqual(0, engine.BridgeNotesScheduled);
            Assert.IsTrue(audio.All(x => !float.IsNaN(x) && Math.Abs(x) < 1));
        }

        [Test]
        public void PlannerWaitsForCompatibleUpcomingBarWithinBound()
        {
            var source = new MusicCue("Progression", 120, 4, 16, Chord(61, 0, 8), Chord(60, 8, 8));
            var target = new MusicCue("C", 120, 4, 16, Chord(60));
            var engine = new AdaptiveMusicEngine(source, 8000); engine.TransitionTo(target);
            engine.Render(new float[24000]); // beat 6; bar 4 was incompatible
            Assert.IsFalse(engine.IsTransitioning);
            engine.Render(new float[10000]);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.IsTrue(engine.UsesHarmonicBridge);
        }

        [Test]
        public void DrumAttacksAreNeitherDoubledNorLostAndEmptyCueKeepsGroove()
        {
            var source = new MusicCue("A", 120, 4, 8, Drums());
            var reference = new AdaptiveMusicEngine(source, 8000);
            var changing = new AdaptiveMusicEngine(source, 8000);
            changing.TransitionTo(new MusicCue("B", 120, 4, 8, Drums()));
            var a = new float[48000]; var b = new float[48000];
            reference.Render(a); changing.Render(b);
            Assert.AreEqual(reference.DrumNotesScheduled, changing.DrumNotesScheduled);
            for (int i = 0; i < a.Length; i++) Assert.That(b[i], Is.EqualTo(a[i]).Within(1e-5), "Drum phase/tail at " + i);
            changing.TransitionTo(new MusicCue("No drums", 120, 4, 8));
            reference.Render(a); changing.Render(b);
            Assert.AreEqual(reference.DrumNotesScheduled, changing.DrumNotesScheduled);
            Assert.Greater(b.Sum(v => v * v), 1);
        }

        [Test]
        public void BridgeNotesStayInCommonChordAndWithinMidiRange()
        {
            int common = (1 << 0) | (1 << 4) | (1 << 7);
            for (int key = 0; key < 128; key++)
            {
                int result = MusicHarmony.NearestCommonKey(common, key);
                Assert.That(result, Is.InRange(0, 127));
                Assert.AreNotEqual(0, common & (1 << (result % 12)));
                Assert.LessOrEqual(Math.Abs(result - key), MusicHarmony.MaximumBridgeLeap);
            }
            Assert.AreEqual(-1, MusicHarmony.NearestCommonKey(0, 60));
        }

        [Test]
        public void TempoRampKeepsExactlyOneDrumAttackPerHalfBeat()
        {
            var engine = new AdaptiveMusicEngine(new MusicCue("Fast", 144, 4, 8, Drums()), 8000);
            engine.TransitionTo(new MusicCue("Slow", 90, 4, 8, Drums()), 5); // rounded to two target bars
            var block = new float[257];
            for (int i = 0; i < 400; i++)
            {
                engine.Render(block);
                Assert.AreEqual((int)Math.Ceiling((engine.Beat - 1e-8) * 2), engine.DrumNotesScheduled);
            }
            Assert.AreEqual(90, engine.Bpm);
            Assert.AreEqual("Slow", engine.CurrentCue.Name);
        }

        [Test]
        public void SameRoleButDifferentInstrumentDoesNotGenerateBridge()
        {
            var source = new MusicCue("A", 120, 4, 16, Chord(60));
            var other = new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .06, "Other",
                new MusicNote(0, 16, 60), new MusicNote(0, 16, 64), new MusicNote(0, 16, 67));
            var engine = new AdaptiveMusicEngine(source, 8000);
            engine.TransitionTo(new MusicCue("B", 120, 4, 16, other));
            engine.Render(new float[32001]);
            Assert.AreEqual(0, engine.BridgeNotesScheduled);
            Assert.IsFalse(engine.UsesHarmonicBridge);
        }
    }
}
