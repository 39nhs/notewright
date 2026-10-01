using System;

namespace Graze.Presentation.Audio
{
    /// <summary>
    /// Sound-rendering versions. A song records the version it was authored and approved with, and the engine keeps
    /// every released version, so improving playback never changes how an existing song or instrument sounds.
    /// A version covers everything that turns a compiled cue into audio: sample downmix and pitching, oscillators,
    /// envelopes, velocity and pan law, additive noise and glitch layers, master mixer, soft limiter and whole-mix
    /// glitches, plus note timing inside a cue (tempo map, loop). Transitions between cues are engine behavior, not
    /// part of a version.
    /// </summary>
    /// <remarks>
    /// Released versions are frozen: their fingerprints in Tests/Editor/MusicPlaybackFingerprints.cs must keep passing.
    /// To change the sound, add a version (V3 = 3, set Latest = V3), branch on it in the engine's version dispatch
    /// (AdaptiveMusicEngine.Playback.cs) or pass it into the compile step you change, record its fingerprints, and
    /// describe it in <see cref="Describe"/>. See engine/playback-versions.md.
    /// </remarks>
    public static class MusicPlaybackVersion
    {
        /// <summary>com.graze.music 0.1.0 playback.</summary>
        public const int V1 = 1;
        /// <summary>V1 sound with up to 1024 simultaneous voices instead of 128, plus per-track legato glide (<see cref="MusicPart.GlideSeconds"/>) and drive (<see cref="MusicPart.DriveDb"/>, <see cref="MusicMixerSettings.Drive"/>).</summary>
        public const int V2 = 2;
        public const int Oldest = V1;
        /// <summary>Version new songs are created with.</summary>
        public const int Latest = V2;
        /// <summary>
        /// Version for songs without a recorded version (assets, song.json and generated code from before versioning; serialized 0):
        /// the playback module that was current when versioning was introduced. It is pinned, not <see cref="Latest"/>,
        /// so adding a version never changes how those songs sound. Never change this value.
        /// </summary>
        public const int Unrecorded = V1;

        /// <summary>Serialized 0 means "recorded before versioning existed" and plays as <see cref="Unrecorded"/>.</summary>
        public static int Resolve(int serialized) => serialized == 0 ? Unrecorded : serialized;

        public static bool IsSupported(int version) => version >= Oldest && version <= Latest;

        /// <summary>Resolves a serialized song version, or throws when this package cannot play it faithfully.</summary>
        public static int Require(int serialized, string owner)
        {
            int version = Resolve(serialized);
            if (!IsSupported(version))
                throw new ArgumentException(owner + ": playback version " + version + " is not supported by this com.graze.music (supports V" +
                    Oldest + "–V" + Latest + "). Update the package instead of playing it with a different sound.");
            return version;
        }

        /// <summary>
        /// Simultaneous voices a cue of this version may sound (notes beyond it are dropped and counted in DroppedNotes).
        /// V1 keeps its 128 so songs that relied on dropping keep sounding the same. It follows the cue's version; a track pin
        /// changes only that track's sound.
        /// </summary>
        public static int VoiceLimit(int version) => version == V1 ? 128 : AdaptiveMusicEngine.VoiceCapacity;

        /// <summary>One-line description shown in the Music Arranger.</summary>
        public static string Describe(int version)
        {
            switch (version)
            {
                case V1: return "V1 — 0.1.0: linear-interpolated samples with 5 ms end taper, linear attack/release, linear balance pan, tanh limiter; 128 voices.";
                case V2: return "V2 — V1 sound with up to 1024 simultaneous voices (V1 drops notes beyond 128), track glide (a held note slides into the next), drive (track and master waveshapers) and per-track effect chains.";
                default: return "V" + version + " — unknown to this package.";
            }
        }
    }
}
