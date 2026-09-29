using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>
/// Results from one engine's point of view: game counts and pentanomial pair counts, as in
/// fastchess (app/src/matchmaking/stats.hpp). A pair is the two games played on one opening with
/// colors swapped; LD means one loss and one draw, WL one win and one loss, and so on.
/// </summary>
public readonly record struct MatchStats(
    int Wins, int Draws, int Losses,
    int LL = 0, int LD = 0, int WL = 0, int DD = 0, int WD = 0, int WW = 0)
{
    public int Games => Wins + Draws + Losses;
    public int Pairs => LL + LD + WL + DD + WD + WW;
    public double Points => Wins + 0.5 * Draws;

    /// <summary>The same results from the opponent's point of view.</summary>
    public MatchStats Inverted => new(Losses, Draws, Wins, WW, WD, WL, DD, LD, LL);

    internal NativeMethods.FcdMatchStats ToNative() => new()
    {
        Wins = Wins, Draws = Draws, Losses = Losses, LL = LL, LD = LD, WL = WL, DD = DD, WD = WD, WW = WW,
    };

    internal static MatchStats FromNative(in NativeMethods.FcdMatchStats s) =>
        new(s.Wins, s.Draws, s.Losses, s.LL, s.LD, s.WL, s.DD, s.WD, s.WW);
}

/// <summary>
/// Elo estimate with a 95% confidence margin, normalized Elo and likelihood of superiority (percent),
/// computed by fcd_core with the formulas of fastchess. Values can be infinite or NaN when a side has
/// scored 0% or 100%, as in fastchess.
/// </summary>
public sealed record EloEstimate(double Elo, double Error, double NElo, double NEloError, double Los)
{
    /// <summary>From game results (fastchess EloWDL). Null without games.</summary>
    public static EloEstimate? FromGames(MatchStats s) => Estimate(s, pentanomial: false);

    /// <summary>From completed game pairs (fastchess EloPentanomial). Null without pairs.</summary>
    public static EloEstimate? FromPairs(MatchStats s) => Estimate(s, pentanomial: true);

    private static EloEstimate? Estimate(MatchStats s, bool pentanomial)
    {
        var status = NativeMethods.EloEstimate(s.ToNative(), pentanomial ? 1 : 0, out var e);
        if (status == FcdStatus.NotFound) return null;
        NativeMethods.Check(status);
        return new EloEstimate(e.Elo, e.Error, e.NElo, e.NEloError, e.Los);
    }
}

public enum SprtOutcome
{
    Continue,
    AcceptH0,
    AcceptH1,
}

/// <summary>An SPRT evaluation: the log-likelihood ratio, the bounds, progress toward the nearer bound
/// (negative toward H0) and the decision.</summary>
public sealed record SprtState(double Llr, double LowerBound, double UpperBound, double Fraction, SprtOutcome Outcome);

/// <summary>Sequential probability ratio test, evaluated by fcd_core exactly as fastchess does.</summary>
public sealed class SprtTest(double alpha, double beta, double elo0, double elo1, SprtModel model)
{
    public double Elo0 { get; } = elo0;
    public double Elo1 { get; } = elo1;
    public SprtModel Model { get; } = model;

    public SprtState Evaluate(MatchStats stats, bool pentanomial)
    {
        var parameters = new NativeMethods.FcdSprtParams
        {
            Alpha = alpha, Beta = beta, Elo0 = Elo0, Elo1 = Elo1, Model = (int)Model,
        };
        NativeMethods.Check(NativeMethods.SprtEvaluate(parameters, stats.ToNative(), pentanomial ? 1 : 0, out var s));
        return new SprtState(s.Llr, s.LowerBound, s.UpperBound, s.Fraction, (SprtOutcome)s.Outcome);
    }
}
