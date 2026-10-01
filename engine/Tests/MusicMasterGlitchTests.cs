using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public class MusicMasterGlitchTests
    {
        [TestCase(MusicNoiseKind.SlowingGlitch)]
        [TestCase(MusicNoiseKind.AcceleratingGlitch)]
        [TestCase(MusicNoiseKind.PitchGlitch)]
        [TestCase(MusicNoiseKind.StutterGlitch)]
        [TestCase(MusicNoiseKind.BitcrushGlitch)]
        [TestCase(MusicNoiseKind.GateGlitch)]
        public void MasterFilterProcessesActualMusicWithoutAddingVoices(MusicNoiseKind kind)
        {
            var song = ScriptableObject.CreateInstance<MusicCueDefinition>();
            try
            {
                song.name = "Master test"; song.LoopBeats = 8;
                song.Parts = new[] { new MusicCueDefinition.Part { Name = "Drums", Layer = MusicLayer.Drums, FallbackVoice = MusicVoice.Bass, Notes = new[] { new MusicNote(0,8,60) } } };
                var dryCue = song.Compile();
                song.NoiseRegions = new[] { new MusicNoiseRegion { MasterFilter = true, Kind = kind, StartBeat = 2, DurationBeats = 4, FadeInBeats = .1, FadeOutBeats = .1 } };
                var wetCue = song.Compile();
                Assert.AreEqual(dryCue.PartCount, wetCue.PartCount);
                var dry = new float[32000]; var wet = new float[32000];
                new AdaptiveMusicEngine(dryCue,8000,playOnce:true).Render(dry);
                new AdaptiveMusicEngine(wetCue,8000,playOnce:true).Render(wet);
                CollectionAssert.AreEqual(dry.Take(8000), wet.Take(8000));
                CollectionAssert.AreEqual(dry.Skip(24002), wet.Skip(24002));
                Assert.Greater(dry.Zip(wet,(x,y)=>(x-y)*(x-y)).Sum(), .01);
                var chunked = new AdaptiveMusicEngine(wetCue,8000,playOnce:true); var block = new float[125];
                for (int offset = 0; offset < wet.Length; offset += block.Length)
                { chunked.Render(block); for(int i=0;i<block.Length;i++) Assert.AreEqual(wet[offset+i],block[i]); }
                song.Parts = new MusicCueDefinition.Part[0];
                var silence = new float[32000]; new AdaptiveMusicEngine(song.Compile(),8000,playOnce:true).Render(silence);
                Assert.IsTrue(silence.All(x => x == 0), "No music means no effect sound.");
            }
            finally { Object.DestroyImmediate(song); }
        }
    }
}
