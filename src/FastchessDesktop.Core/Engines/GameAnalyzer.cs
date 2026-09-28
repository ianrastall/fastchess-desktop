using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Models;

namespace FastchessDesktop.Core.Engines;

public sealed record AnalysisSettings
{
    public int Depth { get; init; } = 16;
    public int MoveTimeMs { get; init; }
    public int Threads { get; init; } = 1;
    public int HashMb { get; init; } = 64;
}

/// <summary>
/// Evaluation after a ply (ply 0 is the start position), from White's point of view.
/// Mate is moves to mate (positive: White mates). Mate 0 marks a checkmated position; Cp then holds +/-1000.
/// </summary>
public sealed record PlyEvaluation(int Ply, int? Cp, int? Mate, int Depth, string? BestMove);

/// <summary>The analysis document stored per game (fcd_db_set_analysis). Format version 1.</summary>
public sealed record GameAnalysis
{
    public int Format { get; init; } = 1;
    public string Engine { get; init; } = "";
    public int Depth { get; init; }
    public int MoveTimeMs { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public IReadOnlyList<PlyEvaluation> Evals { get; init; } = [];

    /// <summary>Average centipawn loss per side (evaluations capped at +/-1000 cp, mates count as the cap).</summary>
    public double? WhiteAcpl { get; init; }

    public double? BlackAcpl { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, AnalysisJsonContext.Default.GameAnalysis);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GameAnalysis))]
internal sealed partial class AnalysisJsonContext : JsonSerializerContext;

/// <summary>Evaluates every position of a game with a UCI engine.</summary>
public static class GameAnalyzer
{
    private const int Cap = 1000;

    public static async Task<GameAnalysis> AnalyzeAsync(UciEngine engine, GameDetail game, AnalysisSettings settings,
        IProgress<(int Ply, int Total)>? progress, CancellationToken ct)
    {
        await engine.NewGameAsync(ct).ConfigureAwait(false);
        var limit = new UciLimit(settings.Depth, settings.MoveTimeMs);
        var whiteToMoveAtStart = game.StartFen is null || SideToMove(game.StartFen) == 'w';
        var evals = new List<PlyEvaluation>(game.UciMoves.Count + 1);

        for (var ply = 0; ply <= game.UciMoves.Count; ply++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((ply, game.UciMoves.Count));
            var whiteToMove = (ply % 2 == 0) == whiteToMoveAtStart;
            var r = await engine.SearchAsync(game.StartFen, game.UciMoves.Take(ply), limit, ct).ConfigureAwait(false);
            var score = whiteToMove ? r.Score : r.Score.Negate();
            // "mate 0" means the side to move is checkmated; its sign is lost by negation,
            // so the loser is recorded through a capped centipawn value instead.
            var cp = score.Mate == 0 ? (whiteToMove ? -Cap : Cap) : score.Centipawns;
            // A terminal position yields "bestmove (none)".
            evals.Add(new PlyEvaluation(ply, cp, score.Mate, r.Depth, r.BestMove is "" or "(none)" ? null : r.BestMove));
        }

        var (white, black) = AverageLoss(evals, whiteToMoveAtStart);
        return new GameAnalysis
        {
            Engine = engine.Name,
            Depth = settings.Depth,
            MoveTimeMs = settings.MoveTimeMs,
            AnalyzedAt = DateTimeOffset.UtcNow,
            Evals = evals,
            WhiteAcpl = white,
            BlackAcpl = black,
        };
    }

    /// <summary>Capped White-POV centipawns; null when the engine gave no score.</summary>
    public static int? CappedCp(PlyEvaluation e) =>
        e.Mate is { } m && m != 0 ? (m > 0 ? Cap : -Cap)
        : e.Cp is { } cp ? Math.Clamp(cp, -Cap, Cap)
        : null;

    public static (double? White, double? Black) AverageLoss(IReadOnlyList<PlyEvaluation> evals, bool whiteMovesFirst)
    {
        double whiteSum = 0, blackSum = 0;
        int whiteMoves = 0, blackMoves = 0;
        for (var i = 1; i < evals.Count; i++)
        {
            if (CappedCp(evals[i - 1]) is not { } before || CappedCp(evals[i]) is not { } after) continue;
            var moverIsWhite = ((i - 1) % 2 == 0) == whiteMovesFirst;
            var loss = moverIsWhite ? before - after : after - before;
            if (moverIsWhite)
            {
                whiteSum += Math.Max(0, loss);
                whiteMoves++;
            }
            else
            {
                blackSum += Math.Max(0, loss);
                blackMoves++;
            }
        }
        return (whiteMoves > 0 ? whiteSum / whiteMoves : null, blackMoves > 0 ? blackSum / blackMoves : null);
    }

    private static char SideToMove(string fen)
    {
        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts[1] == "b" ? 'b' : 'w';
    }
}
