using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Models;
using FastchessDesktop.Core.Native;

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

    public static GameAnalysis FromJson(string json) =>
        JsonSerializer.Deserialize(json, AnalysisJsonContext.Default.GameAnalysis)
        ?? throw new FcdException(FcdStatus.Internal, "empty analysis document");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GameAnalysis))]
internal sealed partial class AnalysisJsonContext : JsonSerializerContext;

/// <summary>Evaluates every position of a game with a UCI engine (fcd_uci_analyze_game).</summary>
public static unsafe class GameAnalyzer
{
    public static Task<GameAnalysis> AnalyzeAsync(UciEngine engine, GameDetail game, AnalysisSettings settings,
        IProgress<(int Ply, int Total)>? progress, CancellationToken ct) => Task.Run(() =>
    {
        using var cancel = new NativeCancellation(ct);
        var state = GCHandle.Alloc(progress);
        try
        {
            var native = new NativeMethods.FcdAnalysisSettings { Depth = settings.Depth, MoveTimeMs = settings.MoveTimeMs };
            var status = NativeMethods.UciAnalyzeGame(engine.Handle, game.StartFen, string.Join(' ', game.UciMoves),
                native, &OnProgress, GCHandle.ToIntPtr(state), cancel.Handle, out var json);
            NativeMethods.CheckProcess(status, ct);
            return GameAnalysis.FromJson(NativeMethods.TakeString(json));
        }
        finally
        {
            state.Free();
        }
    }, CancellationToken.None);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(nint user, long ply, long total)
    {
        try
        {
            (GCHandle.FromIntPtr(user).Target as IProgress<(int, int)>)?.Report(((int)ply, (int)total));
        }
        catch
        {
            // Exceptions must not cross into native code.
        }
        return 0;
    }
}
