# Handoff

State of the repository after the first implementation pass (2026-09-28).

## Current State

The full stack exists: native core, C# services, view models, WinUI 3 views, build script and tests.

| Layer | Status |
| --- | --- |
| `native/` | Built and tested on Linux (GCC, system SQLite). 109 checks pass. Not yet built with MSVC. |
| `FastchessDesktop.Core` | Builds with warnings as errors; 35 tests pass on Linux against the real native library. |
| `FastchessDesktop.ViewModels` | Builds with warnings as errors; 14 tests pass on Linux, including full database workflows. |
| `FastchessDesktop.App` | C# compiles against the real Windows App SDK 1.8 references; XAML not compiled (see below). |

## Verification Performed

All in a Linux container (Ubuntu 24.04, .NET SDK 10.0.112, GCC):

- `native/`: `cmake --preset linux && cmake --build --preset linux && ./native/out/build/linux/fcd_tests`
  (109 checks, including a PGN export and re-import round trip and all 3,815 Lichess rows validated
  by replaying their UCI moves).
- Import of Ordo's sample `games.pgn` (161,200 games, CR-heavy line endings): 1.4 s; crosstable and
  PGN export of all games: 1.4 s.
- Ordoprep and Ordo built from the source zips in `assets/`: the full rating pipeline with the exact
  arguments the app generates, and the resulting CSV imported into a database (60 players).
- pgn-extract built from its source zip: classification with the generated Lichess `eco.pgn`
  (Najdorf English Attack recognized; a transposition to D02 recognized), `-D`, `--fixresulttags`.
- `dotnet test` for both test projects (a fake UCI engine script drives the UCI client end to end).
- WinUI App: `dotnet restore` succeeds; all App C# compiles with warnings as errors against the
  Windows App SDK 1.8 reference assemblies, using stubs for XAML-generated members.
- A reflection-based checker validated all 199 `x:Bind` paths in the XAML against the compiled view
  models (member names, types, TwoWay setters). It was confirmed to catch injected errors.
- The `BundleTools` MSBuild target was run on its own and produced the expected `tools\` layout.

Not verified: the XAML compiler pass, the `BuildNativeCore` target, MSVC compilation of `native/`,
vcpkg SQLite resolution, and running the app. These need Windows.

## Decisions

- SQLite is the database; the file extension is `.fcdb`. Schema version 1 in the `meta` table.
- chess-library 0.9.4 was reused from the fastchess source zip rather than writing a move generator.
  It was patched: comments, NAGs and variations that start a new line were read as moves.
- The PGN reader strips carriage returns before parsing, which fixed the CR-run file above.
- Moveless games are never deduplicated; with moves, the key is players, date, round, result, start
  FEN and moves (FNV-1a).
- Windows App SDK 1.8 (serviced, 1.8.260921001) instead of 2.x, to stay on a known project format.
- The data table is a `ListView` with a sortable header rather than a DataGrid control, avoiding the
  unmaintained WinUI DataGrid package.
- Each tournament runs in its own folder under `%LOCALAPPDATA%\FastchessDesktop\runs\`.
- Analysis JSON (format 1) is written by `GameAnalyzer`: per-ply White-POV `cp`/`mate`, depth, best
  move, and average centipawn loss per side. `mate: 0` marks a checkmated position.

## Files Changed

Everything except `assets/`, `LICENSE` and `.gitattributes` is new in this pass. See `AGENTS.md`
for the layout. `.gitignore` gained `native/out/`.

## Unresolved Issues

- Stockfish 19 is not in `assets/` (the request said it was included). Place it under
  `assets\stockfish\` or set its path in Settings.
- The XAML has not been compiled. Expect possible XAML compiler errors on the first Windows build.
- Stopping fastchess kills the process tree instead of sending Ctrl+C.
- Analysis results are not written into exported PGN comments.
- No installer or publish profile.

## Next Recommended Action

On Windows, from a Developer PowerShell, run `.\build.ps1` and report the output. Fix any XAML
compiler errors first, then launch the app and walk through: add two engines, run a short
tournament with import enabled, then classify openings, compute ratings and export a crosstable.
