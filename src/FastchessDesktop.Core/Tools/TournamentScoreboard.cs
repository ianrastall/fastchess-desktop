namespace FastchessDesktop.Core.Tools;

/// <summary>
/// Live results of a tournament, built from fastchess's "Finished game" lines. Game counts update
/// with every game; pentanomial counts update when both games of a pair are done. Pairs are
/// identified as fastchess schedules them (base_scheduler.hpp): game numbers 1..n per pair of
/// <c>gamesPerEncounter</c> consecutive games, which the staged formats preserve.
/// </summary>
public sealed class TournamentScoreboard(int gamesPerEncounter, bool pentanomial)
{
    private readonly Dictionary<string, MatchStats> _stats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _warnings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), MatchStats> _headToHead = [];
    private readonly Dictionary<int, (string White, string Black, double WhiteScore)> _openPairs = [];
    private readonly List<string> _engines = [];

    /// <summary>True when Elo, LOS and the SPRT use completed pairs (fastchess -report penta with -games 2).</summary>
    public bool Pentanomial { get; } = pentanomial && gamesPerEncounter == 2;

    /// <summary>Engines in the order they first appeared.</summary>
    public IReadOnlyList<string> Engines => _engines;

    public void AddEngine(string name)
    {
        if (_stats.ContainsKey(name)) return;
        _engines.Add(name);
        _stats[name] = default;
    }

    /// <summary>Records a finished game. Unfinished results ("*") are ignored.</summary>
    public void AddGame(int number, string white, string black, string result, string reason)
    {
        double? whiteScore = result switch { "1-0" => 1, "0-1" => 0, "1/2-1/2" => 0.5, _ => null };
        if (whiteScore is not { } ws) return;
        AddEngine(white);
        AddEngine(black);
        Add(white, black, GameStats(ws));

        if (FastchessOutputParser.IsEngineFailureReason(reason))
        {
            var culprit = reason.StartsWith("White", StringComparison.Ordinal) ? white
                : reason.StartsWith("Black", StringComparison.Ordinal) ? black : null;
            if (culprit is not null) _failures[culprit] = Failures(culprit) + 1;
        }

        if (!Pentanomial || number < 1) return;
        var pair = (number - 1) / gamesPerEncounter;
        if (!_openPairs.Remove(pair, out var first))
        {
            _openPairs[pair] = (white, black, ws);
            return;
        }
        if (first.White != black || first.Black != white) return; // not the color-swapped partner

        // Both games from the point of view of the engine that had White in the first game.
        var pairStats = (first.WhiteScore + (1 - ws)) switch
        {
            2.0 => new MatchStats(0, 0, 0, WW: 1),
            1.5 => new MatchStats(0, 0, 0, WD: 1),
            1.0 when first.WhiteScore == 0.5 => new MatchStats(0, 0, 0, DD: 1),
            1.0 => new MatchStats(0, 0, 0, WL: 1),
            0.5 => new MatchStats(0, 0, 0, LD: 1),
            _ => new MatchStats(0, 0, 0, LL: 1),
        };
        Add(first.White, first.Black, pairStats);
    }

    /// <summary>Counts one of fastchess's engine-output warnings against an engine it knows.</summary>
    public void AddWarning(string engine)
    {
        if (_stats.ContainsKey(engine)) _warnings[engine] = Warnings(engine) + 1;
    }

    public MatchStats StatsOf(string engine) => _stats.GetValueOrDefault(engine);

    /// <summary>Results of <paramref name="engine"/> against <paramref name="opponent"/> only.</summary>
    public MatchStats HeadToHead(string engine, string opponent) =>
        _headToHead.GetValueOrDefault((engine, opponent));

    public EloEstimate? EloOf(string engine) => Estimate(StatsOf(engine));

    public EloEstimate? Estimate(MatchStats stats) =>
        Pentanomial ? EloEstimate.FromPairs(stats) : EloEstimate.FromGames(stats);

    /// <summary>Games the engine lost by time forfeit, disconnect, stall or illegal move.</summary>
    public int Failures(string engine) => _failures.GetValueOrDefault(engine);

    public int Warnings(string engine) => _warnings.GetValueOrDefault(engine);

    private void Add(string engine, string opponent, MatchStats stats)
    {
        _stats[engine] = StatsOf(engine) + stats;
        _stats[opponent] = StatsOf(opponent) + stats.Inverted;
        _headToHead[(engine, opponent)] = HeadToHead(engine, opponent) + stats;
        _headToHead[(opponent, engine)] = HeadToHead(opponent, engine) + stats.Inverted;
    }

    private static MatchStats GameStats(double whiteScore) => whiteScore switch
    {
        1 => new MatchStats(1, 0, 0),
        0 => new MatchStats(0, 0, 1),
        _ => new MatchStats(0, 1, 0),
    };
}
