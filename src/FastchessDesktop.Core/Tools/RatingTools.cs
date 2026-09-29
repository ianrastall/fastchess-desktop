using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>Options for the Ordoprep + Ordo rating workflow (see assets/ordo and assets/ordoprep help XML).</summary>
public sealed record RatingSettings
{
    /// <summary>Pool average, or the anchor's rating when AnchorPlayer is set (-a).</summary>
    public double Average { get; init; } = 2300;

    /// <summary>Player fixed at Average (-A). Empty for a pool-relative scale.</summary>
    public string AnchorPlayer { get; init; } = "";

    public bool WhiteAdvantageAuto { get; init; } = true;
    public bool DrawRateAuto { get; init; } = true;

    /// <summary>Monte Carlo simulations for error bars (-s); 0 disables errors.</summary>
    public int Simulations { get; init; } = 200;

    public int Cpus { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int Decimals { get; init; } = 1;

    public bool RunOrdoprep { get; init; } = true;

    /// <summary>Ordoprep accepts at most one structural filter per run.</summary>
    public OrdoprepFilter OrdoprepFilter { get; init; } = OrdoprepFilter.RemovePerfectScores;

    /// <summary>Threshold for <see cref="OrdoprepFilter.MinGames"/>.</summary>
    public int MinGames { get; init; } = 10;

    /// <summary>Write the resulting ratings into WhiteElo/BlackElo.</summary>
    public bool FillEloTags { get; init; } = true;

    public bool OverwriteEloTags { get; init; }
}

/// <summary>Ordoprep's mutually exclusive structural filters (see constraint-one-structural-filter in its help).</summary>
public enum OrdoprepFilter
{
    None,

    /// <summary>-d: remove players with perfect or zero scores, which Ordo cannot rate.</summary>
    RemovePerfectScores,

    /// <summary>-M N: remove players with fewer than N games.</summary>
    MinGames,

    /// <summary>--major-only: keep only the largest rating-connected group.</summary>
    MajorGroupOnly,
}

/// <summary>Ordoprep and Ordo arguments, built by fcd_core.</summary>
public static class RatingToolArgs
{
    public static IReadOnlyList<string> Ordoprep(RatingSettings s, string inputPgn, string outputPgn)
    {
        var status = NativeMethods.OrdoprepArgs(NativeMethods.ToJson(s), inputPgn, outputPgn, out var json);
        return NativeMethods.TakeStringList(status, json);
    }

    public static IReadOnlyList<string> Ordo(RatingSettings s, string inputPgn, string reportTxt, string ratingsCsv)
    {
        var status = NativeMethods.OrdoArgs(NativeMethods.ToJson(s), inputPgn, reportTxt, ratingsCsv, out var json);
        return NativeMethods.TakeStringList(status, json);
    }
}

public enum PgnExtractPreset
{
    /// <summary>Add ECO/Opening/Variation/SubVariation tags from the Lichess-derived eco.pgn (-e).</summary>
    ClassifyOpenings,

    /// <summary>Drop duplicate games (-D).</summary>
    RemoveDuplicates,

    /// <summary>Correct Result tags that contradict the game (--fixresulttags) and add PlyCount.</summary>
    FixResultTags,

    /// <summary>Only the user's arguments.</summary>
    Custom,
}

/// <summary>pgn-extract arguments, built by fcd_core.</summary>
public static class PgnExtractArgs
{
    /// <summary>
    /// Arguments for one pgn-extract run over inputPgn writing outputPgn. customArgs are appended
    /// for every preset, so a preset can be refined (for example with -t tag criteria).
    /// Opening classification without an ECO file throws ArgumentException.
    /// </summary>
    public static IReadOnlyList<string> Build(PgnExtractPreset preset, string customArgs, string ecoPgnPath,
        string inputPgn, string outputPgn)
    {
        var status = NativeMethods.PgnExtractArgs((int)preset, customArgs, ecoPgnPath, inputPgn, outputPgn, out var json);
        NativeMethods.CheckArgument(status);
        return NativeMethods.TakeStringList(status, json);
    }
}
