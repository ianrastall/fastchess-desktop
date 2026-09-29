# Handoff

State of the repository on 2026-09-29, after the Stop fix and engine ratings from the UCERL list.

## Current State

| Layer | Status |
| --- | --- |
| `native/` | 428 checks pass on Linux (GCC). FCD_ABI_VERSION is now 4 (rating list functions added). Not built with MSVC or MinGW since this pass. |
| `FastchessDesktop.Core` | A thin wrapper over the ABI. Warnings as errors; 79 tests pass on Linux. |
| `FastchessDesktop.ViewModels` | Warnings as errors; 23 tests pass on Linux. |
| `FastchessDesktop.App` | Changed in this pass (engine list template, legend, Sort button, rating list setting, tier brushes, `TierColors.cs`). Not compiled: the XAML compiler only runs on Windows. The last Windows build was package 1.0.271.30217, before this pass. |

## Changes In This Pass

### Stopping a tournament

The owner reported that after Stop the interface became unusable. It could not be reproduced on
Linux (the native cancel returned promptly), so the fixes address every way the Tournament page
could stay locked after a stop, and make each stage visible in the log:

- The settings stayed disabled until the post-run PGN import had finished, because `IsRunning` was
  cleared in a `finally` after `await ImportLastGamesAsync()`. The import can be slow (a large
  database) or wait on the database lock while the Database page runs another job, and it showed
  nothing on the Tournament page meanwhile. `IsRunning` is now cleared as soon as fastchess has
  exited; the import follows, and the log says when the database is busy with another job. The
  Start button stays disabled until the import ends (the command is still running).
- Stop gave no feedback until the native run returned. The cancellation now logs
  "Stop requested: stopping fastchess and its engines..." and shows "Stopping..." at once.
- Native (process.cpp): after stopping the process tree, the reader threads were joined without a
  limit. A process outside the job object (or, on POSIX, the process group) that still held the
  output pipes would keep the run, and so the page, waiting for as long as that process lived.
  `finish_output` now waits 3 s after the kill, then stops reading: POSIX readers poll and check a
  flag; Windows readers are cancelled with `CancelSynchronousIo` on a duplicated thread handle. The
  run then logs "the output of ... was still open ...". After a cancel the first wait is 1 s
  instead of 5 s, since the tree was already stopped.
- The PGN import and the fill jobs created their `Progress<T>` inside `Task.Run`, so busy-text
  updates ran on a thread-pool thread and reached the (cached) Database page's x:Bind bindings off
  the UI thread. The reporters are now created on the calling thread.
- An import error from the post-run import (raised when it runs beside another database job) is
  logged instead of escaping the command.

Tests: `test_process_escaped_child` (native) uses a new fake_fastchess mode `cmd=escape` that starts
a child outside the tree (setsid on POSIX; a job breakaway attempt on Windows, which our job does
not allow, so there it stays inside and only the time limits are checked). Against the old
process.cpp this test fails (the run lasts as long as the escaped child, 15 s). The view-model test
`Stop_ends_the_run_and_unlocks_the_page` runs the fake fastchess, presses Stop and checks the
"Stopping..." state, the log lines and that the page and Start unlock.

### Engine ratings (UCERL list)

- The owner supplied `assets/ucerl/ucerl-ratings.csv`: the UCERL (Universal Chess Engines Rating
  List), an Ordo CSV of 7427 engines built from the merged, deduplicated games of the public rating
  lists. It is bundled as `data\ucerl-ratings.csv` (a Content item, so the MSIX includes it). It was
  force-added like the other assets (`/assets/` is in `.gitignore`).
- native `ratings.cpp` (ABI: `fcd_ratings_load_csv`, `_free`, `_count`, `_lookup`): Ordo CSV parsing
  (quoted fields, BOM, CRLF, columns found by header name), name normalization and lookup. See the
  README section "Engine ratings" for the rules. Stockfish 19, not yet in the list, comes out as
  3833.3 (Stockfish 18 plus 10), at the top, as the owner asked.
- C#: `RatingList` and `EngineRating` in Core; `ToolPaths.RatingList` with the bundled default and a
  Settings field; `EngineViewModel.Rating`, `RatingText` ("3823", "~3833"), `RatingDetail`, `Tier`
  (200-Elo bands, `TierOf`); `TournamentViewModel` looks engines up when added or renamed and when
  the list path changes, places a newly added engine by rating, and has `SortEnginesByRatingCommand`.
- App: engine list items show a tier color bar, the name and the rating in the tier color, with the
  details as a tooltip and under the Status line; a legend under the list; a Sort by rating button;
  drag reordering (`CanReorderItems`). Tier brushes are `RatingTier0Brush` to `RatingTier6Brush` in
  App.xaml.
- List order matters: it is the seeding for gauntlet, pyramid and knockout. Automatic placement
  only happens when an engine is added; loaded settings keep their saved order.

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

In the Linux container (this pass):

- `native/`: `cmake --preset linux && cmake --build --preset linux && ./native/out/build/linux/fcd_tests`
  from `native/`: 428 checks, 0 failures, no compiler warnings.
- The new escaped-child test was run against the previous process.cpp: 4 failures (the run lasted
  as long as the escaped process). With the change it passes.
- `dotnet test` for both test projects: 79 and 23 pass. The Stop test fails against the previous
  view model (no "Stopping..." state) and passes now.
- The .NET SDK download host is blocked by this environment's network policy. The SDK came from
  Ubuntu's `dotnet-sdk-10.0` package: `apt-get download` of it and its dependencies, unpacked with
  `dpkg -x` into the session scratchpad (nothing installed system-wide), with `DOTNET_ROOT`, `PATH`
  and `NUGET_PACKAGES` pointing there.
- Engine-name matching was checked by hand against the full UCERL list for about 25 real engine
  names (Stockfish, Reckless, PlentyChess, Obsidian, Berserk, Caissa, Torch, Viridithas, Stormphrax,
  Integral, Lc0, Dragon, Ethereal, Koivisto, Alexandria, Clover, Patricia).
- The changed XAML files are well-formed XML, and every new x:Bind path was checked by hand against
  the view-model members. The App project build on Linux stops at the XAML compiler (a Windows
  program), as expected.

Not verified: the Windows code in process.cpp (`CancelSynchronousIo` path) with MSVC or MinGW; the
XAML changes (compile and look); the rating list in the packaged app; the Stop fix against the
owner's real tournament.

## Unresolved Issues

- The root cause of the reported Stop problem was not reproduced; see "Stopping a tournament" for
  what was fixed. If the page still locks up after Stop, the log now shows which stage it reached.
- Engines not in the rating list (development builds, renamed binaries) are unrated; there is no
  manual rating override yet. It would need a per-engine field kept outside `EngineSettings` or
  ignored by the native settings parser.
- A PGN cut off inside a game header imports as an extra empty game (seen while testing truncated
  PGNs; fastchess writes whole games, so this needs a kill during a write).
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

On Windows, after the current tournament has finished, from the repository root:
`git pull; .\build.ps1`, then `.\package.ps1`. Check that the build passes 428 native checks and
79 plus 23 C# tests, that the Tournament page shows the engine ratings, colors, legend and Sort by
rating button, and that Stop during a match logs "Stop requested", shows "Stopping...", then
"Stopped", and unlocks the settings.
