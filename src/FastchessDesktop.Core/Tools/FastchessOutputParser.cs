using System.Globalization;
using System.Text.RegularExpressions;

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

/// <summary>
/// Recognizes the progress lines fastchess prints in both output formats
/// (app/src/matchmaking/output/output_fastchess.hpp and output_cutechess.hpp).
/// Other lines are left to the log.
/// </summary>
public static partial class FastchessOutputParser
{
    [GeneratedRegex(@"^Started game (\d+) of (\d+) \((.+) vs (.+)\)\s*$")]
    private static partial Regex StartedRegex();

    [GeneratedRegex(@"^Finished game (\d+) \((.+) vs (.+)\): (1-0|0-1|1/2-1/2|\*) \{(.*)\}\s*$")]
    private static partial Regex FinishedRegex();

    public static FastchessEvent? Parse(string line)
    {
        var m = StartedRegex().Match(line);
        if (m.Success)
            return new GameStartedEvent(Int(m.Groups[1].Value), Int(m.Groups[2].Value), m.Groups[3].Value,
                m.Groups[4].Value);

        m = FinishedRegex().Match(line);
        if (m.Success)
            return new GameFinishedEvent(Int(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value,
                m.Groups[4].Value, m.Groups[5].Value);

        var trimmed = line.Trim();
        // "Tournament finished", or "SPRT (...) completed - H0 was accepted" (roundrobin.cpp).
        if (trimmed == "Tournament finished" ||
            (trimmed.StartsWith("SPRT (", StringComparison.Ordinal) && trimmed.EndsWith(" was accepted", StringComparison.Ordinal)))
            return new TournamentFinishedEvent(trimmed);
        return null;
    }

    // Game end reasons from match.hpp that mean an engine failed.
    private static readonly string[] FailureReasons =
        [" loses on time", " disconnects", "'s connection stalls", " makes an illegal move", "Game interrupted"];

    // Prefixes of the lines fastchess prints for one engine-output warning (match.cpp).
    private static readonly string[] WarningPrefixes = ["Warning;", "Info;", "Infos;", "Position;", "Moves;"];

    [GeneratedRegex(@"^\s*(Timeouts|Crashed): (\d+)\s*$")]
    private static partial Regex TrackerRegex();

    /// <summary>
    /// The engine a "Warning;" line is about. fastchess ends these with "from &lt;engine name&gt;"
    /// (match.cpp); returns null for other lines.
    /// </summary>
    public static string? WarningEngine(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("Warning;", StringComparison.Ordinal)) return null;
        var at = trimmed.LastIndexOf(" from ", StringComparison.Ordinal);
        return at < 0 ? null : trimmed[(at + " from ".Length)..];
    }

    public static bool IsEngineFailureReason(string reason) =>
        FailureReasons.Any(r => reason.Contains(r, StringComparison.Ordinal));

    public static FastchessLineKind Classify(string line)
    {
        if (FinishedRegex().Match(line) is { Success: true } finished)
            return IsEngineFailureReason(finished.Groups[5].Value) ? FastchessLineKind.EngineFailure : FastchessLineKind.Normal;
        // "Player: X / Timeouts: n / Crashed: n", printed at the end when an engine timed out or crashed.
        if (TrackerRegex().Match(line) is { Success: true } tracker)
            return tracker.Groups[2].Value == "0" ? FastchessLineKind.Normal : FastchessLineKind.EngineFailure;
        var trimmed = line.TrimStart();
        return WarningPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal))
            ? FastchessLineKind.Warning
            : FastchessLineKind.Normal;
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
