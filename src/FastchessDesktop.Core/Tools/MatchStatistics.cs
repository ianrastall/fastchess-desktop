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

    public static MatchStats operator +(MatchStats a, MatchStats b) => new(
        a.Wins + b.Wins, a.Draws + b.Draws, a.Losses + b.Losses,
        a.LL + b.LL, a.LD + b.LD, a.WL + b.WL, a.DD + b.DD, a.WD + b.WD, a.WW + b.WW);
}

/// <summary>
/// Elo estimate with a 95% confidence margin, normalized Elo and likelihood of superiority,
/// computed as fastchess does (app/src/matchmaking/elo/elo_wdl.cpp and elo_pentanomial.cpp).
/// Values can be infinite or NaN when a side has scored 0% or 100%, as in fastchess.
/// </summary>
public sealed record EloEstimate(double Elo, double Error, double NElo, double NEloError, double Los)
{
    private const double Ci95Z = 1.959963984540054;
    private static readonly double NEloScale = 800 / Math.Log(10);

    /// <summary>From game results (fastchess EloWDL). Null without games.</summary>
    public static EloEstimate? FromGames(MatchStats s)
    {
        if (s.Games == 0) return null;
        double n = s.Games, w = s.Wins / n, d = s.Draws / n, l = s.Losses / n;
        var score = w + 0.5 * d;
        var variance = w * Sq(1 - score) + d * Sq(0.5 - score) + l * Sq(0 - score);
        return Build(score, variance, variance / n, Math.Sqrt(variance));
    }

    /// <summary>From completed game pairs (fastchess EloPentanomial). Null without pairs.</summary>
    public static EloEstimate? FromPairs(MatchStats s)
    {
        if (s.Pairs == 0) return null;
        double n = s.Pairs;
        double ww = s.WW / n, wd = s.WD / n, wldd = (s.WL + s.DD) / n, ld = s.LD / n, ll = s.LL / n;
        var score = ww + 0.75 * wd + 0.5 * wldd + 0.25 * ld;
        var variance = ww * Sq(1 - score) + wd * Sq(0.75 - score) + wldd * Sq(0.5 - score) + ld * Sq(0.25 - score) +
                       ll * Sq(0 - score);
        return Build(score, variance, variance / n, Math.Sqrt(2 * variance));
    }

    private static EloEstimate Build(double score, double variance, double variancePerUnit, double nEloSigma)
    {
        var upper = score + Ci95Z * Math.Sqrt(variancePerUnit);
        var lower = score - Ci95Z * Math.Sqrt(variancePerUnit);
        double NElo(double s) => (s - 0.5) / nEloSigma * NEloScale;
        var los = (1 - SpecialFunctions.Erf(-(score - 0.5) / Math.Sqrt(2.0 * variancePerUnit))) / 2.0;
        return new EloEstimate(
            ScoreToElo(score), (ScoreToElo(upper) - ScoreToElo(lower)) / 2.0,
            NElo(score), (NElo(upper) - NElo(lower)) / 2.0, los * 100.0);
    }

    public static double ScoreToElo(double score) => -400.0 * Math.Log10(1.0 / score - 1.0);

    private static double Sq(double x) => x * x;
}

public enum SprtOutcome
{
    Continue,
    AcceptH0,
    AcceptH1,
}

/// <summary>
/// Sequential probability ratio test, ported from fastchess (app/src/matchmaking/sprt/sprt.cpp)
/// so the live value matches the LLR fastchess prints.
/// </summary>
public sealed class SprtTest(double alpha, double beta, double elo0, double elo1, SprtModel model)
{
    public double Elo0 { get; } = elo0;
    public double Elo1 { get; } = elo1;
    public SprtModel Model { get; } = model;
    public double LowerBound { get; } = Math.Log(beta / (1 - alpha));
    public double UpperBound { get; } = Math.Log((1 - beta) / alpha);

    public double Llr(MatchStats s, bool pentanomial) =>
        pentanomial ? LlrPairs(s) : LlrGames(s.Wins, s.Draws, s.Losses);

    /// <summary>Progress toward the bound on the LLR's side, negative toward H0 (fastchess getFraction).</summary>
    public double Fraction(double llr) => llr >= 0 ? llr / UpperBound : -llr / LowerBound;

    public SprtOutcome Outcome(double llr) =>
        llr >= UpperBound ? SprtOutcome.AcceptH1 : llr <= LowerBound ? SprtOutcome.AcceptH0 : SprtOutcome.Continue;

    private static readonly double NEloScale = 800.0 / Math.Log(10);

    private static double Regularize(int value) => value == 0 ? 1e-3 : value;

    private double LlrGames(int win, int draw, int loss)
    {
        double l = Regularize(loss), d = Regularize(draw), w = Regularize(win);
        var total = l + d + w;
        double[] probs = [l / total, d / total, w / total];
        double[] scores = [0.0, 0.5, 1.0];
        switch (Model)
        {
            case SprtModel.Normalized:
                return LlrNormalized(total, scores, probs, Elo0 / NEloScale, Elo1 / NEloScale);
            case SprtModel.Bayesian:
                if (win == 0 || loss == 0) return 0.0;
                var drawElo = 200 * Math.Log10((1 - probs[0]) / probs[0] * (1 - probs[2]) / probs[2]);
                return LlrLogistic(total, scores, probs, BayesEloToScore(Elo0, drawElo), BayesEloToScore(Elo1, drawElo));
            default:
                return LlrLogistic(total, scores, probs, LogisticEloToScore(Elo0), LogisticEloToScore(Elo1));
        }
    }

    private double LlrPairs(MatchStats s)
    {
        double ll = Regularize(s.LL), ld = Regularize(s.LD), wldd = Regularize(s.DD + s.WL), wd = Regularize(s.WD), ww = Regularize(s.WW);
        var total = ww + wd + wldd + ld + ll;
        double[] probs = [ll / total, ld / total, wldd / total, wd / total, ww / total];
        double[] scores = [0.0, 0.25, 0.5, 0.75, 1.0];
        return Model == SprtModel.Normalized
            ? LlrNormalized(total, scores, probs, Math.Sqrt(2.0) * Elo0 / NEloScale, Math.Sqrt(2.0) * Elo1 / NEloScale)
            : LlrLogistic(total, scores, probs, LogisticEloToScore(Elo0), LogisticEloToScore(Elo1));
    }

    private static double LogisticEloToScore(double elo) => 1 / (1 + Math.Pow(10, -elo / 400));

    private static double BayesEloToScore(double bayesElo, double drawElo)
    {
        var pwin = 1.0 / (1.0 + Math.Pow(10.0, (-bayesElo + drawElo) / 400.0));
        var ploss = 1.0 / (1.0 + Math.Pow(10.0, (bayesElo + drawElo) / 400.0));
        return pwin + 0.5 * (1.0 - pwin - ploss);
    }

    private static double Mean(double[] x, double[] p)
    {
        var result = 0.0;
        for (var i = 0; i < x.Length; i++) result += x[i] * p[i];
        return result;
    }

    // Van den Bergh, "Comparing the approximations for the generalized log likelihood ratio of a
    // multinomial distribution", Proposition 1.1.
    private static double LlrLogistic(double total, double[] scores, double[] probs, double s0, double s1)
    {
        double[] Mle(double s)
        {
            var minTheta = -1.0 / (scores[^1] - s);
            var maxTheta = -1.0 / (scores[0] - s);
            var theta = Itp(x =>
            {
                var result = 0.0;
                for (var i = 0; i < scores.Length; i++) result += probs[i] * (scores[i] - s) / (1.0 + x * (scores[i] - s));
                return result;
            }, minTheta, maxTheta, double.PositiveInfinity, double.NegativeInfinity, 0.1, 2.0, 0.99, 1e-3);
            var p = new double[scores.Length];
            for (var i = 0; i < p.Length; i++) p[i] = probs[i] / (1 + theta * (scores[i] - s));
            return p;
        }

        return total * Mean(LogRatio(Mle(s1), Mle(s0)), probs);
    }

    // Van den Bergh, "Comments on normalized Elo", section 4.1.
    private static double LlrNormalized(double total, double[] scores, double[] probs, double t0, double t1)
    {
        double[] Mle(double muRef, double tStar)
        {
            var n = scores.Length;
            var p = Enumerable.Repeat(1.0 / n, n).ToArray();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var mu = Mean(scores, p);
                var variance = 0.0;
                for (var i = 0; i < n; i++) variance += p[i] * (scores[i] - mu) * (scores[i] - mu);
                var sigma = Math.Sqrt(variance);
                var phi = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var z = (scores[i] - mu) / sigma;
                    phi[i] = scores[i] - muRef - 0.5 * tStar * sigma * (1.0 + z * z);
                }
                var theta = Itp(x =>
                {
                    var result = 0.0;
                    for (var i = 0; i < n; i++) result += probs[i] * phi[i] / (1.0 + x * phi[i]);
                    return result;
                }, -1.0 / phi.Max(), -1.0 / phi.Min(), double.PositiveInfinity, double.NegativeInfinity, 0.1, 2.0, 0.99, 1e-7);

                var maxDiff = 0.0;
                for (var i = 0; i < n; i++)
                {
                    var next = probs[i] / (1.0 + theta * phi[i]);
                    maxDiff = Math.Max(maxDiff, Math.Abs(next - p[i]));
                    p[i] = next;
                }
                if (maxDiff < 1e-4) break;
            }
            return p;
        }

        return total * Mean(LogRatio(Mle(0.5, t1), Mle(0.5, t0)), probs);
    }

    private static double[] LogRatio(double[] p1, double[] p0)
    {
        var r = new double[p1.Length];
        for (var i = 0; i < r.Length; i++) r[i] = Math.Log(p1[i]) - Math.Log(p0[i]);
        return r;
    }

    // Oliveira and Takahashi, "An Enhancement of the Bisection Method Average Performance Preserving
    // Minmax Optimality" (2020). A literal port: the NaN and infinity behavior matches the C++.
    private static double Itp(Func<double, double> f, double a, double b, double fa, double fb,
        double k1, double k2, double n0, double epsilon)
    {
        if (fa > 0)
        {
            (a, b) = (b, a);
            (fa, fb) = (fb, fa);
        }

        var nHalf = Math.Ceiling(Math.Log2(Math.Abs(b - a) / (2.0 * epsilon)));
        var nMax = nHalf + n0;
        for (var i = 0; Math.Abs(b - a) > 2.0 * epsilon; i++)
        {
            var xHalf = (a + b) / 2.0;
            var r = epsilon * Math.Pow(2.0, nMax - i) - (b - a) / 2.0;
            var delta = k1 * Math.Pow(b - a, k2);
            var xf = (fb * a - fa * b) / (fb - fa);
            var sigma = (xHalf - xf) / Math.Abs(xHalf - xf);
            var xt = delta <= Math.Abs(xHalf - xf) ? xf + sigma * delta : xHalf;
            var xItp = Math.Abs(xt - xHalf) <= r ? xt : xHalf - sigma * r;

            var fItp = f(xItp);
            if (fItp == 0)
            {
                a = xItp;
                b = xItp;
            }
            else if (double.IsNegative(fItp))
            {
                a = xItp;
                fa = fItp;
            }
            else
            {
                b = xItp;
                fb = fItp;
            }
        }
        return (a + b) / 2.0;
    }
}

internal static class SpecialFunctions
{
    /// <summary>Error function; absolute error below 1.2e-7 (Numerical Recipes erfc, Chebyshev fit).</summary>
    public static double Erf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.5 * z);
        var erfc = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
            t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
            t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? 1.0 - erfc : erfc - 1.0;
    }
}
