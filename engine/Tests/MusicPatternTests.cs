using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPatternTests
    {
        // 16 beats; pattern "Riff" (2 beats: 60 at 0, 63 at 1) placed at 0 and 8 (+12, twice); the track also has a free note at 6.
        static MusicCueDefinition Song()
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Patterns"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.LoopBeats = 16;
            song.Patterns = new[] { new MusicPattern { Name = "Riff", LengthBeats = 2, Notes = new[] { new MusicNote(0, .5, 60, .8f), new MusicNote(1, .5, 63, .6f) } } };
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Lead", Notes = new[] { new MusicNote(6, 1, 70) },
                Placements = new[] { new MusicPatternPlacement("Riff", 0), new MusicPatternPlacement("Riff", 8, 12, 2) } } };
            return song;
        }

        static string Played(MusicCueDefinition s, int part = 0) => string.Join(" ", MusicPatterns.PlayedNotes(s, s.Parts[part]).OrderBy(n => n.Beat).ThenBy(n => n.Key).Select(n => $"{n.Beat:0.##}:{n.Key}"));
        static string Compiled(MusicCueDefinition s) { var p = s.Compile().GetPart(0); return string.Join(" ", Enumerable.Range(0, p.NoteCount).Select(i => p.GetNote(i)).OrderBy(n => n.Beat).ThenBy(n => n.Key).Select(n => $"{n.Beat:0.##}:{n.Key}")); }

        [Test]
        public void ValidationNamesTheProblem()
        {
            var song = Song();
            try
            {
                song.Patterns = song.Patterns.Concat(new[] { new MusicPattern { Name = "Riff", LengthBeats = 1 } }).ToArray();
                var error = Assert.Throws<InvalidOperationException>(() => song.Compile()); Assert.IsTrue(error.Message.Contains("unique"), error.Message);
                song.Patterns = new[] { new MusicPattern { Name = "Riff", LengthBeats = 1, Notes = new[] { new MusicNote(1, .5, 60) } } };
                error = Assert.Throws<InvalidOperationException>(() => song.Compile()); Assert.IsTrue(error.Message.Contains("inside"), error.Message);
                song.Patterns = Array.Empty<MusicPattern>();
                error = Assert.Throws<InvalidOperationException>(() => song.Compile()); Assert.IsTrue(error.Message.Contains("Lead") && error.Message.Contains("missing"), error.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }

        [Test]
        public void PlacementsSoundLikeTheirNotes()
        {
            var song = Song(); var flat = Song();
            try
            {
                flat.Parts[0].Notes = MusicPatterns.PlayedNotes(song, song.Parts[0]); flat.Parts[0].Placements = Array.Empty<MusicPatternPlacement>(); flat.Patterns = Array.Empty<MusicPattern>();
                float[] Render(MusicCueDefinition s) { var pcm = new float[8000 * 8]; new AdaptiveMusicEngine(s.Compile(), 8000, playOnce: true).RenderStereo(pcm); return pcm; }
                CollectionAssert.AreEqual(Render(flat), Render(song));
            }
            finally { UnityEngine.Object.DestroyImmediate(song); UnityEngine.Object.DestroyImmediate(flat); }
        }
    }
}
