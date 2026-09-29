<#
.SYNOPSIS
Configures and builds the native core (fcd_core.dll) with Visual Studio's own CMake and vcpkg.

.DESCRIPTION
Used by build.ps1 and by the App project's BuildNativeCore target. It does not rely on
whatever cmake.exe is first on PATH: MSYS2, MinGW or standalone CMake installs default to
other generators and toolchains and break the MSVC + vcpkg build. For the duration of the
build it uses the CMake and Ninja that ship with Visual Studio, removes MSYS2/MinGW/Cygwin
directories from PATH, and ignores CMAKE_GENERATOR* environment variables. The caller's
environment is restored afterwards. Works from any PowerShell (5.1 or 7); a Developer
PowerShell is not required.

.EXAMPLE
.\native\build-native.ps1 -Configuration Release -Test
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at $vswhere. Install Visual Studio with the 'Desktop development with C++' workload."
}
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath |
    Select-Object -First 1
if (-not $vs) {
    throw "No Visual Studio installation with the MSVC x64 tools was found. Install the 'Desktop development with C++' workload."
}

$cmakeDir = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$ninjaDir = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja'
$cmake = Join-Path $cmakeDir 'cmake.exe'
if (-not (Test-Path $cmake)) {
    throw "Visual Studio's CMake was not found at $cmake. In the Visual Studio Installer, add the 'C++ CMake tools for Windows' component."
}

$saved = @{
    PATH = $env:PATH
    VCPKG_ROOT = $env:VCPKG_ROOT
    CMAKE_GENERATOR = $env:CMAKE_GENERATOR
    CMAKE_GENERATOR_PLATFORM = $env:CMAKE_GENERATOR_PLATFORM
    CMAKE_GENERATOR_TOOLSET = $env:CMAKE_GENERATOR_TOOLSET
    CMAKE_GENERATOR_INSTANCE = $env:CMAKE_GENERATOR_INSTANCE
}

try {
    # Visual Studio's CMake and Ninja first (vcpkg's port builds find them on PATH); other toolchains removed.
    $kept = $env:PATH -split ';' | Where-Object { $_ -and $_ -notmatch '(?i)\\(msys64|msys2|mingw\w*|cygwin\w*)(\\|$)' }
    $env:PATH = (@($cmakeDir, $ninjaDir) + $kept) -join ';'
    foreach ($name in 'CMAKE_GENERATOR', 'CMAKE_GENERATOR_PLATFORM', 'CMAKE_GENERATOR_TOOLSET', 'CMAKE_GENERATOR_INSTANCE') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }

    if (-not $env:VCPKG_ROOT) {
        $bundled = Join-Path $vs 'VC\vcpkg'
        if (-not (Test-Path (Join-Path $bundled 'scripts\buildsystems\vcpkg.cmake'))) {
            throw "VCPKG_ROOT is not set and Visual Studio's vcpkg was not found at $bundled. Add the 'vcpkg package manager' component or set VCPKG_ROOT."
        }
        $env:VCPKG_ROOT = $bundled
    }
    Write-Host "Visual Studio: $vs"
    Write-Host "CMake: $cmake"
    Write-Host "vcpkg: $env:VCPKG_ROOT"

    # A build folder configured by a different generator cannot be reused; it is build output only.
    $buildDir = Join-Path $PSScriptRoot 'out\build\windows-x64'
    $cache = Join-Path $buildDir 'CMakeCache.txt'
    if ((Test-Path $cache) -and -not (Select-String -Path $cache -Pattern '^CMAKE_GENERATOR:INTERNAL=Visual Studio' -Quiet)) {
        Write-Host "Removing $buildDir (configured with another generator)." -ForegroundColor Yellow
        Remove-Item $buildDir -Recurse -Force
    }

    Push-Location $PSScriptRoot
    try {
        Invoke-Checked 'Configure native core' { & $cmake --preset windows-x64 }
        Invoke-Checked 'Build native core' { & $cmake --build --preset "windows-x64-$($Configuration.ToLowerInvariant())" }
        if ($Test) {
            Invoke-Checked 'Native tests' { & (Join-Path $buildDir "$Configuration\fcd_tests.exe") }
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $saved[$name] }
    }
}
