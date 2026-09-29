# Handoff

State of the repository on 2026-09-29, after moving the internals into the C++ core and routing
engine warnings out of the tournament log.

## Current State

| Layer | Status |
| --- | --- |
| `native/` | 371 checks pass on Linux (GCC). The non-database parts (262 checks) also pass as a MinGW build under Wine. The owner built the core with MSVC 14.51 and ran all native and C# tests on Windows (before the engine-warning change). |
| `FastchessDesktop.Core` | A thin wrapper over the ABI. Warnings as errors; 78 tests pass on Linux against the native implementation, none skipped. |
| `FastchessDesktop.ViewModels` | Warnings as errors; 21 tests pass on Linux. |
| `FastchessDesktop.App` | Built and installed as MSIX on Windows by the owner (package 1.0.271.30217, after the migration); a 500-game SPRT match ran from the installed app. |

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
  The check is unconditional in fastchess (it fires without `-check-mate-pvs`) and the cause is
  the engine: Peacekeeper 3.01 triggers it several times per game, Reckless 0.9.0 never. They do
  not change moves or results.
- The native runner (`EngineWarnings` in tournament.cpp) takes these blocks out of the output
  lines, appends them to `engine-warnings.log` in the run's working directory and reports each
  as an `engineWarning` event with a running count per engine and message. The tournament log
  shows only the first of each message per engine, with an explanation and the file path; the
  Warnings column counts all of them. A "Warning;" line that names no engine (a fastchess
  problem such as CPU affinity) still appears in the log in amber.
- Real engine failures show up as game end reasons: "loses on time", "disconnects",
  "connection stalls", "makes an illegal move", and as nonzero Timeouts/Crashed counts at the end.
  They are shown in red, and a summary line after each run counts them.
- The post-run import now always reports its outcome in the tournament log, including when no
  database is open (it used to log that only on the Database page).

## Native Core Migration

The internals moved from C# into `native/` in two stages (FCD_ABI_VERSION 2, then 3). The C#
classes kept their public API and now call the C ABI, so the view models barely changed and the
existing C# tests became tests of the native code.

- Stage 1, pure computation: Windows command-line quoting (cmdline.cpp); tournament settings,
  validation, the fastchess command line, expected games and the first-stage preview, and
  fastchess output parsing (fastchess.cpp); Elo, nElo, LOS, SPRT and the live scoreboard
  (stats.cpp); knockout and Swiss pairings (formats.cpp); Ordo, Ordoprep and pgn-extract command
  lines (tools.cpp); a small JSON reader for input crossing the ABI (json.cpp).
- Stage 2, processes: process.cpp runs programs with redirected output. On Windows it uses
  CreateProcessW with an explicit inherited-handle list (so concurrent launches cannot leak pipe
  handles) and a job object, so stopping a run stops fastchess and its engines. On POSIX it uses
  fork/exec in a new process group. Output is split at \n, \r\n and lone \r as .NET did. After a
  process exits, the remaining tree is stopped when the pipes stay open for 5 s (engines left behind
  by a crashed fastchess). uci.cpp is the UCI client, analysis.cpp the per-ply analysis (the same
  analysis document, format 1), and tournament.cpp the runner for every format.
- Cancellation crosses the ABI as an `fcd_cancel` token driven by a CancellationToken; output lines
  and tournament events come back through callbacks on native threads, one call at a time.
- Status codes FCD_ERR_TIMEOUT and FCD_ERR_PROCESS were added; the C# wrappers map them (and
  NOT_FOUND, CANCELLED) to TimeoutException, InvalidOperationException, FileNotFoundException and
  OperationCanceledException, as the former C# code threw.
- Test programs: native/tests/fake_fastchess.cpp and fake_uci_engine.cpp replace the Python fake
  engine and the in-process fake fastchess. They build with the native tests, and the C# test
  projects copy them, so the engine and runner tests now also run on Windows.
- Removed from C#: GameAnalyzer.AverageLoss/CappedCp, UciLimit.ToGoCommand and the runner's
  ProcessLauncher hook (their behavior is tested natively). SprtTest now returns an SprtState from
  Evaluate.

Language split after the move (GitHub's measure: third-party code and XAML excluded): C++ about
49%, C# about 45%, C header about 4%, PowerShell about 2%. The C# that remains is the view models,
the C# tests and the thin wrapper layer.

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

- `native/`: `cmake --preset linux && cmake --build --preset linux && ./native/out/build/linux/fcd_tests`
  (371 checks: database, tools, statistics, processes, UCI, analysis, tournaments, engine warnings).
- Windows code paths: the native sources without the database, the fake programs and a test driver
  built with MinGW (x86_64-w64-mingw32-g++-posix, static) and run under Wine 9: 262 checks pass,
  including cancellation and the cleanup of an orphaned child that holds the output pipes open.
  SQLite could not be fetched in the container, so the database is not part of that build.
- `dotnet test` for both test projects (78 and 21 pass).
- The x:Bind checker, now also checking function bindings (263 paths and 30 function bindings,
  0 problems; confirmed to catch injected errors), and a stub compile of the App C#.
- `dotnet restore` of the App project in both package modes; property and item evaluation of the
  App project in both modes (output paths, content links).
- `package.ps1` parses without errors under PowerShell 7; its version and manifest-rewrite logic
  and its Windows PowerShell helper pattern were exercised under pwsh.

On Windows (by the owner, before the engine-warning change): `.\build.ps1` built the native core
with MSVC 14.51 and passed 355 native checks, 77 Core and 20 ViewModel tests; `.\package.ps1`
built and installed the MSIX, and a match ran from the installed app.

Not verified: the engine-warning change with MSVC and in the app.

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

On Windows, from the repository root: `git pull; .\build.ps1`, then `.\package.ps1 -SkipTests`.
Run a match with an engine that triggers the PV warning (Peacekeeper 3.01) and check that the log
shows one explanation line per engine, the Warnings column counts every occurrence, and
`engine-warnings.log` in the run folder holds the full blocks.
