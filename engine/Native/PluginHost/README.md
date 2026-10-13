# Windows x64 VST3 worker

notewright's renderer launches `graze-plugin-host.exe` (this folder) to play the VST3 instruments and effects a song names, one
worker process per plugin instance. **Windows x64 only**: on macOS, Linux and the claude.ai/Cowork sandbox the renderer reports that VST3 is
unavailable instead of rendering. The worker needs no Python, DAW, JUCE runtime or managed library. User-installed VST3 plugins remain separate;
only plugins installed in the trusted VST3 folders (by default `C:\Program Files\Common Files\VST3` and
`%LOCALAPPDATA%\Programs\Common\VST3`) load, and a song names a plugin, never a file.

The included executable is built from `host.cpp` with the official **VST3 SDK 3.8.1**, commit `3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96` and its
pinned submodules (SHA-256 of the executable: `e344cc99e350170daceb0331a82aed9f67d4c207e84843c10bf0e9e06bea8c08`). SDK MIT copyright/license is
in `VST3-SDK-LICENSE.txt`. VST is a trademark of Steinberg Media Technologies GmbH.

Rebuild with CMake 3.25+, Git and either Visual Studio x64 C++ plus the Windows SDK, or Ubuntu 24.04 WSL with `cmake` and
`g++-mingw-w64-x86-64-posix`:

```powershell
git clone --recursive https://github.com/steinbergmedia/vst3sdk.git <sdk-directory>
git -C <sdk-directory> checkout 3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96
git -C <sdk-directory> submodule update --init --recursive
./Build.ps1 -SdkRoot <sdk-directory>
# Or use the cross compiler:
./Build.ps1 -SdkRoot <sdk-directory> -CrossCompileWsl
```

The release MinGW build links its C++ runtime and the mingw-w64 runtime (winpthreads) statically; their notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `MINGW-W64-COPYRIGHT.txt`. On MSVC, install the matching Microsoft VC++ runtime.
Rendering only needs the packaged executable and the user's plugin; rebuilding needs the SDK.

## Protocol 3 (V3 insert development)

Instrument operations remain byte-compatible with protocol 2; the updated C# client accepts protocol 2 for instruments and requires 3 for effects.
Op 9 loads an effect using the op 2 request; it additionally returns int32 stepCount after each parameter value. Op 10 processes audio input using
the op 8 request (zero MIDI events, parameter count may be zero), replying with int32 frames and uint32 current latencySamples.
For effects the mapping is `64 + blockFrames * 16` bytes: output stereo at byte 64, input stereo at `64 + blockFrames * 8`.
The parent writes input before processing; the worker reads the main input bus. Mono input is the L/R average; mono output is duplicated.
Main input/output each have one or two channels; other audio buses are disabled. Preparation rejects changing latency and non-finite PCM.
No op 9/10 or mapping access runs on an audio callback. The C# renderer prepares input in dependency order, compensates latency per node,
and publishes an immutable PCM snapshot. Effect failures preserve that insert's input and report a diagnostic.

## Protocol 2 instrument operations

All scalar values are little endian. Each stdin/stdout frame starts with `uint32 0x31485047`, `uint16 1`, `uint16 operation`, `uint32 payloadBytes`. Messages are bounded to 16 MiB. Strings/blobs are `int32 byteCount` then UTF-8/raw bytes. Replies echo the operation, then `int32 status` (0 success; nonzero followed by an error string).

| Op | Request | Success reply |
|---|---|---|
| 0 | empty | int32 protocol version (4 in the development worker, 2 in the released worker) |
| 1 | plugin installation path | class count, identity tuples (ID/name/vendor/version/instrument flag) |
| 2 | path, classId, MMF name, int32 sampleRate/blockFrames | name, uint32 latencySamples, parameter count, parameter ID/title/unit/normalized value |
| 3 | component blob, controller blob, parameter count with uint32 ID/double value, int32 warm | uint32 latencySamples |
| 4 | int32 frames, int64 sampleTime, double bpm/ppq/cycleStart/cycleEnd, int32 playing/meter/eventCount, events | int32 frames; PCM in MMF |
| 5 | empty | component/controller state blobs |
| 6 | empty | close instrument |
| 7 | empty | close worker |
| 8 | op 4 request + int32 pointCount, points (uint32 parameterId, int32 offset, double normalized value; sorted by ID then offset) | same as op 4 |

An event contains `int32 offset/key`, `float32 velocity`, `int32 noteId/on`. Maximum 65536 events/block. Op 8 adds up to 65536 sample-accurate parameter points (playback V3 automation) to the block's `IParameterChanges`, after any values queued by op 3. Protocol 2 differs from 1 only by op 8 and the version number. The main float32 output is mono (duplicated to stereo) or stereo. Effects/multichannel main output are rejected. Extra outputs are inactive.

The C# parent creates a named Windows memory mapping: magic at offset 0, int64 produced frames at 8, int32 block frames at 16, interleaved float32 stereo at 64. The native worker writes a block before its synchronous reply. A C# background thread copies it into the managed tagged ring (2–4 s). The engine's audio loop only reads that preallocated ring.

One worker per part per cue deck keeps repeat/transition instances independent. Each worker monitors its parent PID and exits when the parent dies. Native crashes and request timeouts terminate only that worker. Process isolation prevents a plugin crash from taking the renderer down; it is not a sandbox against malicious native code. Plugin stdout is redirected to stderr so it cannot corrupt protocol frames.

## Protocol 4: instrument output buses

Operations 2/4/8/9/10 retain their protocol 2/3 wire layouts and default bus activation. The client accepts 2..4; multi-output requires 4.
Op 11 = op 2 load request + int32 selectedCount (1..16) + int32 busIndex entries (unique, 0..63).
Reply = op 2 reply + int32 totalOutputBuses (1..64) + UTF-8 name / int32 channels / int32 active for each bus in native index order.
Selected buses must be mono/stereo. Main bus 0 stays active for internal plugin compatibility, even if not selected; unselected auxiliaries and audio inputs are inactive.
The mapping is `64 + blockFrames * selectedCount * 8` bytes. Each requested bus has an independent interleaved stereo slot at
`64 + requestedSlot * blockFrames * 8`. Only selected buses are published/mixed. Mono duplicates to stereo. Output silence flags are respected.
Op 12 = op 8 request (points count can be zero), reply = int32 frames + uint32 current latency. All outputs belong to the same process call and MIDI schedule.
The preparation layer rejects changed layout/latency, compensates fixed latency, and copies finite PCM into destination tracks before their own effects.
No native call runs in the engine's audio loop. Live MusicPluginSession mods explicitly reject routed outputs; use prepared snapshot playback.
