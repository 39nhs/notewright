using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicSectionTests
    {
        // Eight 4/4 bars (32 beats), one note per bar: Intro (bars 1–2), A (3–4), B (5–6), Chorus (7–8).
        static MusicCueDefinition Song()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Form"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 120; song.LoopBeats = 32;
            song.Sections = new[] { new MusicSectionMarker("Intro", 0), new MusicSectionMarker("A", 8), new MusicSectionMarker("B", 16), new MusicSectionMarker("Chorus", 24) };
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Lead", Notes = Enumerable.Range(0, 8).Select(i => new MusicNote(i * 4, 2, 60 + i)).ToArray() } };
            return song;
        }

        static string Form(MusicCueDefinition s) => string.Join(" ", s.Sections.Select(m => $"{m.Name}@{m.Beat:0.##}"));

        static void Run(Action<MusicCueDefinition> test)
        {
            var song = Song();
            try { test(song); Assert.DoesNotThrow(() => song.Compile()); }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void SectionsAnswerWhereABeatIs()
        {
            var sections = new MusicSections(32, new[] { new MusicSectionMarker("B", 16), new MusicSectionMarker("A", 4), new MusicSectionMarker("A", 24) });
            Assert.AreEqual(3, sections.Count);
            Assert.AreEqual("A", sections[0].Name, "sorted by beat");
            Assert.AreEqual(-1, sections.IndexAt(3.9), "before the first marker");
            Assert.AreEqual("", sections.NameAt(0));
            Assert.AreEqual(0, sections.IndexAt(4));
            Assert.AreEqual("B", sections.NameAt(23.99));
            Assert.AreEqual(2, sections.IndexAt(31.9));
            Assert.AreEqual(16, sections.End(0)); Assert.AreEqual(32, sections.End(2), "the last runs to the loop end");
            Assert.AreEqual(2, sections.IndexOf("A", 1), "second occurrence");
            Assert.AreEqual(-1, sections.IndexOf("Chorus"));
            Assert.IsTrue(sections.TryRange("B", out double start, out double end)); Assert.AreEqual((16.0, 24.0), (start, end));
            Assert.IsFalse(sections.TryRange("a", out _, out _), "names match exactly");
        }

        [Test]
        public void InvalidMarkersAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker(" ", 0) }), "blank name");
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker("End", 16) }), "at the loop end");
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker("A", -1) }));
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker("A", double.NaN) }));
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker("A", 4), new MusicSectionMarker("B", 4) }), "same beat");
            Assert.Throws<ArgumentException>(() => new MusicSections(16, new[] { new MusicSectionMarker(new string('x', 65), 0) }));
            Assert.AreEqual(0, new MusicSections(16, null).Count);
            Run(s =>
            {
                s.Sections = new[] { new MusicSectionMarker("Outro", 40) };
                var e = Assert.Throws<InvalidOperationException>(() => s.Compile());
                Assert.IsTrue(e.Message.StartsWith("Form: ") && e.Message.Contains("Outro"), e.Message);
                s.Sections = Song().Sections;
            });
        }

        [Test]
        public void CompiledCueAndEngineReportTheSection() => Run(s =>
        {
            var cue = s.Compile();
            Assert.AreEqual(4, cue.Sections.Count);
            var engine = new AdaptiveMusicEngine(cue, 8000);
            var block = new float[800]; // 0.05 s stereo
            var names = new List<string>();
            for (int i = 0; i < 20 * 16; i++) { engine.RenderStereo(block); if (i % 20 == 9) names.Add(engine.CurrentSectionName); } // beats 1, 3, 5 … (every second = 2 beats)
            Assert.AreEqual("Intro Intro Intro Intro A A A A B B B B Chorus Chorus Chorus Chorus", string.Join(" ", names));
            Assert.AreEqual(0, engine.CurrentSection, "16 s = beat 32: the loop wrapped back to the intro");
            for (int i = 0; i < 10 * 20 - 10; i++) engine.RenderStereo(block); // to beat 51 (19 of the second pass)
            Assert.AreEqual(2, engine.CurrentSection);
        });

        [Test]
        public void MidiMarkersBecomeSections()
        {
            var events = new List<byte>();
            void Ev(int delta, params int[] bytes) { Var(events, delta); foreach (int b in bytes) events.Add((byte)b); }
            void Marker(int delta, string text) { var bytes = System.Text.Encoding.UTF8.GetBytes(text); Var(events, delta); events.AddRange(new byte[] { 0xFF, 0x06 }); Var(events, bytes.Length); events.AddRange(bytes); }
            Marker(0, "Intro");
            Ev(0, 0x90, 60, 100);
            Marker(1920, "사비");
            Ev(1920, 0x80, 60, 0);
            Ev(0, 0xFF, 0x2F, 0);
            var file = new List<byte>(); file.AddRange(System.Text.Encoding.ASCII.GetBytes("MThd")); file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0 });
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("MTrk")); file.AddRange(BitConverter.GetBytes(events.Count).Reverse()); file.AddRange(events);
            var midi = MidiMusicReader.Read(file.ToArray());
            Assert.AreEqual("Intro@0 사비@4", string.Join(" ", midi.Markers.Select(m => $"{m.Name}@{m.Beat:0.##}")));
        }

        static void Var(List<byte> output, int value)
        {
            var stack = new Stack<byte>(); stack.Push((byte)(value & 127));
            while ((value >>= 7) > 0) stack.Push((byte)(value & 127 | 128));
            output.AddRange(stack);
        }
    }
}
