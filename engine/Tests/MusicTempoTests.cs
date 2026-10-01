using System;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public class MusicTempoTests
    {
        static MusicTempoChange Change(double beat,double bpm,double ramp=0) => new MusicTempoChange { Beat=beat,Bpm=bpm,RampBeats=ramp };
        static MusicCue Cue(params MusicTempoChange[] changes) => new MusicCue("Tempo",120,4,8,Array.Empty<MusicPart>(),Array.Empty<MusicMixEffect>(),changes);
        [Test]
        public void ImportedTwentyBpmEndingIsPreserved()
        {
            var cue=Cue(Change(1.5,20));
            Assert.AreEqual(20,cue.Tempo.At(2));
            Assert.AreEqual(.75+6.5*3,cue.Tempo.SecondsAt(8),1e-9);
        }
        [Test]
        public void FractionalBeatInstantChangeAndLoopReset()
        {
            var cue=Cue(Change(1.5,240));
            var engine=new AdaptiveMusicEngine(cue,8000);
            engine.Render(new float[6000]); Assert.AreEqual(1.5,engine.Beat,.001);
            engine.Render(new float[2000]); Assert.AreEqual(2.5,engine.Beat,.002); Assert.AreEqual(240,engine.Bpm);
            Assert.AreEqual(120,cue.Tempo.At(8));
            Assert.AreEqual(2.375,cue.Tempo.SecondsAt(8),1e-10);
        }
        [Test]
        public void LinearRampDurationMatchesAudioClockAcrossBuffers()
        {
            var cue=Cue(Change(.5,240,2));
            Assert.AreEqual(180,cue.Tempo.At(1.5),1e-10);
            double seconds=.25+Math.Log(2);
            Assert.AreEqual(seconds,cue.Tempo.SecondsAt(2.5),1e-10);
            var a=new AdaptiveMusicEngine(cue,8000,playOnce:true);
            var b=new AdaptiveMusicEngine(cue,8000,playOnce:true);
            int count=(int)Math.Round(seconds*8000);
            a.Render(new float[count]);
            for(int i=0;i<count;i++) b.Render(new float[1]);
            Assert.AreEqual(a.Beat,b.Beat,1e-10); Assert.AreEqual(2.5,a.Beat,.002);
        }
        [Test]
        public void InvalidOverlapsAndDuplicatePointsAreRejected()
        {
            Assert.Throws<ArgumentException>(()=>Cue(Change(1,180,3),Change(2,200)));
            Assert.Throws<ArgumentException>(()=>Cue(Change(1,180),Change(1,200)));
            Assert.Throws<ArgumentException>(()=>Cue(Change(double.NaN,120)));
            Assert.Throws<ArgumentException>(()=>Cue(Change(7,120,2)));
        }
        [Test]
        public void MapIsCopiedAndStartPointAffectsInitialTempo()
        {
            var points=new[]{Change(0,160),Change(3.25,80)};
            var cue=Cue(points); points[0]=Change(0,300);
            var engine=new AdaptiveMusicEngine(cue,8000);
            Assert.AreEqual(160,engine.Bpm);
            Assert.AreEqual(160,cue.Tempo.At(3.249)); Assert.AreEqual(80,cue.Tempo.At(3.25));
        }
    }
}
