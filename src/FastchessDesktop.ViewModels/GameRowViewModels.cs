using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.ViewModels;

/// <summary>
/// A table row. All display values are strings so XAML needs no converters. Rows are updated in
/// place after edits so the table keeps its selection.
/// </summary>
public sealed class GameRow(GameSummary game) : ObservableObject
{
    public GameSummary Game { get; private set; } = game;

    public long Id => Game.Id;
    public string IdText => Game.Id.ToString(CultureInfo.InvariantCulture);
    public string Date => Game.Date;
    public string Event => Game.Event;
    public string Round => Game.Round;
    public string White => Game.White;
    public string Black => Game.Black;
    public string Result => Game.Result;
    public string WhiteElo => Game.WhiteElo?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string BlackElo => Game.BlackElo?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string Eco => Game.Eco;
    public string Opening => string.IsNullOrEmpty(Game.Variation) ? Game.Opening : $"{Game.Opening}: {Game.Variation}";
    public string Plies => Game.PlyCount.ToString(CultureInfo.InvariantCulture);
    public string Analysis => Game.HasAnalysis ? "yes" : "";

    public void Update(GameSummary game)
    {
        Game = game;
        OnPropertyChanged(string.Empty); // every display property derives from Game
    }
}

/// <summary>An editable PGN tag of the selected game.</summary>
public sealed partial class TagViewModel(string name, string value, bool isWellKnown) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsWellKnown { get; } = isWellKnown;
    public string OriginalValue { get; private set; } = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string Value { get; set; } = value;

    public bool IsDirty => Value != OriginalValue;

    public void MarkSaved()
    {
        OriginalValue = Value;
        OnPropertyChanged(nameof(IsDirty));
    }
}

public static class GameText
{
    public static readonly string[] WellKnownTags =
        ["Event", "Site", "Date", "Round", "White", "Black", "Result", "WhiteElo", "BlackElo", "ECO", "Opening",
         "Variation", "TimeControl", "Termination"];

    public static IEnumerable<TagViewModel> Tags(GameDetail g)
    {
        string?[] values =
        [
            g.Event, g.Site, g.Date, g.Round, g.White, g.Black, g.Result,
            g.WhiteElo?.ToString(CultureInfo.InvariantCulture), g.BlackElo?.ToString(CultureInfo.InvariantCulture),
            g.Eco, g.Opening, g.Variation, g.TimeControl, g.Termination,
        ];
        for (var i = 0; i < WellKnownTags.Length; i++) yield return new TagViewModel(WellKnownTags[i], values[i] ?? "", true);
        foreach (var (name, value) in g.Tags) yield return new TagViewModel(name, value, false);
    }

    /// <summary>Move text with numbers, e.g. "1. e4 e5 2. Nf3", honoring a FEN start.</summary>
    public static string Moves(GameDetail g)
    {
        var sb = new StringBuilder();
        var blackFirst = false;
        var number = 1;
        if (g.StartFen is { } fen)
        {
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            blackFirst = parts.Length > 1 && parts[1] == "b";
            if (parts.Length > 5 && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                number = Math.Max(1, n);
        }
        for (var i = 0; i < g.SanMoves.Count; i++)
        {
            var whiteMove = (i % 2 == 0) != blackFirst;
            if (whiteMove) sb.Append(number).Append(". ");
            else if (i == 0) sb.Append(number).Append("... ");
            sb.Append(g.SanMoves[i]).Append(' ');
            if (!whiteMove) number++;
        }
        return sb.Append(g.Result).ToString();
    }

    /// <summary>One-line summary of a stored analysis document, or empty.</summary>
    public static string AnalysisSummary(GameDetail g)
    {
        if (g.Analysis is not { ValueKind: JsonValueKind.Object } a) return "";
        string Get(string name, string format) =>
            a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetDouble().ToString(format, CultureInfo.InvariantCulture)
                : "-";
        var engine = a.TryGetProperty("engine", out var e) ? e.GetString() : "engine";
        var depth = Get("depth", "0");
        return $"{engine}, depth {depth}: average centipawn loss White {Get("whiteAcpl", "0.0")}, Black {Get("blackAcpl", "0.0")}";
    }
}
