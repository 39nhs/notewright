using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>Note variation: skip chance and every-n-th-pass conditions, decided per pass of the loop.</summary>
    public sealed class MusicNoteVariationTests
    {
        const int Rate = 8000;

        [Test]
        public void DrawsAreDeterministicAndFollowTheChance()
        {
            var note = new MusicNote(1.5, .5, 60) { SkipChance = .3f };
            int played = Enumerable.Range(0, 4000).Count(p => MusicVariation.Plays(note, p, 2, 7));
            Assert.AreEqual(2800, played, 120, "about 70 % of passes");
            Assert.IsTrue(Enumerable.Range(0, 200).All(p => MusicVariation.Plays(note, p, 2, 7) == MusicVariation.Plays(note, p, 2, 7)), "same song, same draws");
            int differ = Enumerable.Range(0, 200).Count(p => MusicVariation.Plays(note, p, 2, 7) != MusicVariation.Plays(note, p, 2, 8));
            Assert.IsTrue(differ > 40, "another seed, other draws: " + differ);
            Assert.IsTrue(Enumerable.Range(0, 50).All(p => MusicVariation.Plays(new MusicNote(0, 1, 60), p, 0, 0)), "default: always");
            Assert.IsFalse(Enumerable.Range(0, 50).Any(p => MusicVariation.Plays(new MusicNote(0, 1, 60) { SkipChance = 1 }, p, 0, 0)));
            var everyFourth = new MusicNote(0, 1, 60) { RepeatEvery = 4, RepeatOffset = 1 };
            Assert.AreEqual("1 5 9", string.Join(" ", Enumerable.Range(0, 12).Where(p => MusicVariation.Plays(everyFourth, p, 0, 0))));
        }

        static MusicCueDefinition Song(params MusicNote[] notes)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            song.name = "Var"; song.PlaybackVersion = MusicPlaybackVersion.V3; song.Bpm = 240; song.LoopBeats = 4; song.VariationSeed = 3; // 1 s per pass
            var synth = MusicSynthPresets.Create("Init"); synth.Osc1Wave = MusicSynthWave.Sine; synth.AmpAttack = .001f; synth.AmpRelease = .01f;
            song.Parts = new[] { new MusicCueDefinition.Part { Name = "Tone", Gain = .2f, Synth = synth, Notes = notes } };
            return song;
        }

        static double Rms(float[] pcm, double from, double to)
        {
            double sum = 0; int a = (int)(from * Rate), b = (int)(to * Rate);
            for (int i = a; i < b; i++) sum += pcm[i * 2] * pcm[i * 2];
            return Math.Sqrt(sum / (b - a));
        }

        [Test]
        public void TheLoopingEngineLeavesNotesOutPerPass()
        {
            var song = Song(new MusicNote(0, 1, 69) { SkipChance = .5f }, new MusicNote(2, 1, 72) { RepeatEvery = 3 });
            try
            {
                var cue = song.Compile();
                var pcm = new float[Rate * 2 * 24]; new AdaptiveMusicEngine(cue, Rate).RenderStereo(pcm);
                int heardFirst = 0;
                for (int pass = 0; pass < 24; pass++)
                {
                    bool first = Rms(pcm, pass + .05, pass + .2) > .01, second = Rms(pcm, pass + .55, pass + .7) > .01;
                    Assert.AreEqual(MusicVariation.OnPass(cue, 0, pass).Any(n => n.Key == 69), first, $"pass {pass}: skip-chance note");
                    Assert.AreEqual(pass % 3 == 0, second, $"pass {pass}: every third pass");
                    if (first) heardFirst++;
                }
                Assert.IsTrue(heardFirst > 4 && heardFirst < 20, "some passes without it: " + heardFirst);
                var v2 = new float[Rate * 2 * 6]; new AdaptiveMusicEngine(song.Compile(MusicPlaybackVersion.V2), Rate).RenderStereo(v2);
                for (int pass = 0; pass < 6; pass++) Assert.IsTrue(Rms(v2, pass + .05, pass + .2) > .01 && Rms(v2, pass + .55, pass + .7) > .01, "V2 plays every note");
            }
            finally { UnityEngine.Object.DestroyImmediate(song); }
        }
    }
}
