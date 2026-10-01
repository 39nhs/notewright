using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    /// <summary>320+ BPM tempo, master bitcrusher, threshold noise gate, step-pattern gate.</summary>
    public class MusicHexFxTests
    {
        MusicCueDefinition song;
        [SetUp] public void Setup()
        {
            song=ScriptableObject.CreateInstance<MusicCueDefinition>();song.name="HexFx";song.LoopBeats=8;
            song.Parts=new[]{new MusicCueDefinition.Part {Name="Bass",FallbackVoice=MusicVoice.Bass,Gain=.3f,Notes=new[]{new MusicNote(0,8,60)}}};
        }
        [TearDown] public void Cleanup()=>UnityEngine.Object.DestroyImmediate(song);
        float[] Render(int samples=16000)
        {
            var pcm=new float[samples];new AdaptiveMusicEngine(song.Compile(),8000,playOnce:true).Render(pcm);return pcm;
        }

        [Test] public void TempoUpToMaxBpmIsAcceptedAndAboveRejected()
        {
            song.Bpm=320;var cue=song.Compile();
            Assert.AreEqual(320,cue.Bpm);Assert.AreEqual(4*60/320.0,cue.Tempo.SecondsAt(4),1e-9);
            song.TempoChanges=new[]{new MusicTempoChange{Beat=4,Bpm=MusicTempoMap.MaxBpm,RampBeats=2}};Assert.DoesNotThrow(()=>song.Compile());
            song.TempoChanges=new[]{new MusicTempoChange{Beat=4,Bpm=MusicTempoMap.MaxBpm+1}};Assert.Throws<ArgumentException>(()=>song.Compile());
            song.TempoChanges=new MusicTempoChange[0];song.Bpm=MusicTempoMap.MaxBpm;Assert.DoesNotThrow(()=>song.Compile());
            song.Bpm=MusicTempoMap.MaxBpm+1;Assert.Throws<ArgumentException>(()=>song.Compile());
        }

        [Test] public void NoteAt320BpmSoundsWhereBeatTwoAt160Would()
        {
            song.Bpm=320;song.Parts[0].Notes=new[]{new MusicNote(4,1,60)};
            int fast=Array.FindIndex(Render(),x=>x!=0);
            song.Bpm=160;song.Parts[0].Notes=new[]{new MusicNote(2,1,60)};
            int slow=Array.FindIndex(Render(),x=>x!=0);
            Assert.AreEqual(6000,fast,1);Assert.AreEqual(slow,fast,1);
        }

        [Test] public void BitcrusherQuantizesHoldsAndMixZeroIsDry()
        {
            var dry=Render();
            song.Mixer=new MusicMixerSettings{CrushBits=3};
            Assert.LessOrEqual(Render().Skip(1000).Distinct().Count(),9,"3 bits = 9 levels from -1 to 1");
            song.Mixer=new MusicMixerSettings{CrushDownsample=4};
            var held=Render().Skip(1000).ToArray();int changes=0;
            for(int i=1;i<held.Length;i++) if(held[i]!=held[i-1]) changes++;
            Assert.LessOrEqual(changes,held.Length/4+1);Assert.Greater(changes,held.Length/8);
            song.Mixer=new MusicMixerSettings{CrushBits=3,CrushDownsample=4,CrushMix=0};
            CollectionAssert.AreEqual(dry,Render());
        }

        [Test] public void NoiseGateSilencesQuietMaterialAndPassesLoud()
        {
            song.Mixer=new MusicMixerSettings{NoiseGate=true,NoiseGateThresholdDb=-30};
            song.Parts[0].Gain=.005f;
            Assert.Less(Render().Max(x=>Math.Abs(x)),1e-4f);
            song.Parts[0].Gain=.3f;var gated=Render();
            song.Mixer.NoiseGate=false;var dry=Render();
            Assert.Greater(gated.Max(x=>Math.Abs(x)),.1f);
            Assert.Less(dry.Zip(gated,(a,b)=>Math.Abs(a-b)).Skip(400).Max(),1e-3f,"Open gate must not chatter on zero crossings.");
        }

        [Test] public void NoiseGateClosesAfterHoldAndRelease()
        {
            song.Mixer=new MusicMixerSettings{NoiseGate=true,NoiseGateThresholdDb=-30,NoiseGateHoldMs=10,NoiseGateReleaseMs=5};
            song.Mixer.EchoWet=.5f;song.Mixer.EchoSeconds=.1f;song.Mixer.EchoFeedback=.6f;
            song.Parts[0].Notes=new[]{new MusicNote(0,1,60)}; // 0.5 s at 120 BPM, then quiet echo tail
            var pcm=Render();
            song.Mixer.NoiseGate=false;var open=Render();
            Assert.Greater(open.Skip(12000).Max(x=>Math.Abs(x)),5e-4f,"Echo tail exists without the gate.");
            Assert.Less(pcm.Skip(12000).Max(x=>Math.Abs(x)),1e-5f,"Gate removes the tail below threshold.");
        }

        [Test] public void GatePatternOpensOnlyMarkedStepsWithoutDipsBetweenOpenSteps()
        {
            song.Bpm=60;song.Mixer=new MusicMixerSettings{Gate=true,GateBeats=1,GatePattern="xx-x"};
            var pcm=Render(32000);
            Assert.IsTrue(pcm.Skip(16100).Take(7800).All(x=>x==0),"Closed step is silent.");
            Assert.Greater(pcm.Skip(24400).Take(7000).Max(x=>Math.Abs(x)),.1f);
            song.Mixer.Gate=false;var dry=Render(32000);
            for(int i=7900;i<8100;i++) Assert.AreEqual(dry[i],pcm[i],1e-6,"x followed by x stays open.");
            Assert.AreEqual(4,song.Compile().Mixer.GateStepCount);
            song.Mixer.GatePattern="x?x";Assert.Throws<ArgumentException>(()=>song.Compile());
            song.Mixer.GatePattern=new string('x',129);Assert.Throws<ArgumentException>(()=>song.Compile());
            song.Mixer.GatePattern=" | ";Assert.AreEqual(0,song.Compile().Mixer.GateStepCount,"Blank pattern = duty cycle gate.");
        }

        [Test] public void MasterOnlyGlitchKindsRejectAdditiveMode()
        {
            foreach(var kind in new[]{MusicNoiseKind.BitcrushGlitch,MusicNoiseKind.GateGlitch})
            {
                song.NoiseRegions=new[]{new MusicNoiseRegion{Kind=kind,MasterFilter=false,StartBeat=0,DurationBeats=4,FadeInBeats=0,FadeOutBeats=0}};
                Assert.Throws<ArgumentException>(()=>song.Compile());
                song.NoiseRegions[0].MasterFilter=true;Assert.DoesNotThrow(()=>song.Compile());
            }
        }

        [Test] public void GateGlitchIsSeededAndKeepsTheDownbeat()
        {
            song.NoiseRegions=new[]{new MusicNoiseRegion{Kind=MusicNoiseKind.GateGlitch,MasterFilter=true,StartBeat=0,DurationBeats=8,FadeInBeats=0,FadeOutBeats=0,GlitchRate=4,PitchDepth=18,Seed=7}};
            var a=Render();var b=Render();CollectionAssert.AreEqual(a,b);
            song.NoiseRegions[0].Seed=8;Assert.IsFalse(a.SequenceEqual(Render()));
            // First quarter of every beat (4000 samples at 120 BPM) is open.
            for(int beat=1;beat<4;beat++) Assert.Greater(a.Skip(beat*4000+100).Take(800).Max(x=>Math.Abs(x)),.1f);
            Assert.Greater(a.Count(x=>x==0),2000,"Depth 18/24 closes most off-beat steps.");
        }
    }
}
