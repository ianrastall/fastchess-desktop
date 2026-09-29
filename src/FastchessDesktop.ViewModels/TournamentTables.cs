using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.ViewModels;

/// <summary>Number formatting for the result tables. Infinite and undefined values (a 0% or 100% score) stay readable.</summary>
internal static class TableFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Number(double value, string format) =>
        double.IsNaN(value) ? "-" :
        double.IsPositiveInfinity(value) ? "inf" :
        double.IsNegativeInfinity(value) ? "-inf" :
        value.ToString(format, Inv);

    public static string Signed(double value, string format) =>
        double.IsFinite(value) && value > 0 ? "+" + Number(value, format) : Number(value, format);

    public static string Int(int value) => value.ToString(Inv);

    public static string Percent(double value) => Number(value, "0.0") + " %";
}

/// <summary>
/// One engine in the standings. Sort values are plain numbers; the bound cells are strings that
/// change only when their value does, so a live update redraws only the cells that changed.
/// </summary>
public sealed partial class StandingRow(string engine) : ObservableObject
{
    public string Engine { get; } = engine;

    public int Rank { get; private set; }
    public double Elo { get; private set; } = double.NaN;
    public double Error { get; private set; } = double.NaN;
    public double NElo { get; private set; } = double.NaN;
    public double NEloError { get; private set; } = double.NaN;
    public double Los { get; private set; } = double.NaN;
    public int Games { get; private set; }
    public double Points { get; private set; }
    public double Score { get; private set; } = double.NaN;
    public int Wins { get; private set; }
    public int Draws { get; private set; }
    public int Losses { get; private set; }
    public double DrawRatio { get; private set; } = double.NaN;
    public double Change { get; private set; } = double.NaN;
    public int Failures { get; private set; }
    public int Warnings { get; private set; }

    [ObservableProperty] public partial string RankText { get; private set; } = "";
    [ObservableProperty] public partial string EloText { get; private set; } = "";
    [ObservableProperty] public partial string ErrorText { get; private set; } = "";
    [ObservableProperty] public partial string NEloText { get; private set; } = "";
    [ObservableProperty] public partial string NEloErrorText { get; private set; } = "";
    [ObservableProperty] public partial string LosText { get; private set; } = "";
    [ObservableProperty] public partial string GamesText { get; private set; } = "";
    [ObservableProperty] public partial string PointsText { get; private set; } = "";
    [ObservableProperty] public partial string ScoreText { get; private set; } = "";
    [ObservableProperty] public partial string WinsText { get; private set; } = "";
    [ObservableProperty] public partial string DrawsText { get; private set; } = "";
    [ObservableProperty] public partial string LossesText { get; private set; } = "";
    [ObservableProperty] public partial string DrawRatioText { get; private set; } = "";
    [ObservableProperty] public partial string PtnmlText { get; private set; } = "";
    [ObservableProperty] public partial string ChangeText { get; private set; } = "";
    [ObservableProperty] public partial string FailuresText { get; private set; } = "";
    [ObservableProperty] public partial string WarningsText { get; private set; } = "";

    internal void Update(MatchStats s, EloEstimate? elo, bool pentanomial, int failures, int warnings)
    {
        var newElo = elo?.Elo ?? double.NaN;
        // Change: how much the engine's latest result moved its Elo (kept until the next change).
        if (double.IsFinite(newElo) && double.IsFinite(Elo) && newElo != Elo) Change = newElo - Elo;
        Elo = newElo;
        Error = elo?.Error ?? double.NaN;
        NElo = elo?.NElo ?? double.NaN;
        NEloError = elo?.NEloError ?? double.NaN;
        Los = elo?.Los ?? double.NaN;
        Games = s.Games;
        Points = s.Points;
        Score = s.Games > 0 ? 100.0 * s.Points / s.Games : double.NaN;
        Wins = s.Wins;
        Draws = s.Draws;
        Losses = s.Losses;
        DrawRatio = s.Games > 0 ? 100.0 * s.Draws / s.Games : double.NaN;
        Failures = failures;
        Warnings = warnings;

        EloText = TableFormat.Signed(Elo, "0.0");
        ErrorText = TableFormat.Number(Error, "0.0");
        NEloText = TableFormat.Signed(NElo, "0.0");
        NEloErrorText = TableFormat.Number(NEloError, "0.0");
        LosText = TableFormat.Percent(Los);
        GamesText = TableFormat.Int(Games);
        PointsText = TableFormat.Number(Points, "0.0");
        ScoreText = TableFormat.Percent(Score);
        WinsText = TableFormat.Int(Wins);
        DrawsText = TableFormat.Int(Draws);
        LossesText = TableFormat.Int(Losses);
        DrawRatioText = TableFormat.Percent(DrawRatio);
        PtnmlText = pentanomial ? $"{s.LL}, {s.LD}, {s.WL + s.DD}, {s.WD}, {s.WW}" : "";
        ChangeText = TableFormat.Signed(Change, "0.0");
        FailuresText = TableFormat.Int(Failures);
        WarningsText = TableFormat.Int(Warnings);
    }

    internal void SetRank(int rank)
    {
        Rank = rank;
        RankText = TableFormat.Int(rank);
    }
}

/// <summary>A finished game. Rows never change once added.</summary>
public sealed record FinishedGameRow(
    int Number, int Pair, string White, string Black, string Result, string Reason, bool IsEngineFailure,
    DateTime? Started, DateTime Finished)
{
    public TimeSpan? Duration => Started is { } s ? Finished - s : null;

    public string NumberText => TableFormat.Int(Number);
    public string PairText => TableFormat.Int(Pair);
    public string StartedText => Started?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    public string FinishedText => Finished.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string DurationText => Duration is { } d ? d.ToString(@"m\:ss", CultureInfo.InvariantCulture) : "";
}

public sealed class StandingsTable() : SortableTable<StandingRow>(
[
    new("Rank", (a, b) => a.Rank.CompareTo(b.Rank)),
    new("Engine", (a, b) => string.Compare(a.Engine, b.Engine, StringComparison.CurrentCultureIgnoreCase)),
    new("Elo", (a, b) => a.Elo.CompareTo(b.Elo), DescendingFirst: true),
    new("Error", (a, b) => a.Error.CompareTo(b.Error)),
    new("NElo", (a, b) => a.NElo.CompareTo(b.NElo), DescendingFirst: true),
    new("NEloError", (a, b) => a.NEloError.CompareTo(b.NEloError)),
    new("Los", (a, b) => a.Los.CompareTo(b.Los), DescendingFirst: true),
    new("Games", (a, b) => a.Games.CompareTo(b.Games), DescendingFirst: true),
    new("Points", (a, b) => a.Points.CompareTo(b.Points), DescendingFirst: true),
    new("Score", (a, b) => a.Score.CompareTo(b.Score), DescendingFirst: true),
    new("Wins", (a, b) => a.Wins.CompareTo(b.Wins), DescendingFirst: true),
    new("Draws", (a, b) => a.Draws.CompareTo(b.Draws), DescendingFirst: true),
    new("Losses", (a, b) => a.Losses.CompareTo(b.Losses), DescendingFirst: true),
    new("DrawRatio", (a, b) => a.DrawRatio.CompareTo(b.DrawRatio), DescendingFirst: true),
    new("Change", (a, b) => a.Change.CompareTo(b.Change), DescendingFirst: true),
    new("Failures", (a, b) => a.Failures.CompareTo(b.Failures), DescendingFirst: true),
    new("Warnings", (a, b) => a.Warnings.CompareTo(b.Warnings), DescendingFirst: true),
], (a, b) => a.Rank.CompareTo(b.Rank));

public sealed class FinishedGamesTable() : SortableTable<FinishedGameRow>(
[
    new("Number", (a, b) => a.Number.CompareTo(b.Number), DescendingFirst: true),
    new("Pair", (a, b) => a.Pair.CompareTo(b.Pair)),
    new("White", (a, b) => string.Compare(a.White, b.White, StringComparison.CurrentCultureIgnoreCase)),
    new("Black", (a, b) => string.Compare(a.Black, b.Black, StringComparison.CurrentCultureIgnoreCase)),
    new("Result", (a, b) => string.CompareOrdinal(a.Result, b.Result)),
    new("Reason", (a, b) => string.Compare(a.Reason, b.Reason, StringComparison.CurrentCultureIgnoreCase)),
    new("Started", (a, b) => Nullable.Compare(a.Started, b.Started)),
    new("Duration", (a, b) => Nullable.Compare(a.Duration, b.Duration), DescendingFirst: true),
], (a, b) => b.Number.CompareTo(a.Number));
