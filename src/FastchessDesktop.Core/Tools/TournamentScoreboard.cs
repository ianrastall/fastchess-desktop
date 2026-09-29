using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>
/// Live results of a tournament, built from fastchess's "Finished game" lines by fcd_core. Game
/// counts update with every game; pentanomial counts when both games of a pair are done (pairs are
/// games (n - 1) / gamesPerEncounter, as fastchess schedules them). Not thread-safe.
/// </summary>
public sealed class TournamentScoreboard : IDisposable
{
    private readonly ScoreboardHandle _handle;

    public TournamentScoreboard(int gamesPerEncounter, bool pentanomial)
    {
        NativeMethods.Check(NativeMethods.ScoreboardNew(gamesPerEncounter, pentanomial ? 1 : 0, out var raw));
        _handle = new ScoreboardHandle(raw);
    }

    /// <summary>True when Elo, LOS and the SPRT use completed pairs (fastchess -report penta with -games 2).</summary>
    public bool Pentanomial => NativeMethods.ScoreboardIsPentanomial(_handle) != 0;

    /// <summary>Engines in the order they were added or first played.</summary>
    public IReadOnlyList<string> Engines
    {
        get
        {
            var status = NativeMethods.ScoreboardEngines(_handle, out var json);
            return NativeMethods.TakeStringList(status, json);
        }
    }

    public void AddEngine(string name) => NativeMethods.Check(NativeMethods.ScoreboardAddEngine(_handle, name));

    /// <summary>Records a finished game. Unfinished results ("*") are ignored.</summary>
    public void AddGame(int number, string white, string black, string result, string reason) =>
        NativeMethods.Check(NativeMethods.ScoreboardAddGame(_handle, number, white, black, result, reason));

    /// <summary>Counts one of fastchess's engine-output warnings against an engine it knows.</summary>
    public void AddWarning(string engine) => NativeMethods.Check(NativeMethods.ScoreboardAddWarning(_handle, engine));

    public MatchStats StatsOf(string engine) => MatchStats.FromNative(Totals(engine).Stats);

    /// <summary>Results of <paramref name="engine"/> against <paramref name="opponent"/> only.</summary>
    public MatchStats HeadToHead(string engine, string opponent)
    {
        NativeMethods.Check(NativeMethods.ScoreboardHeadToHead(_handle, engine, opponent, out var s));
        return MatchStats.FromNative(s);
    }

    public EloEstimate? EloOf(string engine) => Estimate(StatsOf(engine));

    public EloEstimate? Estimate(MatchStats stats) =>
        Pentanomial ? EloEstimate.FromPairs(stats) : EloEstimate.FromGames(stats);

    /// <summary>Games the engine lost by time forfeit, disconnect, stall or illegal move.</summary>
    public int Failures(string engine) => Totals(engine).Failures;

    public int Warnings(string engine) => Totals(engine).Warnings;

    public void Dispose() => _handle.Dispose();

    private NativeMethods.FcdEngineTotals Totals(string engine)
    {
        NativeMethods.Check(NativeMethods.ScoreboardEngine(_handle, engine, out var totals));
        return totals;
    }
}
