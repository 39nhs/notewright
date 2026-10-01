using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class AdaptiveMusicTests
    {
        private static MusicCue Cue(double bpm) => MusicCue.FromScore(MusicScore.CreateDemo(), MusicMood.Boss, "Test " + bpm, bpm);

        [Test]
        public void TempoRampIsQuantizedAndTransportNeverJumpsBack()
        {
            var engine = new AdaptiveMusicEngine(Cue(120), 8000);
            engine.Render(new float[4000]);
            var target = Cue(90);
            engine.TransitionTo(target, 4);
            engine.Render(new float[12000]);
            Assert.IsFalse(engine.IsTransitioning);
            Assert.AreEqual(4, engine.Beat, 1e-8);
            engine.Render(new float[1]);
            Assert.IsTrue(engine.IsTransitioning);
            double priorBeat = engine.Beat, priorBpm = engine.Bpm;
            var block = new float[400];
            for (int i = 0; i < 80; i++)
            {
                engine.Render(block);
                Assert.Greater(engine.Beat, priorBeat);
                Assert.LessOrEqual(engine.Bpm, priorBpm + 1e-8);
                Assert.That(engine.Bpm, Is.InRange(90, 120));
                priorBeat = engine.Beat; priorBpm = engine.Bpm;
            }
            Assert.AreSame(target, engine.CurrentCue);
            Assert.AreEqual(90, engine.Bpm);
        }

        [Test]
        public void LatestRequestWaitsUntilActiveTransitionFinishes()
        {
            var engine = new AdaptiveMusicEngine(Cue(120), 8000);
            var first = Cue(100); var ignored = Cue(130); var last = Cue(150);
            engine.TransitionTo(first);
            engine.Render(new float[17000]);
            Assert.IsTrue(engine.IsTransitioning);
            engine.TransitionTo(ignored); engine.TransitionTo(last);
            engine.Render(new float[100000]);
            Assert.AreSame(last, engine.CurrentCue);
            Assert.AreEqual(150, engine.Bpm);
        }

        [Test]
        public void AdaptiveRenderingIsIndependentOfBufferSize()
        {
            var cue = Cue(120); var target = Cue(96);
            var a = new AdaptiveMusicEngine(cue, 8000); var b = new AdaptiveMusicEngine(cue, 8000);
            a.TransitionTo(target); b.TransitionTo(target);
            var all = new float[48000]; var piece = new float[125]; a.Render(all);
            for (int start = 0; start < all.Length; start += piece.Length)
            {
                b.Render(piece);
                for (int i = 0; i < piece.Length; i++) Assert.AreEqual(all[start + i], piece[i]);
            }
        }

        [Test]
        public void SamplePitchStaysAtA440DuringTempoRamp()
        {
            const int rate = 8000;
            var pcm = new float[rate * 8];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)Math.Sin(2 * Math.PI * 440 * i / rate);
            var part = new MusicPart(MusicLayer.Bass, new MusicInstrument(pcm, rate, 69), .2, new MusicNote(0, 100, 69));
            var source = new MusicCue("A", 120, 4, 128, part);
            var target = new MusicCue("B", 60, 4, 128, part);
            var engine = new AdaptiveMusicEngine(source, rate);
            engine.TransitionTo(target, 8);
            engine.Render(new float[rate * 2]);
            var audio = new float[rate]; engine.Render(audio);
            int crossings = 0;
            for (int i = 1; i < audio.Length; i++) if (audio[i - 1] <= 0 && audio[i] > 0) crossings++;
            Assert.That(crossings, Is.InRange(439, 441));
            Assert.Less(engine.Bpm, 120);
            Assert.Greater(engine.Bpm, 60);
        }

        [Test]
        public void MelodyCanFadeWithoutMutingBassAndVoicePoolIsBounded()
        {
            var engine = new AdaptiveMusicEngine(Cue(120), 8000);
            engine.SetLayerGain(MusicLayer.Melody, 0);
            var audio = new float[8000]; engine.Render(audio);
            Assert.Greater(audio.Sum(v => v * v), 1);
            Assert.IsTrue(audio.All(v => !float.IsNaN(v) && Math.Abs(v) <= 1));
            Assert.AreEqual(0, engine.DroppedNotes);
            var crowded = new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .01,
                Enumerable.Range(0, 200).Select(i => new MusicNote(0, 4, 60)).ToArray());
            var capped = new AdaptiveMusicEngine(new MusicCue("Crowded", 120, 4, 4, crowded), 8000);
            capped.Render(new float[10]);
            Assert.AreEqual(200 - MusicPlaybackVersion.VoiceLimit(MusicPlaybackVersion.V1), capped.DroppedNotes, "Code-built cues are V1: 128 voices");
        }

        // SMF fixture: 480 PPQN, explicit and running note-ons, CC64 sustain, two tempos.
        private static byte[] Midi(params byte[] events)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 77,84,104,100,0,0,0,6,0,0,0,1,1,224,77,84,114,107 });
                writer.Write(new byte[] { (byte)(events.Length >> 24), (byte)(events.Length >> 16), (byte)(events.Length >> 8), (byte)events.Length });
                writer.Write(events); return stream.ToArray();
            }
        }

        [Test]
        public void MidiReadsChordsRunningStatusVelocityZeroAndSustain()
        {
            var result = MidiMusicReader.Read(Midi(
                0, 0xff, 0x51, 3, 7, 0xa1, 0x20,
                0, 0xc0, 5,
                0, 0x90, 60, 100, 0, 64, 80,
                0, 0xb0, 64, 127,
                0x83, 0x60, 0x90, 60, 0, 0, 64, 0,
                0x83, 0x60, 0xb0, 64, 0,
                0, 0xff, 0x51, 3, 9, 0x27, 0xc0,
                0, 0xff, 0x2f, 0));
            Assert.AreEqual(120, result.Bpm);
            Assert.AreEqual(2, result.Tempos.Count);
            Assert.AreEqual(100, result.Tempos[1].Bpm);
            Assert.AreEqual(5, result.Parts[0].Program);
            Assert.AreEqual(2, result.Parts[0].Notes.Count);
            Assert.AreEqual(2, result.Parts[0].Notes[0].Duration);
            Assert.AreEqual(100 / 127f, result.Parts[0].Notes[0].Velocity);
        }

        [Test]
        public void MidiRejectsCorruptLengthsAndUnsupportedDivision()
        {
            Assert.Throws<InvalidDataException>(() => MidiMusicReader.Read(new byte[0]));
            var bytes = Midi(0, 0x90, 60); // missing velocity
            Assert.Throws<InvalidDataException>(() => MidiMusicReader.Read(bytes));
            var smpte = Midi(0, 0xff, 0x2f, 0); smpte[12] = 0xe7;
            Assert.Throws<InvalidDataException>(() => MidiMusicReader.Read(smpte));
            Assert.Throws<InvalidDataException>(() => MidiMusicReader.Read(Midi(0, 0xff, 3, 127, 1)));
        }
    }
}
