# AGENTS.md

Project rules for AI agents and contributors. Read `HANDOFF.md` for the current state.

## Project Facts

| Fact | Value |
| --- | --- |
| Project | fastchess-desktop |
| Purpose | Windows GUI for the fastchess tournament runner plus a game database with analysis, ratings, pgn-extract tools and exports |
| Application type | Desktop application, WinUI 3. Built unpackaged by default; `package.ps1` builds and installs a signed MSIX |
| Supported OS | Windows 10 1809+ x64 for the app; the native core and all non-UI C# also build and test on Linux |
| Languages | C# 14 on .NET 10; C++20 for the native core |
| UI framework | WinUI 3, Windows App SDK 1.8, MVVM with CommunityToolkit.Mvvm 8.4 |
| Native build | CMake 3.25+ with presets; SQLite from vcpkg (Windows) or the system (Linux) |
| Solution | `FastchessDesktop.slnx` |
| Entry points | `src/FastchessDesktop.App/App.xaml.cs` (`OnLaunched`); C ABI in `native/include/fcd/fcd.h` |
| Build | `.\build.ps1` (Windows, any PowerShell); native only: `.\native\build-native.ps1`; MSIX package and install: `.\package.ps1` |
| Run | `src\FastchessDesktop.App\bin\x64\<Config>\net10.0-windows10.0.19041.0\win-x64\FastchessDesktop.exe` |
| Test | `native/out/build/<preset>/.../fcd_tests`, `dotnet test tests\FastchessDesktop.Core.Tests`, `dotnet test tests\FastchessDesktop.ViewModels.Tests` |
| Lint and format | None configured. Warnings are errors in all C# projects except the App; native builds with `-Wall -Wextra` or `/W4` |
| Output directories | `native/out/`, `**/bin/`, `**/obj/`, `dist/` (all ignored by git); the MSIX build uses `bin\msix\` and `obj\msix\` |
| Generated files | XAML `*.g.cs` in `obj/`; MVVM Toolkit and System.Text.Json source generators; the icons in `src/FastchessDesktop.App/Assets/` were rendered once from the DejaVu Sans knight glyph |
| Do not edit by hand | `native/third_party/chess-library/chess.hpp` except as recorded in `PATCHES.md`; anything under `assets/` |
| External tools | fastchess, Ordo, Ordoprep, pgn-extract (bundled from `assets/`), Stockfish (user supplied) |

## Directory Layout

```
native/                          C++ core, exported as a C ABI (fcd_core.dll)
  include/fcd/fcd.h              the ABI contract; bump FCD_ABI_VERSION on breaking changes
  src/                           database, PGN import, chess rules, openings, exports
  tests/                         fcd_tests (uses only the public header) and test data
  third_party/chess-library/     chess.hpp 0.9.4 (MIT) with one local patch
src/FastchessDesktop.Core/       P/Invoke, process runner, command builders, UCI client, settings
src/FastchessDesktop.ViewModels/ view models; no WinUI references allowed here
src/FastchessDesktop.App/        WinUI 3 views and platform services only
tests/                           xUnit v3 tests for Core and ViewModels
assets/                          tool archives and data supplied by the owner (read only)
```

## Layer Rules

- Dependencies point down only: App -> ViewModels -> Core -> native C ABI.
- The native core must not launch processes, use the network, or know about the UI.
- ViewModels reach the UI only through `IDialogService`, `IUiDispatcher` and `IAppEnvironment`.
- Keep `NativeMethods.cs` in step with `fcd.h`. Strings crossing the ABI are UTF-8; strings returned
  by the library are freed with `fcd_free`.
- XAML binds with `x:Bind`. Bound display values are strings, numbers edited in `NumberBox` are
  `double`, and `ComboBox` choices bind `SelectedIndex` to an `int` that maps to an enum.

## Tool Surfaces

Command-line syntax for the external tools is documented in the help XML files under `assets/`
(`fastchess-help.xml`, `ordo-help.xml`, `ordoprep-help.xml`, `pgn-extract-help.xml`). Check those
before changing a command builder. Known constraints already encoded:

- fastchess: `tc` and `st` conflict; every engine needs a limit; SPRT needs exactly two engines.
- Ordoprep: at most one of `-d`, `-m`, `-M`, `-g`, `--major-only` per run.
- pgn-extract: the ECO file is attached to the switch (`-e<path>`).
- fastchess only schedules round robin and gauntlet. Pyramid, knockout and Swiss are orchestrated by
  `TournamentRunner` as several fastchess runs; `FastchessCommandBuilder.Build` rejects them.

## Packaging

- `src/FastchessDesktop.App/Package.appxmanifest` is used only when `WindowsPackageType=MSIX`.
  Its Publisher must equal the certificate subject in `package.ps1` (`CN=FastchessDesktop`).
- Files the app needs at run time must be `Content` items with `CopyToOutputDirectory`, not ad hoc
  `Copy` tasks, otherwise they are missing from the MSIX.
- Never commit certificates, `.pfx` files or signing passwords.

## Git Workflow

- Work on `main` and commit to `main`. Do not create feature branches or pull requests.
- The owner explicitly permits pushing to `main`. If a session is started on another branch,
  switch to `main` (fast-forward it with the session's work if needed) instead of leaving work
  on a side branch for the owner to merge.

## Verification

- The native core and the C# libraries build and test on Linux as well as Windows (see HANDOFF.md).
- The WinUI project can only be fully compiled on Windows (its XAML compiler is a Windows program).
  After XAML changes, build on Windows before claiming success.
