using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicConvolutionTests
    {
        static AudioClip Clip(float[] pcm,int rate=48000,int channels=1)
        { var clip=AudioClip.Create("IR test",pcm.Length/channels,channels,rate,false);clip.SetData(pcm,0);return clip; }
        [Test]
        public void PartitionedStereoMatchesDirectConvolutionIncludingHeadAndTailBoundaries()
        {
            var random=new System.Random(923);var h=new float[773*2];var x=new double[2100*2];
            for(int i=0;i<h.Length;i++)h[i]=(float)((random.NextDouble()-.5)*.012);
            h[0]=.4f;h[1]=-.2f;h[128*2]=.17f;h[256*2+1]=-.21f;
            for(int i=0;i<1300*2;i++)x[i]=(random.NextDouble()-.5)*.4;
            var processor=new MusicConvolutionImpulse(h,2,48000).CreateProcessor(48000,.7,3);
            var rendered=new double[x.Length];
            for(int f=0;f<x.Length/2;f++)
            {
                double l=x[f*2],r=x[f*2+1];processor.Process(ref l,ref r);rendered[f*2]=l;rendered[f*2+1]=r;
                for(int c=0;c<2;c++)
                {
                    double expected=x[f*2+c]*.3;
                    for(int j=0;j<=Math.Min(f,772);j++)expected+=x[(f-j)*2+c]*h[j*2+c]*.7*Math.Pow(10,3.0/20);
                    Assert.AreEqual(expected,rendered[f*2+c],1e-10,"frame "+f+" channel "+c);
                }
            }
            processor.Reset();
            for(int f=0;f<x.Length/2;f++) {double l=x[f*2],r=x[f*2+1];processor.Process(ref l,ref r);Assert.AreEqual(rendered[f*2],l);Assert.AreEqual(rendered[f*2+1],r);}
        }
        [Test]
        public void UnityImpulseCaptureIsImmutableAndMonoDoesNotMixChannels()
        {
            var pcm=new float[385];pcm[0]=.5f;pcm[127]=.2f;pcm[128]=-.1f;pcm[384]=.3f;
            var clip=Clip(pcm);var impulse=MusicConvolutionImpulse.FromClip(clip);clip.SetData(new float[pcm.Length],0);
            var p=impulse.CreateProcessor(48000);
            for(int i=0;i<520;i++){double l=i==0?1:0,r=i==7?2:0;p.Process(ref l,ref r);Assert.AreEqual(i<pcm.Length?pcm[i]:0,l,1e-10);Assert.AreEqual(i>=7&&i-7<pcm.Length?2*pcm[i-7]:0,r,1e-10);}
            UnityEngine.Object.DestroyImmediate(clip);
        }
        [Test]
        public void IdentityImpulseAddsNoLatencyAndZeroMixIsExactBypass()
        {
            var identity=new MusicConvolutionImpulse(new[]{1f},1,48000).CreateProcessor(48000);
            var bypass=new MusicConvolutionImpulse(new[]{.2f,.8f},1,48000).CreateProcessor(48000,0);
            for(int i=0;i<400;i++){double a=Math.Sin(i*.07),b=Math.Cos(i*.3),l=a,r=b;identity.Process(ref l,ref r);Assert.AreEqual(a,l);Assert.AreEqual(b,r);bypass.Process(ref l,ref r);Assert.AreEqual(a,l);Assert.AreEqual(b,r);}
        }
        [Test]
        public void ResamplingPreservesPhysicalDelayAt24And96KHz()
        {
            var taps=new float[700];taps[256]=1;
            foreach(int rate in new[]{24000,96000})
            {
                var p=new MusicConvolutionImpulse(taps,1,48000).CreateProcessor(rate);double peak=0;int index=-1;
                for(int f=0;f<1600;f++){double l=f==0?1:0,r=0;p.Process(ref l,ref r);if(Math.Abs(l)>peak){peak=Math.Abs(l);index=f;}Assert.AreEqual(0,r,1e-10);}
                Assert.AreEqual(256*rate/48000,index);
            }
        }
        [Test]
        public void IrValidationAndNormalizationBoundTheGain()
        {
            Assert.Throws<ArgumentException>(()=>new MusicConvolutionImpulse(new float[0],1,48000));
            Assert.Throws<ArgumentException>(()=>new MusicConvolutionImpulse(new[]{float.NaN},1,48000));
            Assert.Throws<ArgumentException>(()=>new MusicConvolutionImpulse(new[]{0f},1,48000));
            Assert.Throws<ArgumentException>(()=>new MusicConvolutionImpulse(new float[12001],1,48000));
            foreach(int rate in new[]{24000,48000,96000}){
                var p=new MusicConvolutionImpulse(Enumerable.Repeat(.2f,440).ToArray(),1,48000,true).CreateProcessor(rate);
                for(int f=0;f<2600;f++){double l=.5,r=-.25;p.Process(ref l,ref r);Assert.LessOrEqual(Math.Abs(l),.500001);Assert.LessOrEqual(Math.Abs(r),.250001);}
            }
        }
        [Test]
        public void TrackBusAndMasterIrShareTheEngineAndFrozenVersionsIgnoreIt()
        {
            var clip=Clip(new[]{.5f});
            try {using(var scenario=MusicPlaybackScenarios.Build("Oscillators"))
            {
                var d=scenario.Definition;d.Parts=new[]{d.Parts[1]};
                var v1=MusicPlaybackScenarios.Render(d.Compile(1));var v2=MusicPlaybackScenarios.Render(d.Compile(2));
                d.Mixer.CabinetImpulse=clip;
                CollectionAssert.AreEqual(v1,MusicPlaybackScenarios.Render(d.Compile(1)));CollectionAssert.AreEqual(v2,MusicPlaybackScenarios.Render(d.Compile(2)));
                var master=MusicPlaybackScenarios.Render(d.Compile(3));d.Mixer.CabinetImpulse=null;
                d.Parts[0].TrackEffects=true;d.Parts[0].Effects.CabinetImpulse=clip;
                var track=MusicPlaybackScenarios.Render(d.Compile(3));CollectionAssert.AreEqual(master,track);
                d.Parts[0].Effects.CabinetImpulse=null;d.Parts[0].Output="Cab";d.Buses=new[]{new MusicBusDefinition{Name="Cab",Effects=new MusicMixerSettings{CabinetImpulse=clip}}};
                CollectionAssert.AreEqual(master,MusicPlaybackScenarios.Render(d.Compile(3)));
                d.Buses=Array.Empty<MusicBusDefinition>();d.Parts[0].Output="";d.Parts[0].Effects.CabinetImpulse=clip;d.Parts[0].PlaybackVersion=2;
                CollectionAssert.AreEqual(v2,MusicPlaybackScenarios.Render(d.Compile(3)),"pinned V2 track bypasses IR");
            }} finally{UnityEngine.Object.DestroyImmediate(clip);}
        }
#if UNITY_5_3_OR_NEWER
        [Test]
        public void ConvolutionAndMeteringAllocateNothingInTheAudioCallback()
        {
            var clip=Clip(Enumerable.Range(0,511).Select(i=>(float)(.01*Math.Exp(-i/80.0))).ToArray());
            try{using(var scenario=MusicPlaybackScenarios.Build("Oscillators")){
                scenario.Definition.Mixer.CabinetImpulse=clip;var engine=new AdaptiveMusicEngine(scenario.Definition.Compile(3),48000);
                var meter=new MusicLevelMeter(48000);engine.AttachLevelMeter(meter);var buffer=new float[1024];
                for(int i=0;i<10;i++)engine.RenderStereo(buffer);
                long allocated=0;for(int i=0;i<100;i++){long before=GC.GetAllocatedBytesForCurrentThread();engine.RenderStereo(buffer);allocated+=GC.GetAllocatedBytesForCurrentThread()-before;}
                Assert.AreEqual(0,allocated);Assert.IsTrue(meter.TryRead(out var read));Assert.Greater(read.Frames,0);
            }}finally{UnityEngine.Object.DestroyImmediate(clip);}
        }
#endif
    }
}
