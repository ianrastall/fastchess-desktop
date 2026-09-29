using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Tests;

public class MatchStatisticsTests
{
    // A real 20-game match (fastchess 1.8.2, -games 2, SPRT elo0=0 elo1=2 alpha=beta=0.05 normalized):
    // game number, white, result; engines B and R alternate colors. Listed in the order the games finished.
    private static readonly (int Number, string Result)[] Match =
    [
        (3, "1/2-1/2"), (1, "1/2-1/2"), (2, "1/2-1/2"), (4, "0-1"), (6, "1-0"), (5, "1/2-1/2"), (7, "1/2-1/2"),
        (9, "1/2-1/2"), (11, "1/2-1/2"), (8, "1-0"), (10, "1-0"), (13, "1/2-1/2"), (12, "1/2-1/2"), (16, "1/2-1/2"),
        (14, "1/2-1/2"), (15, "1/2-1/2"), (19, "1/2-1/2"), (18, "1-0"), (17, "1/2-1/2"), (20, "1-0"),
    ];

    private static TournamentScoreboard PlayMatch()
    {
        var board = new TournamentScoreboard(gamesPerEncounter: 2, pentanomial: true);
        foreach (var (n, result) in Match)
        {
            var (white, black) = n % 2 == 1 ? ("B", "R") : ("R", "B");
            board.AddGame(n, white, black, result, "test");
        }
        return board;
    }

    [Fact]
    public void Scoreboard_rebuilds_the_pentanomial_counts_fastchess_printed()
    {
        using var board = PlayMatch();
        var b = board.StatsOf("B");
        // fastchess: "Games: 20, Wins: 1, Losses: 5, Draws: 14" and "Ptnml(0-2): [0, 5, 4, 1, 0], WL/DD Ratio: 0.00"
        Assert.Equal(new MatchStats(1, 14, 5, LL: 0, LD: 5, WL: 0, DD: 4, WD: 1, WW: 0), b);
        Assert.Equal(b.Inverted, board.StatsOf("R"));
        Assert.Equal(b, board.HeadToHead("B", "R"));
    }

    [Fact]
    public void Elo_matches_the_fastchess_report()
    {
        // fastchess: "Elo: -70.44 +/- 75.72, nElo: -148.15 +/- 152.27" and "LOS: 2.83 %"
        var elo = PlayMatch().EloOf("B")!;
        Assert.Equal(-70.44, Math.Round(elo.Elo, 2));
        Assert.Equal(75.72, Math.Round(elo.Error, 2));
        Assert.Equal(-148.15, Math.Round(elo.NElo, 2));
        Assert.Equal(152.27, Math.Round(elo.NEloError, 2));
        Assert.Equal(2.83, Math.Round(elo.Los, 2));
        Assert.Equal(70.44, Math.Round(PlayMatch().EloOf("R")!.Elo, 2));
    }

    [Fact]
    public void Sprt_matches_the_fastchess_report()
    {
        // fastchess: "LLR: -0.04 (-1.2%) (-2.94, 2.94) [0.00, 2.00]"
        var sprt = new SprtTest(0.05, 0.05, 0, 2, SprtModel.Normalized).Evaluate(PlayMatch().StatsOf("B"), pentanomial: true);
        Assert.Equal(-0.04, Math.Round(sprt.Llr, 2));
        Assert.Equal(-1.2, Math.Round(sprt.Fraction * 100, 1));
        Assert.Equal(-2.94, Math.Round(sprt.LowerBound, 2));
        Assert.Equal(2.94, Math.Round(sprt.UpperBound, 2));
        Assert.Equal(SprtOutcome.Continue, sprt.Outcome);
    }

    [Theory]
    [InlineData(SprtModel.Normalized, true)]
    [InlineData(SprtModel.Normalized, false)]
    [InlineData(SprtModel.Logistic, true)]
    [InlineData(SprtModel.Logistic, false)]
    [InlineData(SprtModel.Bayesian, false)]
    public void Sprt_accepts_the_right_hypothesis_for_clear_results(SprtModel model, bool pairs)
    {
        var sprt = new SprtTest(0.05, 0.05, 0, 5, model);
        var strong = new MatchStats(600, 900, 300, LL: 20, LD: 150, WL: 80, DD: 300, WD: 250, WW: 100);
        Assert.Equal(SprtOutcome.AcceptH1, sprt.Evaluate(strong, pairs).Outcome);
        Assert.Equal(SprtOutcome.AcceptH0, sprt.Evaluate(strong.Inverted, pairs).Outcome);
    }

    [Fact]
    public void Game_based_elo_is_zero_for_an_even_score()
    {
        var elo = EloEstimate.FromGames(new MatchStats(10, 20, 10))!;
        Assert.Equal(0, elo.Elo, 9);
        Assert.Equal(50, elo.Los, 5);
        Assert.True(elo.Error > 0);
        Assert.Null(EloEstimate.FromGames(default));
        Assert.Null(EloEstimate.FromPairs(new MatchStats(3, 0, 0)));
    }

    [Fact]
    public void Scoreboard_counts_failures_and_warnings_per_engine()
    {
        using var board = new TournamentScoreboard(2, pentanomial: false);
        board.AddGame(1, "A", "B", "1-0", "Black loses on time (12ms overrun)");
        board.AddGame(2, "B", "A", "0-1", "White disconnects");
        board.AddGame(3, "A", "B", "*", "Game interrupted");
        board.AddWarning("A");
        board.AddWarning("Nobody");
        Assert.Equal(2, board.Failures("B"));
        Assert.Equal(0, board.Failures("A"));
        Assert.Equal(1, board.Warnings("A"));
        Assert.Equal(new MatchStats(2, 0, 0), board.StatsOf("A"));
        Assert.False(board.Pentanomial);
        Assert.Equal(["A", "B"], board.Engines);
    }
}
