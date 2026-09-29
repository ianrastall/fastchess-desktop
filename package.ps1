<#
.SYNOPSIS
Builds fastchess-desktop as a signed MSIX package and installs it for the current user.

.DESCRIPTION
The installed app appears in the Start menu as "Fastchess Desktop" and can be pinned to the
taskbar. Running this script again builds a package with a higher version and upgrades the
installed app in place, so Start menu entries and taskbar pins keep pointing at the newest build.

Signing uses a self-signed certificate with the subject CN=FastchessDesktop, created once in
the current user's certificate store (Cert:\CurrentUser\My). Windows only installs packages
whose signer it trusts, so on first use the script adds the public certificate to the local
machine's Trusted People store. That one step needs administrator rights and shows a UAC
prompt; later runs skip it. The private key never leaves the certificate store and nothing
secret is written to the repository.

The package version is <major>.<minor>.<days since 2026-01-01>.<UTC seconds since midnight / 2>,
with major and minor taken from src\FastchessDesktop.App\Package.appxmanifest. The script writes
that version into the manifest for the build and restores the file afterwards.

The package lands in dist\ (ignored by git). Double-clicking a .msix there also installs it.
Runs from any PowerShell (5.1 or 7); the Appx and certificate cmdlets run in Windows PowerShell.

.EXAMPLE
.\package.ps1
.\package.ps1 -SkipTests
.\package.ps1 -NoInstall
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

# Runs a script block in Windows PowerShell and returns its output. The Appx and PKI modules
# are Windows PowerShell modules and do not load reliably in every PowerShell 7 release.
function Invoke-WindowsPowerShell([scriptblock]$Script, [object[]]$Arguments = @()) {
    $output = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $Script -args $Arguments
    if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell step failed with exit code $LASTEXITCODE." }
    $output
}

$root = $PSScriptRoot
$appProject = Join-Path $root 'src\FastchessDesktop.App'
$manifestPath = Join-Path $appProject 'Package.appxmanifest'
$dist = Join-Path $root 'dist'
$packageName = 'FastchessDesktop'
$publisher = 'CN=FastchessDesktop'

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---- Signing certificate -------------------------------------------------------------------

Write-Host '== Signing certificate' -ForegroundColor Cyan
$thumbprint = Invoke-WindowsPowerShell {
    param($subject)
    $ErrorActionPreference = 'Stop'
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'fastchess-desktop package signing' `
            -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $cert.Thumbprint
} @($publisher) | Select-Object -Last 1
if (-not $thumbprint) { throw 'Could not find or create the signing certificate.' }
Write-Host "Certificate $publisher, thumbprint $thumbprint"

if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint")) {
    $cerPath = Join-Path $dist 'FastchessDesktop.cer'
    $cert = Get-Item "Cert:\CurrentUser\My\$thumbprint"
    [IO.File]::WriteAllBytes($cerPath, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host 'Trusting the certificate for package installs (one time, needs administrator rights).' -ForegroundColor Yellow
    $process = Start-Process certutil.exe -ArgumentList @('-addstore', 'TrustedPeople', "`"$cerPath`"") -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "certutil -addstore TrustedPeople failed with exit code $($process.ExitCode)." }
}

# ---- Version -------------------------------------------------------------------------------

$installed = Invoke-WindowsPowerShell {
    param($name)
    (Get-AppxPackage -Name $name | Sort-Object Version -Descending | Select-Object -First 1).Version
} @($packageName) | Select-Object -Last 1

$original = [IO.File]::ReadAllText($manifestPath)
$identity = [regex]::Match($original, '<Identity\b[^>]*?\bVersion="(\d+)\.(\d+)\.\d+\.\d+"')
if (-not $identity.Success) { throw "No Identity Version found in $manifestPath." }
$now = [DateTime]::UtcNow
$version = [version]::new([int]$identity.Groups[1].Value, [int]$identity.Groups[2].Value,
    [int]($now.Date - [DateTime]::new(2026, 1, 1)).TotalDays, [int][Math]::Floor($now.TimeOfDay.TotalSeconds / 2))
if ($installed -and [version]$installed -ge $version) {
    $v = [version]$installed
    $version = [version]::new($v.Major, $v.Minor, $v.Build, $v.Revision + 1)
}
Write-Host "Package version $version$(if ($installed) { " (installed: $installed)" })"

# ---- Build ---------------------------------------------------------------------------------

& (Join-Path $root 'native\build-native.ps1') -Configuration $Configuration -Test:(-not $SkipTests)

if (-not $SkipTests) {
    Invoke-Step 'Core tests' { dotnet test (Join-Path $root 'tests\FastchessDesktop.Core.Tests') -c $Configuration }
    Invoke-Step 'ViewModel tests' { dotnet test (Join-Path $root 'tests\FastchessDesktop.ViewModels.Tests') -c $Configuration }
}

$utf8 = [Text.UTF8Encoding]::new($false)
try {
    $versioned = [regex]::Replace($original, '(<Identity\b[^>]*?\bVersion=")[^"]+(")', '${1}' + $version + '${2}')
    [IO.File]::WriteAllText($manifestPath, $versioned, $utf8)
    Invoke-Step 'Build MSIX package' {
        dotnet build $appProject -c $Configuration -p:Platform=x64 -p:BuildNative=false `
            -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$dist/" `
            -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$thumbprint
    }
}
finally {
    [IO.File]::WriteAllText($manifestPath, $original, $utf8)
}

$msix = Get-ChildItem $dist -Recurse -Filter "*_$($version)_x64*.msix" | Select-Object -First 1
if (-not $msix) { throw "The build finished but no .msix for version $version was found under $dist." }
Write-Host "Package: $($msix.FullName)" -ForegroundColor Green

# ---- Install -------------------------------------------------------------------------------

if ($NoInstall) { return }

Write-Host '== Install' -ForegroundColor Cyan
Invoke-WindowsPowerShell {
    param($path, $name)
    $ErrorActionPreference = 'Stop'
    Add-AppxPackage -Path $path -ForceApplicationShutdown
    $package = Get-AppxPackage -Name $name | Sort-Object Version -Descending | Select-Object -First 1
    "Installed $($package.Name) $($package.Version)"
} @($msix.FullName, $packageName)
Write-Host 'Start it from the Start menu: Fastchess Desktop. Right-click the running app in the taskbar to pin it.' -ForegroundColor Green
