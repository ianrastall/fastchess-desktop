# Handoff

State of the repository on 2026-09-29, after the first Windows build and the owner's feedback pass.

## Current State

| Layer | Status |
| --- | --- |
| `native/` | Builds with MSVC through `native\build-native.ps1` (confirmed on the owner's machine) and with GCC on Linux. 109 checks pass. |
| `FastchessDesktop.Core` | Warnings as errors; 75 tests pass on Linux, including the staged tournament runner and the statistics checked against a real fastchess report. |
| `FastchessDesktop.ViewModels` | Warnings as errors; 20 tests pass on Linux. |
| `FastchessDesktop.App` | Built and installed as MSIX on Windows by the owner (package 1.0.271.8108); a two-engine match ran from the installed app, with run folders in the real `%LOCALAPPDATA%\FastchessDesktop`. |

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

## Tournament Log

- fastchess checks every engine's search output and prints a block ("Warning;", "Info;",
  "Position;", "Moves;") when, for example, the best move is not the first move of the last PV.
  These are informational; they do not change moves or results. The log shows them in amber
  (`LogKind.Warning`, classified by `FastchessOutputParser.Classify`).
- Real engine failures show up as game end reasons: "loses on time", "disconnects",
  "connection stalls", "makes an illegal move", and as nonzero Timeouts/Crashed counts at the end.
  They are shown in red, and a summary line after each run counts them.
- The post-run import now always reports its outcome in the tournament log, including when no
  database is open (it used to log that only on the Database page).

## Tournament Results Tables

- Standings and finished games are grid tables (header and cells with rules, numbers right
  aligned) with sortable headers. `SortableTable<TRow>` keeps rows in the chosen order and, on a
  live update, moves only rows whose position changed; rows are view models that update their own
  cells. The old code cleared and refilled the standings after every game, and every ListView ran
  the default entrance animation, which is what made the tables redraw visibly.
- Statistics are computed live from the "Finished game" lines by `TournamentScoreboard` and
  `MatchStatistics.cs`, ported from fastchess 1.8.2 (elo_wdl.cpp, elo_pentanomial.cpp, sprt.cpp,
  scoreboard.hpp). Pairs are games `(n - 1) / games` as fastchess schedules them. Pentanomial
  statistics are used when fastchess would report them (`-games 2`, its own output format, not the
  Bayesian SPRT model). A test replays a real 20-game match and matches fastchess's printed Elo,
  error, nElo, LOS and LLR to the printed precision.
- Standings columns: rank (by Elo, as fastchess ranks), Elo and 95% margin, nElo and margin, LOS,
  games, points, score, W/D/L, draw ratio, pentanomial counts, Elo change from the latest result,
  engine failures and fastchess output warnings per engine. With two engines a head-to-head
  summary shows the figures of fastchess's report, and the live SPRT state when SPRT is on.
- The log keeps the last line in view with `ItemsUpdatingScrollMode.KeepLastItemInView` instead of
  calling `ScrollIntoView` for every batch of lines; that and the entrance animation made it flash.

## Default Database

- There is always somewhere for games to go: `%LOCALAPPDATA%\FastchessDesktop\games.fcdb` is the
  default database. It is opened at startup unless the last used database still exists, and
  `DatabaseViewModel.EnsureOpenAsync` opens it whenever an import finds no database open (the
  post-tournament import, Import PGN, and the Tournament page's Import games now button).
- It is a normal file rather than a temporary clipbase, so imported tournament games survive a
  restart. Before this, importing required creating or opening a database first, and Import PGN
  was disabled with none open.

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

- `dotnet test` for both test projects (75 and 20 pass).
- The x:Bind checker, now also checking function bindings (263 paths and 30 function bindings,
  0 problems; confirmed to catch injected errors), and a stub compile of the App C#.
- `dotnet restore` of the App project in both package modes; property and item evaluation of the
  App project in both modes (output paths, content links).
- `package.ps1` parses without errors under PowerShell 7; its version and manifest-rewrite logic
  and its Windows PowerShell helper pattern were exercised under pwsh.

Not verified: anything that needs Windows. That covers the XAML compiler pass for the new tables
and log panel, and how they look and behave at run time (header and cell alignment, horizontal
scrolling, that the log no longer flashes).

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

On Windows, from the repository root: `git pull`, then `.\package.ps1 -SkipTests`. Run a short
two-engine match with SPRT and compare the Standings tab with fastchess's periodic report in the
log; check sorting, the log with Follow output on, and report any XAML compiler errors.
