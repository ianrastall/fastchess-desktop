using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FastchessDesktop.Core.Tools;

/// <summary>A stage (one fastchess run) of a multi-stage tournament is starting.</summary>
public sealed record StageStartedEvent(int Stage, string Description) : FastchessEvent;

/// <summary>A fastchess process is about to start with this command line.</summary>
public sealed record CommandStartedEvent(string CommandLine) : FastchessEvent;

/// <summary>A message from the runner itself (pairings, byes, tiebreaks, final ranking).</summary>
public sealed record TournamentNoteEvent(string Message) : FastchessEvent;

public sealed record TournamentOutcome(bool Cancelled, int ExitCode, TimeSpan Duration, IReadOnlyList<string> Ranking);

/// <summary>Starts one fastchess process; replaceable in tests.</summary>
public delegate Task<ProcessResult> ProcessLauncher(ProcessSpec spec, Action<OutputLine> onLine, CancellationToken ct);

/// <summary>
/// Runs a tournament of any <see cref="TournamentType"/>. Round robin and gauntlet are a single
/// fastchess run; pyramid, knockout and Swiss are a series of fastchess runs whose results decide
/// the next pairings. All runs append to the same PGN file. Game numbers reported through
/// <c>onEvent</c> are continuous across runs.
/// </summary>
public sealed class TournamentRunner(ProcessLauncher? launcher = null)
{
    private readonly ProcessLauncher _launch = launcher ?? ((spec, onLine, ct) => ProcessRunner.RunAsync(spec, onLine, ct));

    public async Task<TournamentOutcome> RunAsync(string fastchessPath, string workingDirectory, TournamentSettings settings,
        Action<OutputLine> onLine, Action<FastchessEvent> onEvent, CancellationToken cancellationToken)
    {
        var run = new Run(this, fastchessPath, workingDirectory, settings, onLine, onEvent, cancellationToken);
        return await run.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>The fastchess command of the first run, for the command preview.</summary>
    public static IReadOnlyList<string> FirstStageArguments(TournamentSettings s)
    {
        if (TournamentFormats.IsRunByFastchess(s.Type) || s.Engines.Count < 2) return FastchessCommandBuilder.Build(Native(s));
        var first = s.Type switch
        {
            TournamentType.Pyramid => Stage(s, [s.Engines[1], s.Engines[0]], TournamentType.Gauntlet, s.Rounds, 1),
            TournamentType.Knockout => Match(s, FirstKnockoutMatch(s), s.Rounds, 1),
            _ => Match(s, [s.Engines[0], s.Engines[1]], s.Rounds, 1),
        };
        return FastchessCommandBuilder.Build(first);
    }

    private static IReadOnlyList<EngineSettings> FirstKnockoutMatch(TournamentSettings s)
    {
        var byName = s.Engines.ToDictionary(FastchessCommandBuilder.EngineName);
        var pair = TournamentFormats.KnockoutFirstRound([.. byName.Keys]).First(p => p.Black is not null);
        return [byName[pair.White], byName[pair.Black!]];
    }

    private static TournamentSettings Native(TournamentSettings s) =>
        TournamentFormats.IsRunByFastchess(s.Type) ? s : s with { Type = TournamentType.RoundRobin };

    private static TournamentSettings Stage(TournamentSettings s, IReadOnlyList<EngineSettings> engines, TournamentType type,
        int rounds, int stage) => s with
    {
        Engines = engines,
        Type = type,
        Seeds = 1,
        Rounds = rounds,
        Sprt = false,
        // Later stages must not truncate the games written by earlier ones.
        PgnAppend = stage == 1 ? s.PgnAppend : true,
        StateFile = $"stage-{stage:000}.json",
    };

    private static TournamentSettings Match(TournamentSettings s, IReadOnlyList<EngineSettings> pair, int rounds, int stage) =>
        Stage(s, pair, TournamentType.RoundRobin, rounds, stage);

    private sealed class Run(TournamentRunner owner, string fastchess, string workingDirectory, TournamentSettings settings,
        Action<OutputLine> onLine, Action<FastchessEvent> onEvent, CancellationToken ct)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, EngineSettings> _engines =
            settings.Engines.ToDictionary(FastchessCommandBuilder.EngineName, StringComparer.Ordinal);
        private readonly List<string> _seeded = [.. settings.Engines.Select(FastchessCommandBuilder.EngineName)];
        private readonly long? _expected = settings.ExpectedGames;
        private int _stage;
        private int _gamesBefore;

        public async Task<TournamentOutcome> ExecuteAsync()
        {
            try
            {
                return settings.Type switch
                {
                    TournamentType.Pyramid => await PyramidAsync().ConfigureAwait(false),
                    TournamentType.Knockout => await KnockoutAsync().ConfigureAwait(false),
                    TournamentType.Swiss => await SwissAsync().ConfigureAwait(false),
                    _ => await SingleAsync().ConfigureAwait(false),
                };
            }
            catch (StageFailedException e)
            {
                return new TournamentOutcome(e.Result.Cancelled, e.Result.ExitCode, _clock.Elapsed, []);
            }
        }

        private async Task<TournamentOutcome> SingleAsync()
        {
            var spec = new ProcessSpec(fastchess, FastchessCommandBuilder.Build(settings), workingDirectory);
            onEvent(new CommandStartedEvent(spec.DisplayCommand));
            var result = await owner._launch(spec, line =>
            {
                onLine(line);
                if (FastchessOutputParser.Parse(line.Text) is { } evt) onEvent(evt);
            }, ct).ConfigureAwait(false);
            return new TournamentOutcome(result.Cancelled, result.ExitCode, _clock.Elapsed, []);
        }

        private async Task<TournamentOutcome> PyramidAsync()
        {
            var totals = _seeded.ToDictionary(n => n, _ => 0.0);
            for (var k = 1; k < _seeded.Count; k++)
            {
                var newcomer = _seeded[k];
                var field = new List<EngineSettings> { _engines[newcomer] };
                field.AddRange(_seeded.Take(k).Select(n => _engines[n]));
                var points = await StageAsync($"Pyramid stage {k}: {newcomer} joins and plays {string.Join(", ", _seeded.Take(k))}",
                    Stage(settings, field, TournamentType.Gauntlet, settings.Rounds, _stage + 1)).ConfigureAwait(false);
                foreach (var (name, p) in points) totals[name] += p;
            }
            var ranking = _seeded.OrderByDescending(n => totals[n]).ThenBy(_seeded.IndexOf).ToList();
            Note("Pyramid finished. Final ranking:" + Environment.NewLine +
                 Table(ranking.Select(n => (n, $"{Fmt(totals[n])} points"))));
            onEvent(new TournamentFinishedEvent($"Pyramid finished: {ranking[0]} first"));
            return Done(ranking);
        }

        private async Task<TournamentOutcome> KnockoutAsync()
        {
            var round = TournamentFormats.KnockoutFirstRound(_seeded);
            var eliminated = new List<string>(); // in order of elimination
            for (var r = 1; ; r++)
            {
                var winners = new List<string>();
                foreach (var pair in round)
                {
                    if (pair.Black is null)
                    {
                        Note($"Knockout round {r}: {pair.White} has a bye.");
                        winners.Add(pair.White);
                        continue;
                    }
                    var winner = await KnockoutMatchAsync(r, pair.White, pair.Black).ConfigureAwait(false);
                    winners.Add(winner);
                    eliminated.Add(winner == pair.White ? pair.Black : pair.White);
                }
                if (winners.Count == 1)
                {
                    var ranking = new List<string> { winners[0] };
                    ranking.AddRange(Enumerable.Reverse(eliminated));
                    Note($"Knockout finished. Winner: {winners[0]}. Order of elimination (last out first):" + Environment.NewLine +
                         Table(ranking.Select(n => (n, ""))));
                    onEvent(new TournamentFinishedEvent($"Knockout finished: {winners[0]} wins"));
                    return Done(ranking);
                }
                round = TournamentFormats.KnockoutNextRound(winners);
            }
        }

        private async Task<string> KnockoutMatchAsync(int round, string a, string b)
        {
            double pa = 0, pb = 0;
            var points = await StageAsync($"Knockout round {round}: {a} vs {b}",
                Match(settings, [_engines[a], _engines[b]], settings.Rounds, _stage + 1)).ConfigureAwait(false);
            pa += points.GetValueOrDefault(a);
            pb += points.GetValueOrDefault(b);
            for (var t = 1; pa == pb && t <= settings.KnockoutTiebreakPairs; t++)
            {
                points = await StageAsync($"Knockout round {round}: {a} vs {b}, tiebreak {t}",
                    Match(settings, [_engines[a], _engines[b]], 1, _stage + 1)).ConfigureAwait(false);
                pa += points.GetValueOrDefault(a);
                pb += points.GetValueOrDefault(b);
            }
            var higherSeed = _seeded.IndexOf(a) < _seeded.IndexOf(b) ? a : b;
            var winner = pa > pb ? a : pb > pa ? b : higherSeed;
            Note(pa == pb
                ? $"{a} {Fmt(pa)} - {Fmt(pb)} {b}: still tied after tiebreaks; {winner} advances as the higher seed."
                : $"{a} {Fmt(pa)} - {Fmt(pb)} {b}: {winner} advances.");
            return winner;
        }

        private async Task<TournamentOutcome> SwissAsync()
        {
            var scores = _seeded.ToDictionary(n => n, _ => 0.0);
            var opponents = _seeded.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal));
            var hadBye = new HashSet<string>(StringComparer.Ordinal);
            var whites = _seeded.ToDictionary(n => n, _ => 0);
            var byePoints = (double)settings.Rounds * settings.GamesPerEncounter;

            for (var r = 1; r <= settings.SwissRounds; r++)
            {
                var (pairs, bye) = TournamentFormats.SwissRound(_seeded, scores, opponents, hadBye, whites);
                Note($"Swiss round {r} pairings: " + string.Join("; ", pairs.Select(p => $"{p.White} - {p.Black}")) +
                     (bye is null ? "" : $"; bye: {bye} (+{Fmt(byePoints)})"));
                if (bye is not null)
                {
                    hadBye.Add(bye);
                    scores[bye] += byePoints;
                }
                foreach (var pair in pairs)
                {
                    var black = pair.Black!;
                    var points = await StageAsync($"Swiss round {r}: {pair.White} vs {black}",
                        Match(settings, [_engines[pair.White], _engines[black]], settings.Rounds, _stage + 1)).ConfigureAwait(false);
                    scores[pair.White] += points.GetValueOrDefault(pair.White);
                    scores[black] += points.GetValueOrDefault(black);
                    opponents[pair.White].Add(black);
                    opponents[black].Add(pair.White);
                    whites[pair.White]++;
                }
            }

            double Buchholz(string n) => opponents[n].Sum(o => scores[o]);
            var ranking = _seeded.OrderByDescending(n => scores[n]).ThenByDescending(Buchholz).ThenBy(_seeded.IndexOf).ToList();
            Note("Swiss finished. Final ranking (score, Buchholz):" + Environment.NewLine +
                 Table(ranking.Select(n => (n, $"{Fmt(scores[n])}  ({Fmt(Buchholz(n))})"))));
            onEvent(new TournamentFinishedEvent($"Swiss finished: {ranking[0]} first"));
            return Done(ranking);
        }

        /// <summary>Runs one fastchess process and returns the points each engine scored in it.</summary>
        private async Task<Dictionary<string, double>> StageAsync(string description, TournamentSettings stageSettings)
        {
            _stage++;
            onEvent(new StageStartedEvent(_stage, description));
            var spec = new ProcessSpec(fastchess, FastchessCommandBuilder.Build(stageSettings), workingDirectory);
            onEvent(new CommandStartedEvent(spec.DisplayCommand));

            var points = new Dictionary<string, double>(StringComparer.Ordinal);
            var finished = 0;
            var result = await owner._launch(spec, line =>
            {
                onLine(line);
                switch (FastchessOutputParser.Parse(line.Text))
                {
                    case GameStartedEvent s:
                        onEvent(s with { Number = _gamesBefore + s.Number, Total = (int)(_expected ?? _gamesBefore + s.Total) });
                        break;
                    case GameFinishedEvent f:
                        lock (points)
                        {
                            finished++;
                            var (w, b) = f.Result switch { "1-0" => (1.0, 0.0), "0-1" => (0.0, 1.0), "1/2-1/2" => (0.5, 0.5), _ => (0.0, 0.0) };
                            points[f.White] = points.GetValueOrDefault(f.White) + w;
                            points[f.Black] = points.GetValueOrDefault(f.Black) + b;
                        }
                        onEvent(f with { Number = _gamesBefore + f.Number });
                        break;
                    // A single run's "Tournament finished" is not the end of a multi-stage tournament.
                }
            }, ct).ConfigureAwait(false);

            _gamesBefore += finished;
            if (result.Cancelled || result.ExitCode != 0) throw new StageFailedException(result);
            return points;
        }

        private void Note(string message) => onEvent(new TournamentNoteEvent(message));

        private TournamentOutcome Done(IReadOnlyList<string> ranking) => new(false, 0, _clock.Elapsed, ranking);

        private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        private static string Table(IEnumerable<(string Name, string Detail)> rows)
        {
            var sb = new StringBuilder();
            var i = 0;
            foreach (var (name, detail) in rows)
                sb.Append(CultureInfo.InvariantCulture, $"{++i,3}. {name}  {detail}").AppendLine();
            return sb.ToString().TrimEnd();
        }
    }

    private sealed class StageFailedException(ProcessResult result) : Exception("A fastchess run failed or was stopped.")
    {
        public ProcessResult Result { get; } = result;
    }
}
