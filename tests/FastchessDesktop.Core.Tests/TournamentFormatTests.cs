using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Tests;

public class TournamentFormatTests
{
    [Fact]
    public void Bracket_order_keeps_top_seeds_apart()
    {
        Assert.Equal([1, 8, 4, 5, 2, 7, 3, 6], TournamentFormats.BracketOrder(8));
        Assert.Equal([1, 2], TournamentFormats.BracketOrder(2));
    }

    [Fact]
    public void Knockout_gives_byes_to_the_top_seeds()
    {
        var round = TournamentFormats.KnockoutFirstRound(["A", "B", "C", "D", "E"]);
        Assert.Equal([new Pairing("A", null), new Pairing("D", "E"), new Pairing("B", null), new Pairing("C", null)], round);
        Assert.Equal([new Pairing("A", "D"), new Pairing("B", "C")], TournamentFormats.KnockoutNextRound(["A", "D", "B", "C"]));
    }

    [Fact]
    public void Swiss_pairs_by_score_and_avoids_rematches()
    {
        string[] seeded = ["A", "B", "C", "D"];
        var none = seeded.ToDictionary(n => n, _ => new HashSet<string>());
        var (first, bye) = TournamentFormats.SwissRound(seeded, new Dictionary<string, double>(), none, new HashSet<string>(),
            new Dictionary<string, int>());
        Assert.Null(bye);
        Assert.Equal([new Pairing("A", "B"), new Pairing("C", "D")], first);

        var scores = new Dictionary<string, double> { ["A"] = 2, ["C"] = 2, ["B"] = 0, ["D"] = 0 };
        var met = new Dictionary<string, HashSet<string>>
        {
            ["A"] = ["B"], ["B"] = ["A"], ["C"] = ["D"], ["D"] = ["C"],
        };
        var whites = new Dictionary<string, int> { ["A"] = 1, ["C"] = 1 };
        var (second, _) = TournamentFormats.SwissRound(seeded, scores, met, new HashSet<string>(), whites);
        Assert.Equal([new Pairing("A", "C"), new Pairing("B", "D")], second);
    }

    [Fact]
    public void Swiss_gives_the_bye_to_the_lowest_player_without_one()
    {
        string[] seeded = ["A", "B", "C"];
        var none = seeded.ToDictionary(n => n, _ => new HashSet<string>());
        var (_, bye) = TournamentFormats.SwissRound(seeded, new Dictionary<string, double>(), none, new HashSet<string> { "C" },
            new Dictionary<string, int>());
        Assert.Equal("B", bye);
    }

    [Fact]
    public void Swiss_allows_a_rematch_only_when_unavoidable()
    {
        string[] seeded = ["A", "B"];
        var met = new Dictionary<string, HashSet<string>> { ["A"] = ["B"], ["B"] = ["A"] };
        var (pairs, _) = TournamentFormats.SwissRound(seeded, new Dictionary<string, double>(), met, new HashSet<string>(),
            new Dictionary<string, int>());
        Assert.Single(pairs);
    }
}

/// <summary>Drives <see cref="TournamentRunner"/> with an in-process stand-in for fastchess.</summary>
public class TournamentRunnerTests
{
    /// <summary>
    /// Reads the real fastchess arguments and "plays" the games: the stronger engine wins every
    /// game, equal engines draw. Prints fastchess-format progress lines.
    /// </summary>
    private sealed class FakeFastchess(Dictionary<string, int> strength, int failAtRun = 0)
    {
        public List<IReadOnlyList<string>> Runs { get; } = [];

        public Task<ProcessResult> Launch(ProcessSpec spec, Action<OutputLine> onLine, CancellationToken ct)
        {
            var a = spec.Arguments;
            Runs.Add(a);
            if (Runs.Count == failAtRun) return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false));

            var names = a.Where(x => x.StartsWith("name=", StringComparison.Ordinal)).Select(x => x[5..]).ToList();
            var rounds = int.Parse(a[a.ToList().IndexOf("-rounds") + 1]);
            var games = int.Parse(a[a.ToList().IndexOf("-games") + 1]);
            var gauntlet = a[a.ToList().IndexOf("-tournament") + 1] == "gauntlet";
            var pairs = gauntlet
                ? names.Skip(1).Select(o => (names[0], o)).ToList()
                : [.. from i in Enumerable.Range(0, names.Count) from j in Enumerable.Range(i + 1, names.Count - i - 1) select (names[i], names[j])];

            var schedule = new List<(string W, string B)>();
            for (var r = 0; r < rounds; r++)
                foreach (var (x, y) in pairs)
                    for (var g = 0; g < games; g++)
                        schedule.Add(g % 2 == 0 ? (x, y) : (y, x));
            for (var i = 0; i < schedule.Count; i++)
            {
                var (w, b) = schedule[i];
                onLine(new OutputLine(OutputStream.StandardOutput, $"Started game {i + 1} of {schedule.Count} ({w} vs {b})"));
                var result = strength[w] > strength[b] ? "1-0" : strength[w] < strength[b] ? "0-1" : "1/2-1/2";
                onLine(new OutputLine(OutputStream.StandardOutput, $"Finished game {i + 1} ({w} vs {b}): {result} {{test}}"));
            }
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false));
        }
    }

    private static TournamentSettings Settings(TournamentType type, params string[] names) => new()
    {
        Type = type,
        Engines = [.. names.Select(n => new EngineSettings { Name = n, Command = n + ".exe" })],
        Rounds = 1,
        GamesPerEncounter = 2,
        PgnOut = "games.pgn",
        PgnAppend = false,
        SwissRounds = 3,
    };

    private static async Task<(TournamentOutcome Outcome, List<FastchessEvent> Events)> RunAsync(
        FakeFastchess fake, TournamentSettings settings)
    {
        var events = new List<FastchessEvent>();
        var outcome = await new TournamentRunner(fake.Launch).RunAsync("fastchess", ".", settings, _ => { }, events.Add,
            TestContext.Current.CancellationToken);
        return (outcome, events);
    }

    [Fact]
    public async Task Knockout_strongest_engine_wins_with_continuous_game_numbers()
    {
        var fake = new FakeFastchess(new() { ["A"] = 5, ["B"] = 4, ["C"] = 3, ["D"] = 2, ["E"] = 1 });
        var (outcome, events) = await RunAsync(fake, Settings(TournamentType.Knockout, "A", "B", "C", "D", "E"));

        Assert.False(outcome.Cancelled);
        Assert.Equal(["A", "B", "C", "D", "E"], outcome.Ranking);
        Assert.Equal(4, fake.Runs.Count); // D-E, then A-D and B-C, then the final
        var numbers = events.OfType<GameFinishedEvent>().Select(e => e.Number).ToList();
        Assert.Equal(Enumerable.Range(1, 8), numbers);
        Assert.Contains(events, e => e is TournamentFinishedEvent { Message: "Knockout finished: A wins" });
        // Only the first run may truncate the PGN; later runs append.
        Assert.Contains("append=false", fake.Runs[0]);
        Assert.All(fake.Runs.Skip(1), r => Assert.Contains("append=true", r));
    }

    [Fact]
    public async Task Knockout_tie_plays_tiebreaks_then_the_higher_seed_advances()
    {
        var fake = new FakeFastchess(new() { ["A"] = 1, ["B"] = 1 });
        var (outcome, events) = await RunAsync(fake, Settings(TournamentType.Knockout, "A", "B"));
        Assert.Equal(3, fake.Runs.Count); // the match and two tiebreaks
        Assert.Equal("A", outcome.Ranking[0]);
        Assert.Contains(events, e => e is TournamentNoteEvent n && n.Message.Contains("higher seed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swiss_plays_every_round_without_rematches()
    {
        var fake = new FakeFastchess(new() { ["A"] = 4, ["B"] = 3, ["C"] = 2, ["D"] = 1 });
        var (outcome, _) = await RunAsync(fake, Settings(TournamentType.Swiss, "A", "B", "C", "D"));
        Assert.Equal(6, fake.Runs.Count); // 3 rounds x 2 pairings
        var pairings = fake.Runs.Select(r => string.Join("-", r.Where(x => x.StartsWith("name=", StringComparison.Ordinal)).Order())).ToList();
        Assert.Equal(pairings.Count, pairings.Distinct().Count());
        Assert.Equal(["A", "B", "C", "D"], outcome.Ranking);
    }

    [Fact]
    public async Task Pyramid_adds_one_engine_per_stage()
    {
        var fake = new FakeFastchess(new() { ["A"] = 1, ["B"] = 3, ["C"] = 2 });
        var (outcome, events) = await RunAsync(fake, Settings(TournamentType.Pyramid, "A", "B", "C"));
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(["name=B", "name=A"], fake.Runs[0].Where(x => x.StartsWith("name=", StringComparison.Ordinal)));
        Assert.Equal(["name=C", "name=A", "name=B"], fake.Runs[1].Where(x => x.StartsWith("name=", StringComparison.Ordinal)));
        Assert.All(fake.Runs, r => Assert.Contains("gauntlet", r));
        Assert.Equal(["B", "C", "A"], outcome.Ranking);
        Assert.Equal(2, events.OfType<StageStartedEvent>().Count());
    }

    [Fact]
    public async Task A_failed_run_stops_the_tournament()
    {
        var fake = new FakeFastchess(new() { ["A"] = 2, ["B"] = 1, ["C"] = 3, ["D"] = 0 }, failAtRun: 1);
        var (outcome, _) = await RunAsync(fake, Settings(TournamentType.Swiss, "A", "B", "C", "D"));
        Assert.Equal(1, outcome.ExitCode);
        Assert.Single(fake.Runs);
        Assert.Empty(outcome.Ranking);
    }

    [Fact]
    public async Task Round_robin_is_a_single_fastchess_run()
    {
        var fake = new FakeFastchess(new() { ["A"] = 1, ["B"] = 2, ["C"] = 3 });
        var (_, events) = await RunAsync(fake, Settings(TournamentType.RoundRobin, "A", "B", "C"));
        Assert.Single(fake.Runs);
        Assert.Equal(6, events.OfType<GameFinishedEvent>().Count());
    }

    [Fact]
    public void Staged_formats_are_validated_and_previewed()
    {
        var swiss = Settings(TournamentType.Swiss, "A", "B", "C") with { SwissRounds = 3, Sprt = true };
        var errors = FastchessCommandBuilder.Validate(swiss);
        Assert.Contains(errors, e => e.StartsWith("SPRT is only available", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Swiss rounds must be", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => FastchessCommandBuilder.Build(swiss));
        Assert.Contains("roundrobin", TournamentRunner.FirstStageArguments(swiss));
        // 5 engines: 3 Swiss rounds x 2 pairings (one bye) x 1 game pair x 2 games.
        Assert.Equal(12, Settings(TournamentType.Swiss, "A", "B", "C", "D", "E").ExpectedGames);
    }
}
