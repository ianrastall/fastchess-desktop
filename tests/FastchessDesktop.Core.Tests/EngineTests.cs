using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Models;
using FastchessDesktop.Tests;

namespace FastchessDesktop.Core.Tests;

public class UciParsingTests
{
    [Fact]
    public void Parses_score_depth_and_pv()
    {
        Assert.True(UciEngine.TryParseInfo("info depth 20 seldepth 30 multipv 1 score cp -35 nodes 1 nps 2 pv e2e4 e7e5",
            out var score, out var depth, out var pv));
        Assert.Equal(new UciScore(-35, null), score);
        Assert.Equal(20, depth);
        Assert.Equal(["e2e4", "e7e5"], pv);

        Assert.True(UciEngine.TryParseInfo("info depth 9 score mate -3 pv h7h8q", out score, out _, out _));
        Assert.Equal(new UciScore(null, -3), score);
    }

    [Theory]
    [InlineData("info depth 5 score cp 20 lowerbound pv e2e4")]
    [InlineData("info depth 5 multipv 2 score cp 20 pv e2e4")]
    [InlineData("info string NNUE evaluation using nn.nnue")]
    [InlineData("info depth 5 currmove e2e4 currmovenumber 1")]
    public void Ignores_lines_that_are_not_a_final_mainline_score(string line) =>
        Assert.False(UciEngine.TryParseInfo(line, out _, out _, out _));
}

/// <summary>
/// Runs the UCI client in fcd_core against the fake engine built with the native tests
/// (native/tests/fake_uci_engine.cpp). Average centipawn loss is tested in the native tests.
/// </summary>
public class FakeEngineTests
{
    [Fact]
    public async Task Analyzes_every_ply_from_whites_point_of_view()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var engine = await UciEngine.StartAsync(FakePrograms.UciEngine,
            new Dictionary<string, string> { ["Hash"] = "32" }, ct);
        Assert.Equal("FakeEngine 1.0", engine.Name);

        var game = new GameDetail { UciMoves = ["f2f3", "e7e5", "g2g4", "d8h4"] };
        var analysis = await GameAnalyzer.AnalyzeAsync(engine, game, new AnalysisSettings { Depth = 5 }, null, ct);

        Assert.Equal(5, analysis.Evals.Count);
        // Side-to-move scores are 30, 10, -10, -30 (30 - 20n); White's view flips the sign on Black's turns.
        Assert.Equal([30, -10, -10, 30], analysis.Evals.Take(4).Select(e => e.Cp!.Value));
        Assert.Equal(5, analysis.Evals[0].Depth);
        Assert.Equal("e2e4", analysis.Evals[0].BestMove);
        // Final position: White to move and checkmated.
        Assert.Equal(0, analysis.Evals[4].Mate);
        Assert.Equal(-1000, analysis.Evals[4].Cp);
        Assert.Null(analysis.Evals[4].BestMove);
        Assert.Equal("FakeEngine 1.0", analysis.Engine);
        Assert.Contains("\"evals\":[", analysis.ToJson(), StringComparison.Ordinal);
        Assert.Equal(20, analysis.WhiteAcpl);
        Assert.Equal(0, analysis.BlackAcpl);

        var search = await engine.SearchAsync(null, ["e2e4"], new UciLimit(Depth: 5), ct);
        Assert.Equal(new UciScore(10, null), search.Score);
        Assert.Equal(["e2e4", "e7e5"], search.Pv);
    }

    [Fact]
    public async Task Missing_or_silent_engines_raise_the_usual_exceptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            UciEngine.StartAsync(Path.Combine(AppContext.BaseDirectory, "no-such-engine"), new Dictionary<string, string>(), ct));
        // fake_fastchess is not a UCI engine: it exits without answering.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UciEngine.StartAsync(FakePrograms.Fastchess, new Dictionary<string, string>(), ct));
    }
}
