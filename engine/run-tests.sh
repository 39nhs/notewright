#!/bin/bash
# Engine tests without Unity: compiles engine/Runtime and engine/Tests against small UnityEngine/NUnit stubs with Roslyn on
# Mono, then runs them (playback fingerprints, mixer, tempo, glide, drive, ...). Needs: mono (mono-devel), curl, unzip.
# usage: engine/run-tests.sh [test-name-filter...]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cache=${NOTEWRIGHT_CI_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/notewright-ci}
roslyn=4.8.0
csc="$cache/roslyn-$roslyn/tasks/net472/csc.exe"
if [ ! -f "$csc" ]; then
  mkdir -p "$cache/roslyn-$roslyn"
  curl -sSL -o "$cache/roslyn.nupkg" "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$roslyn/microsoft.net.compilers.toolset.$roslyn.nupkg"
  unzip -qo "$cache/roslyn.nupkg" -d "$cache/roslyn-$roslyn"
fi
fw=/usr/lib/mono/4.7.2-api
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
# The stub framework has no Array.Fill (.NET Standard 2.1); tests get a shim.
mkdir -p "$out/tests"
for f in "$here"/Tests/*.cs; do sed 's/Array\.Fill(/ArrayShim.Fill(/g' "$f" > "$out/tests/$(basename "$f")"; done
mono "$csc" -nologo -langversion:9 -warnaserror- -nowarn:0414,0169,0649,0162 -noconfig -nostdlib \
  -r:$fw/mscorlib.dll -r:$fw/System.dll -r:$fw/System.Core.dll -r:$fw/Facades/netstandard.dll \
  -out:"$out/tests.exe" "$here/Stubs/UnityStubs.cs" "$here/Tests/Harness/NUnitShim.cs" "$here/Tests/Harness/Runner.cs" \
  "$here"/Runtime/*.cs "$out"/tests/*.cs
mono "$out/tests.exe" "$@"
