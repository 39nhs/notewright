using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPositionTransitionTests
    {
        private static MusicCue Cue(string name, double beats = 16, double bpm = 120) => new MusicCue(name, bpm, 4, beats,
            new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bass), .1, "lead", new MusicNote(0, beats, 69)));

        [Test]
        public void ShortRemainingSongFinishesBeforeStartEntryEvenBetweenBars()
        {
            var source = Cue("A", 15); // 7.5 seconds, not a whole number of bars
            var engine = new AdaptiveMusicEngine(source, 8000);
            engine.Render(new float[52000]); // 6.5s
            engine.TransitionTo(Cue("B"), MusicTransitionMode.ToStart, 2);
            engine.Render(new float[7999]);
            Assert.IsFalse(engine.IsTransitioning);
            engine.Render(new float[2]);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(15, engine.LastTransitionStartBeat, 1e-7);
            Assert.AreEqual(0, engine.LastEntryBeat);
        }

        [Test]
        public void MiddleEntryUsesSamePercentageOfDifferentLengthCue()
        {
            var engine = new AdaptiveMusicEngine(Cue("Short"), 8000);
            engine.Render(new float[32000]);
            engine.TransitionTo(Cue("Long", 32), MusicTransitionMode.MatchProgress, 4);
            engine.Render(new float[1]);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(.5, engine.LastSourceProgress, 1e-7);
            Assert.AreEqual(16, engine.LastEntryBeat, 1e-7);
            var audio = new float[32000]; engine.Render(audio);
            Assert.AreEqual("Long", engine.CurrentCue.Name);
            Assert.Greater(audio.Skip(16000).Sum(x => x * x), 1, "Held note must be restored at middle entry");
        }

        [Test]
        public void ForcedFadeStartsImmediatelyAndDisablesBridge()
        {
            var engine = new AdaptiveMusicEngine(Cue("A"), 8000);
            engine.Render(new float[26000]);
            engine.TransitionTo(Cue("B"), MusicTransitionMode.ForceFadeToStart, 2);
            engine.Render(new float[1]);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(6.5, engine.LastTransitionStartBeat, 1e-6);
            Assert.AreEqual(0, engine.LastEntryBeat);
            Assert.IsFalse(engine.UsesHarmonicBridge);
            engine.Render(new float[16001]);
            Assert.AreEqual("B", engine.CurrentCue.Name);
        }

        [Test]
        public void MaximumTimeIncludesSearchAndBlend()
        {
            var engine = new AdaptiveMusicEngine(Cue("A", 128, 150), 8000);
            engine.Render(new float[2345]);
            engine.TransitionTo(Cue("B", 128, 60), MusicTransitionMode.MatchProgress, .3);
            engine.Render(new float[2401]);
            Assert.IsFalse(engine.IsTransitioning);
            Assert.AreEqual("B", engine.CurrentCue.Name);
        }

        [Test]
        public void PositionedRenderingIsBufferIndependent()
        {
            var source = Cue("A"); var target = Cue("B", 32, 95);
            var a = new AdaptiveMusicEngine(source, 8000); var b = new AdaptiveMusicEngine(source, 8000);
            a.Render(new float[23125]); b.Render(new float[23125]);
            a.TransitionTo(target, MusicTransitionMode.MatchProgress, 3);
            b.TransitionTo(target, MusicTransitionMode.MatchProgress, 3);
            var all = new float[48000]; var block = new float[125]; a.Render(all);
            for (int start = 0; start < all.Length; start += block.Length)
            { b.Render(block); for (int i = 0; i < block.Length; i++) Assert.AreEqual(all[start + i], block[i]); }
        }

        [Test]
        public void FixedTwoSecondFadeDoesNotShrinkWhenTempoRises()
        {
            var engine = new AdaptiveMusicEngine(Cue("A", 128, 120), 8000);
            engine.TransitionTo(Cue("B", 128, 180), MusicTransitionMode.MatchProgress, 2, 4, 2);
            engine.Render(new float[15999]);
            Assert.IsTrue(engine.IsTransitioning);
            engine.Render(new float[2]);
            Assert.IsFalse(engine.IsTransitioning);
            Assert.AreEqual("B", engine.CurrentCue.Name);
            Assert.AreEqual(180, engine.Bpm);
            Assert.AreEqual(5, engine.Beat, .001);
        }

        [Test]
        public void OneShotScoreDoesNotRestartMelodyOrDrumsAtItsEnd()
        {
            var cue = new MusicCue("One shot", 120, 4, 4,
                new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .1, new MusicNote(0, .2, 60)),
                new MusicPart(MusicLayer.Drums, new MusicInstrument(MusicVoice.Kick), .1, new MusicNote(0, .2, 60)));
            var engine = new AdaptiveMusicEngine(cue, 8000, 2, 1, 3, playOnce: true);
            var audio = new float[40000]; engine.Render(audio);
            Assert.AreEqual(1, engine.DrumNotesScheduled);
            Assert.AreEqual(0, engine.RepeatTransitions);
            Assert.IsTrue(audio.Skip(16000).All(x => x == 0));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void SongChangeOverlapsForFiveSecondsUnlessGapIsExplicit(double gap)
        {
            var instrument = new MusicInstrument(Enumerable.Repeat(1f, 320000).ToArray(), 8000, 69);
            MusicCue Constant(string name) => new MusicCue(name, 120, 4, 64,
                new MusicPart(MusicLayer.Melody, instrument, .1, "lead", new MusicNote(0, 64, 69)));
            var engine = new AdaptiveMusicEngine(Constant("A"), 8000, 2, 1, 3);
            engine.Render(new float[8000]);
            engine.TransitionTo(Constant("B"), MusicTransitionMode.MatchProgress, 2, 4, 2, false, 5, gap);
            var audio = new float[64001]; engine.Render(audio);
            if (gap == 0)
            {
                Assert.IsTrue(audio.Take(40000).All(x => x > 0), "No zero-gain handoff");
                Assert.AreEqual(Math.Tanh(.1 * (.5 + .028)), audio[4000], 1e-5, "Both songs sound at half a second");
                Assert.AreEqual(Math.Tanh(.1 * .104), audio[8000], 1e-5);
                Assert.AreEqual(Math.Tanh(.1), audio[40000], 1e-5);
            }
            else Assert.IsTrue(audio.Skip(8000).Take(8000).All(x => x == 0));
            Assert.AreEqual("B", engine.CurrentCue.Name);
            Assert.AreEqual(0, engine.RepeatTransitions);
        }

        [Test]
        public void SameCueRepeatsThroughSequentialFadeOnEveryCycle()
        {
            var cue = Cue("Repeat", 17, 120);
            var engine = new AdaptiveMusicEngine(cue, 8000, 2);
            engine.Render(new float[56001]); // 7s: final complete bar ends at 8s
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(1, engine.RepeatTransitions);
            Assert.AreEqual(16, engine.LastHandoffBeat, 1e-8);
            Assert.AreEqual(0, engine.LastEntryBeat);
            engine.Render(new float[8000]);
            Assert.AreEqual(.5, engine.TransitionProgress, .001);
            engine.Render(new float[8000]);
            Assert.IsFalse(engine.IsTransitioning);
            Assert.AreSame(cue, engine.CurrentCue);
            engine.Render(new float[48000]);
            Assert.AreEqual(2, engine.RepeatTransitions);
            Assert.AreEqual(32, engine.LastHandoffBeat, 1e-8);
            Assert.AreEqual(0, engine.DroppedNotes);
        }

        [Test]
        public void RepeatHasOneSecondOfDigitalSilenceThenThreeSecondWholeMixRise()
        {
            var instrument = new MusicInstrument(Enumerable.Repeat(1f, 320000).ToArray(), 8000, 69);
            var cue = new MusicCue("Rest", 120, 4, 16,
                new MusicPart(MusicLayer.Melody, instrument, .1, "lead", new MusicNote(0, 16, 69)),
                new MusicPart(MusicLayer.Drums, instrument, .1, "drum", new MusicNote(0, 16, 69)));
            var engine = new AdaptiveMusicEngine(cue, 8000, 2, 1, 3);
            var audio = new float[13 * 8000]; engine.Render(audio);
            Assert.IsTrue(audio.Skip(8 * 8000).Take(8000).All(x => x == 0), "All instruments and tails must be silent for the entire rest");
            double full = Math.Tanh(.2);
            Assert.AreEqual(full * (7.0 / 27), audio[10 * 8000], 1e-5, "One second into the rise is only 26 percent gain");
            Assert.AreEqual(full * (20.0 / 27), audio[11 * 8000], 1e-5);
            Assert.AreEqual(full, audio[12 * 8000], 1e-5);
            Assert.AreEqual(24, engine.Beat, .001, "The musical clock rests for one second too");
            Assert.AreEqual(1, engine.RepeatTransitions);
            Assert.AreEqual(0, engine.DroppedNotes);
        }

        [Test]
        public void RestAndSlowRestartAreIndependentOfAudioBufferSize()
        {
            var cue = Cue("Rest", 16);
            var a = new AdaptiveMusicEngine(cue, 8000, 2, 1, 3);
            var b = new AdaptiveMusicEngine(cue, 8000, 2, 1, 3);
            var all = new float[24 * 8000]; a.Render(all);
            var block = new float[125];
            for (int start = 0; start < all.Length; start += block.Length)
            {
                b.Render(block);
                for (int i = 0; i < block.Length; i++) Assert.AreEqual(all[start + i], block[i]);
            }
            Assert.AreEqual(2, a.RepeatTransitions);
        }

        [Test]
        public void ExternalCueRequestTakesPriorityOverAutomaticRepeat()
        {
            var engine = new AdaptiveMusicEngine(Cue("A"), 8000, 2);
            engine.Render(new float[52000]);
            engine.TransitionTo(Cue("B"), MusicTransitionMode.MatchProgress, 2, 4, 2, true);
            engine.Render(new float[24001]);
            Assert.AreEqual("B", engine.CurrentCue.Name);
            Assert.AreEqual(0, engine.RepeatTransitions);
        }

        [TestCase(128, 140)]
        [TestCase(140, 96)]
        public void BarAlignedFadeCentersOnSourceBarAndSnapsTargetEntry(double from, double to)
        {
            var source = Cue("A", 64, from);
            var target = Cue("B", 70, to);
            var engine = new AdaptiveMusicEngine(source, 8000);
            engine.Render(new float[72000]); // request at nine seconds, between bars
            engine.TransitionTo(target, MusicTransitionMode.MatchProgress, 2, 4, 2, true);
            for (int i = 0; i < 40000 && !engine.IsTransitioning; i++) engine.Render(new float[1]);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.AreEqual(0, engine.LastHandoffBeat % 4, 1e-7);
            Assert.AreEqual(0, engine.LastEntryBeat % 4, 1e-7);
            Assert.LessOrEqual(Math.Abs(engine.LastEntryBeat - engine.LastSourceProgress * 70), 2);
            double expectedStart = engine.LastTransitionStartSeconds;
            Assert.GreaterOrEqual(expectedStart, 9);
            engine.Render(new float[8000]);
            Assert.AreEqual(.5, engine.TransitionProgress, .0002);
            Assert.AreEqual(engine.LastHandoffBeat, engine.Beat, .001);
            engine.Render(new float[8001]);
            Assert.AreEqual(target, engine.CurrentCue);
            Assert.IsFalse(engine.IsTransitioning);
        }

        [Test]
        public void FixedHandoffFadesOutThenInWithoutBridgeOrTonalOverlap()
        {
            MusicCue Constant(string name, float value) => new MusicCue(name, 120, 4, 32,
                new MusicPart(MusicLayer.Melody,
                    new MusicInstrument(Enumerable.Repeat(value, 320000).ToArray(), 8000, 69), .1, "lead",
                    new MusicNote(0, 32, 69), new MusicNote(0, 32, 73)));
            var engine = new AdaptiveMusicEngine(Constant("A", 1), 8000);
            engine.Render(new float[8000]);
            engine.TransitionTo(Constant("B", -1), MusicTransitionMode.MatchProgress, 2, 4, 2);
            var audio = new float[16001]; engine.Render(audio);
            Assert.IsFalse(engine.UsesHarmonicBridge);
            Assert.AreEqual(0, engine.BridgeNotesScheduled);
            Assert.AreEqual(Math.Tanh(.1), audio[4000], 1e-5, "Half gain at 0.5 seconds");
            Assert.AreEqual(0, audio[8000], 1e-6, "Silent tonal handoff at one second");
            Assert.AreEqual(-Math.Tanh(.1), audio[12000], 1e-5, "Half gain at 1.5 seconds");
            Assert.IsTrue(audio.Take(8000).All(x => x >= 0));
            Assert.IsTrue(audio.Skip(8000).All(x => x <= 0));
        }
    }
}
