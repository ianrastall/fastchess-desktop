# Handoff

State of the repository on 2026-09-29, after the first Windows build and the owner's feedback pass.

## Current State

| Layer | Status |
| --- | --- |
| `native/` | Builds with MSVC through `native\build-native.ps1` (confirmed on the owner's machine) and with GCC on Linux. 109 checks pass. |
| `FastchessDesktop.Core` | Warnings as errors; 47 tests pass on Linux, including the staged tournament runner. |
| `FastchessDesktop.ViewModels` | Warnings as errors; 17 tests pass on Linux. |
| `FastchessDesktop.App` | Unpackaged build confirmed on Windows at commit 95b17cd. Later XAML changes and the MSIX packaging are not yet built on Windows. |

## Changes In This Pass

- Tournament page: resizable panels (CommunityToolkit GridSplitter), padding on the settings panel,
  no inline spin buttons on NumberBoxes (their popup could only be closed with Tab).
- Engine names: a provisional name from the file name, then the name the engine reports (`id name`)
  over UCI. A Detect button repeats the query.
- Tournament formats: pyramid, knockout and Swiss, run by `TournamentRunner` as a series of
  fastchess runs (fastchess 1.8 only schedules round robin and gauntlet). Pairing rules live in
  `TournamentFormats`. See README for the rules.
- Packaging: `package.ps1` builds a signed MSIX and installs or upgrades it for the current user,
  which gives a Start menu entry and a taskbar pin that follows upgrades.

## Packaging Design

- One project, two modes. The default stays unpackaged (`WindowsPackageType=None`), so
  `build.ps1`, Visual Studio F5 and the bin\ run path are unchanged. `package.ps1` passes
  `-p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true`. In that mode the csproj moves
  output to `bin\msix\` and `obj\msix\` so the two modes never share generated manifests.
- The native DLL and the bundled tools became `Content` items (previously `Copy` tasks after
  Build), because the MSIX payload is computed from items. Checked on Linux: the items evaluate with
  the expected `Link` paths (`fcd_core.dll`, `tools\...`, `data\lichess-openings.tsv`).
- Manifest: `runFullTrust`, and file system write virtualization disabled (`desktop6`, with the
  `unvirtualizedResources` capability) so the packaged app and the tools it launches use the real
  `%LOCALAPPDATA%\FastchessDesktop`. That needs Windows 10 2004, so the MSIX mode sets
  `TargetPlatformMinVersion` to 10.0.19041.0. The manifest validates against the AppX schemas
  embedded in the Windows SDK's `appxpackaging.dll` (the check was confirmed to catch injected errors).
- Signing: self-signed `CN=FastchessDesktop` in `Cert:\CurrentUser\My`, trusted once in
  `LocalMachine\TrustedPeople` through an elevated `certutil`. MSBuild signs by thumbprint.
- Versioning: `<major>.<minor>.<days since 2026-01-01>.<UTC seconds since midnight / 2>`, raised
  above the installed version if needed. The script writes it into the manifest and restores the
  file in a `finally` block.
- Icons: `src/FastchessDesktop.App/Assets/` (tile, taskbar, store and splash PNGs plus `app.ico`,
  also used as the unpackaged exe icon), rendered from the DejaVu Sans knight glyph.

## Verification Performed

In the Linux container:

- `dotnet test` for both test projects (47 and 17 pass).
- The x:Bind checker (207 paths, 0 problems) and a stub compile of the App C#.
- `dotnet restore` of the App project in both package modes; property and item evaluation of the
  App project in both modes (output paths, content links).
- `package.ps1` parses without errors under PowerShell 7; its version and manifest-rewrite logic
  and its Windows PowerShell helper pattern were exercised under pwsh.

Not verified: anything that needs Windows. That covers the XAML compiler pass after the latest
XAML changes, the MSIX build (MakePri, MakeAppx, signing), certificate creation and trust,
`Add-AppxPackage`, and running the packaged app.

## Unresolved Issues

- Stockfish is not in `assets/`. Place it under `assets\stockfish\` or set its path in Settings.
- Stopping fastchess kills the process tree instead of sending Ctrl+C.
- Staged tournaments (pyramid, knockout, Swiss) cannot be resumed after a stop.
- Analysis results are not written into exported PGN comments.

## Windows Build Notes

- First Windows run (2026-09-29) failed because an MSYS2 `cmake.exe` came first on PATH: it chose
  the Ninja generator and broke the vcpkg SQLite build. `native\build-native.ps1` now uses Visual
  Studio's CMake, Ninja and vcpkg explicitly, strips MSYS2/MinGW/Cygwin from PATH for the build
  only, and removes a build folder configured by another generator.

## Next Recommended Action

On Windows, from the repository root: `git pull`, then `.\build.ps1` to confirm the unpackaged
build, then `.\package.ps1` to build and install the package. Report the output of any failure.
Then start Fastchess Desktop from the Start menu, pin it, and check that a tournament run writes
to `%LOCALAPPDATA%\FastchessDesktop\runs`.
