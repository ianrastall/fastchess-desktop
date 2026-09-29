using FastchessDesktop.Core.Models;
using FastchessDesktop.Core.Settings;
using FastchessDesktop.Core.Tools;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels.Tests;

internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> Paths { get; } = new();
    public List<string> Messages { get; } = [];
    public bool ConfirmResult { get; set; } = true;

    public Task<string?> PickOpenFileAsync(IReadOnlyList<FileFilter> filters) => Task.FromResult(Paths.Dequeue());
    public Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileFilter> filters) => Task.FromResult(Paths.Dequeue());
    public Task<string?> PickFolderAsync() => Task.FromResult(Paths.Dequeue());
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(ConfirmResult);

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(title + ": " + message);
        return Task.CompletedTask;
    }
}

internal sealed class TempEnvironment : IAppEnvironment, IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("fcd-vm");
    public string AppDirectory => _dir.FullName;
    public string DataDirectory => Path.Combine(_dir.FullName, "data");
    public void Dispose() => _dir.Delete(recursive: true);
}

public sealed class TournamentViewModelTests : IDisposable
{
    private readonly TempEnvironment _env = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ShellViewModel _shell;

    public TournamentViewModelTests() =>
        _shell = new ShellViewModel(_dialogs, new ImmediateDispatcher(), _env, new SettingsStore(Path.Combine(_env.DataDirectory, "s.json")));

    public void Dispose()
    {
        _shell.Dispose();
        _env.Dispose();
    }

    [Fact]
    public void Settings_round_trip_through_the_view_model()
    {
        var settings = new TournamentSettings
        {
            Engines = [new EngineSettings { Name = "A", Command = "a.exe", Options = [new("Hash", "64"), new("Threads", "2")] },
                new EngineSettings { Name = "B", Command = "b.exe" }],
            Type = TournamentType.Gauntlet,
            Limit = LimitKind.Nodes,
            Nodes = 5000,
            Sprt = true,
            SprtModel = SprtModel.Logistic,
            PgnNotation = PgnNotation.Uci,
            LogLevel = FastchessLogLevel.Trace,
        };
        var vm = _shell.Tournament;
        vm.Load(settings, importResults: false);
        var back = vm.ToSettings();

        Assert.Equal(settings.Type, back.Type);
        Assert.Equal(settings.Limit, back.Limit);
        Assert.Equal(5000, back.Nodes);
        Assert.Equal(SprtModel.Logistic, back.SprtModel);
        Assert.Equal(PgnNotation.Uci, back.PgnNotation);
        Assert.Equal(FastchessLogLevel.Trace, back.LogLevel);
        Assert.Equal(settings.Engines[0].Options, back.Engines[0].Options);
        Assert.False(vm.ImportResults);
    }

    [Fact]
    public void Preview_and_validation_follow_edits()
    {
        var vm = _shell.Tournament;
        Assert.Contains("At least two engines", vm.ValidationSummary, StringComparison.Ordinal);

        vm.Engines.Add(new EngineViewModel { Name = "Alpha", Command = "alpha.exe" });
        vm.Engines.Add(new EngineViewModel { Command = "beta.exe" });
        Assert.StartsWith("Ready: 20 games", vm.ValidationSummary, StringComparison.Ordinal);

        vm.Engines[1].Name = "Bravo";
        Assert.Contains("name=Bravo", vm.CommandPreview, StringComparison.Ordinal);

        vm.LimitKindIndex = (int)LimitKind.Depth;
        vm.Depth = 12;
        Assert.Contains("depth=12", vm.CommandPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("tc=", vm.CommandPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleared_number_boxes_do_not_become_int_min_value()
    {
        var vm = _shell.Tournament;
        vm.Rounds = double.NaN;
        vm.Nodes = double.NaN;
        vm.SprtElo1 = double.NaN;
        var s = vm.ToSettings();
        Assert.Equal(0, s.Rounds);
        Assert.Equal(0, s.Nodes);
        Assert.Equal(0, s.SprtElo1);
        Assert.Contains("Rounds must be positive", vm.ValidationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_flags_follow_the_selected_kind()
    {
        var vm = _shell.Tournament;
        Assert.True(vm.IsTimeControlLimit);
        vm.LimitKindIndex = (int)LimitKind.Nodes;
        Assert.True(vm.IsNodesLimit);
        Assert.False(vm.IsTimeControlLimit);
    }

    [Fact]
    public void Parsed_events_drive_progress_and_standings()
    {
        var vm = _shell.Tournament;
        vm.Apply(new GameStartedEvent(1, 4, "A", "B"));
        vm.Apply(new GameFinishedEvent(1, "A", "B", "1-0", "White mates"));
        vm.Apply(new GameFinishedEvent(2, "B", "A", "1/2-1/2", "Draw by adjudication"));

        Assert.Equal(2, vm.GamesFinished);
        Assert.Equal(4, vm.GamesTotal);
        Assert.Equal(2, vm.FinishedGames.Count);
        Assert.Equal(2, vm.FinishedGames[0].Number);
        Assert.Equal(["A", "B"], vm.Standings.Select(s => s.Engine));
        Assert.Equal("1.5 / 2", vm.Standings[0].Score);
        Assert.Equal("+1 =1 -0", vm.Standings[0].Wdl);
        Assert.Equal("1", vm.Standings[0].RankText);
        // Command names the XAML binds to (generated by the MVVM Toolkit).
        Assert.NotNull(vm.StartCancelCommand);
        Assert.NotNull(vm.BrowseEngineCommandCommand);
    }

    [Fact]
    public async Task Start_reports_validation_errors_without_running()
    {
        await _shell.Tournament.StartCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Messages, m => m.StartsWith("Cannot start the tournament", StringComparison.Ordinal));
        Assert.False(_shell.Tournament.IsRunning);
    }

    private static string? FakeEngine()
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "fake_uci_engine.py");
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Added_engines_take_the_name_they_report()
    {
        var path = FakeEngine();
        Assert.SkipWhen(path is null, "The fake engine is a Python script and runs only on Linux/macOS.");
        var vm = _shell.Tournament;

        _dialogs.Paths.Enqueue(path);
        await vm.AddEngineCommand.ExecuteAsync(null);
        Assert.Equal("FakeEngine 1.0", vm.Engines[0].Name);
        Assert.Equal("Name reported by the engine.", vm.Engines[0].Status);

        // A second copy gets a distinct name, as fastchess requires.
        _dialogs.Paths.Enqueue(path);
        await vm.AddEngineCommand.ExecuteAsync(null);
        Assert.Equal("FakeEngine 1.0 (2)", vm.Engines[1].Name);
        Assert.Same(vm.Engines[1], vm.SelectedEngine);

        vm.Engines[1].Name = "My build";
        await vm.DetectEngineNameCommand.ExecuteAsync(null);
        Assert.Equal("FakeEngine 1.0 (2)", vm.Engines[1].Name);
    }

    [Fact]
    public async Task Missing_executable_keeps_the_provisional_name()
    {
        var vm = _shell.Tournament;
        _dialogs.Paths.Enqueue(Path.Combine(_env.AppDirectory, "gone", "stockfish-19.exe"));
        await vm.AddEngineCommand.ExecuteAsync(null);
        Assert.Equal("stockfish-19", vm.Engines[0].Name);
        Assert.Equal("Executable not found.", vm.Engines[0].Status);
    }

    [Fact]
    public void Engine_options_parse_name_value_lines()
    {
        var options = EngineViewModel.ParseOptions("Hash=128\r\n# comment\n\nSyzygyPath = C:\\tb\nPonder");
        Assert.Equal([new EngineOption("Hash", "128"), new EngineOption("SyzygyPath", "C:\\tb"), new EngineOption("Ponder", "")], options);
    }
}

public sealed class DatabaseViewModelTests : IDisposable
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly TempEnvironment _env = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ShellViewModel _shell;

    public DatabaseViewModelTests()
    {
        _shell = new ShellViewModel(_dialogs, new ImmediateDispatcher(), _env, new SettingsStore(Path.Combine(_env.DataDirectory, "s.json")));
        _shell.Settings.OpeningsTsvPath = Path.Combine(Data, "lichess-openings.tsv");
    }

    public void Dispose()
    {
        _shell.Dispose();
        _env.Dispose();
    }

    private async Task<DatabaseViewModel> OpenWithSampleAsync()
    {
        var vm = _shell.Database;
        await vm.OpenAsync(Path.Combine(_env.AppDirectory, "games.fcdb"));
        Assert.True(vm.IsOpen);
        var r = await vm.ImportFileAsync(Path.Combine(Data, "sample.pgn"));
        Assert.Equal(new ImportResult(4, 1, 1), r);
        return vm;
    }

    [Fact]
    public async Task Import_search_sort_and_page()
    {
        var vm = await OpenWithSampleAsync();
        Assert.Equal(4, vm.Games.Count);
        Assert.Equal(4, vm.TotalCount);
        Assert.Equal("Page 1 of 1  (4 matching, 4 total)", vm.PageText);

        await vm.SortCommand.ExecuteAsync(nameof(GameSortColumn.White));
        Assert.Equal("Alpha", vm.Games[0].White);
        await vm.SortCommand.ExecuteAsync(nameof(GameSortColumn.White));
        Assert.Equal("René", vm.Games[0].White);

        vm.SearchText = "gamma";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.MatchingCount);
        Assert.All(vm.Games, g => Assert.True(g.White == "Gamma" || g.Black == "Gamma"));
    }

    [Fact]
    public async Task Selecting_one_game_loads_editable_tags()
    {
        var vm = await OpenWithSampleAsync();
        var row = vm.Games.Single(g => g.Id == 1);
        vm.SetSelection([row]);
        await vm.SelectionLoaded;

        Assert.Equal("1 game selected", vm.SelectionText);
        Assert.StartsWith("1. e4 e5 2. Bc4 Nc6 3. Qh5 Nf6 4. Qxf7# 1-0", vm.SelectedMoves, StringComparison.Ordinal);
        var elo = vm.SelectedTags.Single(t => t.Name == "WhiteElo");
        elo.Value = "2700";
        Assert.True(elo.IsDirty);
        await vm.SaveTagsCommand.ExecuteAsync(null);
        Assert.False(elo.IsDirty);
        Assert.Equal("2700", vm.Games.Single(g => g.Id == 1).WhiteElo);
    }

    [Fact]
    public async Task Tools_run_on_the_selected_scope()
    {
        var vm = await OpenWithSampleAsync();
        vm.SetSelection([vm.Games.Single(g => g.Id == 2)]);
        await vm.FillResultsCommand.ExecuteAsync(null);
        Assert.Equal("0-1", vm.Games.Single(g => g.Id == 2).Result);

        vm.ScopeIndex = (int)OperationScope.AllMatchingFilter;
        await vm.ClassifyOpeningsCommand.ExecuteAsync(null);
        Assert.Equal("C23", vm.Games.Single(g => g.Id == 1).Eco);

        var export = Path.Combine(_env.AppDirectory, "out.txt");
        _dialogs.Paths.Enqueue(export);
        vm.ExportFormatIndex = (int)ExportFormat.CrosstableText;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("Crosstable: 4 players", File.ReadAllText(export), StringComparison.Ordinal);
        Assert.Empty(_dialogs.Messages);
    }

    [Fact]
    public async Task Selected_scope_without_selection_asks_the_user()
    {
        var vm = await OpenWithSampleAsync();
        vm.ScopeIndex = (int)OperationScope.SelectedGames;
        await vm.FillResultsCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Messages, m => m.StartsWith("No games selected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_tools_are_reported_not_thrown()
    {
        var vm = await OpenWithSampleAsync();
        vm.ScopeIndex = (int)OperationScope.AllMatchingFilter;
        await vm.AnalyzeCommand.ExecuteAsync(null);
        await vm.ComputeRatingsCommand.ExecuteAsync(null);
        await vm.RunPgnExtractCommand.ExecuteAsync(null);
        Assert.Contains(_dialogs.Messages, m => m.StartsWith("Stockfish not found", StringComparison.Ordinal));
        Assert.Contains(_dialogs.Messages, m => m.StartsWith("Ordo not found", StringComparison.Ordinal));
        Assert.Contains(_dialogs.Messages, m => m.StartsWith("pgn-extract not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        var vm = await OpenWithSampleAsync();
        vm.SetSelection([vm.Games[0], vm.Games[1]]);
        _dialogs.ConfirmResult = false;
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.TotalCount);
        vm.SetSelection([vm.Games[0], vm.Games[1]]);
        _dialogs.ConfirmResult = true;
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.TotalCount);
    }

    [Fact]
    public async Task Settings_persist_including_last_database()
    {
        await OpenWithSampleAsync();
        _shell.Settings.StockfishPath = @"D:\sf\stockfish.exe";
        _shell.SaveSettings();

        using var again = new ShellViewModel(_dialogs, new ImmediateDispatcher(), _env,
            new SettingsStore(Path.Combine(_env.DataDirectory, "s.json")));
        await again.InitializeAsync();
        Assert.Equal(@"D:\sf\stockfish.exe", again.Settings.StockfishPath);
        Assert.True(again.Database.IsOpen);
        Assert.Equal(4, again.Database.TotalCount);
    }
}
