using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Models;
using FastchessDesktop.Core.Native;
using FastchessDesktop.Core.Tools;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

/// <summary>Which games a tool operates on.</summary>
public enum OperationScope
{
    SelectedGames,
    AllMatchingFilter,
}

/// <summary>What happens to pgn-extract's output.</summary>
public enum PgnExtractOutputMode
{
    ImportAsNewGames,
    ReplaceScope,
}

/// <summary>The Database page: game table, selected game editor, and tool workflows.</summary>
public sealed partial class DatabaseViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly IAppEnvironment _environment;
    private readonly SettingsViewModel _settings;
    private GameDatabase? _db;
    private OpeningBook? _book;
    private string _bookPath = "";
    private CancellationTokenSource? _busyCts;
    private IReadOnlyList<long> _selectedIds = [];

    public DatabaseViewModel(IDialogService dialogs, IUiDispatcher dispatcher, IAppEnvironment environment,
        SettingsViewModel settings)
    {
        _dialogs = dialogs;
        _environment = environment;
        _settings = settings;
        Log = new LogViewModel(dispatcher);
    }

    public LogViewModel Log { get; }

    public ObservableCollection<GameRow> Games { get; } = [];
    public ObservableCollection<TagViewModel> SelectedTags { get; } = [];
    public ObservableCollection<PlayerRating> Ratings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(ImportPgnCommand), nameof(ExportCommand), nameof(ClassifyOpeningsCommand),
        nameof(FillResultsCommand), nameof(AnalyzeCommand), nameof(ComputeRatingsCommand), nameof(RunPgnExtractCommand),
        nameof(DeleteSelectedCommand), nameof(RefreshCommand), nameof(CloseDatabaseCommand))]
    public partial string DatabasePath { get; set; } = "";

    public bool IsOpen => _db is not null;

    public string Title => !IsOpen ? "No database open"
        : IsDefaultDatabase(DatabasePath) ? Path.GetFileName(DatabasePath) + " (default database)"
        : Path.GetFileName(DatabasePath);

    /// <summary>
    /// The database used when no other is open: %LOCALAPPDATA%\FastchessDesktop\games.fcdb. It is
    /// created on first use. Unlike a temporary clipbase it is a normal file, so tournament games
    /// imported into it are kept.
    /// </summary>
    public string DefaultDatabasePath => Path.Combine(_environment.DataDirectory, "games.fcdb");

    private bool IsDefaultDatabase(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(DefaultDatabasePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the default database unless a database is already open. Returns whether one is open.</summary>
    public async Task<bool> EnsureOpenAsync()
    {
        if (IsOpen) return true;
        Directory.CreateDirectory(_environment.DataDirectory);
        await OpenAsync(DefaultDatabasePath);
        return IsOpen;
    }

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial GameSortColumn SortColumn { get; set; } = GameSortColumn.Id;
    [ObservableProperty] public partial bool SortDescending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(PageCount))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial long MatchingCount { get; set; }

    [ObservableProperty] public partial long TotalCount { get; set; }

    public int PageSize => double.IsNaN(_settings.PageSize) ? 250 : Math.Clamp((int)_settings.PageSize, 10, 100_000);
    public int PageCount => (int)Math.Max(1, (MatchingCount + PageSize - 1) / PageSize);
    public string PageText => $"Page {PageIndex + 1} of {PageCount}  ({MatchingCount} matching, {TotalCount} total)";

    [ObservableProperty] public partial string SelectionText { get; set; } = "No games selected";
    [ObservableProperty] public partial GameDetail? SelectedGame { get; set; }
    [ObservableProperty] public partial string SelectedMoves { get; set; } = "";
    [ObservableProperty] public partial string SelectedAnalysis { get; set; } = "";
    [ObservableProperty] public partial string NewTagName { get; set; } = "";
    [ObservableProperty] public partial string NewTagValue { get; set; } = "";

    /// <summary>Index into <see cref="OperationScope"/>.</summary>
    [ObservableProperty] public partial int ScopeIndex { get; set; }

    [ObservableProperty] public partial bool SkipDuplicatesOnImport { get; set; } = true;
    [ObservableProperty] public partial bool OverwriteOpenings { get; set; }

    /// <summary>Index into <see cref="PgnExtractPreset"/>.</summary>
    [ObservableProperty] public partial int PgnExtractPresetIndex { get; set; }
    [ObservableProperty] public partial string PgnExtractArguments { get; set; } = "";

    /// <summary>Index into <see cref="PgnExtractOutputMode"/>.</summary>
    [ObservableProperty] public partial int PgnExtractOutputIndex { get; set; }

    /// <summary>Index into <see cref="ExportFormat"/>.</summary>
    [ObservableProperty] public partial int ExportFormatIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ImportPgnCommand), nameof(ExportCommand), nameof(ClassifyOpeningsCommand),
        nameof(FillResultsCommand), nameof(AnalyzeCommand), nameof(ComputeRatingsCommand), nameof(RunPgnExtractCommand),
        nameof(DeleteSelectedCommand), nameof(NewDatabaseCommand), nameof(OpenDatabaseCommand), nameof(OpenDefaultDatabaseCommand),
        nameof(CloseDatabaseCommand), nameof(WriteEcoFileCommand), nameof(CancelBusyCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty] public partial string BusyText { get; set; } = "";
    [ObservableProperty] public partial double BusyProgress { get; set; }
    [ObservableProperty] public partial bool BusyIndeterminate { get; set; } = true;

    // ---- Database lifetime -------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task NewDatabaseAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("games.fcdb", FileFilters.Database);
        if (path is not null) await OpenAsync(path);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenDatabaseAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Database);
        if (path is not null) await OpenAsync(path);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenDefaultDatabaseAsync()
    {
        Directory.CreateDirectory(_environment.DataDirectory);
        await OpenAsync(DefaultDatabasePath);
    }

    /// <summary>Opens (or creates) a database file and loads the first page.</summary>
    public async Task OpenAsync(string path)
    {
        try
        {
            var db = await Task.Run(() => GameDatabase.Open(path));
            _db?.Dispose();
            _db = db;
            DatabasePath = path;
            PageIndex = 0;
            Log.Add("Opened " + path);
            await ReloadAsync();
            await LoadRatingsAsync();
        }
        catch (FcdException e)
        {
            Log.Error(e.Message);
            await _dialogs.ShowMessageAsync("Cannot open database", e.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCloseDatabase))]
    private void CloseDatabase()
    {
        _db?.Dispose();
        _db = null;
        DatabasePath = "";
        Games.Clear();
        Ratings.Clear();
        SetSelection([]);
        MatchingCount = TotalCount = 0;
    }

    private bool CanCloseDatabase() => IsOpen && !IsBusy;

    // ---- Table ---------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        PageIndex = 0;
        await ReloadAsync();
    }

    /// <summary>Sorts by a <see cref="GameSortColumn"/> name; choosing the current column again reverses the order.</summary>
    [RelayCommand]
    private async Task SortAsync(string columnName)
    {
        var column = Enum.Parse<GameSortColumn>(columnName);
        if (SortColumn == column) SortDescending = !SortDescending;
        else (SortColumn, SortDescending) = (column, false);
        PageIndex = 0;
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPageAsync()
    {
        PageIndex--;
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPageAsync()
    {
        PageIndex++;
        await ReloadAsync();
    }

    private bool CanGoPrevious() => PageIndex > 0;
    private bool CanGoNext() => PageIndex + 1 < PageCount;

    private GameQuery FilterQuery => new() { Search = SearchText, OrderBy = SortColumn, Descending = SortDescending };

    private async Task ReloadAsync()
    {
        if (_db is not { } db) return;
        var filter = FilterQuery;
        var page = filter with { Offset = (long)PageIndex * PageSize, Limit = PageSize };
        try
        {
            var (rows, matching, total) = await Task.Run(() => (db.Query(page), db.Count(filter), db.Count(GameQuery.All)));
            TotalCount = total;
            MatchingCount = matching;
            if (PageIndex >= PageCount)
            {
                PageIndex = PageCount - 1;
                rows = await Task.Run(() => db.Query(filter with { Offset = (long)PageIndex * PageSize, Limit = PageSize }));
            }
            Games.Clear();
            foreach (var g in rows) Games.Add(new GameRow(g));
        }
        catch (FcdException e)
        {
            Log.Error(e.Message);
        }
    }

    /// <summary>Re-reads one row without touching the rest of the table or the selection.</summary>
    private async Task RefreshRowAsync(long id)
    {
        if (_db is not { } db || Games.FirstOrDefault(g => g.Id == id) is not { } row) return;
        var summary = await Task.Run(() => db.Query(GameQuery.ForIds([id])));
        if (summary.Count == 1) row.Update(summary[0]);
    }

    /// <summary>Called by the view when the table selection changes.</summary>
    public void SetSelection(IReadOnlyList<GameRow> rows)
    {
        _selectedIds = [.. rows.Select(r => r.Id)];
        SelectionText = rows.Count switch { 0 => "No games selected", 1 => "1 game selected", var n => $"{n} games selected" };
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectionLoaded = LoadSelectedGameAsync(rows.Count == 1 ? rows[0].Id : null);
    }

    /// <summary>Completes when the detail pane reflects the latest selection.</summary>
    public Task SelectionLoaded { get; private set; } = Task.CompletedTask;

    private async Task LoadSelectedGameAsync(long? id)
    {
        SelectedTags.Clear();
        SelectedGame = null;
        SelectedMoves = SelectedAnalysis = "";
        if (id is null || _db is not { } db) return;
        try
        {
            var game = await Task.Run(() => db.GetGame(id.Value));
            if (_selectedIds.Count != 1 || _selectedIds[0] != id) return; // selection moved on meanwhile
            foreach (var tag in GameText.Tags(game)) SelectedTags.Add(tag);
            SelectedMoves = GameText.Moves(game);
            SelectedAnalysis = GameText.AnalysisSummary(game);
            SelectedGame = game;
        }
        catch (FcdException e)
        {
            Log.Error(e.Message);
        }
    }

    // ---- Editing -------------------------------------------------------------

    [RelayCommand]
    private async Task SaveTagsAsync()
    {
        if (_db is not { } db || SelectedGame is not { } game) return;
        var changed = SelectedTags.Where(t => t.IsDirty).ToList();
        try
        {
            foreach (var tag in changed)
            {
                await Task.Run(() => db.SetTag(game.Id, tag.Name, tag.Value));
                tag.MarkSaved();
            }
            if (changed.Count > 0) Log.Add($"Saved {changed.Count} tag(s) of game {game.Id}.", LogKind.Success);
            await RefreshRowAsync(game.Id);
        }
        catch (FcdException e)
        {
            Log.Error(e.Message);
            await _dialogs.ShowMessageAsync("Tag not saved", e.Message);
        }
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (_db is not { } db || SelectedGame is not { } game || string.IsNullOrWhiteSpace(NewTagName)) return;
        var name = NewTagName.Trim();
        try
        {
            await Task.Run(() => db.SetTag(game.Id, name, NewTagValue));
            NewTagName = NewTagValue = "";
            await LoadSelectedGameAsync(game.Id);
            await RefreshRowAsync(game.Id);
        }
        catch (FcdException e)
        {
            await _dialogs.ShowMessageAsync("Tag not added", e.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        if (_db is not { } db) return;
        var ids = _selectedIds;
        if (!await _dialogs.ConfirmAsync("Delete games", $"Permanently delete {ids.Count} game(s) from the database?", "Delete"))
            return;
        await Task.Run(() => db.DeleteGames([.. ids]));
        Log.Add($"Deleted {ids.Count} game(s).");
        SetSelection([]);
        await ReloadAsync();
    }

    private bool CanDelete() => IsOpen && !IsBusy && _selectedIds.Count > 0;

    // ---- Tools ---------------------------------------------------------------

    private bool CanRunTool() => IsOpen && !IsBusy;

    /// <summary>Available without an open database: the default database is opened for the import.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportPgnAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Pgn);
        if (path is not null) await ImportFileAsync(path);
    }

    /// <summary>
    /// Imports a PGN file into the open database, opening the default database first if none is
    /// open. Returns null if nothing was imported.
    /// </summary>
    public async Task<ImportResult?> ImportFileAsync(string path)
    {
        if (!await EnsureOpenAsync() || _db is not { } db)
        {
            Log.Error("No database could be opened; import skipped for " + path);
            return null;
        }
        if (IsBusy)
        {
            // Another job owns the busy indicator; the database lock serializes the two.
            var direct = await Task.Run(() => db.ImportPgn(path, SkipDuplicatesOnImport));
            Log.Add($"Imported {direct.Imported} games from {Path.GetFileName(path)}.", LogKind.Success);
            await ReloadAsync();
            return direct;
        }
        ImportResult? result = null;
        await RunBusyAsync("Importing " + Path.GetFileName(path), async (ct, progress) =>
        {
            // Created here, on the UI thread, so reports come back to it (not inside Task.Run).
            var reporter = new Progress<NativeProgress>(p => progress($"Importing: {p.Done} games read", null));
            var r = await Task.Run(() => db.ImportPgn(path, SkipDuplicatesOnImport, reporter, ct), ct);
            Log.Add($"Imported {r.Imported} games from {Path.GetFileName(path)} ({r.Duplicates} duplicates skipped, {r.Failed} unreadable).",
                LogKind.Success);
            result = r;
        });
        await ReloadAsync();
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task ClassifyOpeningsAsync() => FillAsync(FillOptions.Opening | (OverwriteOpenings ? FillOptions.OverwriteOpening : 0),
        "Classifying openings");

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private Task FillResultsAsync() => FillAsync(FillOptions.Result, "Filling missing results");

    private async Task FillAsync(FillOptions options, string text)
    {
        if (_db is not { } db || !TryGetScope(out var scope)) return;
        OpeningBook? book = null;
        if (options.HasFlag(FillOptions.Opening) && (book = await GetOpeningBookAsync()) is null) return;
        await RunBusyAsync(text, async (ct, progress) =>
        {
            var reporter = new Progress<NativeProgress>(p =>
                progress($"{text}: {p.Done} of {p.Total}", p.Total > 0 ? (double)p.Done / p.Total : null));
            var n = await Task.Run(() => db.FillMissing(scope, book, options, reporter, ct), ct);
            Log.Add($"{text}: {n} game(s) updated.", LogKind.Success);
        });
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task AnalyzeAsync()
    {
        if (_db is not { } db || !TryGetScope(out var scope)) return;
        var stockfish = _settings.EffectiveTools.Stockfish;
        if (!File.Exists(stockfish))
        {
            await _dialogs.ShowMessageAsync("Stockfish not found", "Set the Stockfish executable in Settings.");
            return;
        }
        var ids = await Task.Run(() => db.Query(scope).Select(g => g.Id).ToList());
        if (ids.Count > 20 &&
            !await _dialogs.ConfirmAsync("Analyze games", $"Analyze {ids.Count} games? This can take a long time.", "Analyze"))
            return;
        var settings = _settings.ToAnalysisSettings();

        await RunBusyAsync("Analyzing", async (ct, progress) =>
        {
            var options = new Dictionary<string, string>
            {
                ["Threads"] = settings.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Hash"] = settings.HashMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            await using var engine = await UciEngine.StartAsync(stockfish, options, ct);
            Log.Add($"Analyzing {ids.Count} game(s) with {engine.Name}, depth {settings.Depth}" +
                    (settings.MoveTimeMs > 0 ? $", {settings.MoveTimeMs} ms per position" : "") + ".");
            for (var i = 0; i < ids.Count; i++)
            {
                var game = await Task.Run(() => db.GetGame(ids[i]), ct);
                if (game.Chess960)
                {
                    Log.Add($"Game {game.Id}: Chess960 is not analyzed yet; skipped.", LogKind.Error);
                    continue;
                }
                var index = i;
                var perGame = new Progress<(int Ply, int Total)>(p => progress(
                    $"Game {index + 1} of {ids.Count} (id {game.Id}): ply {p.Ply} of {p.Total}",
                    (index + (p.Total > 0 ? (double)p.Ply / (p.Total + 1) : 0)) / ids.Count));
                var analysis = await GameAnalyzer.AnalyzeAsync(engine, game, settings, perGame, ct);
                await Task.Run(() => db.SetAnalysis(game.Id, analysis.ToJson()), ct);
                Log.Add($"Game {game.Id} {game.White} - {game.Black}: ACPL White {analysis.WhiteAcpl:0.0}, Black {analysis.BlackAcpl:0.0}");
            }
            Log.Add("Analysis finished.", LogKind.Success);
        });
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ComputeRatingsAsync()
    {
        if (_db is not { } db || !TryGetScope(out var scope)) return;
        var tools = _settings.EffectiveTools;
        var s = _settings.ToRatingSettings();
        if (!File.Exists(tools.Ordo) || (s.RunOrdoprep && !File.Exists(tools.Ordoprep)))
        {
            await _dialogs.ShowMessageAsync("Ordo not found", "Set the Ordo (and Ordoprep) executables in Settings.");
            return;
        }

        await RunBusyAsync("Computing ratings", async (ct, progress) =>
        {
            var work = CreateWorkDirectory();
            var input = Path.Combine(work, "games.pgn");
            var n = await Task.Run(() => db.Export(scope, ExportFormat.Pgn, input), ct);
            Log.Add($"Rating {n} games (work files in {work}).");
            if (s.RunOrdoprep)
            {
                var prepped = Path.Combine(work, "prepped.pgn");
                progress("Running Ordoprep", null);
                if (!await RunToolAsync(new ProcessSpec(tools.Ordoprep, RatingToolArgs.Ordoprep(s, input, prepped), work), ct))
                    return;
                input = prepped;
            }
            var report = Path.Combine(work, "ratings.txt");
            var csv = Path.Combine(work, "ratings.csv");
            progress("Running Ordo", null);
            if (!await RunToolAsync(new ProcessSpec(tools.Ordo, RatingToolArgs.Ordo(s, input, report, csv), work), ct))
                return;
            if (File.Exists(report))
                foreach (var line in await File.ReadAllLinesAsync(report, ct)) Log.Add(line, LogKind.Output);
            var (players, written) = await Task.Run(() => db.ApplyOrdoCsv(csv, s.FillEloTags, s.OverwriteEloTags), ct);
            Log.Add($"Stored ratings for {players} players; wrote {written} Elo tag value(s).", LogKind.Success);
        });
        await LoadRatingsAsync();
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RunPgnExtractAsync()
    {
        if (_db is not { } db || !TryGetScope(out var scope)) return;
        var tools = _settings.EffectiveTools;
        if (!File.Exists(tools.PgnExtract))
        {
            await _dialogs.ShowMessageAsync("pgn-extract not found", "Set the pgn-extract executable in Settings.");
            return;
        }
        var preset = (PgnExtractPreset)PgnExtractPresetIndex;
        var mode = (PgnExtractOutputMode)PgnExtractOutputIndex;
        var ecoPath = "";
        if (preset == PgnExtractPreset.ClassifyOpenings)
        {
            if (await GetOpeningBookAsync() is not { } book) return;
            ecoPath = Path.Combine(_environment.DataDirectory, "lichess-eco.pgn");
            await Task.Run(() => book.WriteEcoPgn(ecoPath));
        }
        var ids = await Task.Run(() => db.Query(scope).Select(g => g.Id).ToList());
        if (mode == PgnExtractOutputMode.ReplaceScope &&
            !await _dialogs.ConfirmAsync("Replace games",
                $"The {ids.Count} game(s) in scope will be deleted and replaced by pgn-extract's output. " +
                "Games that pgn-extract drops are lost. Continue?", "Replace"))
            return;

        await RunBusyAsync("Running pgn-extract", async (ct, progress) =>
        {
            var work = CreateWorkDirectory();
            var input = Path.Combine(work, "input.pgn");
            var output = Path.Combine(work, "output.pgn");
            await Task.Run(() => db.Export(GameQuery.ForIds(ids), ExportFormat.Pgn, input), ct);
            var args = PgnExtractArgs.Build(preset, PgnExtractArguments, ecoPath, input, output);
            if (!await RunToolAsync(new ProcessSpec(tools.PgnExtract, args, work), ct)) return;
            if (!File.Exists(output))
            {
                Log.Error("pgn-extract produced no output file.");
                return;
            }
            progress("Importing pgn-extract output", null);
            // Duplicate skipping stays off: the output is derived from games already stored (same moves and
            // players), so it would otherwise be discarded as duplicates of its own source.
            var r = await Task.Run(() => db.ImportPgn(output, skipDuplicates: false), ct);
            if (mode == PgnExtractOutputMode.ReplaceScope) await Task.Run(() => db.DeleteGames(ids), ct);
            Log.Add($"pgn-extract output: {r.Imported} games imported, {r.Duplicates} duplicates skipped, {r.Failed} unreadable" +
                    (mode == PgnExtractOutputMode.ReplaceScope ? $"; {ids.Count} original game(s) removed." : "."), LogKind.Success);
        });
        SetSelection([]);
        await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task ExportAsync()
    {
        if (_db is not { } db || !TryGetScope(out var scope)) return;
        var format = (ExportFormat)ExportFormatIndex;
        var (name, filter) = format switch
        {
            ExportFormat.Pgn => ("games.pgn", new FileFilter("PGN games", ".pgn")),
            ExportFormat.Json => ("games.json", new FileFilter("JSON", ".json")),
            ExportFormat.Xml => ("games.xml", new FileFilter("XML", ".xml")),
            _ => ("crosstable.txt", new FileFilter("Text", ".txt")),
        };
        var path = await _dialogs.PickSaveFileAsync(name, [filter]);
        if (path is null) return;
        await RunBusyAsync("Exporting", async (ct, _) =>
        {
            var n = await Task.Run(() => db.Export(scope, format, path), ct);
            Log.Add($"Exported {n} game(s) to {path}.", LogKind.Success);
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task WriteEcoFileAsync()
    {
        if (await GetOpeningBookAsync() is not { } book) return;
        var path = await _dialogs.PickSaveFileAsync("eco.pgn", FileFilters.Pgn);
        if (path is null) return;
        await Task.Run(() => book.WriteEcoPgn(path));
        Log.Add($"Wrote {book.Count} Lichess openings as a pgn-extract ECO file: {path}", LogKind.Success);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelBusy() => _busyCts?.Cancel();

    // ---- Helpers -------------------------------------------------------------

    private bool TryGetScope(out GameQuery query)
    {
        if ((OperationScope)ScopeIndex == OperationScope.SelectedGames)
        {
            query = GameQuery.ForIds(_selectedIds);
            if (_selectedIds.Count > 0) return true;
            _ = _dialogs.ShowMessageAsync("No games selected",
                "Select games in the table, or set the scope to all games matching the filter.");
            return false;
        }
        query = new GameQuery { Search = SearchText, OrderBy = SortColumn, Descending = SortDescending };
        return true;
    }

    private async Task<OpeningBook?> GetOpeningBookAsync()
    {
        var path = _settings.EffectiveTools.OpeningsTsv;
        if (_book is not null && _bookPath == path) return _book;
        if (!File.Exists(path))
        {
            await _dialogs.ShowMessageAsync("Openings file not found",
                "Set the Lichess openings TSV (eco, name, pgn, uci, epd) in Settings.");
            return null;
        }
        try
        {
            var book = await Task.Run(() => OpeningBook.LoadTsv(path));
            _book?.Dispose();
            (_book, _bookPath) = (book, path);
            Log.Add($"Loaded {book.Count} openings from {path}.");
            return book;
        }
        catch (FcdException e)
        {
            await _dialogs.ShowMessageAsync("Openings file rejected", e.Message);
            return null;
        }
    }

    private async Task LoadRatingsAsync()
    {
        Ratings.Clear();
        if (_db is not { } db) return;
        foreach (var r in await Task.Run(db.GetRatings)) Ratings.Add(r);
    }

    private string CreateWorkDirectory()
    {
        var dir = Path.Combine(_environment.DataDirectory, "work", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Runs a tool with its output in the log. Returns false (and logs) on failure.</summary>
    private async Task<bool> RunToolAsync(ProcessSpec spec, CancellationToken ct)
    {
        Log.Add(spec.DisplayCommand, LogKind.Command);
        var result = await ProcessRunner.RunAsync(spec,
            l => Log.Add(l.Text, l.Stream == OutputStream.StandardError ? LogKind.Error : LogKind.Output), ct);
        ct.ThrowIfCancellationRequested();
        if (result.ExitCode == 0) return true;
        Log.Error($"{Path.GetFileName(spec.FileName)} failed with exit code {result.ExitCode}.");
        return false;
    }

    /// <summary>Runs work with the busy indicator; progress(text, fraction or null for indeterminate).</summary>
    private async Task RunBusyAsync(string text, Func<CancellationToken, Action<string, double?>, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _busyCts = cts;
        BusyText = text;
        BusyIndeterminate = true;
        BusyProgress = 0;
        IsBusy = true;
        void Progress(string t, double? fraction)
        {
            BusyText = t;
            BusyIndeterminate = fraction is null;
            BusyProgress = (fraction ?? 0) * 100;
        }
        try
        {
            await work(cts.Token, Progress);
        }
        catch (OperationCanceledException)
        {
            Log.Error(text + ": cancelled.");
        }
        catch (Exception e) when (e is FcdException or IOException or InvalidOperationException or TimeoutException
                                      or UnauthorizedAccessException)
        {
            Log.Error($"{text}: {e.Message}");
            await _dialogs.ShowMessageAsync(text + " failed", e.Message);
        }
        finally
        {
            _busyCts = null;
            IsBusy = false;
            BusyText = "";
        }
    }

    public void Dispose()
    {
        _busyCts?.Cancel();
        _db?.Dispose();
        _book?.Dispose();
    }
}
