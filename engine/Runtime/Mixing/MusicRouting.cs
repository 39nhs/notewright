using System;
using System.Collections.Generic;
using UnityEngine;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Serialized mix bus (song data, playback V3+): tracks route into it (<see cref="MusicCueDefinition.Part.Output"/>) or send a copy
    /// (<see cref="MusicCueDefinition.Part.Sends"/>); it runs its own effect chain (the master mixer's stages) and feeds the master or another bus.
    /// </summary>
    [Serializable]
    public sealed class MusicBusDefinition
    {
        [Tooltip("Unique name tracks and other buses refer to.")]
        public string Name;
        [Tooltip("This bus's effect chain (same stages as the master mixer). OutputDb is the bus level.")]
        public MusicMixerSettings Effects = new MusicMixerSettings();
        [Tooltip("Bus this bus feeds. Empty = master.")]
        public string Output = "";
        [Tooltip("Playback V3+: bus volume (dB added to OutputDb) and effect values over the cue's beats. Pan is not a bus target.")]
        public MusicAutomationLane[] Automation = Array.Empty<MusicAutomationLane>();
        [Tooltip("Balance of the bus output (-1 left … 1 right), after its volume.")]
        [Range(-1, 1)] public float Pan;
    }

    /// <summary>Serialized send: a copy of the track's signal (after its own effects and level) into a bus.</summary>
    [Serializable]
    public struct MusicSendDefinition
    {
        public string Bus;
        [Tooltip("Send level in dB (-80 = off, 0 = same level as the track, up to +12).")]
        [Range(-80, 12)] public float LevelDb;
        [Tooltip("Playback V3+: at most one enabled VolumeDb lane replacing LevelDb (-80..+12 dB). Empty or bypassed = authored level.")]
        public MusicAutomationLane[] LevelAutomation;
        [Tooltip("Pre-fader: the send level ignores the track's Gain and volume lane (post-fader by default).")]
        public bool PreFader;
        public MusicSendDefinition(string bus, float levelDb, MusicAutomationLane levelAutomation = null, bool preFader = false)
        { Bus = bus; LevelDb = levelDb; LevelAutomation = levelAutomation == null ? Array.Empty<MusicAutomationLane>() : new[] { levelAutomation }; PreFader = preFader; }
    }

    /// <summary>Compiled bus: effect chain and where it goes (-1 = master, else a bus with a higher index).</summary>
    public sealed class MusicBus
    {
        public readonly string Name;
        public readonly MusicMixerSnapshot Effects;
        public readonly int Output;
        internal readonly MusicAutomation[] Automation;
        public readonly MusicAutomation VolumeAutomation;
        /// <summary>Balance of the bus output, -1..1 (playback V3).</summary>
        public readonly double Pan;
        public MusicAutomation GetAutomation(MusicAutomationTarget target) => Automation?[(int)target];
        public MusicBus(string name, MusicMixerSnapshot effects, int output = -1, MusicAutomation[] automation = null, double pan = 0)
        {
            if (string.IsNullOrWhiteSpace(name) || effects == null) throw new ArgumentException("A bus needs a name and an effect chain.");
            if (!MusicTrack.Finite(pan) || pan < -1 || pan > 1) throw new ArgumentException("Bus pan must be -1..1.");
            Name = name; Effects = effects; Output = output; Pan = pan;
            Automation = MusicAutomation.ByTarget(automation, effects, "Bus " + name, true);
            VolumeAutomation = GetAutomation(MusicAutomationTarget.VolumeDb);
        }
    }

    /// <summary>Compiled send: bus index and linear level.</summary>
    public readonly struct MusicSend
    {
        public readonly int Bus;
        public readonly double Gain;
        public readonly MusicAutomation LevelAutomation;
        /// <summary>The level ignores the track's Gain and volume lane.</summary>
        public readonly bool PreFader;
        public double GainAt(double localBeat) => LevelAutomation == null ? Gain : MusicAutomation.Gain(LevelAutomation.Evaluate(localBeat));
        public MusicSend(int bus, double gain, MusicAutomation levelAutomation = null, bool preFader = false)
        {
            PreFader = preFader;
            if (bus < 0 || !MusicTrack.Finite(gain) || gain < 0 || gain > 4) throw new ArgumentException("Invalid send.");
            if (levelAutomation != null && (levelAutomation.Target != MusicAutomationTarget.VolumeDb || !levelAutomation.ValuesWithin(MusicRouting.MinSendDb, MusicRouting.MaxSendDb)))
                throw new ArgumentException("Send automation must be VolumeDb with values -80..12 dB.");
            Bus = bus; Gain = gain; LevelAutomation = levelAutomation;
        }
    }

    public static class MusicRouting
    {
        public const int MaxBuses = 16, MaxSendsPerTrack = 8;
        public const float MinSendDb = -80, MaxSendDb = 12;

        /// <summary>
        /// Compiles buses in processing order: every bus comes before the bus it feeds, so one pass in index order mixes everything.
        /// Rejects duplicate or empty names, unknown outputs and feedback loops. <paramref name="index"/> maps names to the compiled order.
        /// </summary>
        public static MusicBus[] CompileBuses(MusicBusDefinition[] buses, out Dictionary<string, int> index, double loopBeats = MusicCue.MaxLoopBeats)
        {
            index = new Dictionary<string, int>(StringComparer.Ordinal);
            if (buses == null || buses.Length == 0) return Array.Empty<MusicBus>();
            if (buses.Length > MaxBuses) throw new ArgumentException($"At most {MaxBuses} buses.");
            var byName = new Dictionary<string, MusicBusDefinition>(StringComparer.Ordinal);
            foreach (var bus in buses)
            {
                if (bus == null || string.IsNullOrWhiteSpace(bus.Name)) throw new ArgumentException("Every bus needs a name.");
                if (!byName.ContainsKey(bus.Name)) byName.Add(bus.Name, bus); else throw new ArgumentException("Duplicate bus name: " + bus.Name);
            }
            foreach (var bus in buses)
                if (!string.IsNullOrEmpty(bus.Output) && !byName.ContainsKey(bus.Output)) throw new ArgumentException($"Bus {bus.Name}: unknown output bus {bus.Output}.");
            // Depth-first: a bus is placed after everything that feeds it is placed... reversed, so feeders come first.
            var order = new List<MusicBusDefinition>(); var state = new Dictionary<string, int>(StringComparer.Ordinal);
            void Visit(MusicBusDefinition bus)
            {
                state.TryGetValue(bus.Name, out int s);
                if (s == 2) return;
                if (s == 1) throw new ArgumentException("Buses feed each other in a loop: " + bus.Name);
                state[bus.Name] = 1;
                if (!string.IsNullOrEmpty(bus.Output)) Visit(byName[bus.Output]);
                state[bus.Name] = 2; order.Add(bus);
            }
            foreach (var bus in buses) Visit(bus);
            order.Reverse(); // outputs were added before their feeders
            for (int i = 0; i < order.Count; i++) index[order[i].Name] = i;
            var compiled = new MusicBus[order.Count];
            for (int i = 0; i < compiled.Length; i++)
            {
                var bus = order[i];
                try { compiled[i] = new MusicBus(bus.Name, (bus.Effects ?? new MusicMixerSettings()).Compile(), string.IsNullOrEmpty(bus.Output) ? -1 : index[bus.Output],
                    MusicAutomation.Compile(bus.Automation, loopBeats, "Bus " + bus.Name, true), bus.Pan); }
                catch (ArgumentException e) { throw new ArgumentException($"Bus {bus.Name}: {e.Message}"); }
            }
            return compiled;
        }

        /// <summary>A track's output bus (-1 = master) and sends, resolved against compiled bus names.</summary>
        public static int CompileTrack(string output, MusicSendDefinition[] sends, Dictionary<string, int> index, string owner, out MusicSend[] compiled, double loopBeats = MusicCue.MaxLoopBeats)
        {
            compiled = null;
            int bus = -1;
            if (!string.IsNullOrEmpty(output) && !index.TryGetValue(output, out bus)) throw new ArgumentException($"{owner}: unknown output bus {output}.");
            if (sends == null || sends.Length == 0) return bus;
            if (sends.Length > MaxSendsPerTrack) throw new ArgumentException($"{owner}: at most {MaxSendsPerTrack} sends.");
            var list = new List<MusicSend>();
            var targets = new HashSet<int>();
            foreach (var send in sends)
            {
                if (string.IsNullOrEmpty(send.Bus) || !index.TryGetValue(send.Bus, out int target)) throw new ArgumentException($"{owner}: unknown send bus {send.Bus}.");
                if (!MusicTrack.Finite(send.LevelDb) || send.LevelDb < MinSendDb || send.LevelDb > MaxSendDb) throw new ArgumentException($"{owner}: send level must be {MinSendDb}..{MaxSendDb} dB.");
                if (target == bus) throw new ArgumentException($"{owner}: sends to its own output bus {send.Bus}.");
                if (!targets.Add(target)) throw new ArgumentException($"{owner}: two sends to bus {send.Bus}.");
                MusicAutomation level = null;
                var lanes = MusicAutomation.Compile(send.LevelAutomation, loopBeats, owner + " send " + send.Bus, true);
                if (lanes != null)
                {
                    if (lanes.Length != 1 || lanes[0].Target != MusicAutomationTarget.VolumeDb) throw new ArgumentException($"{owner}: send {send.Bus} automation must be one VolumeDb lane.");
                    level = lanes[0];
                    if (!level.ValuesWithin(MinSendDb, MaxSendDb)) throw new ArgumentException($"{owner}: send {send.Bus} automation must be {MinSendDb}..{MaxSendDb} dB.");
                }
                if (send.LevelDb > MinSendDb || level != null) list.Add(new MusicSend(target, MusicAutomation.Gain(send.LevelDb), level, send.PreFader));
            }
            compiled = list.Count > 0 ? list.ToArray() : null;
            return bus;
        }
    }
}
