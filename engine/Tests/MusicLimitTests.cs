using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Polyphony per playback version, tempo ceiling, loop length and the run-length harmony map.</summary>
    public sealed class MusicLimitTests
    {
        static MusicPart Cluster(int notes, int pin = 0) => new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .001, "Cluster",
            Enumerable.Range(0, notes).Select(i => new MusicNote(0, 3, 36 + i % 60)).ToArray(), 0, pin);

        static AdaptiveMusicEngine Play(MusicCue cue, int samples = 400)
        {
            var engine = new AdaptiveMusicEngine(cue, 8000);
            engine.Render(new float[samples]);
            return engine;
        }

        [Test]
        public void V2PlaysUpTo1024VoicesWhileV1KeepsItsOld128()
        {
            Assert.AreEqual(128, MusicPlaybackVersion.VoiceLimit(MusicPlaybackVersion.V1));
            Assert.AreEqual(1024, MusicPlaybackVersion.VoiceLimit(MusicPlaybackVersion.V2));
            var v1 = Play(new MusicCue("V1", 120, 4, 4, new[] { Cluster(500) }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V1));
            Assert.AreEqual(372, v1.DroppedNotes);
            Assert.AreEqual(128, v1.PeakVoices);
            var v2 = Play(new MusicCue("V2", 120, 4, 4, new[] { Cluster(500) }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V2));
            Assert.AreEqual(0, v2.DroppedNotes);
            Assert.AreEqual(500, v2.PeakVoices);
            Assert.AreEqual(500, v2.ActiveVoices);
            var full = Play(new MusicCue("Full", 120, 4, 4, new[] { Cluster(1100) }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V2));
            Assert.AreEqual(1100 - AdaptiveMusicEngine.VoiceCapacity, full.DroppedNotes);
        }

        [Test]
        public void TrackPinChangesSoundNotPolyphony()
        {
            var cue = new MusicCue("Pinned", 120, 4, 4, new[] { Cluster(500, MusicPlaybackVersion.V1) }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V2);
            Assert.AreEqual(0, Play(cue).DroppedNotes, "A V1-pinned track in a V2 song uses the song's 1024 voices");
        }

        [Test]
        public void MaxTempoPlaysEveryBeat()
        {
            var kick = new MusicPart(MusicLayer.Drums, new MusicInstrument(MusicVoice.Kick, .0001, .0001), .3, "Kick",
                Enumerable.Range(0, 4).Select(i => new MusicNote(i, .5, 36)).ToArray());
            var cue = new MusicCue("Extratone", MusicTempoMap.MaxBpm, 4, 4, kick);
            var engine = new AdaptiveMusicEngine(cue, 8000);
            var audio = new float[8000];
            engine.Render(audio);
            // 60000 BPM = 1000 beats per second, one hit per beat.
            Assert.That(engine.DrumNotesScheduled, Is.InRange(999, 1001));
            Assert.IsTrue(audio.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1));
            Assert.Throws<ArgumentException>(() => new MusicCue("Too fast", MusicTempoMap.MaxBpm + 1, 4, 4, kick));
        }

        [Test]
        public void TransitionsStillPlanAtMaxTempoWithLongFixedFades()
        {
            var part = new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .05, "Pad",
                new MusicNote(0, 4, 60), new MusicNote(0, 4, 64), new MusicNote(0, 4, 67));
            var from = new MusicCue("A", MusicTempoMap.MaxBpm, 4, 4, part);
            var to = new MusicCue("B", MusicTempoMap.MaxBpm, 4, 4, part);
            var engine = new AdaptiveMusicEngine(from, 8000);
            engine.Render(new float[100]);
            // A 60 s crossfade at 60000 BPM spans 60000 beats; harmony checks per candidate bar are capped (MaxBridgeChecks).
            engine.TransitionTo(to, MusicTransitionMode.MatchProgress, 120, 4, 0, false, 60, 0, 60);
            var audio = new float[16000];
            engine.Render(audio);
            Assert.IsTrue(engine.IsTransitioning);
            Assert.Greater(engine.TransitionProgress, 0);
            Assert.IsTrue(audio.All(x => !float.IsNaN(x) && Math.Abs(x) <= 1));
        }

        [Test]
        public void MaximumLoopLengthCompilesWithoutPerBeatMemory()
        {
            var part = new MusicPart(MusicLayer.Harmony, new MusicInstrument(MusicVoice.Pad), .05, "Pad",
                new MusicNote(0, 2, 60), new MusicNote(MusicCue.MaxLoopBeats - 4, 2, 67));
            var cue = new MusicCue("Marathon", 120, 4, MusicCue.MaxLoopBeats, part);
            Assert.AreEqual(1 << 0, MusicHarmony.MaskAt(cue, 1));
            Assert.AreEqual(1 << 7, MusicHarmony.MaskAt(cue, MusicCue.MaxLoopBeats - 3));
            Assert.AreEqual(0, MusicHarmony.MaskAt(cue, MusicCue.MaxLoopBeats / 2));
            Assert.Greater(cue.Tempo.SecondsAt(MusicCue.MaxLoopBeats), 24 * 3600 * 365, "Years of music at 120 BPM");
            Assert.Throws<ArgumentException>(() => new MusicCue("Too long", 120, 4, MusicCue.MaxLoopBeats * 2, part));
            var many = Enumerable.Repeat(new MusicNote(0, 1, 60), MusicPart.MaxNotes + 1).ToArray();
            Assert.Throws<ArgumentException>(() => new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .1, "Many", many));
            Assert.AreEqual(MusicPart.MaxNotes, new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Bell), .1, "Many",
                many.Take(MusicPart.MaxNotes).ToArray()).NoteCount);
        }

        [Test]
        public void RunLengthHarmonyMatchesThePerHalfBeatAnalysis()
        {
            var random = new Random(20261001);
            var noise = new MusicInstrument(new float[] { 0, .5f, -.5f, 0 }, 8000, 60, .01, .01, new NoisePlayback(0, 0, true, true));
            for (int trial = 0; trial < 300; trial++)
            {
                double loop = 4 + random.Next(0, 120) + (random.Next(0, 3) == 0 ? random.NextDouble() : 0);
                var parts = Enumerable.Range(0, random.Next(1, 6)).Select(p =>
                {
                    var layer = (MusicLayer)random.Next(0, 4);
                    var notes = Enumerable.Range(0, random.Next(0, 40)).Select(n => new MusicNote(
                        Math.Min(loop - 1e-3, random.Next(0, 4) == 0 ? random.NextDouble() * loop : Math.Floor(random.NextDouble() * loop * 4) / 4),
                        random.Next(0, 6) == 0 ? loop * (1 + random.NextDouble()) : .01 + random.NextDouble() * 6,
                        random.Next(0, 128), random.Next(0, 8) == 0 ? 0f : (float)random.NextDouble())).ToArray();
                    return new MusicPart(layer, random.Next(0, 10) == 0 ? noise : new MusicInstrument(MusicVoice.Bell),
                        random.Next(0, 8) == 0 ? 0 : .1, "P" + p, notes);
                }).ToArray();
                var cue = new MusicCue("Random", 120, 4, loop, parts);
                var expected = DenseReference(cue);
                for (int slot = 0; slot < expected.Length; slot++)
                    Assert.AreEqual(expected[slot], MusicHarmony.MaskAt(cue, slot * MusicHarmony.Resolution), $"trial {trial}, slot {slot}");
            }
        }

        /// <summary>The analysis as written before run-length storage (one mask per half beat), kept as the reference.</summary>
        static ushort[] DenseReference(MusicCue cue)
        {
            double resolution = MusicHarmony.Resolution;
            int count = (int)Math.Ceiling(cue.LoopBeats / resolution);
            var primary = new ushort[count];
            var melody = new ushort[count];
            var harmonicDiff = new int[12, count + 1];
            var melodicDiff = new int[12, count + 1];
            for (int p = 0; p < cue.PartCount; p++)
            {
                var part = cue.GetPart(p);
                if (part.Layer == MusicLayer.Drums || part.Gain == 0 || part.Instrument.Noise != null) continue;
                var diff = part.Layer == MusicLayer.Melody ? melodicDiff : harmonicDiff;
                for (int n = 0; n < part.NoteCount; n++)
                {
                    var note = part.GetNote(n);
                    if (note.Velocity == 0) continue;
                    int pc = note.Key % 12;
                    int first = (int)Math.Floor(note.Beat / resolution);
                    double endBeat = note.Beat + Math.Min(note.Duration, cue.LoopBeats);
                    int end = Math.Min(count, (int)Math.Ceiling(endBeat / resolution));
                    diff[pc, first]++; diff[pc, end]--;
                    if (endBeat > cue.LoopBeats)
                    { diff[pc, 0]++; diff[pc, Math.Min(count, (int)Math.Ceiling((endBeat - cue.LoopBeats) / resolution))]--; }
                }
            }
            for (int pc = 0; pc < 12; pc++)
            {
                int h = 0, m = 0;
                for (int i = 0; i < count; i++)
                {
                    h += harmonicDiff[pc, i]; m += melodicDiff[pc, i];
                    if (h > 0) primary[i] |= (ushort)(1 << pc);
                    if (m > 0) melody[i] |= (ushort)(1 << pc);
                }
            }
            for (int i = 0; i < count; i++) if (primary[i] == 0) primary[i] = melody[i];
            return primary;
        }
    }
}
