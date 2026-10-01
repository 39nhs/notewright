using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    [Serializable]
    public sealed class MusicMixerSettings
    {
        [Tooltip("Output gain into the drive stage and the final soft limiter. Above +12 dB the limiter itself saturates.")]
        [Range(-36, 30)] public float OutputDb;
        [Range(0, 18000)] public float LowPassHz;
        [Range(0, 2000)] public float HighPassHz;
        public bool Compressor;
        [Range(-48, 0)] public float ThresholdDb = -12;
        [Range(1, 20)] public float Ratio = 4;
        [Range(1, 100)] public float AttackMs = 10;
        [Range(10, 1000)] public float ReleaseMs = 150;
        [Range(0, 1)] public float EchoWet;
        [Range(.01f, 2)] public float EchoSeconds = .25f;
        [Range(0, .85f)] public float EchoFeedback = .3f;
        [Tooltip("Bit depth after crushing; 0 = off.")]
        [Range(0, 16)] public int CrushBits;
        [Tooltip("Sample-and-hold factor at the output rate; 1 = off.")]
        [Range(1, 32)] public int CrushDownsample = 1;
        [Range(0, 1)] public float CrushMix = 1;
        [Tooltip("Threshold noise gate on the whole mix (after echo and crusher): silences everything between hits.")]
        public bool NoiseGate;
        [Range(-80, 0)] public float NoiseGateThresholdDb = -30;
        [Tooltip("Attenuation while closed; -96 is practically silence.")]
        [Range(-96, 0)] public float NoiseGateRangeDb = -96;
        [Range(0, 500)] public float NoiseGateHoldMs = 20;
        [Range(1, 500)] public float NoiseGateReleaseMs = 30;
        public bool Gate;
        [Range(.0625f, 4)] public float GateBeats = .25f;
        [Range(.05f, .95f)] public float GateOpen = .5f;
        [Range(0, 1)] public float GateDepth = 1;
        [Tooltip("Step gate: x = open, - or . = closed, one step = GateBeats (spaces and | ignored). Empty = GateOpen duty cycle.")]
        public string GatePattern = "";
        [Tooltip("Playback V2+: waveshaper after the output gain, before the soft limiter. V1 ignores it.")]
        public bool Drive;
        public MusicDriveMode DriveMode = MusicDriveMode.Hard;
        [Tooltip("Gain into the shaper.")]
        [Range(0, 48)] public float DriveDb = 12;
        [Tooltip("Dry/driven blend (1 = only driven).")]
        [Range(0, 1)] public float DriveMix = 1;
        [Tooltip("Low-pass on the driven signal to tame fizz; 0 = off.")]
        [Range(0, 18000)] public float DriveToneHz;
        [Tooltip("Below this frequency the signal bypasses the shaper (clean sub under a torn mid); 0 = drive everything.")]
        [Range(0, 300)] public float DriveKeepBassHz;
        [Tooltip("Level of the driven signal before the blend.")]
        [Range(-24, 12)] public float DriveTrimDb;
        public MusicMixerSnapshot Compile() => new MusicMixerSnapshot(this);
    }

    public sealed class MusicMixerSnapshot
    {
        public const int MaxGateSteps = 128;
        public readonly double Gain, LowPass, HighPass, Threshold, Ratio, Attack, Release, Wet, Delay, Feedback, GateBeats, GateOpen, GateDepth;
        public readonly bool Compressor, Gate;
        public readonly int CrushBits, CrushDownsample;
        public readonly double CrushMix, CrushLevels;
        public readonly bool NoiseGate;
        public readonly double NoiseGateThreshold, NoiseGateRange, NoiseGateHold, NoiseGateRelease;
        internal readonly bool[] GateSteps; // null = duty-cycle gate
        public readonly bool Drive;
        public readonly MusicDriveMode DriveMode;
        public readonly double DriveGain, DriveMix, DriveToneHz, DriveKeepBassHz, DriveTrim;
        public bool Crusher => CrushBits > 0 || CrushDownsample > 1;
        public int GateStepCount => GateSteps?.Length ?? 0;

        public MusicMixerSnapshot(MusicMixerSettings s)
        {
            Check(s.OutputDb,-36,30); Check(s.LowPassHz,0,18000); Check(s.HighPassHz,0,2000);
            Check(s.ThresholdDb,-48,0); Check(s.Ratio,1,20); Check(s.AttackMs,1,100); Check(s.ReleaseMs,10,1000);
            Check(s.EchoWet,0,1); Check(s.EchoSeconds,.009,2); Check(s.EchoFeedback,0,.85);
            Check(s.GateBeats,.0625,4); Check(s.GateOpen,.049,.951); Check(s.GateDepth,0,1);
            Check(s.CrushBits,0,16); Check(s.CrushDownsample,1,32); Check(s.CrushMix,0,1);
            Check(s.NoiseGateThresholdDb,-80,0); Check(s.NoiseGateRangeDb,-96,0); Check(s.NoiseGateHoldMs,0,500); Check(s.NoiseGateReleaseMs,1,500);
            Gain=Math.Pow(10,s.OutputDb/20); LowPass=s.LowPassHz; HighPass=s.HighPassHz;
            Threshold=Math.Pow(10,s.ThresholdDb/20); Ratio=s.Ratio; Attack=s.AttackMs/1000; Release=s.ReleaseMs/1000;
            Wet=s.EchoWet; Delay=s.EchoSeconds; Feedback=s.EchoFeedback;
            Gate=s.Gate; GateBeats=s.GateBeats; GateOpen=s.GateOpen; GateDepth=s.GateDepth; Compressor=s.Compressor;
            CrushBits=s.CrushBits; CrushDownsample=s.CrushDownsample; CrushMix=s.CrushMix;
            CrushLevels=CrushBits>0?Math.Pow(2,CrushBits-1):0;
            NoiseGate=s.NoiseGate; NoiseGateThreshold=Math.Pow(10,s.NoiseGateThresholdDb/20); NoiseGateRange=Math.Pow(10,s.NoiseGateRangeDb/20);
            NoiseGateHold=s.NoiseGateHoldMs/1000; NoiseGateRelease=s.NoiseGateReleaseMs/1000;
            GateSteps=ParsePattern(s.GatePattern);
            Check(s.DriveDb,0,MusicDrive.MaxDriveDb); Check(s.DriveMix,0,1); Check(s.DriveToneHz,0,18000); Check(s.DriveKeepBassHz,0,300); Check(s.DriveTrimDb,-24,12);
            if(!MusicDrive.IsDefined(s.DriveMode)) throw new ArgumentException("Invalid mixer parameter.");
            Drive=s.Drive; DriveMode=s.DriveMode; DriveGain=Math.Pow(10,s.DriveDb/20); DriveMix=s.DriveMix;
            DriveToneHz=s.DriveToneHz; DriveKeepBassHz=s.DriveKeepBassHz; DriveTrim=Math.Pow(10,s.DriveTrimDb/20);
        }

        public static bool[] ParsePattern(string pattern)
        {
            if(string.IsNullOrWhiteSpace(pattern)) return null;
            var steps=new System.Collections.Generic.List<bool>();
            foreach(char c in pattern)
            {
                if(c=='x'||c=='X'||c=='1') steps.Add(true);
                else if(c=='-'||c=='.'||c=='0'||c=='_') steps.Add(false);
                else if(!char.IsWhiteSpace(c)&&c!='|') throw new ArgumentException("Gate pattern accepts x/1 (open), -/./0/_ (closed), spaces and |.");
            }
            if(steps.Count==0) return null; // only separators: same as empty
            if(steps.Count>MaxGateSteps) throw new ArgumentException("Gate pattern must have at most 128 steps.");
            return steps.ToArray();
        }

        static void Check(double v,double min,double max)
        { if(!MusicTrack.Finite(v)||v<min-1e-6||v>max+1e-6) throw new ArgumentException("Invalid mixer parameter."); }
    }

    // Stereo-linked compressor/noise gate and independent delay/filter/crusher histories. Allocated outside the callback.
    internal sealed class MusicMasterMixer
    {
        readonly float[][] delay;
        readonly double[] low=new double[2], high=new double[2], previous=new double[2], held=new double[2];
        readonly int rate;
        int cursor, crushCounter;
        double envelope, gateGain, gateHold;
        public MusicMasterMixer(int sampleRate) { rate=sampleRate; delay=new[]{new float[rate*2+1],new float[rate*2+1]}; }
        /// <summary>Echo buffer sized to one fixed delay (track buses): the delay time of a snapshot never changes.</summary>
        public MusicMasterMixer(int sampleRate,double delaySeconds) { rate=sampleRate; int n=(int)(delaySeconds*rate)+2; delay=new[]{new float[n],new float[n]}; }
        public void Process(ref double left, ref double right, MusicMixerSnapshot s, double beat)
        {
            double l=Filter(left,s,0), r=Filter(right,s,1);
            double peak=Math.Max(Math.Abs(l),Math.Abs(r));
            double coefficient=Math.Exp(-1/(rate*(peak>envelope?s.Attack:s.Release)));
            envelope=coefficient*envelope+(1-coefficient)*peak;
            double compression=s.Compressor && envelope>s.Threshold ? Math.Pow(envelope/s.Threshold,1/s.Ratio-1):1;
            l*=compression; r*=compression;
            int read=(cursor-(int)(s.Delay*rate)+delay[0].Length)%delay[0].Length;
            double dl=delay[0][read],dr=delay[1][read];
            delay[0][cursor]=(float)(l+dl*s.Feedback); delay[1][cursor]=(float)(r+dr*s.Feedback);
            cursor=(cursor+1)%delay[0].Length;
            l=l*(1-s.Wet)+dl*s.Wet; r=r*(1-s.Wet)+dr*s.Wet;
            if(s.Crusher)
            {
                if(crushCounter==0){ held[0]=Quantize(l,s); held[1]=Quantize(r,s); }
                crushCounter=(crushCounter+1)%s.CrushDownsample;
                l=l*(1-s.CrushMix)+held[0]*s.CrushMix; r=r*(1-s.CrushMix)+held[1]*s.CrushMix;
            }
            else crushCounter=0;
            double noiseGate=1;
            if(s.NoiseGate)
            {
                // Opens instantly above threshold, closes 3 dB lower after the hold time: no chatter on waveform zero crossings.
                double level=Math.Max(Math.Abs(l),Math.Abs(r));
                if(level>=s.NoiseGateThreshold) gateHold=s.NoiseGateHold+.002;
                else if(level<s.NoiseGateThreshold*.708) gateHold-=1.0/rate;
                double target=gateHold>0?1:0;
                double k=target>gateGain?1-Math.Exp(-1/(rate*.0005)):1-Math.Exp(-1/(rate*s.NoiseGateRelease));
                gateGain+=(target-gateGain)*k;
                noiseGate=s.NoiseGateRange+(1-s.NoiseGateRange)*gateGain;
            }
            else { gateGain=1; gateHold=0; }
            double gate=1;
            if(s.Gate)
            {
                double position=Math.Max(0,beat)/s.GateBeats, phase=position%1, open;
                if(s.GateSteps!=null)
                {
                    long step=(long)Math.Floor(position); int n=s.GateSteps.Length;
                    bool on=s.GateSteps[(int)(step%n)], before=s.GateSteps[(int)((step+n-1)%n)], after=s.GateSteps[(int)((step+1)%n)];
                    open=!on?0:Math.Max(0,Math.Min(1,Math.Min(before?1:phase/.03,after?1:(1-phase)/.03)));
                }
                else open=Math.Max(0,Math.Min(1,Math.Min(phase/.03,(s.GateOpen-phase)/.03)));
                gate=1-s.GateDepth+open*s.GateDepth;
            }
            left=l*s.Gain*gate*noiseGate;
            right=r*s.Gain*gate*noiseGate;
        }
        static double Quantize(double x,MusicMixerSnapshot s)=>s.CrushLevels>0?Math.Round(x*s.CrushLevels)/s.CrushLevels:x;
        double Filter(double x,MusicMixerSnapshot s,int ch)
        {
            if(s.HighPass>0)
            { double a=Math.Exp(-2*Math.PI*s.HighPass/rate); high[ch]=a*(high[ch]+x-previous[ch]); previous[ch]=x; x=high[ch]; }
            if(s.LowPass>0)
            { double a=1-Math.Exp(-2*Math.PI*Math.Min(s.LowPass,rate*.45)/rate); low[ch]+=a*(x-low[ch]); x=low[ch]; }
            return x;
        }
    }
}
