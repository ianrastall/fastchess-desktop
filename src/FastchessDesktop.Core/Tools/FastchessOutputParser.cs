using System.Globalization;
using System.Text.RegularExpressions;

namespace FastchessDesktop.Core.Tools;

public abstract record FastchessEvent;

public sealed record GameStartedEvent(int Number, int Total, string White, string Black) : FastchessEvent;

public sealed record GameFinishedEvent(int Number, string White, string Black, string Result, string Reason)
    : FastchessEvent;

public sealed record TournamentFinishedEvent(string Message) : FastchessEvent;

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

    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
