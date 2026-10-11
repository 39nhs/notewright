using System;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    [Serializable]
    public sealed class MusicMixerSettings
    {
        [Tooltip("Output gain into the drive stage and the final soft limiter. Above +12 dB the limiter itself saturates.")]
        [Range(-36, 30)] public float OutputDb;
        [Tooltip("Playback V3+, master only: a look-ahead true-peak brick-wall limiter replaces the final soft limiter (2 ms latency). Off = the soft limiter, as before.")]
        public bool Limiter;
        [Tooltip("Gain into the limiter (dB). Raise it for loudness; the renderer sets it for a song's targetLufs.")]
        [Range(-24, 24)] public float LimiterInputDb;
        [Tooltip("Highest true peak the limiter lets through (dBTP).")]
        [Range(-12, 0)] public float LimiterCeilingDb = -1;
        [Tooltip("Time the gain takes to recover after a peak (ms).")]
        [Range(5, 2000)] public float LimiterReleaseMs = 150;
        public const float MaxLimiterInputDb = 24;
        /// <summary>Look-ahead (latency) of the master limiter.</summary>
        public const double LimiterLookaheadSeconds = .002;
        [Range(0, 18000)] public float LowPassHz;
        [Range(0, 2000)] public float HighPassHz;
        public bool Compressor;
        [Range(-48, 0)] public float ThresholdDb = -12;
        [Range(1, 20)] public float Ratio = 4;
        [Range(1, 100)] public float AttackMs = 10;
        [Range(10, 1000)] public float ReleaseMs = 150;
        [Tooltip("Playback V3+ sidechain: the compressor reacts to this track's own signal (its name/InstrumentId, before its effects) instead of its input — e.g. the kick ducking a bass bus. Empty = off. Needs Compressor on.")]
        public string SidechainTrack = "";
        [Range(0, 1)] public float EchoWet;
        [Range(.01f, 2)] public float EchoSeconds = .25f;
        [Tooltip("Playback V3+: echo time in beats, following the tempo (0 = use Echo Seconds). Longer than 2 s is held at 2 s.")]
        [Range(0, 8)] public float EchoBeats;
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
        [Tooltip("Playback V3+ beat repeat at the end of the chain, played by a Stutter automation lane (≥ 0.5 = repeat). Older versions ignore it.")]
        public bool Stutter;
        [Tooltip("Slice the stutter repeats, in beats (held at 2 seconds).")]
        [Range(1 / 32f, 4)] public float StutterBeats = .25f;
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
        // ---- Playback V2+: amp and cabinet after drive, before EQ. Off by default; V1 ignores them. ----
        [Tooltip("Playback V2+: guitar/bass amp after drive: tight high-pass, gain into 4x-oversampled tube-style clipping, tone stack, presence. Feed it a clean (DI) instrument. V1 ignores it.")]
        public bool Amp;
        public MusicAmpModel AmpModel = MusicAmpModel.HighGain;
        [Tooltip("Preamp gain into the clipping stages.")]
        [Range(0, 60)] public float AmpGainDb = 30;
        [Tooltip("High-pass before the clipping (tighter palm mutes); 0 = off.")]
        [Range(0, 400)] public float AmpTightHz = 90;
        [Range(-12, 12)] public float AmpBassDb;
        [Range(-12, 12)] public float AmpMidDb;
        [Range(-12, 12)] public float AmpTrebleDb;
        [Range(-12, 12)] public float AmpPresenceDb;
        [Tooltip("Amp output level.")]
        [Range(-36, 12)] public float AmpLevelDb = -6;
        [Tooltip("Playback V2+: speaker cabinet and close-mic model after the amp (also usable alone). Off = none. V1 ignores it.")]
        public MusicCabinet Cabinet;
        [Tooltip("Dry/cabinet blend (1 = only the cabinet).")]
        [Range(0, 1)] public float CabinetMix = 1;
        [Tooltip("Playback V3+: mono/stereo cabinet impulse response, max 250 ms, loaded PCM. After amp/model cabinet, before EQ. Empty = off.")]
        public AudioClip CabinetImpulse;
        [Range(0, 1)] public float CabinetImpulseMix = 1;
        [Range(-24, 24)] public float CabinetImpulseGainDb;
        [Tooltip("Normalize maximum L/R absolute coefficient sum to 1 on compile (bounds convolution gain). Off preserves recorded IR gain.")]
        public bool CabinetImpulseNormalize;
        // ---- Playback V2+: tone and space stage after drive (EQ → chorus → reverb). Off by default; V1 ignores it. ----
        [Tooltip("Playback V2+: 3-band EQ (low shelf, peak, high shelf) after drive. V1 ignores it.")]
        public bool Eq;
        [Range(-24, 24)] public float EqLowDb;
        [Range(20, 1000)] public float EqLowHz = 120;
        [Range(-24, 24)] public float EqMidDb;
        [Range(100, 10000)] public float EqMidHz = 1000;
        [Tooltip("Peak width: higher = narrower.")]
        [Range(.3f, 8)] public float EqMidQ = 1;
        [Range(-24, 24)] public float EqHighDb;
        [Range(1000, 18000)] public float EqHighHz = 6000;
        [Tooltip("Playback V2+: stereo chorus (two modulated delays, left and right a quarter cycle apart). V1 ignores it.")]
        public bool Chorus;
        [Range(0, 1)] public float ChorusMix = .5f;
        [Range(.05f, 10)] public float ChorusRateHz = .8f;
        [Tooltip("Delay swing in milliseconds.")]
        [Range(0, 20)] public float ChorusDepthMs = 3;
        [Tooltip("Centre delay in milliseconds.")]
        [Range(1, 30)] public float ChorusDelayMs = 12;
        [Tooltip("Playback V2+: stereo room reverb (8 comb + 4 all-pass filters per side), last in the chain before the limiter. V1 ignores it.")]
        public bool Reverb;
        [Tooltip("Dry/reverb balance: 0 = dry, 1 = only reverb.")]
        [Range(0, 1)] public float ReverbMix = .25f;
        [Tooltip("Room size: longer tail.")]
        [Range(0, 1)] public float ReverbSize = .6f;
        [Tooltip("High-frequency damping of the tail.")]
        [Range(0, 1)] public float ReverbDamping = .5f;
        [Tooltip("Stereo width of the tail: 0 = mono.")]
        [Range(0, 1)] public float ReverbWidth = 1;
        [Tooltip("Time before the tail starts.")]
        [Range(0, 200)] public float ReverbPreDelayMs;
        [Tooltip("Playback V3+ color stage after EQ / chorus / reverb: parametric EQ, de-esser, multiband compressor, phaser, flanger, tremolo / auto-pan, stereo width.")]
        public MusicColorSettings Color = new MusicColorSettings();
        public MusicMixerSnapshot Compile() => new MusicMixerSnapshot(this);
    }

    public sealed class MusicMixerSnapshot
    {
        public const int MaxGateSteps = 128;
        public readonly double Gain, LowPass, HighPass, Threshold, Ratio, Attack, Release, Wet, Delay, Feedback, GateBeats, GateOpen, GateDepth;
        /// <summary>Playback V3+ tempo-synced echo time in beats (0 = <see cref="Delay"/> seconds).</summary>
        public readonly double EchoBeats;
        public const double MaxEchoSeconds = 2;
        /// <summary>Echo delay in seconds at a tempo for a V3 chain: beats when synced (held at 2 s), else the fixed delay.</summary>
        public double EchoDelayAt(double bpm) => EchoBeats > 0 ? Math.Min(MaxEchoSeconds, EchoBeats * 60 / bpm) : Delay;
        /// <summary>Playback V3 master limiter (see <see cref="MusicMixerSettings.Limiter"/>): linear input gain, ceiling and release.</summary>
        public readonly bool Limiter;
        public readonly double LimiterInput, LimiterCeiling, LimiterReleaseSeconds;
        double releaseCoefficient; int releaseRate;
        internal double LimiterReleaseCoefficient(int rate)
        {
            if (releaseRate != rate) { releaseCoefficient = Math.Exp(-1 / (rate * LimiterReleaseSeconds)); releaseRate = rate; }
            return releaseCoefficient;
        }
        public readonly bool Compressor, Gate;
        /// <summary>Playback V3+ sidechain key track (InstrumentId), or empty. Resolved to a part index by <see cref="MusicCue"/>.</summary>
        public readonly string SidechainTrack;
        public readonly int CrushBits, CrushDownsample;
        public readonly double CrushMix, CrushLevels;
        public readonly bool NoiseGate;
        public readonly double NoiseGateThreshold, NoiseGateRange, NoiseGateHold, NoiseGateRelease;
        internal readonly bool[] GateSteps; // null = duty-cycle gate
        public readonly bool Drive;
        public readonly MusicDriveMode DriveMode;
        public readonly double DriveGain, DriveMix, DriveToneHz, DriveKeepBassHz, DriveTrim;
        public readonly bool Amp;
        public readonly MusicAmpModel AmpModel;
        public readonly double AmpGain, AmpTightHz, AmpBassDb, AmpMidDb, AmpTrebleDb, AmpPresenceDb, AmpLevel;
        public readonly MusicCabinet Cabinet;
        public readonly double CabinetMix;
        public readonly MusicConvolutionImpulse CabinetImpulse;
        public readonly double CabinetImpulseMix, CabinetImpulseGainDb;
        /// <summary>Amp/cabinet stage in use: playback V2+ runs MusicAmp after drive.</summary>
        public bool AmpStage => Amp || Cabinet != MusicCabinet.Off;
        public readonly bool Eq, Chorus, Reverb;
        public readonly double EqLowDb, EqLowHz, EqMidDb, EqMidHz, EqMidQ, EqHighDb, EqHighHz;
        public readonly double ChorusMix, ChorusRateHz, ChorusDepthMs, ChorusDelayMs;
        public readonly double ReverbMix, ReverbSize, ReverbDamping, ReverbWidth, ReverbPreDelayMs;
        /// <summary>Tone/space stage (EQ, chorus, reverb) in use: playback V2+ runs MusicSpace after drive.</summary>
        public bool Space => Eq || Chorus || Reverb;
        /// <summary>Playback V3+ color stage (null = off): runs after the space stage on V3 chains only.</summary>
        public readonly MusicColorSnapshot Color;
        public bool Crusher => CrushBits > 0 || CrushDownsample > 1;
        public int GateStepCount => GateSteps?.Length ?? 0;
        /// <summary>Playback V3+ beat repeat stage and its slice in beats.</summary>
        public readonly bool Stutter;
        public readonly double StutterBeats;

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
            Wet=s.EchoWet; Delay=s.EchoSeconds; Feedback=s.EchoFeedback; Check(s.EchoBeats,0,8); EchoBeats=s.EchoBeats;
            Check(s.LimiterInputDb,-24,24); Check(s.LimiterCeilingDb,-12,0); Check(s.LimiterReleaseMs,5,2000);
            Limiter=s.Limiter; LimiterInput=Math.Pow(10,s.LimiterInputDb/20); LimiterCeiling=Math.Pow(10,s.LimiterCeilingDb/20); LimiterReleaseSeconds=s.LimiterReleaseMs/1000.0;
            Gate=s.Gate; GateBeats=s.GateBeats; GateOpen=s.GateOpen; GateDepth=s.GateDepth; Compressor=s.Compressor; SidechainTrack=s.SidechainTrack??"";
            CrushBits=s.CrushBits; CrushDownsample=s.CrushDownsample; CrushMix=s.CrushMix;
            CrushLevels=CrushBits>0?Math.Pow(2,CrushBits-1):0;
            NoiseGate=s.NoiseGate; NoiseGateThreshold=Math.Pow(10,s.NoiseGateThresholdDb/20); NoiseGateRange=Math.Pow(10,s.NoiseGateRangeDb/20);
            NoiseGateHold=s.NoiseGateHoldMs/1000; NoiseGateRelease=s.NoiseGateReleaseMs/1000;
            GateSteps=ParsePattern(s.GatePattern);
            Check(s.StutterBeats,1/32.0,4); Stutter=s.Stutter; StutterBeats=s.StutterBeats;
            Check(s.DriveDb,0,MusicDrive.MaxDriveDb); Check(s.DriveMix,0,1); Check(s.DriveToneHz,0,18000); Check(s.DriveKeepBassHz,0,300); Check(s.DriveTrimDb,-24,12);
            if(!MusicDrive.IsDefined(s.DriveMode)) throw new ArgumentException("Invalid mixer parameter.");
            Drive=s.Drive; DriveMode=s.DriveMode; DriveGain=Math.Pow(10,s.DriveDb/20); DriveMix=s.DriveMix;
            DriveToneHz=s.DriveToneHz; DriveKeepBassHz=s.DriveKeepBassHz; DriveTrim=Math.Pow(10,s.DriveTrimDb/20);
            Check(s.AmpGainDb,0,60); Check(s.AmpTightHz,0,400); Check(s.AmpBassDb,-12,12); Check(s.AmpMidDb,-12,12); Check(s.AmpTrebleDb,-12,12);
            Check(s.AmpPresenceDb,-12,12); Check(s.AmpLevelDb,-36,12); Check(s.CabinetMix,0,1);
            if(!Enum.IsDefined(typeof(MusicAmpModel),s.AmpModel)||!Enum.IsDefined(typeof(MusicCabinet),s.Cabinet)) throw new ArgumentException("Invalid mixer parameter.");
            Amp=s.Amp; AmpModel=s.AmpModel; AmpGain=Math.Pow(10,s.AmpGainDb/20); AmpTightHz=s.AmpTightHz; AmpBassDb=s.AmpBassDb; AmpMidDb=s.AmpMidDb;
            AmpTrebleDb=s.AmpTrebleDb; AmpPresenceDb=s.AmpPresenceDb; AmpLevel=Math.Pow(10,s.AmpLevelDb/20); Cabinet=s.Cabinet; CabinetMix=s.CabinetMix;
            Check(s.CabinetImpulseMix,0,1); Check(s.CabinetImpulseGainDb,-24,24);
            CabinetImpulseMix=s.CabinetImpulseMix; CabinetImpulseGainDb=s.CabinetImpulseGainDb;
            CabinetImpulse=s.CabinetImpulseMix>0?MusicConvolutionImpulse.FromClip(s.CabinetImpulse,s.CabinetImpulseNormalize):null;
            Check(s.EqLowDb,-24,24); Check(s.EqLowHz,20,1000); Check(s.EqMidDb,-24,24); Check(s.EqMidHz,100,10000); Check(s.EqMidQ,.3,8);
            Check(s.EqHighDb,-24,24); Check(s.EqHighHz,1000,18000);
            Check(s.ChorusMix,0,1); Check(s.ChorusRateHz,.05,10); Check(s.ChorusDepthMs,0,20); Check(s.ChorusDelayMs,1,30);
            Check(s.ReverbMix,0,1); Check(s.ReverbSize,0,1); Check(s.ReverbDamping,0,1); Check(s.ReverbWidth,0,1); Check(s.ReverbPreDelayMs,0,200);
            Eq=s.Eq; EqLowDb=s.EqLowDb; EqLowHz=s.EqLowHz; EqMidDb=s.EqMidDb; EqMidHz=s.EqMidHz; EqMidQ=s.EqMidQ; EqHighDb=s.EqHighDb; EqHighHz=s.EqHighHz;
            Chorus=s.Chorus; ChorusMix=s.ChorusMix; ChorusRateHz=s.ChorusRateHz; ChorusDepthMs=s.ChorusDepthMs; ChorusDelayMs=s.ChorusDelayMs;
            Reverb=s.Reverb; ReverbMix=s.ReverbMix; ReverbSize=s.ReverbSize; ReverbDamping=s.ReverbDamping; ReverbWidth=s.ReverbWidth; ReverbPreDelayMs=s.ReverbPreDelayMs;
            Color=s.Color!=null&&s.Color.Active?new MusicColorSnapshot(s.Color):null;
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
        /// <summary>Playback V3 tempo-synced echo: the delay in seconds the owner sets each sample (negative = the snapshot's Delay, as V1/V2).</summary>
        public double DelayOverride=-1;
        /// <summary>Playback V3 lanes (GateDepth, CrushMix): the value to use instead of the snapshot's, negative = the snapshot's.</summary>
        public double GateDepthOverride=-1, CrushMixOverride=-1;
        public void Process(ref double left, ref double right, MusicMixerSnapshot s, double beat)
            => Process(ref left, ref right, s, beat, s.LowPass, s.HighPass, s.Wet);

        public void Process(ref double left, ref double right, MusicMixerSnapshot s, double beat, double lowPass, double highPass, double wet)
            => Process(ref left, ref right, s, beat, lowPass, highPass, wet, -1);

        /// <summary>
        /// The chain with the filter cutoffs and echo wet given (playback V3 automation) and, when <paramref name="keyPeak"/> is not
        /// negative, the compressor detecting that sidechain level instead of its own input. The snapshot values and -1 give the V1/V2 sound.
        /// </summary>
        public void Process(ref double left, ref double right, MusicMixerSnapshot s, double beat, double lowPass, double highPass, double wet, double keyPeak)
        {
            double l=Filter(left,lowPass,highPass,0), r=Filter(right,lowPass,highPass,1);
            double peak=keyPeak>=0?keyPeak:Math.Max(Math.Abs(l),Math.Abs(r));
            double coefficient=Math.Exp(-1/(rate*(peak>envelope?s.Attack:s.Release)));
            envelope=coefficient*envelope+(1-coefficient)*peak;
            double compression=s.Compressor && envelope>s.Threshold ? Math.Pow(envelope/s.Threshold,1/s.Ratio-1):1;
            l*=compression; r*=compression;
            int read=(cursor-(int)((DelayOverride>=0?DelayOverride:s.Delay)*rate)+delay[0].Length)%delay[0].Length;
            double dl=delay[0][read],dr=delay[1][read];
            delay[0][cursor]=(float)(l+dl*s.Feedback); delay[1][cursor]=(float)(r+dr*s.Feedback);
            cursor=(cursor+1)%delay[0].Length;
            l=l*(1-wet)+dl*wet; r=r*(1-wet)+dr*wet;
            if(s.Crusher)
            {
                if(crushCounter==0){ held[0]=Quantize(l,s); held[1]=Quantize(r,s); }
                crushCounter=(crushCounter+1)%s.CrushDownsample;
                double mix=CrushMixOverride>=0?CrushMixOverride:s.CrushMix;
                l=l*(1-mix)+held[0]*mix; r=r*(1-mix)+held[1]*mix;
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
                double depth=GateDepthOverride>=0?GateDepthOverride:s.GateDepth;
                gate=1-depth+open*depth;
            }
            left=l*s.Gain*gate*noiseGate;
            right=r*s.Gain*gate*noiseGate;
        }
        static double Quantize(double x,MusicMixerSnapshot s)=>s.CrushLevels>0?Math.Round(x*s.CrushLevels)/s.CrushLevels:x;
        double Filter(double x,double lowPass,double highPass,int ch)
        {
            if(highPass>0)
            { double a=Math.Exp(-2*Math.PI*highPass/rate); high[ch]=a*(high[ch]+x-previous[ch]); previous[ch]=x; x=high[ch]; }
            if(lowPass>0)
            { double a=1-Math.Exp(-2*Math.PI*Math.Min(lowPass,rate*.45)/rate); low[ch]+=a*(x-low[ch]); x=low[ch]; }
            return x;
        }
    }
}
