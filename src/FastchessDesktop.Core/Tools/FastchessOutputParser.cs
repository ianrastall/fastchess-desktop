using System.Text.Json;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

public abstract record FastchessEvent;

public sealed record GameStartedEvent(int Number, int Total, string White, string Black) : FastchessEvent;

public sealed record GameFinishedEvent(int Number, string White, string Black, string Result, string Reason)
    : FastchessEvent
{
    /// <summary>True when the game ended because an engine failed rather than by the rules or adjudication.</summary>
    public bool IsEngineFailure => FastchessOutputParser.IsEngineFailureReason(Reason);
}

public sealed record TournamentFinishedEvent(string Message) : FastchessEvent;

/// <summary>How a line of fastchess output should be shown.</summary>
public enum FastchessLineKind
{
    Normal,

    /// <summary>
    /// fastchess's checks on engine search output (match.cpp): a "Warning;" line followed by
    /// "Info;", "Position;" and "Moves;" lines. They do not affect the game.
    /// </summary>
    Warning,

    /// <summary>A game lost to a time forfeit, crash, stall or illegal move, or a nonzero timeout or crash count.</summary>
    EngineFailure,
}

/// <summary>One line of fastchess output: how to show it, the event it reports, and the engine a warning is about.</summary>
public sealed record FastchessLine(FastchessLineKind Kind, FastchessEvent? Event, string? WarningEngine);

/// <summary>
/// Recognizes fastchess progress lines (both output formats) and classifies warnings and engine
/// failures. Implemented in fcd_core (fcd_fastchess_parse_line).
/// </summary>
public static class FastchessOutputParser
{
    public static FastchessLine Analyze(string line)
    {
        var status = NativeMethods.FastchessParseLine(line, out var raw);
        var dto = JsonSerializer.Deserialize(NativeMethods.TakeChecked(status, raw), ToolJsonContext.Default.ParsedLineDto)!;
        var kind = dto.Kind switch
        {
            "warning" => FastchessLineKind.Warning,
            "engineFailure" => FastchessLineKind.EngineFailure,
            _ => FastchessLineKind.Normal,
        };
        return new FastchessLine(kind, ToEvent(dto.Event), dto.WarningEngine);
    }

    public static FastchessEvent? Parse(string line) => Analyze(line).Event;

    public static FastchessLineKind Classify(string line) => Analyze(line).Kind;

    /// <summary>The engine a "Warning;" line is about (fastchess ends them with "from &lt;engine&gt;"); null otherwise.</summary>
    public static string? WarningEngine(string line) => Analyze(line).WarningEngine;

    public static bool IsEngineFailureReason(string reason) => NativeMethods.IsEngineFailureReason(reason) != 0;

    internal static FastchessEvent? ToEvent(FastchessEventDto? e) => e?.Type switch
    {
        "gameStarted" => new GameStartedEvent(e.Number, e.Total, e.White ?? "", e.Black ?? ""),
        "gameFinished" => new GameFinishedEvent(e.Number, e.White ?? "", e.Black ?? "", e.Result ?? "", e.Reason ?? ""),
        "tournamentFinished" => new TournamentFinishedEvent(e.Message ?? ""),
        "stageStarted" => new StageStartedEvent(e.Stage, e.Description ?? ""),
        "commandStarted" => new CommandStartedEvent(e.CommandLine ?? ""),
        "note" => new TournamentNoteEvent(e.Message ?? ""),
        _ => null,
    };
}
