using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Per-note bend / vibrato and articulations (key switches, articulation settings, articulation zones).</summary>
    public sealed class MusicNoteExpressionTests
    {
        const int Rate = 16000;

        static AudioClip Sine(double hz, double seconds = 4, double clickSeconds = 0)
        {
            int n = (int)(Rate * seconds);
            var clip = AudioClip.Create("Sine" + hz, n, 1, Rate, false);
            var pcm = new float[n];
            for (int i = 0; i < n; i++) pcm[i] = (float)(.5 * Math.Sin(2 * Math.PI * hz * i / Rate)) + (i < clickSeconds * Rate ? .45f : 0);
            clip.SetData(pcm, 0);
            return clip;
        }

        // One track at 60 BPM (1 beat = 1 s) playing a 440 Hz sine recording (root A4).
        static MusicCueDefinition Song(params MusicNote[] notes)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Expr"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 60; song.LoopBeats = 8;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Tone", Sample = Sine(440), SampleRootKey = 69, Gain = .2f, AttackSeconds = .002f, ReleaseSeconds = .02f, Notes = notes } };
            return song;
        }

        static void Run(MusicCueDefinition song, Action<MusicCueDefinition> test)
        {
            try { test(song); }
            finally
            {
                foreach (var p in song.Parts) { if (p.Sample != null) UnityEngine.Object.DestroyImmediate(p.Sample); foreach (var z in p.Zones ?? Array.Empty<MusicCueDefinition.SampleZone>()) UnityEngine.Object.DestroyImmediate(z.Sample); }
                UnityEngine.Object.DestroyImmediate(song);
            }
        }

        static float[] Render(MusicCue cue, double seconds)
        {
            var pcm = new float[(int)(seconds * Rate) * 2];
            new AdaptiveMusicEngine(cue, Rate, playOnce: true).RenderStereo(pcm);
            return pcm;
        }

        // Frequency from rising zero crossings of the left channel over [from, to) seconds.
        static double Hz(float[] pcm, double from, double to)
        {
            int a = (int)(from * Rate), b = (int)(to * Rate), first = -1, last = -1, count = 0;
            for (int i = a + 1; i < b; i++)
                if (pcm[(i - 1) * 2] < 0 && pcm[i * 2] >= 0) { if (first < 0) first = i; else count++; last = i; }
            return count == 0 ? 0 : count * Rate / (double)(last - first);
        }

        static double Rms(float[] pcm, double from, double to)
        {
            double sum = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int i = a; i < b; i++) sum += pcm[i * 2] * pcm[i * 2];
            return Math.Sqrt(sum / Math.Max(1, b - a));
        }

        [Test]
        public void BendMovesThePitchOverTheNote() => Run(Song(new MusicNote(0, 3, 69) { BendTo = 2, BendStart = 1, BendEnd = 2 }, new MusicNote(4, 2, 69) { BendFrom = 12, BendTo = 12 }), s =>
        {
            var pcm = Render(s.Compile(), 6);
            Assert.AreEqual(440, Hz(pcm, .2, .9), 2, "before the bend starts");
            Assert.AreEqual(440 * Math.Pow(2, 2 / 12.0), Hz(pcm, 2.1, 2.9), 2, "after it ends: up a whole tone");
            double middle = Hz(pcm, 1.45, 1.55);
            Assert.IsTrue(middle > 450 && middle < 485, "halfway: " + middle);
            Assert.AreEqual(880, Hz(pcm, 4.2, 5.8), 3, "held an octave up for the whole note");
        });

        [Test]
        public void VibratoWobblesAroundThePitch() => Run(Song(new MusicNote(0, 3, 69) { Vibrato = .5f }), s =>
        {
            s.Parts[0].VibratoRateHz = 4;
            var pcm = Render(s.Compile(), 3);
            var hz = Enumerable.Range(0, 16).Select(i => Hz(pcm, .5 + i * .125, .5 + i * .125 + .0625)).ToArray(); // 1/16 s windows across 2 s
            Assert.IsTrue(hz.Max() > 448 && hz.Min() < 432, "±half a semitone: " + string.Join(", ", hz.Select(x => x.ToString("0"))));
            Assert.AreEqual(440, hz.Average(), 3, "centered on the note");
            Assert.AreEqual(440, Hz(pcm, 0, .03), 30, "fades in from the note start");
        });

        [Test]
        public void OlderVersionsIgnoreExpressionAndArticulations() => Run(Song(new MusicNote(0, 2, 69) { BendTo = 5, BendEnd = 1, Vibrato = 1, Articulation = 1 }), s =>
        {
            s.Parts[0].Articulations = new[] { new MusicArticulation(1, "Short", .1f, -6, .25f) };
            var with = Render(s.Compile(MusicPlaybackVersion.V2), 3);
            s.Parts[0].Notes[0] = new MusicNote(0, 2, 69); s.Parts[0].Articulations = Array.Empty<MusicArticulation>();
            Assert.IsTrue(with.SequenceEqual(Render(s.Compile(MusicPlaybackVersion.V2), 3)));
        });

        [Test]
        public void KeySwitchesSetTheLaterNotesArticulation()
        {
            var notes = new[] { new MusicNote(2, 1, 60), new MusicNote(1, .1, 24), new MusicNote(0, 1, 62), new MusicNote(1, 1, 64), new MusicNote(3, 1, 65) { Articulation = 3 },
                new MusicNote(4, .1, 25), new MusicNote(4, 1, 67) };
            var played = MusicArticulations.ResolveKeySwitches(notes, new[] { new MusicKeySwitch(24, 2), new MusicKeySwitch(25, 0) });
            Assert.AreEqual("62:0 64:2 60:2 65:3 67:0", string.Join(" ", played.Select(n => $"{n.Key}:{n.Articulation}")), "switch notes removed; a switch at the same beat counts; set notes keep theirs");
            Assert.IsTrue(ReferenceEquals(notes, MusicArticulations.ResolveKeySwitches(notes, null)));
            Assert.Throws<ArgumentException>(() => MusicArticulations.ResolveKeySwitches(notes, new[] { new MusicKeySwitch(24, 200) }));
            Run(Song(new MusicNote(0, 1, 24), new MusicNote(1, 1, 69)), s =>
            {
                s.Parts[0].KeySwitches = new[] { new MusicKeySwitch(24, 1) };
                var cue = s.Compile();
                Assert.AreEqual(1, cue.GetPart(0).NoteCount, "the switch note does not play");
                Assert.AreEqual(1, cue.GetPart(0).GetNote(0).Articulation);
                Assert.AreEqual(2, MidiMusicReader.Read(MidiMusicWriter.Write(s).Bytes).Parts[0].Notes.Count, "MIDI keeps the switch note");
            });
        }

        [Test]
        public void ArticulationSettingsShapeTheirNotes() => Run(Song(new MusicNote(0, 2, 69), new MusicNote(4, 2, 69) { Articulation = 1 }), s =>
        {
            s.Parts[0].Sample = Sine(440, 4, .05); // a loud 50 ms "pick" at the start of the recording
            s.Parts[0].Articulations = new[] { new MusicArticulation(1, "Palm mute", .05f, -6, .25f) };
            var pcm = Render(s.Compile(), 6.5);
            Assert.IsTrue(Rms(pcm, 0, .04) > 2 * Rms(pcm, 4, 4.04), "the articulation skips the pick");
            Assert.AreEqual(.5, Rms(pcm, 4.1, 4.4) / Rms(pcm, .1, .4), .03, "−6 dB");
            Assert.IsTrue(Rms(pcm, 4.6, 5.9) < .02 * Rms(pcm, .6, 1.9), "a quarter of the length");
            s.Parts[0].Articulations = new[] { new MusicArticulation(1, "A"), new MusicArticulation(1, "B") };
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "duplicate id");
            s.Parts[0].Articulations = new[] { new MusicArticulation(0, "Normal") };
            Assert.Throws<InvalidOperationException>(() => s.Compile(), "0 is the normal articulation");
        });

        [Test]
        public void ZonesCanBePerArticulation() => Run(Song(new MusicNote(0, 1, 69), new MusicNote(2, 1, 69) { Articulation = 1 }, new MusicNote(4, 1, 69) { Articulation = 2 }), s =>
        {
            s.Parts[0].Sample = null;
            s.Parts[0].Zones = new[]
            {
                new MusicCueDefinition.SampleZone { Sample = Sine(440), RootKey = 69 },
                new MusicCueDefinition.SampleZone { Sample = Sine(660), RootKey = 69, Articulation = 1, StartSeconds = .5f },
            };
            var pcm = Render(s.Compile(), 5);
            Assert.AreEqual(440, Hz(pcm, .1, .9), 2);
            Assert.AreEqual(660, Hz(pcm, 2.1, 2.9), 3, "articulation 1 plays its own recording");
            Assert.AreEqual(440, Hz(pcm, 4.1, 4.9), 2, "an articulation without zones uses the normal ones");
            var instrument = s.Compile().GetPart(0).Instrument;
            Assert.AreEqual(1, instrument.ZoneFor(69, 1, 1)); Assert.AreEqual(0, instrument.ZoneFor(69, 1, 5)); Assert.AreEqual(0, instrument.ZoneFor(69, 1));
        });
    }
}
