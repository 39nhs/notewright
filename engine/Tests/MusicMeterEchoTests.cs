using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicMeterEchoTests
    {
        const int Rate = 48000;

        // 4/4 for two bars, 7/8 from beat 8 (bars of 3.5 beats), 3/4 from beat 15; loop 20 (the last 3/4 bar is cut at the loop end).
        static MusicMeter Mixed() => new MusicMeter(4, 20, new[] { new MusicMeterChange(8, 7, 8), new MusicMeterChange(15, 3, 4) });

        [Test]
        public void UniformMeterIsTheOldBeatsPerBarArithmetic()
        {
            var random = new System.Random(5);
            foreach (int bar in new[] { 1, 3, 4, 7, 12 })
            {
                var m = new MusicMeter(bar, 1000);
                Assert.IsTrue(m.Uniform);
                for (int i = 0; i < 200; i++)
                {
                    double x = random.NextDouble() * 900 + (i % 3 == 0 ? Math.Round(random.NextDouble() * 50) * bar : 0);
                    Assert.AreEqual(Math.Floor(x / bar) * bar, m.BarFloor(x));
                    Assert.AreEqual(Math.Ceiling(x / bar) * bar, m.BarCeil(x));
                    Assert.AreEqual((Math.Floor((x + 1e-8) / bar) + 1) * bar, m.NextBarLine(x));
                    Assert.AreEqual(Math.Floor(x / bar + .5) * bar, m.Nearest(x));
                    Assert.AreEqual(x + 3 * bar, m.Advance(x, 3));
                    Assert.AreEqual((double)bar, m.BarLengthAt(x));
                }
            }
        }

        [Test]
        public void MeterChangesMoveTheBarLinesAndRepeatWithTheLoop()
        {
            var m = Mixed();
            var lines = new List<double>(); for (double b = 0; b < 45; b = m.Advance(b, 1)) lines.Add(b);
            CollectionAssert.AreEqual(new[] { 0.0, 4, 8, 11.5, 15, 18, 20, 24, 28, 31.5, 35, 38, 40, 44 }, lines.ToArray());
            Assert.AreEqual(8, m.BarFloor(10)); Assert.AreEqual(11.5, m.BarCeil(9)); Assert.AreEqual(15, m.NextBarLine(11.5));
            Assert.AreEqual(3.5, m.BarLengthAt(12)); Assert.AreEqual(2, m.BarLengthAt(19), "bar cut short by the loop end");
            Assert.AreEqual(11.5, m.Nearest(13)); Assert.AreEqual(15, m.Nearest(13.3));
            Assert.AreEqual(4, m.BarNumber(16)); Assert.AreEqual(6 + 3, m.BarNumber(20 + 12), "bars continue counting across the loop");
            Assert.IsTrue(m.OnBarLine(31.5)); Assert.IsFalse(m.OnBarLine(32));
            m.SignatureAt(9, out int n, out int d); Assert.AreEqual((7, 8), (n, d));
            m.SignatureAt(1, out n, out d); Assert.AreEqual((4, 4), (n, d));
            Assert.Throws<ArgumentException>(() => new MusicMeter(4, 20, new[] { new MusicMeterChange(6, 7, 8) }), "not on a bar line");
            Assert.Throws<ArgumentException>(() => new MusicMeter(4, 20, new[] { new MusicMeterChange(8, 3, 4), new MusicMeterChange(8, 5, 4) }), "not ascending");
            Assert.Throws<ArgumentException>(() => new MusicMeter(4, 20, new[] { new MusicMeterChange(8, 3, 3) }), "denominator");
            var start = new MusicMeter(4, 21, new[] { new MusicMeterChange(0, 7, 8) });
            Assert.AreEqual(3.5, start.BarLengthAt(0), "a change at beat 0 replaces Beats Per Bar");
        }

        static MusicCue Quiet(string name, MusicMeterChange[] meter, double bpm = 120)
        {
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(MusicVoice.Pad), .1, "Pad", new[] { new MusicNote(0, 1, 60) }, 0);
            return new MusicCue(name, bpm, 4, 32, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: MusicPlaybackVersion.V3, meterChanges: meter);
        }

        static void Run(AdaptiveMusicEngine engine, double seconds) { var block = new float[960]; for (int i = 0; i < seconds * Rate * 2 / block.Length; i++) engine.RenderStereo(block); }

        [Test]
        public void TransitionsWaitForTheNextBarOfTheMeter()
        {
            foreach (var (meter, expected) in new[] { ((MusicMeterChange[])null, 12.0), (new[] { new MusicMeterChange(8, 7, 8) }, 11.5) })
            {
                var engine = new AdaptiveMusicEngine(Quiet("A", meter), Rate);
                Run(engine, 4.6); // beat 9.2 at 120 BPM: inside the third bar
                engine.TransitionTo(Quiet("B", null), 4);
                Run(engine, 2);
                Assert.AreEqual(expected, engine.LastTransitionStartBeat, 1e-6, meter == null ? "4/4" : "7/8 bar from beat 8");
            }
        }

        // A one-sample click; the echo returns it after the echo delay.
        static float[] EchoResponse(MusicMixerSettings mixer, int version, double bpm, MusicTempoChange[] tempo = null, double seconds = 1.5)
        {
            var click = new float[64]; for (int k = 0; k < 32; k++) click[k] = .8f;
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(click, Rate, 60, .0001, .0001), 1, "Click", new[] { new MusicNote(0, .25, 60) }, 0);
            var cue = new MusicCue("Echo", bpm, 4, 64, new[] { part }, Array.Empty<MusicMixEffect>(), tempo, mixer.Compile(), version);
            var engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true);
            var pcm = new float[(int)(seconds * Rate) * 2]; engine.RenderStereo(pcm);
            var left = new float[pcm.Length / 2]; for (int i = 0; i < left.Length; i++) left[i] = pcm[i * 2];
            return left;
        }

        // Delay from the dry burst's onset to the echo's onset (first sound at least `after` seconds later).
        static double EchoSeconds(float[] x, double after = .05)
        {
            int first = Array.FindIndex(x, v => Math.Abs(v) > 1e-4);
            int echo = Array.FindIndex(x, first + (int)(after * Rate), v => Math.Abs(v) > 1e-4);
            return (echo - first) / (double)Rate;
        }

        [Test]
        public void EchoBeatsFollowTheTempoInV3Only()
        {
            var mixer = new MusicMixerSettings { EchoWet = .5f, EchoFeedback = 0, EchoSeconds = .3f, EchoBeats = 1 };
            Assert.AreEqual(.5, EchoSeconds(EchoResponse(mixer, MusicPlaybackVersion.V3, 120)), 1e-3, "one beat at 120 BPM");
            Assert.AreEqual(1, EchoSeconds(EchoResponse(mixer, MusicPlaybackVersion.V3, 60)), 1e-3, "one beat at 60 BPM");
            Assert.AreEqual(.3, EchoSeconds(EchoResponse(mixer, MusicPlaybackVersion.V2, 120)), 1e-3, "V2 keeps Echo Seconds");
            var unsynced = new MusicMixerSettings { EchoWet = .5f, EchoFeedback = 0, EchoSeconds = .3f };
            CollectionAssert.AreEqual(EchoResponse(unsynced, MusicPlaybackVersion.V2, 120), EchoResponse(unsynced, MusicPlaybackVersion.V3, 120), "EchoBeats 0 = V2 echo");
            var slow = new MusicMixerSettings { EchoWet = .5f, EchoFeedback = 0, EchoBeats = 8 };
            Assert.AreEqual(2, EchoSeconds(EchoResponse(slow, MusicPlaybackVersion.V3, 120, seconds: 2.5)), 1e-3, "held at 2 s");
            // A tempo jump to 240 BPM at beat 0.25 halves the delay of the echo that follows.
            var faster = EchoResponse(mixer, MusicPlaybackVersion.V3, 120, new[] { new MusicTempoChange { Beat = .25, Bpm = 240 } });
            Assert.AreEqual(.25, EchoSeconds(faster), 1e-3);
        }

        [Test]
        public void TrackEchoSyncsToo()
        {
            var click = new float[64]; for (int k = 0; k < 32; k++) click[k] = .8f;
            var effects = new MusicMixerSettings { EchoWet = .5f, EchoFeedback = 0, EchoSeconds = .1f, EchoBeats = .5f };
            var part = new MusicPart(MusicLayer.Melody, new MusicInstrument(click, Rate, 60, .0001, .0001), 1, "Click", new[] { new MusicNote(0, .25, 60) }, 0, effects: effects.Compile());
            float[] Render(int version)
            {
                var cue = new MusicCue("Track echo", 100, 4, 16, new[] { part }, Array.Empty<MusicMixEffect>(), playbackVersion: version);
                var engine = new AdaptiveMusicEngine(cue, Rate, playOnce: true); var pcm = new float[Rate * 2]; engine.RenderStereo(pcm);
                var left = new float[Rate]; for (int i = 0; i < Rate; i++) left[i] = pcm[i * 2]; return left;
            }
            Assert.AreEqual(.3, EchoSeconds(Render(MusicPlaybackVersion.V3)), 1e-3, "half a beat at 100 BPM");
            Assert.AreEqual(.1, EchoSeconds(Render(MusicPlaybackVersion.V2), .02), 1e-3, "V2 track keeps Echo Seconds");
        }

        [Test]
        public void SongAssetAndMidiImportCarryMeterChanges()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Meter"; song.LoopBeats = 16; song.PlaybackVersion = MusicPlaybackVersion.V3;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Pad", Notes = new[] { new MusicNote(0, 1, 60) } } };
                song.MeterChanges = new[] { new MusicMeterChange(8, 7, 8) };
                Assert.AreEqual(3.5, song.Compile().Meter.BarLengthAt(9));
                song.MeterChanges = new[] { new MusicMeterChange(6, 7, 8) };
                var error = Assert.Throws<InvalidOperationException>(() => song.Compile());
                Assert.IsTrue(error.Message.Contains("bar line"), error.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
            // SMF with 4/4 at 0 and 7/8 at beat 8 (480 PPQN).
            var events = new List<byte>();
            void Ev(int delta, params int[] bytes) { Var(events, delta); foreach (int b in bytes) events.Add((byte)b); }
            Ev(0, 0xFF, 0x58, 4, 4, 2, 24, 8);
            Ev(0, 0x90, 60, 100);
            Ev(3840, 0xFF, 0x58, 4, 7, 3, 24, 8);
            Ev(480, 0x80, 60, 0);
            Ev(0, 0xFF, 0x2F, 0);
            var file = new List<byte>(); file.AddRange(System.Text.Encoding.ASCII.GetBytes("MThd")); file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0 });
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("MTrk")); file.AddRange(BitConverter.GetBytes(events.Count).Reverse()); file.AddRange(events);
            var midi = MidiMusicReader.Read(file.ToArray());
            Assert.AreEqual(4, midi.BeatsPerBar);
            CollectionAssert.AreEqual(new[] { (0.0, 4, 4), (8.0, 7, 8) }, midi.Meters.Select(m => (m.Beat, m.Numerator, m.Denominator)).ToArray());
        }

        static void Var(List<byte> output, int value)
        {
            var stack = new Stack<byte>(); stack.Push((byte)(value & 127));
            while ((value >>= 7) > 0) stack.Push((byte)(value & 127 | 128));
            output.AddRange(stack);
        }
    }
}
