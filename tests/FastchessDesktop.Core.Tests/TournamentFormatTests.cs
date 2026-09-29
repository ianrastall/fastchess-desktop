using FastchessDesktop.Core.Tools;
using FastchessDesktop.Tests;

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

/// <summary>
/// Drives <see cref="TournamentRunner"/> (fcd_core) with the fake fastchess built with the native
/// tests: it reads the real fastchess arguments and "plays" the games; the stronger engine wins
/// every game and equal engines draw. Engine commands are strengths (native/tests/fake_fastchess.cpp).
/// </summary>
public class TournamentRunnerTests
{
    private static TournamentSettings Settings(TournamentType type, params (string Name, string Strength)[] engines) => new()
    {
        Type = type,
        Engines = [.. engines.Select(e => new EngineSettings { Name = e.Name, Command = e.Strength })],
        Rounds = 1,
        GamesPerEncounter = 2,
        PgnOut = "games.pgn",
        PgnAppend = false,
        SwissRounds = 3,
    };

    private sealed record Result(TournamentOutcome Outcome, List<FastchessEvent> Events, List<OutputLine> Lines)
    {
        /// <summary>The arguments of each fastchess run, from the CommandStarted events.</summary>
        public List<IReadOnlyList<string>> Runs =>
            [.. Events.OfType<CommandStartedEvent>().Select(c => CommandLine.Split(c.CommandLine))];
    }

    private static async Task<Result> RunAsync(TournamentSettings settings)
    {
        var events = new List<FastchessEvent>();
        var lines = new List<OutputLine>();
        var dir = Directory.CreateTempSubdirectory("fcd-runner-").FullName;
        var outcome = await new TournamentRunner().RunAsync(FakePrograms.Fastchess, dir, settings,
            l => { lock (lines) lines.Add(l); }, e => { lock (events) events.Add(e); },
            TestContext.Current.CancellationToken);
        return new Result(outcome, events, lines);
    }

    private static IEnumerable<string> Names(IReadOnlyList<string> run) =>
        run.Where(x => x.StartsWith("name=", StringComparison.Ordinal));

    [Fact]
    public async Task Knockout_strongest_engine_wins_with_continuous_game_numbers()
    {
        var r = await RunAsync(Settings(TournamentType.Knockout, ("A", "5"), ("B", "4"), ("C", "3"), ("D", "2"), ("E", "1")));

        Assert.False(r.Outcome.Cancelled);
        Assert.Equal(["A", "B", "C", "D", "E"], r.Outcome.Ranking);
        Assert.Equal(4, r.Runs.Count); // D-E, then A-D and B-C, then the final
        Assert.Equal(Enumerable.Range(1, 8), r.Events.OfType<GameFinishedEvent>().Select(e => e.Number));
        Assert.Contains(r.Events, e => e is TournamentFinishedEvent { Message: "Knockout finished: A wins" });
        // Only the first run may truncate the PGN; later runs append.
        Assert.Contains("append=false", r.Runs[0]);
        Assert.All(r.Runs.Skip(1), run => Assert.Contains("append=true", run));
    }

    [Fact]
    public async Task Knockout_tie_plays_tiebreaks_then_the_higher_seed_advances()
    {
        var r = await RunAsync(Settings(TournamentType.Knockout, ("A", "1"), ("B", "1")));
        Assert.Equal(3, r.Runs.Count); // the match and two tiebreaks
        Assert.Equal("A", r.Outcome.Ranking[0]);
        Assert.Contains(r.Events, e => e is TournamentNoteEvent n && n.Message.Contains("higher seed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swiss_plays_every_round_without_rematches()
    {
        var r = await RunAsync(Settings(TournamentType.Swiss, ("A", "4"), ("B", "3"), ("C", "2"), ("D", "1")));
        Assert.Equal(6, r.Runs.Count); // 3 rounds x 2 pairings
        var pairings = r.Runs.Select(run => string.Join("-", Names(run).Order())).ToList();
        Assert.Equal(pairings.Count, pairings.Distinct().Count());
        Assert.Equal(["A", "B", "C", "D"], r.Outcome.Ranking);
    }

    [Fact]
    public async Task Pyramid_adds_one_engine_per_stage()
    {
        var r = await RunAsync(Settings(TournamentType.Pyramid, ("A", "1"), ("B", "3"), ("C", "2")));
        Assert.Equal(2, r.Runs.Count);
        Assert.Equal(["name=B", "name=A"], Names(r.Runs[0]));
        Assert.Equal(["name=C", "name=A", "name=B"], Names(r.Runs[1]));
        Assert.All(r.Runs, run => Assert.Contains("gauntlet", run));
        Assert.Equal(["B", "C", "A"], r.Outcome.Ranking);
        Assert.Equal(2, r.Events.OfType<StageStartedEvent>().Count());
    }

    [Fact]
    public async Task A_failed_run_stops_the_tournament()
    {
        var r = await RunAsync(Settings(TournamentType.Swiss, ("A", "fail"), ("B", "1"), ("C", "3"), ("D", "0")));
        Assert.Equal(1, r.Outcome.ExitCode);
        Assert.Single(r.Runs);
        Assert.Empty(r.Outcome.Ranking);
    }

    [Fact]
    public async Task Round_robin_is_a_single_fastchess_run()
    {
        var r = await RunAsync(Settings(TournamentType.RoundRobin, ("A", "1"), ("B", "2"), ("C", "3")));
        Assert.Single(r.Runs);
        Assert.Equal(6, r.Events.OfType<GameFinishedEvent>().Count());
        // Every output line arrives as well, standard error included.
        Assert.Contains(r.Lines, l => l.Stream == OutputStream.StandardError && l.Text == "fake fastchess: 3 engines");
        Assert.Contains(r.Lines, l => l.Text == "Finished game 1 (A vs B): 0-1 {test}");
    }

    [Fact]
    public async Task Stopping_ends_the_run_and_reports_it()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var events = new List<FastchessEvent>();
        var dir = Directory.CreateTempSubdirectory("fcd-runner-").FullName;
        var outcome = await new TournamentRunner().RunAsync(FakePrograms.Fastchess, dir,
            Settings(TournamentType.RoundRobin, ("A", "hang"), ("B", "1")),
            _ => { }, e =>
            {
                lock (events) events.Add(e);
                if (e is GameStartedEvent) cts.Cancel();
            }, cts.Token);
        Assert.True(outcome.Cancelled);
        Assert.Contains(events, e => e is GameStartedEvent);
    }

    [Fact]
    public async Task A_missing_fastchess_is_reported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => new TournamentRunner().RunAsync(
            Path.Combine(AppContext.BaseDirectory, "no-fastchess.exe"), ".", Settings(TournamentType.RoundRobin, ("A", "1"), ("B", "2")),
            _ => { }, _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Staged_formats_are_validated_and_previewed()
    {
        var swiss = Settings(TournamentType.Swiss, ("A", "1"), ("B", "1"), ("C", "1")) with { SwissRounds = 3, Sprt = true };
        var errors = FastchessCommandBuilder.Validate(swiss);
        Assert.Contains(errors, e => e.StartsWith("SPRT is only available", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Swiss rounds must be", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => FastchessCommandBuilder.Build(swiss));
        Assert.Contains("roundrobin", TournamentRunner.FirstStageArguments(swiss));
        // 5 engines: 3 Swiss rounds x 2 pairings (one bye) x 1 game pair x 2 games.
        Assert.Equal(12, Settings(TournamentType.Swiss, ("A", "1"), ("B", "1"), ("C", "1"), ("D", "1"), ("E", "1")).ExpectedGames);
    }
}
