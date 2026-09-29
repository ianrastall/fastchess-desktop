<#
.SYNOPSIS
Builds and tests fastchess-desktop on Windows: native core (CMake + vcpkg), C# tests, WinUI app.
Runs from any PowerShell; Visual Studio's CMake and vcpkg are located automatically.

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

$root = $PSScriptRoot

& (Join-Path $root 'native\build-native.ps1') -Configuration $Configuration -Test:(-not $SkipTests)

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
