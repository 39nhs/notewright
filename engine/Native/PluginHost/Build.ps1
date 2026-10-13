param([Parameter(Mandatory=$true)][string]$SdkRoot, [switch]$CrossCompileWsl)
$ErrorActionPreference = 'Stop'
$taskSdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$taskRevision = (& git -C $taskSdk rev-parse HEAD).Trim()
if ($taskRevision -ne '3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96') { throw 'Use the pinned official VST3 SDK 3.8.1 revision (see README.md).' }
$taskBuild = Join-Path $PSScriptRoot 'out/build'
if ($CrossCompileWsl) {
    function To-WslPath([string]$TaskPath) { $taskFull = [IO.Path]::GetFullPath($TaskPath); if ($taskFull -notmatch '^([A-Za-z]):\\(.*)$') { throw 'Use local drive paths for WSL builds.' }; return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\','/') }
    $taskSourceUnix = To-WslPath $PSScriptRoot
    $taskBuildUnix = To-WslPath $taskBuild
    $taskSdkUnix = To-WslPath $taskSdk
    & wsl -d Ubuntu-24.04 -- cmake -S $taskSourceUnix -B $taskBuildUnix '-DCMAKE_SYSTEM_NAME=Windows' '-DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc-posix' '-DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++-posix' "-DVST3_SDK_ROOT=$taskSdkUnix" '-DCMAKE_BUILD_TYPE=Release'
    if ($LASTEXITCODE -ne 0) { throw 'Native configuration failed.' }
    & wsl -d Ubuntu-24.04 -- cmake --build $taskBuildUnix --target graze-plugin-host -j 4
    $taskExe = Join-Path $taskBuild 'graze-plugin-host.exe'
} else {
    & cmake -S $PSScriptRoot -B $taskBuild -A x64 "-DVST3_SDK_ROOT=$taskSdk"
    if ($LASTEXITCODE -ne 0) { throw 'Native configuration failed.' }
    & cmake --build $taskBuild --config Release --target graze-plugin-host
    $taskExe = Join-Path $taskBuild 'Release/graze-plugin-host.exe'
}
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
Copy-Item -LiteralPath $taskExe -Destination (Join-Path $PSScriptRoot 'graze-plugin-host.exe')
Copy-Item -LiteralPath (Join-Path $taskSdk 'LICENSE.txt') -Destination (Join-Path $PSScriptRoot 'VST3-SDK-LICENSE.txt')
# MinGW builds statically link the mingw-w64 runtime (winpthreads, CRT): keep its license texts beside the worker.
if ($CrossCompileWsl) { & wsl -d Ubuntu-24.04 -- cat /usr/share/doc/mingw-w64-x86-64-dev/copyright | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot 'MINGW-W64-COPYRIGHT.txt') }
