<#
.SYNOPSIS
Builds and tests fastchess-desktop on Windows: native core (CMake + vcpkg), C# tests, WinUI app.

.EXAMPLE
.\build.ps1
.\build.ps1 -Configuration Debug -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

if (-not $env:VCPKG_ROOT) {
    throw 'VCPKG_ROOT is not set. Run from a Visual Studio Developer PowerShell, or install vcpkg and set VCPKG_ROOT.'
}

$root = $PSScriptRoot
$native = Join-Path $root 'native'
$preset = "windows-x64-$($Configuration.ToLowerInvariant())"

Push-Location $native
try {
    Invoke-Step 'Configure native core' { cmake --preset windows-x64 }
    Invoke-Step 'Build native core' { cmake --build --preset $preset }
    if (-not $SkipTests) {
        Invoke-Step 'Native tests' { & (Join-Path $native "out\build\windows-x64\$Configuration\fcd_tests.exe") }
    }
}
finally {
    Pop-Location
}

if (-not $SkipTests) {
    Invoke-Step 'Core tests' { dotnet test (Join-Path $root 'tests\FastchessDesktop.Core.Tests') -c $Configuration }
    Invoke-Step 'ViewModel tests' { dotnet test (Join-Path $root 'tests\FastchessDesktop.ViewModels.Tests') -c $Configuration }
}

Invoke-Step 'Build WinUI app' {
    dotnet build (Join-Path $root 'src\FastchessDesktop.App') -c $Configuration -p:Platform=x64 -p:BuildNative=false
}

$exe = Get-ChildItem (Join-Path $root "src\FastchessDesktop.App\bin\x64\$Configuration") -Recurse -Filter 'FastchessDesktop.exe' |
    Select-Object -First 1
Write-Host "Built: $($exe.FullName)" -ForegroundColor Green
