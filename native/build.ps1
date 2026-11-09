# Builds the native kernels into native/out (pricer_core.dll). Called by Pricer.Native.csproj on Windows.
# CMake comes from PATH when it knows the installed Visual Studio, else from the copy Visual Studio ships.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'

function Find-CMake {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vs) {
            $bundled = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path $bundled) { return $bundled }
        }
    }
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'CMake not found: install the "C++ CMake tools" of Visual Studio, or CMake itself.'
}

$cmake = Find-CMake
if (-not (Test-Path (Join-Path $build 'CMakeCache.txt'))) {
    & $cmake -S $root -B $build -A x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }
}
& $cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }
