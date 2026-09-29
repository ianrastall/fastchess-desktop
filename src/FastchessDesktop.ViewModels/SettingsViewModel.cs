using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Native;
using FastchessDesktop.Core.Tools;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

/// <summary>Tool locations, analysis and rating options. Other view models read the live values.</summary>
public sealed partial class SettingsViewModel(IDialogService dialogs, IAppEnvironment environment) : ObservableObject
{
    [ObservableProperty] public partial string FastchessPath { get; set; } = "";
    [ObservableProperty] public partial string StockfishPath { get; set; } = "";
    [ObservableProperty] public partial string OrdoPath { get; set; } = "";
    [ObservableProperty] public partial string OrdoprepPath { get; set; } = "";
    [ObservableProperty] public partial string PgnExtractPath { get; set; } = "";
    [ObservableProperty] public partial string OpeningsTsvPath { get; set; } = "";
    [ObservableProperty] public partial string RatingListPath { get; set; } = "";

    [ObservableProperty] public partial double AnalysisDepth { get; set; } = 16;
    [ObservableProperty] public partial double AnalysisMoveTimeMs { get; set; }
    [ObservableProperty] public partial double AnalysisThreads { get; set; } = 1;
    [ObservableProperty] public partial double AnalysisHashMb { get; set; } = 64;

    [ObservableProperty] public partial double RatingAverage { get; set; } = 2300;
    [ObservableProperty] public partial string RatingAnchor { get; set; } = "";
    [ObservableProperty] public partial bool RatingWhiteAuto { get; set; } = true;
    [ObservableProperty] public partial bool RatingDrawAuto { get; set; } = true;
    [ObservableProperty] public partial double RatingSimulations { get; set; } = 200;
    [ObservableProperty] public partial bool RatingRunOrdoprep { get; set; } = true;

    /// <summary>Index into <see cref="OrdoprepFilter"/>.</summary>
    [ObservableProperty] public partial int RatingOrdoprepFilterIndex { get; set; } = (int)OrdoprepFilter.RemovePerfectScores;
    [ObservableProperty] public partial double RatingMinGames { get; set; } = 10;
    [ObservableProperty] public partial bool RatingFillElo { get; set; } = true;
    [ObservableProperty] public partial bool RatingOverwriteElo { get; set; }

    [ObservableProperty] public partial double PageSize { get; set; } = 250;

    public string NativeVersion
    {
        get
        {
            try
            {
                return $"fcd_core {NativeLibraryInfo.Version} (ABI {NativeLibraryInfo.AbiVersion})";
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return "fcd_core not found: " + e.Message;
            }
        }
    }

    public string DataDirectory => environment.DataDirectory;

    /// <summary>Configured paths with bundled defaults filled in.</summary>
    public ToolPaths EffectiveTools => ToToolPaths().WithDefaults(environment.AppDirectory);

    public ToolPaths ToToolPaths() => new()
    {
        Fastchess = FastchessPath.Trim(),
        Stockfish = StockfishPath.Trim(),
        Ordo = OrdoPath.Trim(),
        Ordoprep = OrdoprepPath.Trim(),
        PgnExtract = PgnExtractPath.Trim(),
        OpeningsTsv = OpeningsTsvPath.Trim(),
        RatingList = RatingListPath.Trim(),
    };

    public AnalysisSettings ToAnalysisSettings() => new()
    {
        Depth = Whole(AnalysisDepth),
        MoveTimeMs = Whole(AnalysisMoveTimeMs),
        Threads = Math.Max(1, Whole(AnalysisThreads)),
        HashMb = Math.Max(1, Whole(AnalysisHashMb)),
    };

    public RatingSettings ToRatingSettings() => new()
    {
        Average = double.IsNaN(RatingAverage) ? 2300 : RatingAverage,
        AnchorPlayer = RatingAnchor.Trim(),
        WhiteAdvantageAuto = RatingWhiteAuto,
        DrawRateAuto = RatingDrawAuto,
        Simulations = Whole(RatingSimulations),
        RunOrdoprep = RatingRunOrdoprep,
        OrdoprepFilter = (OrdoprepFilter)RatingOrdoprepFilterIndex,
        MinGames = Whole(RatingMinGames),
        FillEloTags = RatingFillElo,
        OverwriteEloTags = RatingOverwriteElo,
    };

    // NumberBox reports NaN when cleared; treat that as 0 rather than int.MinValue.
    private static int Whole(double v) => double.IsNaN(v) ? 0 : (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue);

    public void Load(ToolPaths tools, AnalysisSettings analysis, RatingSettings ratings, int pageSize)
    {
        FastchessPath = tools.Fastchess;
        StockfishPath = tools.Stockfish;
        OrdoPath = tools.Ordo;
        OrdoprepPath = tools.Ordoprep;
        PgnExtractPath = tools.PgnExtract;
        OpeningsTsvPath = tools.OpeningsTsv;
        RatingListPath = tools.RatingList;
        AnalysisDepth = analysis.Depth;
        AnalysisMoveTimeMs = analysis.MoveTimeMs;
        AnalysisThreads = analysis.Threads;
        AnalysisHashMb = analysis.HashMb;
        RatingAverage = ratings.Average;
        RatingAnchor = ratings.AnchorPlayer;
        RatingWhiteAuto = ratings.WhiteAdvantageAuto;
        RatingDrawAuto = ratings.DrawRateAuto;
        RatingSimulations = ratings.Simulations;
        RatingRunOrdoprep = ratings.RunOrdoprep;
        RatingOrdoprepFilterIndex = (int)ratings.OrdoprepFilter;
        RatingMinGames = ratings.MinGames;
        RatingFillElo = ratings.FillEloTags;
        RatingOverwriteElo = ratings.OverwriteEloTags;
        PageSize = pageSize;
    }

    [RelayCommand]
    private async Task BrowseAsync(string which)
    {
        var filters = which switch
        {
            nameof(OpeningsTsvPath) => FileFilters.Tsv,
            nameof(RatingListPath) => FileFilters.Csv,
            _ => FileFilters.Executable,
        };
        var path = await dialogs.PickOpenFileAsync(filters);
        if (path is null) return;
        switch (which)
        {
            case nameof(FastchessPath): FastchessPath = path; break;
            case nameof(StockfishPath): StockfishPath = path; break;
            case nameof(OrdoPath): OrdoPath = path; break;
            case nameof(OrdoprepPath): OrdoprepPath = path; break;
            case nameof(PgnExtractPath): PgnExtractPath = path; break;
            case nameof(OpeningsTsvPath): OpeningsTsvPath = path; break;
            case nameof(RatingListPath): RatingListPath = path; break;
            default: throw new ArgumentOutOfRangeException(nameof(which), which, "unknown setting");
        }
    }
}
