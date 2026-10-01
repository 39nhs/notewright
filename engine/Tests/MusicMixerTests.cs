using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public class MusicMixerTests
    {
        MusicCueDefinition song;
        [SetUp] public void Setup()
        {
            song=ScriptableObject.CreateInstance<MusicCueDefinition>();song.name="Mixer";song.LoopBeats=8;
            song.Parts=new[]{new MusicCueDefinition.Part {Name="Bass",FallbackVoice=MusicVoice.Bass,Gain=.3f,Notes=new[]{new MusicNote(0,6,60)}}};
        }
        [TearDown] public void Cleanup()=>UnityEngine.Object.DestroyImmediate(song);
        float[] Render(bool stereo=true)
        {
            var pcm=new float[16000*(stereo?2:1)];var engine=new AdaptiveMusicEngine(song.Compile(),8000,playOnce:true);
            if(stereo)engine.RenderStereo(pcm);else engine.Render(pcm);return pcm;
        }
        [Test] public void ReferenceNeverPlaysAndMuteSoloAreApplied()
        {
            var before=Render();song.ReferenceMelody=new[]{new MusicNote(0,6,90)};
            CollectionAssert.AreEqual(before,Render());
            song.Parts[0].Mute=true;Assert.IsTrue(Render().All(x=>x==0));song.Parts[0].Mute=false;
            song.Parts=new[]{song.Parts[0],new MusicCueDefinition.Part {Name="Solo silence",Solo=true}};
            Assert.IsTrue(Render().All(x=>x==0));
        }
        [Test] public void PanReachesBothRuntimeChannelsAndMonoMatchesCenter()
        {
            var stereo=Render();var mono=Render(false);
            for(int i=0;i<mono.Length;i++){Assert.AreEqual(mono[i],stereo[i*2]);Assert.AreEqual(mono[i],stereo[i*2+1]);}
            song.Parts[0].Pan=-1;stereo=Render();
            Assert.IsTrue(stereo.Where((x,i)=>i%2==1).All(x=>x==0));Assert.Greater(stereo.Sum(x=>x*x),.1);
        }
        [Test] public void MasterCompressionFilterEchoAndGateChangeAudioWithoutNaNs()
        {
            var dry=Render();
            song.Mixer=new MusicMixerSettings {Compressor=true,ThresholdDb=-30,Ratio=8,LowPassHz=500,HighPassHz=30,EchoWet=.2f,Gate=true,GateDepth=.7f,CrushBits=6,CrushDownsample=3,CrushMix=.8f,NoiseGate=true,NoiseGateThresholdDb=-40,GatePattern="x-xx"};
            var wet=Render();Assert.IsTrue(wet.All(x=>!float.IsNaN(x)&&Math.Abs(x)<=1));
            Assert.Greater(dry.Zip(wet,(a,b)=>(a-b)*(a-b)).Sum(),.1);
            var engine=new AdaptiveMusicEngine(song.Compile(),8000,playOnce:true);var block=new float[250];
            for(int offset=0;offset<wet.Length;offset+=block.Length){engine.RenderStereo(block);for(int i=0;i<block.Length;i++)Assert.AreEqual(wet[offset+i],block[i]);}
        }
        [Test] public void CompiledMixerIsSnapshotAndInvalidValuesAreRejected()
        {
            var compiled=song.Compile();song.Mixer.OutputDb=-30;
            Assert.AreEqual(1,compiled.Mixer.Gain);
            song.Mixer.GateBeats=0;Assert.Throws<ArgumentException>(()=>song.Compile());
            song.Mixer=new MusicMixerSettings();song.Parts[0].Pan=float.NaN;Assert.Throws<ArgumentException>(()=>song.Compile());
        }
    }
}
