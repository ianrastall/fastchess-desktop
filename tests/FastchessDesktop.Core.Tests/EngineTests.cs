using FastchessDesktop.Core.Engines;
using FastchessDesktop.Core.Models;

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

    [Fact]
    public void Average_loss_uses_mover_perspective_and_caps()
    {
        // White: 20 -> -30 loses 50. Black: -30 -> 1000 (mate for White) loses 1000 (capped).
        PlyEvaluation[] evals = [new(0, 20, null, 1, null), new(1, -30, null, 1, null), new(2, null, 4, 1, null)];
        var (white, black) = GameAnalyzer.AverageLoss(evals, whiteMovesFirst: true);
        Assert.Equal(50, white);
        Assert.Equal(1030, black);
    }
}

/// <summary>Runs the real UCI client against tests/.../fake_uci_engine.py (needs python3; not on Windows).</summary>
public class FakeEngineTests
{
    private static string? EnginePath()
    {
        if (OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "fake_uci_engine.py");
        if (!File.Exists(path)) return null;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Analyzes_every_ply_from_whites_point_of_view()
    {
        var path = EnginePath();
        Assert.SkipWhen(path is null, "The fake engine is a Python script and runs only on Linux/macOS.");
        var ct = TestContext.Current.CancellationToken;

        await using var engine = await UciEngine.StartAsync(path!, new Dictionary<string, string> { ["Hash"] = "32" }, ct);
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
    }
}
