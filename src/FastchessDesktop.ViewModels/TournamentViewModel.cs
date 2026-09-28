using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastchessDesktop.Core.Tools;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

public sealed record FinishedGameRow(int Number, string White, string Black, string Result, string Reason)
{
    public string NumberText => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Pairing => $"{White} - {Black}";
}

public sealed record StandingRow(int Rank, string Engine, int Games, double Points, int Wins, int Draws, int Losses)
{
    public string RankText => Rank.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Score => $"{Points:0.#} / {Games}";
    public string Percent => Games > 0 ? $"{100.0 * Points / Games:0.0}%" : "";
    public string Wdl => $"+{Wins} ={Draws} -{Losses}";
}

/// <summary>The Tournament page: fastchess configuration, run control and live log.</summary>
public sealed partial class TournamentViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppEnvironment _environment;
    private readonly SettingsViewModel _settings;
    private readonly DatabaseViewModel _database;
    private readonly Dictionary<string, (int W, int D, int L)> _scores = new(StringComparer.Ordinal);

    public TournamentViewModel(IDialogService dialogs, IUiDispatcher dispatcher, IAppEnvironment environment,
        SettingsViewModel settings, DatabaseViewModel database)
    {
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _environment = environment;
        _settings = settings;
        _database = database;
        Log = new LogViewModel(dispatcher);
        Engines.CollectionChanged += OnEnginesChanged;
        PropertyChanged += OnAnyPropertyChanged;
        UpdatePreview();
    }

    public LogViewModel Log { get; }

    public ObservableCollection<EngineViewModel> Engines { get; } = [];
    public ObservableCollection<FinishedGameRow> FinishedGames { get; } = [];
    public ObservableCollection<StandingRow> Standings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveEngineCommand), nameof(DuplicateEngineCommand))]
    public partial EngineViewModel? SelectedEngine { get; set; }

    // Schedule. Indices map to the enums in FastchessDesktop.Core.Tools; numbers are double for NumberBox.
    [ObservableProperty] public partial int TournamentTypeIndex { get; set; }
    [ObservableProperty] public partial double Seeds { get; set; } = 1;
    [ObservableProperty] public partial double Rounds { get; set; } = 10;
    [ObservableProperty] public partial double GamesPerEncounter { get; set; } = 2;
    [ObservableProperty] public partial double Concurrency { get; set; } = 1;
    [ObservableProperty] public partial bool ForceConcurrency { get; set; }

    // Limits
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeControlLimit), nameof(IsMoveTimeLimit), nameof(IsNodesLimit), nameof(IsDepthLimit))]
    public partial int LimitKindIndex { get; set; }

    public bool IsTimeControlLimit => LimitKindIndex == (int)LimitKind.TimeControl;
    public bool IsMoveTimeLimit => LimitKindIndex == (int)LimitKind.FixedTimePerMove;
    public bool IsNodesLimit => LimitKindIndex == (int)LimitKind.Nodes;
    public bool IsDepthLimit => LimitKindIndex == (int)LimitKind.Depth;
    [ObservableProperty] public partial string TimeControl { get; set; } = "10+0.1";
    [ObservableProperty] public partial double MoveTimeSeconds { get; set; } = 1;
    [ObservableProperty] public partial double Nodes { get; set; } = 100_000;
    [ObservableProperty] public partial double Depth { get; set; } = 10;
    [ObservableProperty] public partial double TimeMarginMs { get; set; }
    [ObservableProperty] public partial double Threads { get; set; } = 1;
    [ObservableProperty] public partial double HashMb { get; set; } = 16;

    // Openings
    [ObservableProperty] public partial string OpeningsFile { get; set; } = "";
    [ObservableProperty] public partial int OpeningsFormatIndex { get; set; }
    [ObservableProperty] public partial int OpeningsOrderIndex { get; set; } = 1;
    [ObservableProperty] public partial double OpeningPlies { get; set; }
    [ObservableProperty] public partial double OpeningStart { get; set; } = 1;
    [ObservableProperty] public partial string Srand { get; set; } = "";

    // Adjudication
    [ObservableProperty] public partial bool DrawAdjudication { get; set; }
    [ObservableProperty] public partial double DrawMoveNumber { get; set; } = 40;
    [ObservableProperty] public partial double DrawMoveCount { get; set; } = 8;
    [ObservableProperty] public partial double DrawScore { get; set; } = 10;
    [ObservableProperty] public partial bool ResignAdjudication { get; set; }
    [ObservableProperty] public partial double ResignMoveCount { get; set; } = 3;
    [ObservableProperty] public partial double ResignScore { get; set; } = 600;
    [ObservableProperty] public partial bool ResignTwoSided { get; set; }
    [ObservableProperty] public partial double MaxMoves { get; set; }
    [ObservableProperty] public partial string TablebasePaths { get; set; } = "";
    [ObservableProperty] public partial double TablebasePieces { get; set; }

    // SPRT
    [ObservableProperty] public partial bool Sprt { get; set; }
    [ObservableProperty] public partial double SprtElo0 { get; set; }
    [ObservableProperty] public partial double SprtElo1 { get; set; } = 2;
    [ObservableProperty] public partial double SprtAlpha { get; set; } = 0.05;
    [ObservableProperty] public partial double SprtBeta { get; set; } = 0.05;
    [ObservableProperty] public partial int SprtModelIndex { get; set; }

    // Output
    [ObservableProperty] public partial string Event { get; set; } = "fastchess-desktop tournament";
    [ObservableProperty] public partial string Site { get; set; } = "";
    [ObservableProperty] public partial string PgnOut { get; set; } = "";
    [ObservableProperty] public partial int PgnNotationIndex { get; set; }
    [ObservableProperty] public partial bool PgnAppend { get; set; } = true;
    [ObservableProperty] public partial bool PgnMinimal { get; set; }
    [ObservableProperty] public partial bool PgnSearchInfo { get; set; }
    [ObservableProperty] public partial string EpdOut { get; set; } = "";
    [ObservableProperty] public partial bool Recover { get; set; } = true;
    [ObservableProperty] public partial double AutosaveInterval { get; set; } = 20;
    [ObservableProperty] public partial string LogFile { get; set; } = "";
    [ObservableProperty] public partial int LogLevelIndex { get; set; } = 2;
    [ObservableProperty] public partial bool LogEngineTraffic { get; set; }
    [ObservableProperty] public partial bool CutechessOutput { get; set; }
    [ObservableProperty] public partial double RatingInterval { get; set; } = 10;
    [ObservableProperty] public partial bool ReportPenta { get; set; } = true;
    [ObservableProperty] public partial string ExtraArguments { get; set; } = "";
    [ObservableProperty] public partial bool ImportResults { get; set; } = true;

    // Run state
    [ObservableProperty] public partial string CommandPreview { get; set; } = "";
    [ObservableProperty] public partial string ValidationSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double GamesFinished { get; set; }
    [ObservableProperty] public partial double GamesTotal { get; set; } = 1;
    [ObservableProperty] public partial string ProgressText { get; set; } = "Idle";

    public bool IsIdle => !IsRunning;

    // NumberBox reports NaN when cleared; treat that as 0 rather than int.MinValue.
    private static double Number(double v) => double.IsNaN(v) ? 0 : v;
    private static int Whole(double v) => double.IsNaN(v) ? 0 : (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue);

    public TournamentSettings ToSettings() => new()
    {
        Engines = [.. Engines.Select(e => e.ToSettings())],
        Type = (TournamentType)TournamentTypeIndex,
        Seeds = Whole(Seeds),
        Rounds = Whole(Rounds),
        GamesPerEncounter = Whole(GamesPerEncounter),
        Concurrency = Whole(Concurrency),
        ForceConcurrency = ForceConcurrency,
        Limit = (LimitKind)LimitKindIndex,
        TimeControl = TimeControl,
        MoveTimeSeconds = Number(MoveTimeSeconds),
        Nodes = double.IsNaN(Nodes) ? 0 : (long)Math.Round(Nodes),
        Depth = Whole(Depth),
        TimeMarginMs = Whole(TimeMarginMs),
        Threads = Whole(Threads),
        HashMb = Whole(HashMb),
        OpeningsFile = OpeningsFile.Trim(),
        OpeningsFormat = (OpeningFormat)OpeningsFormatIndex,
        OpeningsOrder = (OpeningOrder)OpeningsOrderIndex,
        OpeningPlies = Whole(OpeningPlies),
        OpeningStart = Whole(OpeningStart),
        Srand = Srand,
        DrawAdjudication = DrawAdjudication,
        DrawMoveNumber = Whole(DrawMoveNumber),
        DrawMoveCount = Whole(DrawMoveCount),
        DrawScore = Whole(DrawScore),
        ResignAdjudication = ResignAdjudication,
        ResignMoveCount = Whole(ResignMoveCount),
        ResignScore = Whole(ResignScore),
        ResignTwoSided = ResignTwoSided,
        MaxMoves = Whole(MaxMoves),
        TablebasePaths = TablebasePaths,
        TablebasePieces = Whole(TablebasePieces),
        Sprt = Sprt,
        SprtElo0 = Number(SprtElo0),
        SprtElo1 = Number(SprtElo1),
        SprtAlpha = Number(SprtAlpha),
        SprtBeta = Number(SprtBeta),
        SprtModel = (SprtModel)SprtModelIndex,
        Event = Event,
        Site = Site,
        PgnOut = PgnOut.Trim(),
        PgnNotation = (PgnNotation)PgnNotationIndex,
        PgnAppend = PgnAppend,
        PgnMinimal = PgnMinimal,
        PgnSearchInfo = PgnSearchInfo,
        EpdOut = EpdOut.Trim(),
        Recover = Recover,
        AutosaveInterval = Whole(AutosaveInterval),
        LogFile = LogFile.Trim(),
        LogLevel = (FastchessLogLevel)LogLevelIndex,
        LogEngineTraffic = LogEngineTraffic,
        CutechessOutput = CutechessOutput,
        RatingInterval = Whole(RatingInterval),
        ReportPenta = ReportPenta,
        ExtraArguments = ExtraArguments,
    };

    public void Load(TournamentSettings s, bool importResults)
    {
        Engines.Clear();
        foreach (var e in s.Engines) Engines.Add(EngineViewModel.From(e));
        SelectedEngine = Engines.FirstOrDefault();
        TournamentTypeIndex = (int)s.Type;
        Seeds = s.Seeds;
        Rounds = s.Rounds;
        GamesPerEncounter = s.GamesPerEncounter;
        Concurrency = s.Concurrency;
        ForceConcurrency = s.ForceConcurrency;
        LimitKindIndex = (int)s.Limit;
        TimeControl = s.TimeControl;
        MoveTimeSeconds = s.MoveTimeSeconds;
        Nodes = s.Nodes;
        Depth = s.Depth;
        TimeMarginMs = s.TimeMarginMs;
        Threads = s.Threads;
        HashMb = s.HashMb;
        OpeningsFile = s.OpeningsFile;
        OpeningsFormatIndex = (int)s.OpeningsFormat;
        OpeningsOrderIndex = (int)s.OpeningsOrder;
        OpeningPlies = s.OpeningPlies;
        OpeningStart = s.OpeningStart;
        Srand = s.Srand;
        DrawAdjudication = s.DrawAdjudication;
        DrawMoveNumber = s.DrawMoveNumber;
        DrawMoveCount = s.DrawMoveCount;
        DrawScore = s.DrawScore;
        ResignAdjudication = s.ResignAdjudication;
        ResignMoveCount = s.ResignMoveCount;
        ResignScore = s.ResignScore;
        ResignTwoSided = s.ResignTwoSided;
        MaxMoves = s.MaxMoves;
        TablebasePaths = s.TablebasePaths;
        TablebasePieces = s.TablebasePieces;
        Sprt = s.Sprt;
        SprtElo0 = s.SprtElo0;
        SprtElo1 = s.SprtElo1;
        SprtAlpha = s.SprtAlpha;
        SprtBeta = s.SprtBeta;
        SprtModelIndex = (int)s.SprtModel;
        Event = s.Event;
        Site = s.Site;
        PgnOut = s.PgnOut;
        PgnNotationIndex = (int)s.PgnNotation;
        PgnAppend = s.PgnAppend;
        PgnMinimal = s.PgnMinimal;
        PgnSearchInfo = s.PgnSearchInfo;
        EpdOut = s.EpdOut;
        Recover = s.Recover;
        AutosaveInterval = s.AutosaveInterval;
        LogFile = s.LogFile;
        LogLevelIndex = (int)s.LogLevel;
        LogEngineTraffic = s.LogEngineTraffic;
        CutechessOutput = s.CutechessOutput;
        RatingInterval = s.RatingInterval;
        ReportPenta = s.ReportPenta;
        ExtraArguments = s.ExtraArguments;
        ImportResults = importResults;
        UpdatePreview();
    }

    [RelayCommand]
    private async Task AddEngineAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Executable);
        if (path is null) return;
        var engine = new EngineViewModel { Command = path };
        Engines.Add(engine);
        SelectedEngine = engine;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedEngine))]
    private void RemoveEngine()
    {
        if (SelectedEngine is null) return;
        var index = Engines.IndexOf(SelectedEngine);
        Engines.Remove(SelectedEngine);
        SelectedEngine = Engines.Count == 0 ? null : Engines[Math.Min(index, Engines.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelectedEngine))]
    private void DuplicateEngine()
    {
        if (SelectedEngine is null) return;
        var copy = EngineViewModel.From(SelectedEngine.ToSettings());
        copy.Name = SelectedEngine.DisplayName + " (copy)";
        Engines.Add(copy);
        SelectedEngine = copy;
    }

    private bool HasSelectedEngine() => SelectedEngine is not null;

    [RelayCommand]
    private async Task BrowseEngineCommandAsync()
    {
        if (SelectedEngine is null) return;
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Executable);
        if (path is not null) SelectedEngine.Command = path;
    }

    [RelayCommand]
    private async Task BrowseOpeningsAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Openings);
        if (path is null) return;
        OpeningsFile = path;
        OpeningsFormatIndex = path.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)
            ? (int)OpeningFormat.Pgn
            : (int)OpeningFormat.Epd;
    }

    [RelayCommand]
    private async Task BrowsePgnOutAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("games.pgn", FileFilters.Pgn);
        if (path is not null) PgnOut = path;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = ToSettings();
        var errors = FastchessCommandBuilder.Validate(settings);
        if (errors.Count > 0)
        {
            await _dialogs.ShowMessageAsync("Cannot start the tournament", string.Join(Environment.NewLine, errors));
            return;
        }
        var fastchess = _settings.EffectiveTools.Fastchess;
        if (!File.Exists(fastchess))
        {
            await _dialogs.ShowMessageAsync("fastchess not found",
                "Set the fastchess executable in Settings. Looked for: " + (fastchess.Length > 0 ? fastchess : "(not configured)"));
            return;
        }

        // Each run gets its own working directory for fastchess's state file and default outputs.
        var runDir = Path.Combine(_environment.DataDirectory, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(runDir);
        if (string.IsNullOrWhiteSpace(settings.PgnOut)) settings = settings with { PgnOut = Path.Combine(runDir, "games.pgn") };

        var spec = new ProcessSpec(fastchess, FastchessCommandBuilder.Build(settings), runDir);
        FinishedGames.Clear();
        Standings.Clear();
        _scores.Clear();
        GamesFinished = 0;
        GamesTotal = settings.ExpectedGames ?? 1;
        ProgressText = "Starting...";
        IsRunning = true;
        Log.Add(spec.DisplayCommand, LogKind.Command);
        Log.Add("Working directory: " + runDir);

        try
        {
            var result = await ProcessRunner.RunAsync(spec, OnOutputLine, cancellationToken);
            if (result.Cancelled)
            {
                var state = string.IsNullOrWhiteSpace(settings.StateFile) ? Path.Combine(runDir, "config.json") : settings.StateFile;
                Log.Add($"Stopped by user after {result.Duration:hh\\:mm\\:ss}. To resume, add " +
                        $"-config file={CommandLine.Quote(state)} to Extra arguments.", LogKind.Error);
            }
            else
            {
                Log.Add($"fastchess exited with code {result.ExitCode} after {result.Duration:hh\\:mm\\:ss}.",
                    result.ExitCode == 0 ? LogKind.Success : LogKind.Error);
            }
            ProgressText = result.Cancelled ? "Stopped" : result.ExitCode == 0 ? "Finished" : $"Failed (exit code {result.ExitCode})";

            if (ImportResults && File.Exists(settings.PgnOut))
            {
                Log.Add("Importing " + settings.PgnOut + " into the database...");
                var imported = await _database.ImportFileAsync(settings.PgnOut);
                if (imported is { } r)
                    Log.Add($"Imported {r.Imported} games ({r.Duplicates} duplicates skipped, {r.Failed} failed).", LogKind.Success);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error(e.Message);
            ProgressText = "Failed";
            await _dialogs.ShowMessageAsync("Tournament failed", e.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnOutputLine(OutputLine line)
    {
        Log.Add(line.Text, line.Stream == OutputStream.StandardError ? LogKind.Error : LogKind.Output);
        var evt = FastchessOutputParser.Parse(line.Text);
        if (evt is not null) _dispatcher.Post(() => Apply(evt));
    }

    /// <summary>Updates progress and live standings from a parsed fastchess line. UI thread only.</summary>
    public void Apply(FastchessEvent evt)
    {
        switch (evt)
        {
            case GameStartedEvent s:
                GamesTotal = Math.Max(1, s.Total);
                ProgressText = $"Playing game {s.Number} of {s.Total}: {s.White} - {s.Black}";
                break;
            case GameFinishedEvent f:
                GamesFinished++;
                FinishedGames.Insert(0, new FinishedGameRow(f.Number, f.White, f.Black, f.Result, f.Reason));
                while (FinishedGames.Count > 500) FinishedGames.RemoveAt(FinishedGames.Count - 1);
                Score(f.White, f.Result switch { "1-0" => 1, "0-1" => -1, "1/2-1/2" => 0, _ => (int?)null });
                Score(f.Black, f.Result switch { "1-0" => -1, "0-1" => 1, "1/2-1/2" => 0, _ => (int?)null });
                RebuildStandings();
                ProgressText = $"{GamesFinished:0} of {GamesTotal:0} games finished";
                break;
            case TournamentFinishedEvent t:
                ProgressText = t.Message;
                break;
        }
    }

    private void Score(string engine, int? outcome)
    {
        if (outcome is null) return;
        _scores.TryGetValue(engine, out var s);
        _scores[engine] = outcome switch { 1 => (s.W + 1, s.D, s.L), 0 => (s.W, s.D + 1, s.L), _ => (s.W, s.D, s.L + 1) };
    }

    private void RebuildStandings()
    {
        Standings.Clear();
        var rank = 0;
        foreach (var (name, s) in _scores.OrderByDescending(kv => kv.Value.W + 0.5 * kv.Value.D)
                     .ThenBy(kv => kv.Value.W + kv.Value.D + kv.Value.L).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            Standings.Add(new StandingRow(++rank, name, s.W + s.D + s.L, s.W + 0.5 * s.D, s.W, s.D, s.L));
    }

    [RelayCommand]
    private void UpdatePreview()
    {
        var settings = ToSettings();
        var errors = FastchessCommandBuilder.Validate(settings);
        ValidationSummary = errors.Count == 0
            ? (settings.ExpectedGames is { } n ? $"Ready: {n} games scheduled." : "Ready.")
            : string.Join(Environment.NewLine, errors);
        var exe = _settings.EffectiveTools.Fastchess;
        CommandPreview = CommandLine.Format([exe.Length > 0 ? exe : "fastchess.exe", .. FastchessCommandBuilder.Build(settings)]);
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommandPreview) or nameof(ValidationSummary) or nameof(IsRunning) or nameof(IsIdle)
            or nameof(GamesFinished) or nameof(GamesTotal) or nameof(ProgressText) or nameof(SelectedEngine))
            return;
        UpdatePreview();
    }

    private void OnEnginesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (EngineViewModel engine in e.NewItems ?? Array.Empty<EngineViewModel>())
            engine.PropertyChanged += OnEngineChanged;
        foreach (EngineViewModel engine in e.OldItems ?? Array.Empty<EngineViewModel>())
            engine.PropertyChanged -= OnEngineChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var engine in Engines) engine.PropertyChanged += OnEngineChanged;
        UpdatePreview();
    }

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e) => UpdatePreview();
}
