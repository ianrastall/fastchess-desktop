using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>A stage (one fastchess run) of a multi-stage tournament is starting.</summary>
public sealed record StageStartedEvent(int Stage, string Description) : FastchessEvent;

/// <summary>A fastchess process is about to start with this command line.</summary>
public sealed record CommandStartedEvent(string CommandLine) : FastchessEvent;

/// <summary>A message from the runner itself (pairings, byes, tiebreaks, final ranking).</summary>
public sealed record TournamentNoteEvent(string Message) : FastchessEvent;

/// <summary>
/// fastchess flagged an engine's search output, for example a best move that is not the first move
/// of the engine's last PV. Count is the occurrences of this message for this engine so far. The
/// warning's lines are written to File (engine-warnings.log in the working directory) instead of
/// being passed to onLine.
/// </summary>
public sealed record EngineWarningEvent(string Engine, string Message, int Count, string File) : FastchessEvent;

public sealed record TournamentOutcome(bool Cancelled, int ExitCode, TimeSpan Duration, IReadOnlyList<string> Ranking);

/// <summary>
/// Runs a tournament of any <see cref="TournamentType"/> through fcd_core. Round robin and gauntlet
/// are a single fastchess run; pyramid, knockout and Swiss are a series of fastchess runs whose
/// results decide the next pairings. All runs append to the same PGN file. Game numbers reported
/// through <c>onEvent</c> are continuous across runs.
/// </summary>
public sealed class TournamentRunner
{
    /// <summary>
    /// Runs the tournament and returns when it ends or is stopped. onLine receives every output line
    /// except engine warnings (see <see cref="EngineWarningEvent"/>) and onEvent the parsed events, both from a background thread, one call at a time. A missing
    /// fastchess executable throws FileNotFoundException.
    /// </summary>
    public Task<TournamentOutcome> RunAsync(string fastchessPath, string workingDirectory, TournamentSettings settings,
        Action<OutputLine> onLine, Action<FastchessEvent> onEvent, CancellationToken cancellationToken) => Task.Run(() =>
    {
        void OnEventJson(string json)
        {
            var dto = JsonSerializer.Deserialize(json, ToolJsonContext.Default.FastchessEventDto);
            if (FastchessOutputParser.ToEvent(dto) is { } evt) onEvent(evt);
        }

        using var cancel = new NativeCancellation(cancellationToken);
        var state = GCHandle.Alloc(new CallbackBridge.State(onLine, OnEventJson));
        try
        {
            FcdStatus status;
            nint raw;
            unsafe
            {
                status = NativeMethods.TournamentRun(fastchessPath, workingDirectory, NativeMethods.ToJson(settings),
                    CallbackBridge.Line, CallbackBridge.Event, GCHandle.ToIntPtr(state), cancel.Handle, out raw);
            }
            NativeMethods.CheckProcess(status, cancellationToken);
            var o = JsonSerializer.Deserialize(NativeMethods.TakeString(raw), RunnerJsonContext.Default.OutcomeDto)!;
            return new TournamentOutcome(o.Cancelled, o.ExitCode, TimeSpan.FromMilliseconds(o.DurationMs), o.Ranking);
        }
        finally
        {
            state.Free();
        }
    }, CancellationToken.None);

    /// <summary>The fastchess command of the first run, for the command preview.</summary>
    public static IReadOnlyList<string> FirstStageArguments(TournamentSettings s) =>
        FastchessCommandBuilder.FirstStageArguments(s);

    internal sealed record OutcomeDto(bool Cancelled, int ExitCode, long DurationMs, List<string> Ranking);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TournamentRunner.OutcomeDto))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;
