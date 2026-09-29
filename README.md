# fastchess-desktop

A Windows desktop front end for the [fastchess](https://github.com/Disservin/fastchess) engine
tournament runner, with a game database for the results.

- **Tournament** page: configure engines and every fastchess option, start and stop a run,
  and watch the live log, progress, standings and finished games. Formats: round robin and
  gauntlet (run by fastchess itself), plus pyramid, knockout and Swiss (run by the app as a series
  of fastchess runs, see below). Engine names are read from the engine (`id name`) when it is added.
  Standings and finished games are sortable tables. The standings show Elo with its error margin,
  nElo, LOS, score, W/D/L, draw ratio, pentanomial counts, Elo change, and engine failures and
  warnings, computed live with fastchess's own formulas; a two-engine match also shows its
  head-to-head figures and the live SPRT state.
- **Database** page: a table of stored games with search, sorting and paging, a tag editor,
  and tools that run on selected games or on everything matching the filter:
  - classify openings from the Lichess chess-openings list (by complete EPD, last matched position)
  - fill missing results (checkmate, stalemate, insufficient material, fifty-move rule, repetition)
  - engine analysis with Stockfish (per-ply evaluations and average centipawn loss per side)
  - ratings with Ordoprep and Ordo, stored in the database and optionally written into WhiteElo/BlackElo
  - pgn-extract runs (opening classification with the Lichess list converted to `eco.pgn`,
    duplicate removal, result-tag repair, or custom arguments)
  - exports: PGN, JSON, XML (games and standings) and a text crosstable
- **Settings**: tool locations, analysis and rating options.

The UI is dark mode only.

## Tournament formats

fastchess 1.8 only schedules round robin and gauntlet tournaments. The other formats are run by
`TournamentRunner` (in `FastchessDesktop.Core`) as one fastchess run per stage or match. All runs
append to the same PGN, and game numbers and progress continue across runs.

| Format | How it is played |
| --- | --- |
| Round robin | One fastchess run; every engine meets every other engine. |
| Gauntlet | One fastchess run; the first *seeds* engines play everyone else. |
| Pyramid | Engines join in list order; each newcomer plays a gauntlet against all engines listed before it. |
| Knockout | Single elimination seeded by list order (top seeds get byes). A tied match plays the tiebreak game pairs, then the higher seed advances. |
| Swiss | A set number of rounds. Engines with equal scores are paired, without rematches where possible; an odd engine out gets a full-point bye. Final ranking by score, then Buchholz. |

SPRT is only available for round robin and gauntlet. A stopped pyramid, knockout or Swiss
tournament cannot be resumed.

## Architecture

```
src/FastchessDesktop.App         WinUI 3 views (XAML), dialogs, pickers. Windows only.
          |
src/FastchessDesktop.ViewModels  MVVM view models (CommunityToolkit.Mvvm). No UI dependency.
          |
src/FastchessDesktop.Core        Thin C# layer: P/Invoke declarations, handles, JSON DTOs,
          |                      exception mapping, the settings file.
          |  C ABI (native/include/fcd/fcd.h)
native/ (fcd_core.dll)           C++20: SQLite game database, PGN import, chess rules, opening
                                 classification, exports, rating import; tournament settings and
                                 the fastchess command line; statistics and SPRT; pairings for the
                                 staged formats; processes, the UCI client, game analysis and the
                                 tournament runner.
```

Dependencies only point downward. The internals are in the native core, which also runs the
external programs (fastchess, engines, Ordo, Ordoprep, pgn-extract). It does no networking and no
UI work. The C# layers hold the UI and view-model logic and forward everything else to the core.

Key decisions:

- Well-known PGN tags are database columns; other tags are kept in file order in a side table.
  PlyCount, SetUp and FEN are derived from the moves and never stored twice.
- Games with moves are deduplicated on import by players, date, round, result and the move
  sequence. Games without moves (rating-pool PGNs) are never treated as duplicates.
- Opening classification follows `assets/lichess-openings/parsing-lichess-openings.md`:
  positions are keyed by the full four-field EPD with legal en passant only, and a game gets the
  last named position it reaches, so transpositions are recognized.
- The chess rules come from chess-library 0.9.4 (`native/third_party/chess-library`, MIT), the same
  header fastchess uses. It carries one documented local patch (`PATCHES.md`).

## Requirements

- Windows 10 (1809) or later, x64
- Visual Studio 2022 17.14 or later (or Visual Studio 2026) with the **.NET desktop development**
  and **Desktop development with C++** workloads, including the **C++ CMake tools for Windows**
  and **vcpkg package manager** components
- .NET 10 SDK

The native build always uses the CMake and vcpkg that ship with Visual Studio (found with
`vswhere`), so other CMake installs on PATH, such as MSYS2 or MinGW, do not interfere.

## Build

From any PowerShell window, in the repository root:

```powershell
.\build.ps1
```

This configures and builds `native/` with CMake and vcpkg, runs the native and C# tests, and builds
the app. Use `.\build.ps1 -Configuration Debug -SkipTests` for a quick debug build.

Opening `FastchessDesktop.slnx` in Visual Studio and building the App project also works: its
`BuildNativeCore` target runs `native\build-native.ps1` when the native sources changed (pass
`-p:BuildNative=false` to skip that when you build `native/` yourself).

## Run

```powershell
.\src\FastchessDesktop.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FastchessDesktop.exe
```

The build copies the bundled tools next to the executable:

| Tool | Source in `assets/` | Location next to the exe |
| --- | --- | --- |
| fastchess 1.8.2 alpha | `fastchess/fastchess-windows-x86-64.zip` | `tools\fastchess\fastchess.exe` |
| Ordo 1.2.6 | `ordo/ordo-1.2.6-win.zip` | `tools\ordo\ordo-win64.exe` |
| Ordoprep 0.9.9.8 | `ordoprep/ordoprep-v0.9.9.8-win.zip` | `tools\ordoprep\ordoprep-win64.exe` |
| pgn-extract | `pgn-extract/pgn-extract.exe` | `tools\pgn-extract\pgn-extract.exe` |
| Stockfish | `stockfish/**/*.exe` (not in the repository yet) | `tools\stockfish\` |
| Lichess openings | `lichess-openings/lichess-openings.tsv` | `data\lichess-openings.tsv` |

Stockfish is not part of the repository. Put the executable under `assets\stockfish\` before
building, or set its path in Settings.

User data lives in `%LOCALAPPDATA%\FastchessDesktop`: `settings.json`, one folder per tournament
run under `runs\` (fastchess state file and PGN), and scratch files for tool runs under `work\`.
Game databases (`.fcdb`, SQLite) are wherever you create them. When no other database is open the
app uses a default database, `games.fcdb` in that folder, created on first use: tournament games
and PGN imports go there, and it is opened at startup unless another database was open last time.

## Install as an app (MSIX)

To install Fastchess Desktop like any other app, with a Start menu entry and a taskbar pin that
always opens the newest build:

```powershell
.\package.ps1
```

This builds the app as an MSIX package, signs it, and installs or upgrades it for the current
user. Run it again after pulling changes: each package gets a higher version, and Windows
upgrades the installed app in place, so the Start menu entry and any taskbar pin stay valid. Use
`-SkipTests` to skip the tests and `-NoInstall` to only build the package.

Signing: Windows only installs packages signed by a trusted publisher. The first run creates a
self-signed certificate `CN=FastchessDesktop` in your user certificate store and, after one UAC
prompt, adds its public part to the machine's Trusted People store. Later runs reuse it. The
private key stays in the certificate store; nothing is written to the repository.

The package goes to `dist\` (ignored by git). Double-clicking a `.msix` there installs it
as well. The packaged app needs Windows 10 2004 or later. It keeps its data in the same
`%LOCALAPPDATA%\FastchessDesktop` folder as the unpackaged build. To uninstall it, use
Settings > Apps, or run `Get-AppxPackage FastchessDesktop | Remove-AppxPackage` in Windows PowerShell.

## Test

Native (any OS with CMake, a C++20 compiler and SQLite development files):

```powershell
.\native\build-native.ps1 -Configuration Release -Test
```

On Linux: `cmake --preset linux && cmake --build --preset linux && ./native/out/build/linux/fcd_tests`.

C# (after the native build, on Windows or Linux):

```powershell
dotnet test tests\FastchessDesktop.Core.Tests; dotnet test tests\FastchessDesktop.ViewModels.Tests
```

The C# test projects copy the native library that the matching native build produced.

## Known limitations

- Stopping a tournament kills fastchess. Its state file is saved periodically (`-autosaveinterval`),
  and the log prints the `-config` argument that resumes the run.
- Analysis skips Chess960 games.
- Evaluations are stored per game but are not yet written into exported PGN comments.
- The MSIX package is signed with a self-signed certificate, so it installs only on machines that
  trust that certificate (package.ps1 sets this up on the machine that builds it).

## License

MIT (see `LICENSE`). Bundled tools keep their own licenses: fastchess and chess-library (MIT),
Ordo and Ordoprep (GPL-3.0), pgn-extract (GPL-3.0), Lichess chess-openings (CC0).
