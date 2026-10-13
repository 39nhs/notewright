using System;
using System.Collections.Generic;
using System.Threading;

namespace Graze.Presentation.Audio
{
    public sealed class MusicPluginOutputRoute
    {
        public const int MaxOutputs = 16;
        public int Bus { get; }
        public string Instrument { get; }
        public MusicPluginOutputRoute(int bus, string instrument)
        {
            if (bus < 0 || bus >= 64 || string.IsNullOrWhiteSpace(instrument)) throw new ArgumentException("Invalid native output route.");
            Bus = bus; Instrument = instrument;
        }
    }
    public sealed class MusicPluginOutputBus
    {
        public int Index { get; }
        public string Name { get; }
        public int Channels { get; }
        public bool Active { get; }
        internal MusicPluginOutputBus(int index,string name,int channels,bool active)
        { Index=index;Name=name;Channels=channels;Active=active; }
    }
    /// <summary>One control-thread instrument instance. Buffers follow the requested bus order. No audio-callback use.</summary>
    public interface IMusicPluginOutputProcessor : IDisposable
    {
        int LatencySamples { get; }
        uint ParameterId(string key);
        void Process(float[][] outputs,int frames,long firstFrame,double bpm,double beat,double loopBeats,int meter,
            IReadOnlyList<MusicPluginMidiEvent> midi,IReadOnlyList<MusicPluginParameterPoint> automation);
    }
    sealed class NativeOutputProcessor : IMusicPluginOutputProcessor
    {
        readonly MusicPluginHost host;
        public int LatencySamples => host.LatencySamples;
        internal NativeOutputProcessor(MusicPluginInsertContext context,MusicPluginTarget target,int rate,int block)
        {
            var registration=context.Resolve(target.Plugin);
            host=new MusicPluginHost(context.HostExecutable);
            try
            {
                var buses=new int[target.Outputs.Count];for(int i=0;i<buses.Length;i++)buses[i]=target.Outputs[i].Bus;
                host.LoadOutputs(registration,buses,rate,block);
                var ids=new HashSet<uint>();foreach(var key in target.Parameters.Keys)if(!ids.Add(host.ParameterId(key)))throw new ArgumentException("Duplicate native parameter ID.");
                host.Configure(target.State,target.Parameters);
            }
            catch { host.Dispose();throw; }
        }
        public uint ParameterId(string key)=>host.ParameterId(key);
        public void Process(float[][] outputs,int frames,long firstFrame,double bpm,double beat,double loopBeats,int meter,
            IReadOnlyList<MusicPluginMidiEvent> midi,IReadOnlyList<MusicPluginParameterPoint> automation)
            =>host.ProcessOutputs(outputs,frames,firstFrame,bpm,beat,loopBeats,midi,meter,automation);
        public void Dispose()=>host.Dispose();
    }
    /// <summary>V3 prepared routing. Source notes drive one instance; destinations replace stock audio before their own effects.</summary>
    public static class MusicPluginMultiOutput
    {
        internal sealed class Plan
        {
            internal int Source;
            internal int[] Destinations;
            internal MusicPluginTarget Target;
        }
        /// <summary>Checks exact/wildcard targets, unique destinations and V3 effect-enabled tracks without launching native code.</summary>
        public static void Validate(MusicCue cue,MusicPluginMod mod) => Plans(cue,mod);
        internal static List<Plan> Plans(MusicCue cue,MusicPluginMod mod)
        {
            if(cue==null)throw new ArgumentNullException(nameof(cue));
            var result=new List<Plan>();if(mod==null)return result;
            var owners=new HashSet<int>();var destinations=new HashSet<int>();
            int Slot(string instrument)
            {
                int found=-1;
                for(int i=0;i<cue.PartCount;i++)if(cue.Parts[i].InstrumentId==instrument){if(found>=0)throw new ArgumentException("Output routing requires unique instrument IDs.");found=i;}
                if(found<0)throw new ArgumentException("Output track not found: "+instrument);
                if(cue.PlaybackVersion<MusicPlaybackVersion.V3||cue.PlaybackVersionOf(cue.Parts[found])<MusicPlaybackVersion.V3||cue.Parts[found].Effects==null)
                    throw new ArgumentException("Multi-output source/destinations need V3 tracks with Track Effects enabled: "+instrument);
                return found;
            }
            // Reject an applicable stale source rather than silently losing its routing. Exact targets supersede wildcard targets.
            foreach(var t in mod.Targets)if(t.Outputs.Count>0&&(t.Song==cue.Name||t.Song=="*"))
            {
                int at=-1;for(int i=0;i<cue.PartCount;i++)if(cue.Parts[i].InstrumentId==t.Instrument){at=i;break;}
                if(at>=0&&!ReferenceEquals(mod.Find(cue,cue.Parts[at]),t))continue;
                Slot(t.Instrument);
            }
            for(int i=0;i<cue.PartCount;i++)
            {
                var target=mod.Find(cue,cue.Parts[i]);if(target==null||target.Outputs.Count==0)continue;
                int source=Slot(target.Instrument);owners.Add(source);
                var plan=new Plan{Source=source,Target=target,Destinations=new int[target.Outputs.Count]};
                for(int b=0;b<plan.Destinations.Length;b++)
                {
                    int slot=Slot(target.Outputs[b].Instrument);
                    if(!destinations.Add(slot))throw new ArgumentException("A destination cannot receive two native outputs.");
                    var other=mod.Find(cue,cue.Parts[slot]);
                    if(other!=null&&!ReferenceEquals(other,target))throw new ArgumentException("An output destination cannot also own a plugin target.");
                    plan.Destinations[b]=slot;
                }
                result.Add(plan);
            }
            foreach(var plan in result)foreach(int slot in plan.Destinations)
                if(slot!=plan.Source&&owners.Contains(slot))throw new ArgumentException("A plugin source cannot receive another plugin output.");
            return result;
        }
        internal static void Render(MusicCue cue,int rate,int frames,float[][] raw,double[] clock,List<Plan> plans,
            Func<MusicPluginTarget,int,int,IMusicPluginOutputProcessor> factory,CancellationToken token)
        {
            const int block=512;
            foreach(var plan in plans)
            {
                token.ThrowIfCancellationRequested();Array.Clear(raw[plan.Source],0,raw[plan.Source].Length);
                var target=plan.Target;
                using(var processor=factory(target,rate,block))
                {
                    if(processor==null||processor.LatencySamples<0||processor.LatencySamples>rate*10)throw new InvalidOperationException("Invalid multi-output processor latency.");
                    int latency=processor.LatencySamples;
                    var pcm=new float[target.Outputs.Count][];for(int b=0;b<pcm.Length;b++)pcm[b]=new float[block*2];
                    var schedule=new MusicPluginSchedule(cue,cue.Parts[plan.Source],target,rate,false);
                    var lanes=new SortedDictionary<uint,MusicAutomation>();
                    foreach(var lane in target.Automation)
                    {
                        uint id=processor.ParameterId(lane.Key);if(lanes.ContainsKey(id))throw new ArgumentException("Two automation keys address the same parameter.");
                        var points=new MusicAutomationPoint[lane.Value.Count];for(int i=0;i<points.Length;i++)points[i]=lane.Value[i];
                        lanes.Add(id,new MusicAutomation(MusicAutomationTarget.PluginParameter,points,cue.LoopBeats));
                    }
                    var automation=new List<MusicPluginParameterPoint>();
                    for(int at=0;at<frames+latency;at+=block)
                    {
                        token.ThrowIfCancellationRequested();int count=Math.Min(block,frames+latency-at);
                        double beat=schedule.BeatAt(at/(double)rate);automation.Clear();
                        foreach(var lane in lanes)for(int offset=0;;offset=Math.Min(offset+64,count-1))
                        {
                            automation.Add(new MusicPluginParameterPoint(lane.Key,offset,lane.Value.Evaluate(Math.Min(cue.LoopBeats,schedule.BeatAt((at+offset)/(double)rate)))));
                            if(offset==count-1)break;
                        }
                        var midi=schedule.Block(at,count);
                        processor.Process(pcm,count,at,cue.Tempo.At(beat,false),beat,cue.LoopBeats,cue.BeatsPerBar,midi,automation);
                        if(processor.LatencySamples!=latency)throw new InvalidOperationException("Native instrument latency changed during preparation.");
                        for(int f=0;f<count;f++)
                        {
                            int output=at+f-latency;if(output<0||output>=frames)continue;
                            double local=Math.Min(cue.LoopBeats,clock[output]);
                            for(int b=0;b<pcm.Length;b++)
                            {
                                var part=cue.Parts[plan.Destinations[b]];
                                double gain=part.Gain*target.Gain*(part.VolumeAutomation==null?1:MusicAutomation.Gain(part.VolumeAutomation.Evaluate(local)));
                                double pan=Math.Max(-1,Math.Min(1,(part.PanAutomation?.Evaluate(local)??part.Pan)+target.Pan));
                                double left=pcm[b][f*2],right=pcm[b][f*2+1];
                                if(!MusicTrack.Finite(left)||!MusicTrack.Finite(right))throw new InvalidOperationException("Non-finite multi-output PCM.");
                                if(part.DriveDb>0){left=MusicDrive.Shape(part.DriveMode,left*part.DriveGain);right=MusicDrive.Shape(part.DriveMode,right*part.DriveGain);}
                                var destination=raw[plan.Destinations[b]];
                                destination[output*2]=(float)(left*gain*(pan>0?1-pan:1));destination[output*2+1]=(float)(right*gain*(pan<0?1+pan:1));
                            }
                        }
                    }
                }
            }
        }
    }
}
