using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>
/// fastchess arguments for <see cref="TournamentSettings"/> (see assets/fastchess/fastchess-help.xml),
/// built by fcd_core.
/// </summary>
public static class FastchessCommandBuilder
{
    /// <summary>Problems that would make fastchess refuse to start. Empty when the settings are usable.</summary>
    public static IReadOnlyList<string> Validate(TournamentSettings s)
    {
        var status = NativeMethods.TournamentValidate(NativeMethods.ToJson(s), out var json);
        return NativeMethods.TakeStringList(status, json);
    }

    /// <summary>
    /// Arguments for one fastchess run. Only round robin and gauntlet are fastchess formats; the
    /// staged formats throw ArgumentException (they run through <see cref="TournamentRunner"/>).
    /// </summary>
    public static IReadOnlyList<string> Build(TournamentSettings s)
    {
        var status = NativeMethods.TournamentBuildArgs(NativeMethods.ToJson(s), out var json);
        NativeMethods.CheckArgument(status);
        return NativeMethods.TakeStringList(status, json);
    }

    /// <summary>The fastchess arguments of the first run of any format, for the command preview.</summary>
    public static IReadOnlyList<string> FirstStageArguments(TournamentSettings s)
    {
        var status = NativeMethods.TournamentFirstStageArgs(NativeMethods.ToJson(s), out var json);
        NativeMethods.CheckArgument(status);
        return NativeMethods.TakeStringList(status, json);
    }

    /// <summary>Configured name, or the executable's file name without extension (as fastchess does).</summary>
    public static string EngineName(EngineSettings e)
    {
        var status = NativeMethods.EngineDisplayName(e.Name, e.Command, out var text);
        return NativeMethods.TakeChecked(status, text);
    }

    /// <summary>Games the schedule will play, or null when it cannot be computed (knockout: without tiebreaks).</summary>
    public static long? ExpectedGames(TournamentSettings s)
    {
        NativeMethods.Check(NativeMethods.TournamentExpectedGames(NativeMethods.ToJson(s), out var games));
        return games < 0 ? null : games;
    }
}
