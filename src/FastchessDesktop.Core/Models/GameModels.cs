using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastchessDesktop.Core.Models;

/// <summary>One row of the game table, as returned by fcd_db_query.</summary>
public sealed record GameSummary
{
    public long Id { get; init; }
    public string Event { get; init; } = "";
    public string Site { get; init; } = "";
    public string Date { get; init; } = "";
    public string Round { get; init; } = "";
    public string White { get; init; } = "";
    public string Black { get; init; } = "";
    public string Result { get; init; } = "*";
    public int? WhiteElo { get; init; }
    public int? BlackElo { get; init; }
    public string Eco { get; init; } = "";
    public string Opening { get; init; } = "";
    public string Variation { get; init; } = "";
    public string TimeControl { get; init; } = "";
    public string Termination { get; init; } = "";
    public int PlyCount { get; init; }
    public bool HasAnalysis { get; init; }
}

/// <summary>A complete game, as returned by fcd_db_get_game.</summary>
public sealed record GameDetail
{
    public long Id { get; init; }
    public string Event { get; init; } = "";
    public string Site { get; init; } = "";
    public string Date { get; init; } = "";
    public string Round { get; init; } = "";
    public string White { get; init; } = "";
    public string Black { get; init; } = "";
    public string Result { get; init; } = "*";
    public int? WhiteElo { get; init; }
    public int? BlackElo { get; init; }
    public string Eco { get; init; } = "";
    public string Opening { get; init; } = "";
    public string Variation { get; init; } = "";
    public string TimeControl { get; init; } = "";
    public string Termination { get; init; } = "";
    public int PlyCount { get; init; }
    public bool HasAnalysis { get; init; }

    /// <summary>Tags other than the well-known ones, in file order.</summary>
    public Dictionary<string, string> Tags { get; init; } = [];

    /// <summary>Null for the standard starting position.</summary>
    public string? StartFen { get; init; }

    public bool Chess960 { get; init; }
    public IReadOnlyList<string> SanMoves { get; init; } = [];
    public IReadOnlyList<string> UciMoves { get; init; } = [];
    public IReadOnlyList<string> Comments { get; init; } = [];
    public string FinalFen { get; init; } = "";
    public string Source { get; init; } = "";
    public JsonElement? Analysis { get; init; }
}

public sealed record PlayerRating
{
    public string Player { get; init; } = "";
    public double Rating { get; init; }
    public double? Error { get; init; }
    public double Points { get; init; }
    public long Played { get; init; }
    public double Percent { get; init; }
}

public sealed record OpeningInfo
{
    public string Eco { get; init; } = "";
    public string Name { get; init; } = "";
    public string Opening { get; init; } = "";
    public string Variation { get; init; } = "";
    public int Ply { get; init; }
}

/// <summary>
/// An engine's rating from a rating list. Estimated: the engine is a newer version of Player, which
/// is listed with BaseRating; Rating is then BaseRating plus 10.
/// </summary>
public sealed record EngineRating
{
    public string Player { get; init; } = "";
    public double Rating { get; init; }
    public long Games { get; init; }
    public bool Estimated { get; init; }
    public double BaseRating { get; init; }
}

public readonly record struct ImportResult(long Imported, long Duplicates, long Failed);

public enum ExportFormat
{
    Pgn = 0,
    Json = 1,
    Xml = 2,
    CrosstableText = 3,
}

public enum GameSortColumn
{
    Id,
    Date,
    Event,
    Site,
    Round,
    White,
    Black,
    Result,
    WhiteElo,
    BlackElo,
    Eco,
    Opening,
    PlyCount,
}

[Flags]
public enum FillOptions : uint
{
    None = 0,
    Opening = 0x01,
    Result = 0x02,
    OverwriteOpening = 0x10,
}

/// <summary>Selects games. An empty query selects every game.</summary>
public sealed record GameQuery
{
    public string? Search { get; init; }
    public GameSortColumn OrderBy { get; init; } = GameSortColumn.Id;
    public bool Descending { get; init; }
    public long Offset { get; init; }

    /// <summary>0 means no limit.</summary>
    public long Limit { get; init; }

    /// <summary>When non-empty, only these games are selected.</summary>
    public IReadOnlyList<long> Ids { get; init; } = [];

    public static GameQuery All { get; } = new();

    public static GameQuery ForIds(IEnumerable<long> ids) => new() { Ids = [.. ids] };

    internal string OrderByName => OrderBy switch
    {
        GameSortColumn.WhiteElo => "white_elo",
        GameSortColumn.BlackElo => "black_elo",
        GameSortColumn.PlyCount => "ply_count",
        _ => OrderBy.ToString().ToLowerInvariant(),
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<GameSummary>))]
[JsonSerializable(typeof(GameDetail))]
[JsonSerializable(typeof(List<PlayerRating>))]
[JsonSerializable(typeof(OpeningInfo))]
[JsonSerializable(typeof(EngineRating))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;
