# fastchess-desktop

A Windows desktop front end for the [fastchess](https://github.com/Disservin/fastchess) engine
tournament runner, with a game database for the results.

- **Tournament** page: configure engines and every fastchess option, start and stop a run,
  and watch the live log, progress, standings and finished games.
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

## Architecture

```
src/FastchessDesktop.App         WinUI 3 views (XAML), dialogs, pickers. Windows only.
          |
src/FastchessDesktop.ViewModels  MVVM view models (CommunityToolkit.Mvvm). No UI dependency.
          |
src/FastchessDesktop.Core        P/Invoke wrapper, external process runner, fastchess/Ordo/
          |                      Ordoprep/pgn-extract command builders, UCI client, settings.
          |  C ABI (native/include/fcd/fcd.h)
native/ (fcd_core.dll)           C++20: SQLite game database, PGN import, chess rules,
                                 opening classification, exports, rating import.
```

Dependencies only point downward. The native core does no process launching, networking or UI
work; the C# layer runs the external programs and passes files to the core.

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
  and **Desktop development with C++** workloads (includes CMake and vcpkg)
- .NET 10 SDK
- `VCPKG_ROOT` set (a Visual Studio Developer PowerShell sets it) so CMake can get SQLite

## Build

From a Developer PowerShell for Visual Studio, in the repository root:

```powershell
.\build.ps1
```

This configures and builds `native/` with CMake and vcpkg, runs the native and C# tests, and builds
the app. Use `.\build.ps1 -Configuration Debug -SkipTests` for a quick debug build.

Opening `FastchessDesktop.slnx` in Visual Studio and building the App project also works: its
`BuildNativeCore` target runs CMake when the native sources changed (pass `-p:BuildNative=false`
to skip that when you build `native/` yourself).

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
Game databases (`.fcdb`, SQLite) are wherever you create them.

## Test

Native (any OS with CMake, a C++20 compiler and SQLite development files):

```powershell
cmake --preset windows-x64; cmake --build --preset windows-x64-release; .\native\out\build\windows-x64\Release\fcd_tests.exe
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
- There is no installer or `dotnet publish` profile yet; run the app from its build folder.

## License

MIT (see `LICENSE`). Bundled tools keep their own licenses: fastchess and chess-library (MIT),
Ordo and Ordoprep (GPL-3.0), pgn-extract (GPL-3.0), Lichess chess-openings (CC0).
