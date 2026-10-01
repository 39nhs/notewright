#!/bin/bash
# Engine tests without Unity: compiles engine/Runtime and engine/Tests against small UnityEngine/NUnit stubs and runs them
# (playback fingerprints, mixer, tempo, glide, drive, ...). Runtime: NOTEWRIGHT_RUNTIME=mono (default; needs mono-devel, curl,
# unzip; Roslyn is downloaded once) or NOTEWRIGHT_RUNTIME=dotnet (needs a .NET 6+ SDK; uses the SDK's compiler). Both must pass:
# every runtime has to render the same audio.
# usage: [NOTEWRIGHT_RUNTIME=dotnet] engine/run-tests.sh [test-name-filter...]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
runtime=${NOTEWRIGHT_RUNTIME:-mono}
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
# The Mono reference framework has no Array.Fill (.NET Standard 2.1); tests get a shim on every runtime.
mkdir -p "$out/tests"
for f in "$here"/Tests/*.cs; do sed 's/Array\.Fill(/ArrayShim.Fill(/g' "$f" > "$out/tests/$(basename "$f")"; done
sources=("$here/Stubs/UnityStubs.cs" "$here/Tests/Harness/NUnitShim.cs" "$here/Tests/Harness/Runner.cs" "$here"/Runtime/*.cs "$out"/tests/*.cs)
flags=(-nologo -langversion:9 -warnaserror- -nowarn:0414,0169,0649,0162 -noconfig -nostdlib)
case "$runtime" in
  mono)
    cache=${NOTEWRIGHT_CI_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/notewright-ci}
    roslyn=4.8.0
    csc="$cache/roslyn-$roslyn/tasks/net472/csc.exe"
    if [ ! -f "$csc" ]; then
      mkdir -p "$cache/roslyn-$roslyn"
      curl -sSL -o "$cache/roslyn.nupkg" "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$roslyn/microsoft.net.compilers.toolset.$roslyn.nupkg"
      unzip -qo "$cache/roslyn.nupkg" -d "$cache/roslyn-$roslyn"
    fi
    fw=/usr/lib/mono/4.7.2-api
    mono "$csc" "${flags[@]}" -r:$fw/mscorlib.dll -r:$fw/System.dll -r:$fw/System.Core.dll -r:$fw/Facades/netstandard.dll \
      -out:"$out/tests.exe" "${sources[@]}"
    mono "$out/tests.exe" "$@"
    ;;
  dotnet)
    root=$(dirname "$(readlink -f "$(command -v dotnet)")")
    csc=$(ls -d "$root"/sdk/*/Roslyn/bincore/csc.dll | sort -V | tail -1)
    ref=$(ls -d "$root"/packs/Microsoft.NETCore.App.Ref/*/ref/net* | sort -V | tail -1)
    version=$(basename "$(dirname "$(dirname "$ref")")")
    refs=()
    for dll in "$ref"/*.dll; do refs+=("-r:$dll"); done
    dotnet "$csc" "${flags[@]}" "${refs[@]}" -out:"$out/tests.dll" "${sources[@]}"
    printf '{"runtimeOptions":{"tfm":"%s","rollForward":"LatestMajor","framework":{"name":"Microsoft.NETCore.App","version":"%s.0"}}}' \
      "$(basename "$ref")" "${version%.*}" > "$out/tests.runtimeconfig.json"
    dotnet "$out/tests.dll" "$@"
    ;;
  *) echo "NOTEWRIGHT_RUNTIME must be mono or dotnet" >&2; exit 2 ;;
esac
