# Third-party notices — `graze-plugin-host.exe`

The VST3 worker in this folder is built from `host.cpp` (Graze contributors) and statically links the components below.
None of these licenses requires publishing other source code; they require keeping these notices with the worker.

| Component | License | Text |
|---|---|---|
| Steinberg VST3 SDK 3.8.1 (`base`, `pluginterfaces`, `public.sdk` hosting) | MIT | [VST3-SDK-LICENSE.txt](VST3-SDK-LICENSE.txt) |
| mingw-w64 runtime: winpthreads (POSIX threads for the `-posix` toolchain) | Expat (MIT); `pthread.h`/`pthread_compat.h` BSD-3-Clause (Lockless Inc.); `clock.c`/`nanosleep.c` public domain | [MINGW-W64-COPYRIGHT.txt](MINGW-W64-COPYRIGHT.txt) |
| mingw-w64 runtime: CRT startup and import libraries | ZPL-2.1 / public domain / permissive (per file) | [MINGW-W64-COPYRIGHT.txt](MINGW-W64-COPYRIGHT.txt) |
| GCC runtime: libstdc++, libgcc (static, MinGW build) | GPL-3.0 with the GCC Runtime Library Exception | No obligation for the compiled program under the exception; listed for transparency |

`MINGW-W64-COPYRIGHT.txt` is the Debian/Ubuntu `mingw-w64-x86-64-dev` copyright file (Ubuntu 24.04 toolchain used for the release build)
and contains every mingw-w64 license text referenced above. VST is a trademark of Steinberg Media Technologies GmbH.

Not shipped: user-installed VST3 instruments and effects (each keeps its own license; the worker only loads them at run time).
