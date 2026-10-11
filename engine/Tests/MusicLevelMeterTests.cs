using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicLevelMeterTests
    {
        static float[] Sine(int rate,double seconds,double gain=0.1,int channels=2)
        {var pcm=new float[(int)(rate*seconds)*channels];for(int f=0;f<pcm.Length/channels;f++)for(int c=0;c<channels;c++)pcm[f*channels+c]=(float)(gain*Math.Sin(2*Math.PI*1000*f/rate));return pcm;}
        [TestCase(44100)] [TestCase(48000)] [TestCase(96000)]
        public void StereoSineHasKnownRmsPeakAndKWeightedLoudness(int rate)
        {
            var meter=new MusicLevelMeter(rate);meter.ProcessStereo(Sine(rate,4));Assert.IsTrue(meter.TryRead(out var s));
            Assert.AreEqual(.1,s.PeakLeft,.0001);Assert.AreEqual(.1/Math.Sqrt(2),s.RmsLeft,1e-6);
            Assert.AreEqual(-23.01,s.RmsLeftDb,.01);Assert.IsTrue(s.MomentaryReady);Assert.IsTrue(s.ShortTermReady);
            // Stereo 1 kHz sine, each channel -20 dBFS amplitude: BS.1770 reference approximately -20 LUFS.
            Assert.AreEqual(-20,s.MomentaryLufs,.08);Assert.AreEqual(-20,s.ShortTermLufs,.08);Assert.AreEqual(-20,meter.ReadIntegratedLufs(),.08);
        }
        [Test]
        public void MonoIsThreeLuBelowDuplicatedStereoAndChunkingDoesNotChangeIt()
        {
            var mono=new MusicLevelMeter(48000,1);mono.ProcessMono(Sine(48000,4,.1,1));
            var stereo=new MusicLevelMeter(48000);var pcm=Sine(48000,4);
            for(int first=0;first<pcm.Length;first+=514)stereo.ProcessStereo(pcm,first,Math.Min(257,(pcm.Length-first)/2));
            Assert.AreEqual(10*Math.Log10(2),stereo.ReadIntegratedLufs()-mono.ReadIntegratedLufs(),1e-6);
            var whole=new MusicLevelMeter(48000);whole.ProcessStereo(pcm);Assert.AreEqual(whole.ReadIntegratedLufs(),stereo.ReadIntegratedLufs());
            Assert.IsTrue(stereo.TryRead(out var a));Assert.IsTrue(whole.TryRead(out var b));Assert.AreEqual(a.RmsLeft,b.RmsLeft);Assert.AreEqual(a.Frames,b.Frames);
        }
        [Test]
        public void SilenceWarmupResetAndAbsoluteGateAreExplicit()
        {
            var m=new MusicLevelMeter(48000);m.ProcessStereo(new float[48000/10*2]);Assert.IsTrue(m.TryRead(out var s));
            Assert.IsFalse(s.MomentaryReady);Assert.IsTrue(double.IsNegativeInfinity(s.MomentaryLufs));Assert.IsTrue(double.IsNegativeInfinity(m.ReadIntegratedLufs()));
            m.ProcessStereo(Sine(48000,1,.000001));Assert.IsTrue(double.IsNegativeInfinity(m.ReadIntegratedLufs()));
            m.ProcessStereo(Sine(48000,3));Assert.Greater(m.ReadIntegratedLufs(),-25);m.Reset();Assert.IsTrue(m.TryRead(out s));
            Assert.AreEqual(0,s.Frames);Assert.AreEqual(0,s.PeakLeft);Assert.IsTrue(double.IsNegativeInfinity(m.ReadIntegratedLufs()));
        }
        [Test]
        public void RelativeGateRejectsVeryQuietPassagesWithoutChangingSignal()
        {
            var loud=Sine(48000,4);var m=new MusicLevelMeter(48000);m.ProcessStereo(loud);double before=m.ReadIntegratedLufs();
            m.ProcessStereo(Sine(48000,4,.0001));Assert.AreEqual(before,m.ReadIntegratedLufs(),.25);
            var original=(float[])loud.Clone();m.ProcessStereo(loud);NUnit.Framework.CollectionAssert.AreEqual(original,loud);
        }
        [Test]
        public void CapacityOverflowAndNonFiniteInputAreDiagnosed()
        {
            var m=new MusicLevelMeter(48000,2,1);m.ProcessStereo(Sine(48000,2));Assert.IsTrue(m.TryRead(out var s));
            Assert.IsTrue(s.IntegratedCapacityExceeded);Assert.IsTrue(double.IsNaN(m.ReadIntegratedLufs()));
            m.Reset();m.ProcessStereo(new[]{float.NaN,float.PositiveInfinity});Assert.IsTrue(m.TryRead(out s));Assert.AreEqual(2,s.InvalidSamples);Assert.AreEqual(0,s.PeakLeft);
            Assert.Throws<ArgumentException>(()=>m.ProcessStereo(new float[3]));Assert.Throws<ArgumentException>(()=>new MusicLevelMeter(7999));
        }
        [Test]
        public void OptInEngineMeterObservesStereoWithoutChangingPlayback()
        {
            using(var scenario=MusicPlaybackScenarios.Build("Oscillators"))
            {
                var cue=scenario.Definition.Compile(3);var plain=new AdaptiveMusicEngine(cue,48000);var observed=new AdaptiveMusicEngine(cue,48000);
                var meter=new MusicLevelMeter(48000);observed.AttachLevelMeter(meter);
                var a=new float[48000*2];var b=new float[a.Length];plain.RenderStereo(a);observed.RenderStereo(b);
                CollectionAssert.AreEqual(a,b);Assert.IsTrue(meter.TryRead(out var s));Assert.AreEqual(48000,s.Frames);Assert.Greater(s.PeakLeft,0);
                Assert.Throws<ArgumentException>(()=>observed.AttachLevelMeter(new MusicLevelMeter(44100)));observed.AttachLevelMeter(null);
            }
        }
        [Test]
        public void ControlThreadReadsConsistentSnapshotsWhileAudioPublishes()
        {
            var meter=new MusicLevelMeter(48000);var pcm=new float[1024];
            for(int i=0;i<pcm.Length;i+=2){pcm[i]=.1f;pcm[i+1]=.2f;}
            var writer=new System.Threading.Thread(()=>{for(int i=0;i<500;i++)meter.ProcessStereo(pcm);});
            writer.Start();long previous=0;
            try{while(writer.IsAlive){if(meter.TryRead(out var s)){Assert.GreaterOrEqual(s.Frames,previous);previous=s.Frames;Assert.AreEqual(s.PeakLeft*2,s.PeakRight,1e-12);Assert.AreEqual(0,s.InvalidSamples);}System.Threading.Thread.Yield();}}
            finally{Assert.IsTrue(writer.Join(30000),"writer finishes");}
            Assert.IsTrue(meter.TryRead(out var final));Assert.AreEqual(500*512,final.Frames);Assert.IsFalse(double.IsNaN(meter.ReadIntegratedLufs()));
        }
    }
}
