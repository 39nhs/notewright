using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicPluginMultiOutputTests
    {
        const string Id="0123456789ABCDEF0123456789ABCDEF";
        static MusicCueDefinition Song()
        {
            var s=ScriptableObject.CreateInstance<MusicCueDefinition>();s.name="Outputs";s.PlaybackVersion=MusicPlaybackVersion.V3;s.Bpm=120;s.LoopBeats=4;
            s.Parts=new[]{new MusicCueDefinition.Part{Name="Source",InstrumentId="Source",Gain=.2f,TrackEffects=true,Notes=new[]{new MusicNote(0,1,60)}},
                new MusicCueDefinition.Part{Name="A",InstrumentId="A",Gain=.2f,TrackEffects=true},new MusicCueDefinition.Part{Name="B",InstrumentId="B",Gain=.2f,TrackEffects=true}};
            return s;
        }
        static MusicPluginTarget Target(params MusicPluginOutputRoute[] routes)=>new MusicPluginTarget("*","Source",new MusicPluginIdentity(Id),outputs:routes);
        static MusicPluginMod Mod(params MusicPluginOutputRoute[] routes)=>new MusicPluginMod("Outputs",Target(routes));
        static MusicPreparedInsertSong Render(MusicCueDefinition song,MusicPluginMod mod,Func<MusicPluginTarget,int,int,IMusicPluginOutputProcessor> factory,double seconds=1)
            =>MusicPluginInsertRenderer.PrepareOutputs(song.Compile(),mod,8000,factory,(i,r,b)=>throw new Exception("No effect expected"),seconds);
        sealed class TestProcessor:IMusicPluginOutputProcessor
        {
            readonly int delay;readonly float[][] rings;int at;internal int Calls,Ons,Offs;internal bool Disposed;internal double LastBpm;internal readonly List<MusicPluginParameterPoint> Points=new List<MusicPluginParameterPoint>();
            internal bool Bad,ChangeLatency;
            public int LatencySamples=>delay+(ChangeLatency&&Calls>1?1:0);
            internal TestProcessor(int outputs,int delay=0){this.delay=delay;rings=new float[outputs][];for(int i=0;i<outputs;i++)rings[i]=new float[Math.Max(1,delay)*2];}
            public uint ParameterId(string key)=>key=="level"||key=="0"?0U:1U;
            public void Process(float[][] outputs,int frames,long first,double bpm,double beat,double loop,int meter,
                IReadOnlyList<MusicPluginMidiEvent> midi,IReadOnlyList<MusicPluginParameterPoint> automation)
            {
                Calls++;LastBpm=bpm;Ons+=midi.Count(e=>e.On);Offs+=midi.Count(e=>!e.On);Points.AddRange(automation);
                for(int f=0;f<frames;f++)
                {
                    for(int b=0;b<outputs.Length;b++)for(int c=0;c<2;c++)
                    {
                        float value=(float)(.01*(b+1)*(c+1));
                        outputs[b][f*2+c]=Bad?float.NaN:delay==0?value:rings[b][at*2+c];rings[b][at*2+c]=value;
                    }
                    at=(at+1)%Math.Max(1,delay);
                }
            }
            public void Dispose(){Disposed=true;}
        }
        [Test] public void OutputRoutesRoundTripAndOldModsKeepNoRoutes()
        {
            var mod=Mod(new MusicPluginOutputRoute(2,"B"),new MusicPluginOutputRoute(1,"A"));
            var copy=MusicPluginMod.FromJson(mod.ToJson());Assert.AreEqual(2,copy.Targets[0].Outputs.Count);
            Assert.IsTrue(mod.ToJson().Contains("graze-plugin-mod/2"));
            Assert.Throws<ArgumentException>(()=>MusicPluginMod.FromJson(mod.ToJson().Replace("graze-plugin-mod/2","graze-plugin-mod/1")));
            Assert.AreEqual(2,copy.Targets[0].Outputs[0].Bus);Assert.AreEqual("B",copy.Targets[0].Outputs[0].Instrument);
            var ordinary=new MusicPluginMod("Old",Target());Assert.AreEqual(0,MusicPluginMod.FromJson(ordinary.ToJson()).Targets[0].Outputs.Count);
            Assert.IsFalse(ordinary.ToJson().Contains("outputs"));
            var array=new[]{new MusicPluginOutputRoute(0,"Source")};var target=Target(array);array[0]=new MusicPluginOutputRoute(1,"B");Assert.AreEqual(0,target.Outputs[0].Bus);
        }
        [Test] public void InvalidDuplicateAndOversizedRoutesAreRejected()
        {
            Assert.Throws<ArgumentException>(()=>new MusicPluginOutputRoute(-1,"A"));Assert.Throws<ArgumentException>(()=>new MusicPluginOutputRoute(64,"A"));Assert.Throws<ArgumentException>(()=>new MusicPluginOutputRoute(0," "));
            Assert.Throws<ArgumentException>(()=>Target(new MusicPluginOutputRoute(0,"A"),new MusicPluginOutputRoute(0,"B")));
            Assert.Throws<ArgumentException>(()=>Target(new MusicPluginOutputRoute(0,"A"),new MusicPluginOutputRoute(1,"A")));
            Assert.Throws<ArgumentException>(()=>Target(Enumerable.Range(0,17).Select(i=>new MusicPluginOutputRoute(i,"Track"+i)).ToArray()));
            Assert.Throws<ArgumentException>(()=>MusicPluginMod.FromJson(Mod(new MusicPluginOutputRoute(0,"A")).ToJson().Replace("\"bus\":0","\"bus\":0.5")));
        }
        [Test] public void MissingOldUnpreparedAndAmbiguousTracksAreRejectedBeforeNativeLoad()
        {
            var s=Song();try{
                var mod=Mod(new MusicPluginOutputRoute(0,"Missing"));Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
                mod=Mod(new MusicPluginOutputRoute(0,"A"));s.PlaybackVersion=MusicPlaybackVersion.V2;Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
                s.PlaybackVersion=MusicPlaybackVersion.V3;s.Parts[1].PlaybackVersion=MusicPlaybackVersion.V2;Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
                s.Parts[1].PlaybackVersion=0;s.Parts[1].TrackEffects=false;Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
                s.Parts[1].TrackEffects=true;s.Parts[2].InstrumentId="A";Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void DestinationCannotAlsoOwnAPluginAndLiveSessionDoesNotSilentlyDropRoutes()
        {
            var s=Song();try{
                var mod=new MusicPluginMod("Conflict",Target(new MusicPluginOutputRoute(0,"A")),new MusicPluginTarget("*","A",new MusicPluginIdentity(Id)));
                Assert.Throws<ArgumentException>(()=>MusicPluginMultiOutput.Validate(s.Compile(),mod));
                Assert.Throws<ArgumentException>(()=>new MusicPluginSession("unused",new MusicPluginCatalog(),new[]{Id},Mod(new MusicPluginOutputRoute(0,"A")),8000));
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void OneInstanceDrivesMultipleOutputsAndDestinationNotesAreReplaced()
        {
            var s=Song();try{
                var mod=Mod(new MusicPluginOutputRoute(2,"B"),new MusicPluginOutputRoute(1,"A"));int instances=0;TestProcessor p=null;
                var expected=Render(s,mod,(t,r,b)=>{instances++;return p=new TestProcessor(2);},1.1).CopyStereo();
                Assert.AreEqual(1,instances);Assert.AreEqual(1,p.Ons);Assert.AreEqual(1,p.Offs);Assert.IsTrue(p.Disposed);Assert.Greater(expected.Sum(x=>Math.Abs(x)),1);
                s.Parts[1].Notes=new[]{new MusicNote(0,2,90)};s.Parts[2].Notes=new[]{new MusicNote(0,2,40)};
                CollectionAssert.AreEqual(expected,Render(s,mod,(t,r,b)=>new TestProcessor(2),1.1).CopyStereo());
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void OutputLatencyIsAlignedForPartialBlocksAndAllDestinations()
        {
            var s=Song();try{
                var mod=Mod(new MusicPluginOutputRoute(1,"A"),new MusicPluginOutputRoute(2,"B"));
                CollectionAssert.AreEqual(Render(s,mod,(t,r,b)=>new TestProcessor(2),.113).CopyStereo(),Render(s,mod,(t,r,b)=>new TestProcessor(2,717),.113).CopyStereo());
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void NativeErrorsCancellationAndBudgetFailExplicitlyAndDispose()
        {
            var s=Song();try{
                var mod=Mod(new MusicPluginOutputRoute(1,"A"));TestProcessor processor=null;
                Assert.Throws<InvalidOperationException>(()=>Render(s,mod,(t,r,b)=>processor=new TestProcessor(1){Bad=true}));Assert.IsTrue(processor.Disposed);
                Assert.Throws<InvalidOperationException>(()=>Render(s,mod,(t,r,b)=>processor=new TestProcessor(1){ChangeLatency=true}));Assert.IsTrue(processor.Disposed);
                var cancel=new System.Threading.CancellationTokenSource();cancel.Cancel();
                Assert.Throws<OperationCanceledException>(()=>MusicPluginInsertRenderer.PrepareOutputs(s.Compile(),mod,8000,(t,r,b)=>throw new Exception(),(i,r,b)=>throw new Exception(),cancellation:cancel.Token));cancel.Dispose();
                s.LoopBeats=120;
                Assert.Throws<ArgumentException>(()=>MusicPluginInsertRenderer.PrepareOutputs(s.Compile(),mod,48000,(t,r,b)=>throw new Exception(),(i,r,b)=>throw new Exception(),60,32));
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void VolumePanAutomationDriveAndSourceSilenceFollowDestinationTrack()
        {
            var s=Song();try{
                s.Parts[1].Pan=-1;s.Parts[2].Gain=0;
                var mod=Mod(new MusicPluginOutputRoute(1,"A"),new MusicPluginOutputRoute(2,"B"));
                var pcm=Render(s,mod,(t,r,b)=>new TestProcessor(2)).CopyStereo();Assert.Greater(pcm.Where((v,i)=>i%2==0).Sum(x=>Math.Abs(x)),1);
                Assert.AreEqual(0,pcm.Where((v,i)=>i%2==1).Sum(x=>Math.Abs(x)),"unmapped source is silent, B muted, A left only");
                s.Parts[1].Automation=new[]{new MusicAutomationLane(MusicAutomationTarget.VolumeDb,new MusicAutomationPoint(0,-80),new MusicAutomationPoint(1,-80))};
                var muted=Render(s,mod,(t,r,b)=>new TestProcessor(2)).CopyStereo();Assert.Less(muted.Sum(x=>Math.Abs(x)),pcm.Sum(x=>Math.Abs(x))*.001);
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void ParameterAutomationUsesSourceClockAndSortedIds()
        {
            var s=Song();try{
                var target=new MusicPluginTarget("*","Source",new MusicPluginIdentity(Id),automation:new Dictionary<string,MusicAutomationPoint[]>{["level"]=new[]{new MusicAutomationPoint(0,.1f),new MusicAutomationPoint(2,.9f)}},outputs:new[]{new MusicPluginOutputRoute(0,"Source")});
                TestProcessor p=null;Render(s,new MusicPluginMod("Automation",target),(t,r,b)=>p=new TestProcessor(1));Assert.Greater(p.Points.Count,16);Assert.AreEqual(.1f,p.Points[0].Value,1e-6);Assert.Greater(p.Points.Last().Value,.8);
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void ExactMultiOutputTargetDoesNotAlsoLaunchShadowedOrdinaryWildcard()
        {
            var s=Song();try{
                var wildcard=new MusicPluginTarget("*","Source",new MusicPluginIdentity(Id));
                var exact=new MusicPluginTarget(s.name,"Source",new MusicPluginIdentity(Id),outputs:new[]{new MusicPluginOutputRoute(1,"A")});
                int loads=0;var prepared=Render(s,new MusicPluginMod("Exact",wildcard,exact),(t,r,b)=>{loads++;Assert.AreEqual(s.name,t.Song);return new TestProcessor(1);});
                Assert.AreEqual(1,loads);Assert.Greater(prepared.CopyStereo().Sum(v=>Math.Abs(v)),1);
                s.Parts[0].PlaybackVersion=MusicPlaybackVersion.V2;s.Parts[0].TrackEffects=false;
                var unused=Target(new MusicPluginOutputRoute(1,"Missing"));
                Assert.DoesNotThrow(()=>MusicPluginMultiOutput.Validate(s.Compile(),new MusicPluginMod("Shadowed",unused,new MusicPluginTarget(s.name,"Source",new MusicPluginIdentity(Id)))));
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
        [Test] public void InstalledSurgeSceneOutputsAreSeparateAndPreparedRoutesRender()
        {
            var roots=Vst3TestHost.Roots("installed instruments");
            string exe=Vst3TestHost.Executable;var catalog=MusicPluginCatalog.Scan(exe,roots);
            var plugin=catalog.Plugins.First(p=>p.Identity.Name=="Surge XT");
            using(var host=new MusicPluginHost(exe))
            {
                host.LoadOutputs(plugin,new[]{2,1,0},48000,512);Assert.AreEqual(4,host.ProtocolVersion);Assert.AreEqual(3,host.OutputBuses.Count);Assert.AreEqual("Scene A",host.OutputBuses[1].Name);
                host.Configure(new MusicPluginState(null));var pcm=new[]{new float[1024],new float[1024],new float[1024]};double a=0,main=0,b=0;
                for(int block=0;block<20;block++){
                    host.ProcessOutputs(pcm,512,block*512,120,block*512/24000.0,4,block==0?new[]{new MusicPluginMidiEvent(0,60,.7f,1,true)}:null);
                    a+=pcm[1].Sum(v=>Math.Abs(v));b+=pcm[0].Sum(v=>Math.Abs(v));main+=pcm[2].Sum(v=>Math.Abs(v));}
                Assert.Greater(a,1);Assert.Greater(main,1);Assert.Less(b,1e-8,"factory single scene B stays silent rather than copying main");
                Assert.Throws<InvalidOperationException>(()=>host.Process(pcm[0],512,0,120,0,4));Assert.Throws<ArgumentException>(()=>host.ProcessOutputs(new[]{pcm[0],pcm[0],pcm[2]},512,0,120,0,4));
                Assert.Throws<System.IO.IOException>(()=>host.LoadOutputs(plugin,new[]{3}));
                host.Load(plugin);host.Process(pcm[0],512,0,120,0,4);
            }
            var s=Song();try{
                var target=new MusicPluginTarget(s.name,"Source",plugin.Identity,outputs:new[]{new MusicPluginOutputRoute(1,"A"),new MusicPluginOutputRoute(2,"B")});
                var mod=new MusicPluginMod("Scenes",target);var context=new MusicPluginInsertContext(exe,catalog,new[]{plugin.Identity.ClassId});
                var prepared=MusicPluginInsertRenderer.PrepareAsync(s.Compile(),context,48000,.4,instruments:mod).GetAwaiter().GetResult();
                Assert.AreEqual(0,prepared.Diagnostics.Count);Assert.Greater(prepared.CopyStereo().Sum(v=>Math.Abs(v)),1);
            }finally{UnityEngine.Object.DestroyImmediate(s);}
        }
    }
}
