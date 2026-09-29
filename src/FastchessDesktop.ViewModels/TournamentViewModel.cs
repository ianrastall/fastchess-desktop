using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Native;
using FastchessDesktop.Core.Tools;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

/// <summary>The Tournament page: fastchess configuration, run control and live log.</summary>
public sealed partial class TournamentViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppEnvironment _environment;
    private readonly SettingsViewModel _settings;
    private readonly DatabaseViewModel _database;
    private readonly Dictionary<string, StandingRow> _standingRows = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DateTime> _gameStarts = [];
    private TournamentScoreboard _scoreboard = new(2, pentanomial: true);
    private SprtTest? _sprt;
    private int _gamesPerEncounter = 2;

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
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.RatingListPath)) UpdateRatings();
        };
        UpdatePreview();
    }

    public void Dispose() => _ratingList?.Dispose();

    public LogViewModel Log { get; }

    public ObservableCollection<EngineViewModel> Engines { get; } = [];
    public FinishedGamesTable FinishedGames { get; } = new();
    public StandingsTable Standings { get; } = new();

    // Head-to-head summary, shown when exactly two engines play (the figures fastchess reports).
    [ObservableProperty] public partial bool HasMatchSummary { get; private set; }
    [ObservableProperty] public partial string MatchTitle { get; private set; } = "";
    [ObservableProperty] public partial string MatchElo { get; private set; } = "";
    [ObservableProperty] public partial string MatchNElo { get; private set; } = "";
    [ObservableProperty] public partial string MatchLos { get; private set; } = "";
    [ObservableProperty] public partial string MatchGames { get; private set; } = "";
    [ObservableProperty] public partial string MatchDrawRatio { get; private set; } = "";
    [ObservableProperty] public partial string MatchPtnml { get; private set; } = "";

    [ObservableProperty] public partial bool HasSprt { get; private set; }
    [ObservableProperty] public partial string SprtLlr { get; private set; } = "";
    [ObservableProperty] public partial string SprtBounds { get; private set; } = "";
    [ObservableProperty] public partial string SprtHypotheses { get; private set; } = "";
    [ObservableProperty] public partial string SprtStatus { get; private set; } = "";

    /// <summary>Progress toward the nearer SPRT bound in percent (0 to 100), for a progress bar.</summary>
    [ObservableProperty] public partial double SprtProgress { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveEngineCommand), nameof(DuplicateEngineCommand), nameof(DetectEngineNameCommand))]
    public partial EngineViewModel? SelectedEngine { get; set; }

    // Schedule. Indices map to the enums in FastchessDesktop.Core.Tools; numbers are double for NumberBox.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGauntlet), nameof(IsSwiss), nameof(IsKnockout), nameof(FormatDescription))]
    public partial int TournamentTypeIndex { get; set; }

    [ObservableProperty] public partial double Seeds { get; set; } = 1;
    [ObservableProperty] public partial double SwissRounds { get; set; } = 5;
    [ObservableProperty] public partial double KnockoutTiebreakPairs { get; set; } = 2;

    public bool IsGauntlet => TournamentTypeIndex == (int)TournamentType.Gauntlet;
    public bool IsSwiss => TournamentTypeIndex == (int)TournamentType.Swiss;
    public bool IsKnockout => TournamentTypeIndex == (int)TournamentType.Knockout;

    /// <summary>How the selected format is played, shown under the format choice.</summary>
    public string FormatDescription => (TournamentType)TournamentTypeIndex switch
    {
        TournamentType.Gauntlet => "The first N engines (seeds) play every other engine; the others do not meet.",
        TournamentType.Pyramid => "Engines join in list order; each newcomer plays every engine listed above it. " +
                                  "Run as one fastchess gauntlet per newcomer.",
        TournamentType.Knockout => "Single elimination seeded by list order (top seeds get byes). Each match is " +
                                   "'Rounds' game pairs; ties play tiebreak pairs, then the higher seed advances.",
        TournamentType.Swiss => "Each round pairs engines with similar scores that have not met; an odd engine out " +
                                "gets a bye worth a full win. Ranked by score, then Buchholz. Each pairing plays 'Rounds' game pairs.",
        _ => "Every engine plays every other engine.",
    };
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
    [NotifyCanExecuteChangedFor(nameof(ImportLastGamesCommand))]
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
        SwissRounds = Whole(SwissRounds),
        KnockoutTiebreakPairs = Whole(KnockoutTiebreakPairs),
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
        SwissRounds = s.SwissRounds;
        KnockoutTiebreakPairs = s.KnockoutTiebreakPairs;
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

    /// <summary>Adds an engine: the file name is shown at once, then replaced by the name the engine reports over UCI.</summary>
    [RelayCommand]
    private async Task AddEngineAsync()
    {
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Executable);
        if (path is null) return;
        var engine = new EngineViewModel { Command = path };
        engine.Name = UniqueName(Path.GetFileNameWithoutExtension(path), engine);
        Engines.Add(engine);
        SelectedEngine = engine;
        await DetectNameAsync(engine, keepUserEdits: true);
        PlaceByRating(engine);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedEngine))]
    private Task DetectEngineNameAsync() =>
        SelectedEngine is { } engine ? DetectNameAsync(engine, keepUserEdits: false) : Task.CompletedTask;

    /// <summary>
    /// Starts the engine briefly and reads its UCI "id name". With keepUserEdits, a name typed while
    /// the engine was starting is left alone.
    /// </summary>
    private async Task DetectNameAsync(EngineViewModel engine, bool keepUserEdits)
    {
        var provisional = engine.Name;
        if (!File.Exists(engine.Command))
        {
            engine.Status = "Executable not found.";
            return;
        }
        engine.Status = "Asking the engine for its name...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var uci = await UciEngine.StartAsync(engine.Command, new Dictionary<string, string>(), cts.Token);
            var reported = uci.Name.Trim();
            if (reported.Length == 0)
            {
                engine.Status = "The engine did not report a name.";
            }
            else if (keepUserEdits && engine.Name != provisional)
            {
                engine.Status = $"The engine reports \"{reported}\"; your name was kept.";
            }
            else
            {
                engine.Name = UniqueName(reported, engine);
                engine.Status = "Name reported by the engine.";
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
                                      or OperationCanceledException or FastchessDesktop.Core.Native.FcdException)
        {
            // FcdException: the file exists but could not be started (for example, not an executable).
            engine.Status = "Could not read the name from the engine: " + e.Message;
        }
    }

    /// <summary>fastchess needs distinct names; appends " (2)", " (3)" ... when another engine already uses one.</summary>
    private string UniqueName(string name, EngineViewModel self)
    {
        bool Taken(string n) => Engines.Any(e => !ReferenceEquals(e, self) &&
                                                 string.Equals(e.DisplayName, n, StringComparison.Ordinal));
        if (!Taken(name)) return name;
        for (var i = 2; ; i++)
            if (!Taken($"{name} ({i})")) return $"{name} ({i})";
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
        if (SelectedEngine is not { } engine) return;
        var path = await _dialogs.PickOpenFileAsync(FileFilters.Executable);
        if (path is null) return;
        engine.Command = path;
        engine.Name = UniqueName(Path.GetFileNameWithoutExtension(path), engine);
        await DetectNameAsync(engine, keepUserEdits: true);
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

        ResetResults(settings);
        _outputWarnings = 0;
        _engineFailures = 0;
        _warningFile = null;
        GamesFinished = 0;
        GamesTotal = settings.ExpectedGames ?? 1;
        ProgressText = "Starting...";
        IsRunning = true;
        Log.Add("Working directory: " + runDir);
        var staged = !TournamentFormats.IsRunByFastchess(settings.Type);

        // Stop cancels on the UI thread; say so at once, since stopping the process tree and
        // collecting its last output can take a moment.
        var stopping = cancellationToken.Register(() =>
        {
            Log.Add("Stop requested: stopping fastchess and its engines...", LogKind.Warning);
            ProgressText = "Stopping...";
        });

        TournamentOutcome result;
        try
        {
            result = await new TournamentRunner().RunAsync(fastchess, runDir, settings, OnOutputLine, OnEvent, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IsRunning = false;
            Log.Error(e.Message);
            ProgressText = "Failed";
            await _dialogs.ShowMessageAsync("Tournament failed", e.Message);
            return;
        }
        finally
        {
            // The run is over once fastchess has exited: the settings unlock before the import below,
            // which can take a while (a large database, or one busy with another job).
            stopping.Dispose();
            IsRunning = false;
        }

        ReportRunEnd(settings, result, runDir, staged);
        LastPgnPath = File.Exists(settings.PgnOut) ? settings.PgnOut : "";
        if (ImportResults && LastPgnPath.Length > 0) await ImportLastGamesAsync();
    }

    private void ReportRunEnd(TournamentSettings settings, TournamentOutcome result, string runDir, bool staged)
    {
        if (result.Cancelled && staged)
        {
            Log.Add($"Stopped by user after {result.Duration:hh\\:mm\\:ss}. {settings.Type} tournaments cannot be resumed; " +
                    "the games played so far are in the PGN file.", LogKind.Error);
        }
        else if (result.Cancelled)
        {
            var state = string.IsNullOrWhiteSpace(settings.StateFile) ? Path.Combine(runDir, "config.json") : settings.StateFile;
            Log.Add($"Stopped by user after {result.Duration:hh\\:mm\\:ss}. To resume, add " +
                    $"-config file={CommandLine.Quote(state)} to Extra arguments.", LogKind.Error);
        }
        else
        {
            Log.Add(staged && result.ExitCode == 0
                    ? $"{settings.Type} tournament completed after {result.Duration:hh\\:mm\\:ss}."
                    : $"fastchess exited with code {result.ExitCode} after {result.Duration:hh\\:mm\\:ss}.",
                result.ExitCode == 0 ? LogKind.Success : LogKind.Error);
        }
        ProgressText = result.Cancelled ? "Stopped" : result.ExitCode == 0 ? "Finished" : $"Failed (exit code {result.ExitCode})";
        ReportOutputChecks(completed: !result.Cancelled && result.ExitCode == 0);
    }

    /// <summary>PGN file of the last run, for importing it later.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportLastGamesCommand))]
    public partial string LastPgnPath { get; private set; } = "";

    /// <summary>
    /// Imports the last run's PGN into the open database, or into the default database when none
    /// is open. Games already in the database are skipped when Skip duplicates is on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportLastGames))]
    private async Task ImportLastGamesAsync()
    {
        if (!File.Exists(LastPgnPath))
        {
            Log.Error("The PGN file of the last run no longer exists: " + LastPgnPath);
            return;
        }
        if (!await _database.EnsureOpenAsync())
        {
            Log.Error("No database could be opened, so the games were not imported. The Database page log has the reason.");
            return;
        }
        Log.Add($"Importing {LastPgnPath} into {_database.Title}...");
        if (_database.IsBusy)
            Log.Add($"The database is busy ({_database.BusyText}); the games are imported when that finishes.", LogKind.Warning);
        try
        {
            var imported = await _database.ImportFileAsync(LastPgnPath);
            if (imported is { } r)
                Log.Add($"Imported {r.Imported} games into {_database.Title} ({r.Duplicates} duplicates skipped, {r.Failed} unreadable). " +
                        "They are on the Database page.", LogKind.Success);
            else
                Log.Add("The import did not finish. The Database page log has the reason.", LogKind.Error);
        }
        catch (Exception e) when (e is FastchessDesktop.Core.Native.FcdException or IOException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            // Raised by the import that runs beside another database job; it must not escape the command.
            Log.Error("The games were not imported: " + e.Message);
        }
    }

    private bool CanImportLastGames() => LastPgnPath.Length > 0 && !IsRunning;

    private int _outputWarnings;
    private int _engineFailures;
    private volatile string? _warningFile;

    /// <summary>
    /// Output lines from the runner. Engine warnings do not arrive here (they are EngineWarningEvents);
    /// a "Warning;" line that does, such as a failed CPU affinity, is about fastchess itself.
    /// </summary>
    private void OnOutputLine(OutputLine line)
    {
        var (kind, evt, _) = FastchessOutputParser.Analyze(line.Text);
        if (kind == FastchessLineKind.EngineFailure && evt is GameFinishedEvent { IsEngineFailure: true })
            Interlocked.Increment(ref _engineFailures);
        Log.Add(line.Text, kind switch
        {
            FastchessLineKind.EngineFailure => LogKind.Error,
            FastchessLineKind.Warning => LogKind.Warning,
            _ => line.Stream == OutputStream.StandardError ? LogKind.Error : LogKind.Output,
        });
    }

    /// <summary>Explains the fastchess warnings and failures seen during the run, so a clean run reads as clean.</summary>
    private void ReportOutputChecks(bool completed)
    {
        var failures = Interlocked.Exchange(ref _engineFailures, 0);
        var warnings = Interlocked.Exchange(ref _outputWarnings, 0);
        if (failures > 0)
            Log.Add($"{failures} game(s) ended by an engine failure (time loss, crash, stalled connection or illegal move). " +
                    "Those lines are shown in red.", LogKind.Error);
        else if (completed)
            Log.Add("No game ended by an engine failure (time loss, crash, stalled connection or illegal move).", LogKind.Success);
        if (warnings > 0)
            Log.Add($"fastchess flagged {warnings} engine search output(s) (see the Warnings column), for example a best " +
                    "move that is not the first move of the engine's last reported PV. These are checks on what the engines " +
                    $"print; they do not change moves or results. Details: {_warningFile}", LogKind.Warning);
    }

    /// <summary>Runner events arrive on a background thread: log lines now (keeps their order), UI state on the UI thread.</summary>
    private void OnEvent(FastchessEvent evt)
    {
        switch (evt)
        {
            case CommandStartedEvent c:
                Log.Add(c.CommandLine, LogKind.Command);
                break;
            case StageStartedEvent s:
                Log.Add($"Stage {s.Stage}: {s.Description}");
                break;
            case TournamentNoteEvent n:
                Log.Add(n.Message, LogKind.Success);
                break;
            case EngineWarningEvent w:
                Interlocked.Increment(ref _outputWarnings);
                _warningFile = w.File;
                // Only the first of each kind per engine is logged; an engine with a habit repeats it every game.
                if (w.Count == 1)
                    Log.Add($"{w.Engine}: {w.Message}. fastchess checks what each engine prints while it searches; this " +
                            "comes from the engine itself and does not change moves or results. Further occurrences " +
                            "are not shown here: they are counted in the Warnings column and written, with the position " +
                            $"and moves, to {w.File}", LogKind.Warning);
                break;
        }
        _dispatcher.Post(() => Apply(evt));
    }

    /// <summary>Updates progress and live standings from a parsed fastchess line. UI thread only.</summary>
    public void Apply(FastchessEvent evt)
    {
        switch (evt)
        {
            case GameStartedEvent s:
                GamesTotal = Math.Max(1, s.Total);
                _gameStarts[s.Number] = DateTime.Now;
                ProgressText = $"Playing game {s.Number} of {s.Total}: {s.White} - {s.Black}";
                break;
            case GameFinishedEvent f:
                GamesFinished++;
                _scoreboard.AddGame(f.Number, f.White, f.Black, f.Result, f.Reason);
                DateTime? started = _gameStarts.Remove(f.Number, out var startTime) ? startTime : null;
                FinishedGames.Add(new FinishedGameRow(f.Number, (f.Number - 1) / _gamesPerEncounter + 1, f.White, f.Black,
                    f.Result, f.Reason, f.IsEngineFailure, started, DateTime.Now));
                UpdateStandings();
                ProgressText = $"{GamesFinished:0} of {GamesTotal:0} games finished";
                break;
            case StageStartedEvent s:
                ProgressText = $"Stage {s.Stage}: {s.Description}";
                break;
            case TournamentFinishedEvent t:
                ProgressText = t.Message;
                break;
            case EngineWarningEvent w:
                _scoreboard.AddWarning(w.Engine);
                UpdateStandings();
                break;
        }
    }

    /// <summary>Clears the result tables and sets up statistics the way fastchess computes them for these settings.</summary>
    private void ResetResults(TournamentSettings settings)
    {
        _gamesPerEncounter = Math.Max(1, settings.GamesPerEncounter);
        // fastchess reports pentanomial statistics only with -games 2, its own output format and a non-Bayesian SPRT.
        var pentanomial = settings.ReportPenta && !settings.CutechessOutput &&
                          !(settings.Sprt && settings.SprtModel == SprtModel.Bayesian);
        _scoreboard.Dispose();
        _scoreboard = new TournamentScoreboard(_gamesPerEncounter, pentanomial);
        foreach (var e in settings.Engines) _scoreboard.AddEngine(FastchessCommandBuilder.EngineName(e));
        _sprt = settings.Sprt && settings.Engines.Count == 2
            ? new SprtTest(settings.SprtAlpha, settings.SprtBeta, settings.SprtElo0, settings.SprtElo1, settings.SprtModel)
            : null;
        _gameStarts.Clear();
        _standingRows.Clear();
        Standings.Clear();
        FinishedGames.Clear();
        UpdateStandings();
    }

    /// <summary>Recomputes every engine's row in place, ranks by Elo as fastchess does, and restores the sort.</summary>
    private void UpdateStandings()
    {
        foreach (var engine in _scoreboard.Engines)
        {
            if (!_standingRows.TryGetValue(engine, out var row))
            {
                row = new StandingRow(engine);
                _standingRows[engine] = row;
                Standings.Rows.Add(row);
            }
            row.Update(_scoreboard.StatsOf(engine), _scoreboard.EloOf(engine), _scoreboard.Pentanomial,
                _scoreboard.Failures(engine), _scoreboard.Warnings(engine));
        }
        var rank = 0;
        foreach (var row in _standingRows.Values
                     .OrderByDescending(r => double.IsNaN(r.Elo) ? double.NegativeInfinity : r.Elo)
                     .ThenByDescending(r => r.Points).ThenBy(r => r.Games).ThenBy(r => r.Engine, StringComparer.Ordinal))
            row.SetRank(++rank);
        Standings.Refresh();
        UpdateMatchSummary();
    }

    private void UpdateMatchSummary()
    {
        var engines = _scoreboard.Engines;
        HasMatchSummary = engines.Count == 2;
        HasSprt = HasMatchSummary && _sprt is not null;
        if (!HasMatchSummary) return;

        var (first, second) = (engines[0], engines[1]);
        var s = _scoreboard.HeadToHead(first, second);
        var elo = _scoreboard.Estimate(s);
        var penta = _scoreboard.Pentanomial;
        MatchTitle = $"{first} vs {second}";
        MatchElo = elo is null ? "-" : $"{TableFormat.Signed(elo.Elo, "0.00")} +/- {TableFormat.Number(elo.Error, "0.00")}";
        MatchNElo = elo is null ? "-" : $"{TableFormat.Signed(elo.NElo, "0.00")} +/- {TableFormat.Number(elo.NEloError, "0.00")}";
        MatchLos = elo is null ? "-" : TableFormat.Percent(elo.Los);
        MatchGames = $"{s.Games} games: +{s.Wins} ={s.Draws} -{s.Losses}, {TableFormat.Number(s.Points, "0.0")} points" +
                     (s.Games > 0 ? $" ({TableFormat.Percent(100.0 * s.Points / s.Games)})" : "");
        // As in fastchess: with pentanomial reporting the draw ratio counts drawn pairs (WL or DD).
        MatchDrawRatio = penta
            ? (s.Pairs > 0 ? TableFormat.Percent(100.0 * (s.WL + s.DD) / s.Pairs) : "-")
            : (s.Games > 0 ? TableFormat.Percent(100.0 * s.Draws / s.Games) : "-");
        MatchPtnml = penta
            ? $"[{s.LL}, {s.LD}, {s.WL + s.DD}, {s.WD}, {s.WW}], pairs ratio " +
              $"{TableFormat.Number((double)(s.WW + s.WD) / (s.LD + s.LL), "0.00")}, WL/DD {TableFormat.Number((double)s.WL / s.DD, "0.00")}"
            : "Not reported (needs 2 games per encounter)";

        if (_sprt is null) return;
        var sprt = _sprt.Evaluate(s, penta);
        SprtLlr = $"{TableFormat.Number(sprt.Llr, "0.00")} ({TableFormat.Number(sprt.Fraction * 100, "0.0")} %)";
        SprtBounds = $"({TableFormat.Number(sprt.LowerBound, "0.00")}, {TableFormat.Number(sprt.UpperBound, "0.00")})";
        SprtHypotheses = $"[{TableFormat.Number(_sprt.Elo0, "0.00")}, {TableFormat.Number(_sprt.Elo1, "0.00")}] " +
                         _sprt.Model.ToString().ToLowerInvariant();
        SprtStatus = sprt.Outcome switch
        {
            SprtOutcome.AcceptH1 => $"H1 accepted: {first} gains at least elo1",
            SprtOutcome.AcceptH0 => $"H0 accepted: {first} does not gain elo1",
            _ => "Running",
        };
        SprtProgress = double.IsFinite(sprt.Fraction) ? Math.Clamp(Math.Abs(sprt.Fraction) * 100, 0, 100) : 0;
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
        var command = CommandLine.Format([exe.Length > 0 ? exe : "fastchess.exe", .. TournamentRunner.FirstStageArguments(settings)]);
        CommandPreview = TournamentFormats.IsRunByFastchess(settings.Type)
            ? command
            : $"{settings.Type} runs fastchess once per stage; the pairings of later stages depend on results. First stage:{Environment.NewLine}{command}";
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
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)
            foreach (EngineViewModel engine in e.NewItems ?? Array.Empty<EngineViewModel>())
                UpdateRating(engine);
        UpdatePreview();
    }

    private void OnEngineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rating, RatingNote and the properties derived from them do not change the command line.
        if (e.PropertyName is nameof(EngineViewModel.Rating) or nameof(EngineViewModel.RatingNote)
            or nameof(EngineViewModel.RatingText) or nameof(EngineViewModel.RatingDetail) or nameof(EngineViewModel.Tier)
            or nameof(EngineViewModel.Status))
            return;
        if (sender is EngineViewModel engine && e.PropertyName is nameof(EngineViewModel.DisplayName))
            UpdateRating(engine);
        UpdatePreview();
    }

    // ---- Engine ratings ----

    private RatingList? _ratingList;
    private string? _ratingListPath;
    private string _ratingListNote = "";

    /// <summary>
    /// The rating list from Settings (the bundled UCERL list by default), loaded again when its path
    /// changes. Null when none is configured or it cannot be read; the reason is in _ratingListNote.
    /// </summary>
    private RatingList? CurrentRatingList()
    {
        var path = _settings.EffectiveTools.RatingList;
        if (path == _ratingListPath) return _ratingList;
        _ratingListPath = path;
        _ratingList?.Dispose();
        _ratingList = null;
        if (path.Length == 0 || !File.Exists(path))
        {
            _ratingListNote = "No engine rating list: set one in Settings (Engine rating list)." +
                              (path.Length > 0 ? " Not found: " + path : "");
            return null;
        }
        try
        {
            _ratingList = RatingList.LoadCsv(path);
            _ratingListNote = $"Not in the rating list ({Path.GetFileName(path)}); sorted after the rated engines.";
        }
        catch (FcdException e)
        {
            _ratingListNote = "The engine rating list could not be read: " + e.Message;
            Log.Add(_ratingListNote, LogKind.Warning);
        }
        return _ratingList;
    }

    /// <summary>
    /// Looks the engine up in the rating list by the name fastchess will use for it. It runs inside the
    /// Engines collection's change notification, so a failure here would keep the engine out of the
    /// list on screen: whatever goes wrong only leaves the engine unrated and is logged.
    /// </summary>
    private void UpdateRating(EngineViewModel engine)
    {
        try
        {
            var list = CurrentRatingList();
            var name = FastchessCommandBuilder.EngineName(engine.ToSettings());
            engine.RatingNote = _ratingListNote;
            engine.Rating = list is null || name.Length == 0 ? null : list.Lookup(name);
        }
#pragma warning disable CA1031 // any failure only costs the rating
        catch (Exception e)
#pragma warning restore CA1031
        {
            engine.Rating = null;
            engine.RatingNote = "The rating could not be looked up: " + e.Message;
            Log.Add($"{engine.DisplayName}: the rating could not be looked up: {e.Message}", LogKind.Warning);
        }
    }

    private void UpdateRatings()
    {
        foreach (var engine in Engines) UpdateRating(engine);
    }

    /// <summary>
    /// Orders the engines by rating, strongest first. Engines without a rating keep their order after
    /// the rated ones. The order is the seeding for gauntlet, pyramid and knockout tournaments.
    /// </summary>
    [RelayCommand]
    private void SortEnginesByRating()
    {
        var selected = SelectedEngine;
        var sorted = Engines.OrderByDescending(e => e.Rating?.Rating ?? double.NegativeInfinity).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(Engines[i], sorted[i])) continue;
            MoveEngine(sorted[i], i);
        }
        SelectedEngine = selected;
    }

    /// <summary>
    /// Moves a newly added engine to its place by rating: before the first engine that is rated lower
    /// or not rated. An engine without a rating stays where it was added, at the end.
    /// </summary>
    private void PlaceByRating(EngineViewModel engine)
    {
        if (engine.Rating is not { } rating) return;
        var others = Engines.Where(e => !ReferenceEquals(e, engine)).ToList();
        var to = others.FindIndex(e => e.Rating is not { } r || r.Rating < rating.Rating);
        if (to < 0) to = others.Count;
        if (Engines.IndexOf(engine) != to) MoveEngine(engine, to);
        SelectedEngine = engine;
    }

    // Remove and insert rather than ObservableCollection.Move: the list view is only sure to follow
    // the plain add and remove notifications. Removing the selected engine clears the selection;
    // the callers restore it.
    private void MoveEngine(EngineViewModel engine, int index)
    {
        Engines.Remove(engine);
        Engines.Insert(index, engine);
    }
}
