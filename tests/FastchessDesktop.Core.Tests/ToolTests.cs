using FastchessDesktop.Core.Settings;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Program Files\x\", "\"C:\\Program Files\\x\\\\\"")]
    public void Quote_follows_windows_rules(string input, string expected) =>
        Assert.Equal(expected, CommandLine.Quote(input));

    [Fact]
    public void Split_round_trips_Format()
    {
        string[] args = ["-t", @"C:\My Files\tags.txt", "say \"hi\"", @"trailing\", "", "--plycount"];
        Assert.Equal(args, CommandLine.Split(CommandLine.Format(args)));
    }

    [Fact]
    public void Split_handles_empty_input() => Assert.Empty(CommandLine.Split("   "));
}

public class FastchessCommandBuilderTests
{
    private static TournamentSettings TwoEngines() => new()
    {
        Engines =
        [
            new EngineSettings { Name = "Alpha", Command = @"C:\Engines\alpha.exe", Options = [new("Threads", "2")] },
            new EngineSettings { Command = @"C:\Engines\Beta Engine.exe", Arguments = "--nnue big.nnue" },
        ],
        Rounds = 5,
        Concurrency = 2,
        OpeningsFile = @"C:\books\UHO.epd",
        PgnOut = @"C:\out\games.pgn",
        DrawAdjudication = true,
    };

    [Fact]
    public void Builds_engines_limits_and_outputs()
    {
        var args = FastchessCommandBuilder.Build(TwoEngines());
        var line = string.Join(" | ", args);

        Assert.Contains(@"-engine | cmd=C:\Engines\alpha.exe | name=Alpha | option.Threads=2", line);
        // Name falls back to the executable stem, as in fastchess itself.
        Assert.Contains(@"cmd=C:\Engines\Beta Engine.exe | name=Beta Engine | args=--nnue big.nnue", line);
        Assert.Contains("-each | tc=10+0.1 | option.Threads=1 | option.Hash=16", line);
        Assert.Contains("-tournament | roundrobin | -rounds | 5 | -games | 2 | -concurrency | 2", line);
        Assert.Contains(@"-openings | file=C:\books\UHO.epd | format=epd | order=random", line);
        Assert.Contains("-draw | movenumber=40 | movecount=8 | score=10", line);
        Assert.Contains(@"-pgnout | file=C:\out\games.pgn | notation=san | append=true", line);
        Assert.Contains("-recover", args);
        Assert.DoesNotContain("-sprt", args);
    }

    [Fact]
    public void Limit_kinds_are_mutually_exclusive()
    {
        var s = TwoEngines() with { Limit = LimitKind.FixedTimePerMove, MoveTimeSeconds = 0.5 };
        var args = FastchessCommandBuilder.Build(s);
        Assert.Contains("st=0.5", args);
        Assert.DoesNotContain(args, a => a.StartsWith("tc=", StringComparison.Ordinal));
    }

    [Fact]
    public void Sprt_uses_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var args = FastchessCommandBuilder.Build(TwoEngines() with { Sprt = true, SprtElo1 = 2.5 });
            Assert.Contains("elo1=2.5", args);
            Assert.Contains("model=normalized", args);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Validation_reports_problems()
    {
        var s = new TournamentSettings
        {
            Engines = [new EngineSettings { Name = "A", Command = "a.exe" }, new EngineSettings { Name = "A" },
                new EngineSettings { Name = "C", Command = "c.exe" }],
            Sprt = true,
            Limit = LimitKind.Nodes,
            Nodes = 0,
        };
        var errors = FastchessCommandBuilder.Validate(s);
        Assert.Contains(errors, e => e.Contains("Engine 2 has no executable", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unique", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Node limit", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("SPRT requires exactly two", StringComparison.Ordinal));
        Assert.Empty(FastchessCommandBuilder.Validate(TwoEngines()));
    }

    [Theory]
    [InlineData(TournamentType.RoundRobin, 4, 1, 10, 2, 120)]
    [InlineData(TournamentType.Gauntlet, 4, 1, 10, 2, 60)]
    [InlineData(TournamentType.Gauntlet, 5, 2, 1, 2, 12)]
    public void Expected_games(TournamentType type, int engines, int seeds, int rounds, int games, long expected)
    {
        var s = new TournamentSettings
        {
            Type = type,
            Seeds = seeds,
            Rounds = rounds,
            GamesPerEncounter = games,
            Engines = [.. Enumerable.Range(0, engines).Select(i => new EngineSettings { Name = $"E{i}", Command = "x" })],
        };
        Assert.Equal(expected, s.ExpectedGames);
    }
}

public class FastchessOutputParserTests
{
    [Fact]
    public void Parses_started_and_finished_lines()
    {
        var started = Assert.IsType<GameStartedEvent>(FastchessOutputParser.Parse("Started game 3 of 40 (Alpha vs Beta Engine)"));
        Assert.Equal(new GameStartedEvent(3, 40, "Alpha", "Beta Engine"), started);

        var finished = Assert.IsType<GameFinishedEvent>(
            FastchessOutputParser.Parse("Finished game 3 (Alpha vs Beta Engine): 1/2-1/2 {Draw by 3-fold repetition}"));
        Assert.Equal(new GameFinishedEvent(3, "Alpha", "Beta Engine", "1/2-1/2", "Draw by 3-fold repetition"), finished);
    }

    [Fact]
    public void Recognizes_tournament_end()
    {
        Assert.IsType<TournamentFinishedEvent>(FastchessOutputParser.Parse("Tournament finished"));
        Assert.IsType<TournamentFinishedEvent>(
            FastchessOutputParser.Parse("SPRT ([0.00, 2.00]) completed - H1 was accepted"));
        Assert.Null(FastchessOutputParser.Parse("Elo: 12.3 +/- 4.5, nElo: 20.1 +/- 7.7"));
    }

    [Theory]
    // A PV check block as fastchess 1.8.2 prints it (match.cpp verifyPvLines).
    [InlineData("Warning; Bestmove does not match beginning of last PV - move f7e6 from Berserk 14", FastchessLineKind.Warning)]
    [InlineData("Info; info depth 14 seldepth 17 multipv 1 score cp 0 nodes 16436 pv a4a2 c6c7", FastchessLineKind.Warning)]
    [InlineData("Position; fen r1bq1rk1/pp2ppbp/2np1np1/8/3NPP2/2N1B3/PPP1B1PP/R2QK2R w KQ - 0 1", FastchessLineKind.Warning)]
    [InlineData("Moves; d1d2 c6d4 e3d4", FastchessLineKind.Warning)]
    [InlineData("Finished game 4 (Reckless 0.9.0 vs Berserk 14): 0-1 {Black mates}", FastchessLineKind.Normal)]
    [InlineData("Finished game 9 (A vs B): 1/2-1/2 {Draw by insufficient mating material}", FastchessLineKind.Normal)]
    [InlineData("Finished game 2 (A vs B): 0-1 {White loses on time (12ms overrun)}", FastchessLineKind.EngineFailure)]
    [InlineData("Finished game 2 (A vs B): 1-0 {Black disconnects}", FastchessLineKind.EngineFailure)]
    [InlineData("Finished game 2 (A vs B): 1-0 {Black's connection stalls}", FastchessLineKind.EngineFailure)]
    [InlineData("Finished game 2 (A vs B): 0-1 {White makes an illegal move}", FastchessLineKind.EngineFailure)]
    [InlineData("  Timeouts: 3", FastchessLineKind.EngineFailure)]
    [InlineData("  Crashed: 0", FastchessLineKind.Normal)]
    [InlineData("Games: 20, Wins: 1, Losses: 5, Draws: 14, Points: 8.0 (40.00 %)", FastchessLineKind.Normal)]
    public void Classifies_warnings_and_engine_failures(string line, FastchessLineKind expected) =>
        Assert.Equal(expected, FastchessOutputParser.Classify(line));

    [Theory]
    [InlineData("Warning; Bestmove does not match beginning of last PV - move f7e6 from Berserk 14", "Berserk 14")]
    [InlineData("Warning; Incomplete mating PV - from Engine X 2.0", "Engine X 2.0")]
    [InlineData("Info; info depth 14 pv a4a2", null)]
    [InlineData("Warning; Failed to set CPU affinity for the tournament thread.", null)]
    public void Finds_the_engine_a_warning_is_about(string line, string? engine) =>
        Assert.Equal(engine, FastchessOutputParser.WarningEngine(line));

    [Fact]
    public void Finished_games_report_engine_failures()
    {
        var timeout = Assert.IsType<GameFinishedEvent>(
            FastchessOutputParser.Parse("Finished game 2 (A vs B): 0-1 {White loses on time (12ms overrun)}"));
        Assert.True(timeout.IsEngineFailure);
        var mate = Assert.IsType<GameFinishedEvent>(FastchessOutputParser.Parse("Finished game 1 (A vs B): 1-0 {White mates}"));
        Assert.False(mate.IsEngineFailure);
    }
}

public class RatingToolArgsTests
{
    [Fact]
    public void Ordo_and_ordoprep_arguments()
    {
        var s = new RatingSettings { AnchorPlayer = "Stockfish 19", Simulations = 100, Cpus = 4 };
        // Ordoprep rejects more than one structural filter, so exactly one is emitted.
        Assert.Equal(["-p", "in.pgn", "-o", "out.pgn", "-d"], RatingToolArgs.Ordoprep(s, "in.pgn", "out.pgn"));
        Assert.Equal(["-p", "in.pgn", "-o", "out.pgn", "-M", "10"],
            RatingToolArgs.Ordoprep(s with { OrdoprepFilter = OrdoprepFilter.MinGames }, "in.pgn", "out.pgn"));
        Assert.Equal(
            ["-p", "in.pgn", "-o", "r.txt", "-c", "r.csv", "-a", "2300", "-A", "Stockfish 19", "-W", "-D", "-s", "100", "-n", "4", "-N", "1"],
            RatingToolArgs.Ordo(s, "in.pgn", "r.txt", "r.csv"));
    }

    [Fact]
    public void Pgn_extract_presets()
    {
        Assert.Equal(["-eC:\\eco.pgn", "--quiet", "-o", "out.pgn", "in.pgn"],
            PgnExtractArgs.Build(PgnExtractPreset.ClassifyOpenings, "", @"C:\eco.pgn", "in.pgn", "out.pgn"));
        Assert.Equal(["-D", "-Tw\"Alpha\"", "--quiet", "-o", "out.pgn", "in.pgn"],
            PgnExtractArgs.Build(PgnExtractPreset.RemoveDuplicates, "-Tw\\\"Alpha\\\"", "", "in.pgn", "out.pgn"));
        Assert.Throws<ArgumentException>(() =>
            PgnExtractArgs.Build(PgnExtractPreset.ClassifyOpenings, "", "", "in.pgn", "out.pgn"));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_and_tolerates_corruption()
    {
        var dir = Directory.CreateTempSubdirectory("fcd-settings");
        try
        {
            var store = new SettingsStore(Path.Combine(dir.FullName, "sub", "settings.json"));
            Assert.Equal(new AppSettings().PageSize, store.Load().PageSize);

            var settings = new AppSettings
            {
                LastDatabasePath = @"D:\games.fcdb",
                Tournament = new TournamentSettings
                {
                    Engines = [new EngineSettings { Name = "A", Command = "a.exe", Options = [new("Hash", "64")] }],
                    Limit = LimitKind.Depth,
                },
            };
            store.Save(settings);
            var loaded = store.Load();
            Assert.Equal(@"D:\games.fcdb", loaded.LastDatabasePath);
            Assert.Equal(LimitKind.Depth, loaded.Tournament.Limit);
            Assert.Equal(new EngineOption("Hash", "64"), loaded.Tournament.Engines[0].Options[0]);
            Assert.Contains("\"limit\": \"Depth\"", File.ReadAllText(store.Path), StringComparison.Ordinal);

            File.WriteAllText(store.Path, "{ not json");
            Assert.Equal("", store.Load().LastDatabasePath);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class ToolPathsTests
{
    [Fact]
    public void Discovers_bundled_layout_without_overriding_configured_paths()
    {
        var dir = Directory.CreateTempSubdirectory("fcd-tools");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "tools", "fastchess"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "tools", "stockfish", "sf19"));
            File.WriteAllText(Path.Combine(dir.FullName, "tools", "fastchess", "fastchess.exe"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "tools", "stockfish", "sf19", "stockfish-windows-x86-64-avx2.exe"), "");

            var paths = new ToolPaths { Ordo = @"D:\custom\ordo.exe" }.WithDefaults(dir.FullName);
            Assert.EndsWith(Path.Combine("tools", "fastchess", "fastchess.exe"), paths.Fastchess, StringComparison.Ordinal);
            Assert.EndsWith("stockfish-windows-x86-64-avx2.exe", paths.Stockfish, StringComparison.Ordinal);
            Assert.Equal(@"D:\custom\ordo.exe", paths.Ordo);
            Assert.Equal("", paths.PgnExtract);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task Streams_output_and_reports_exit_code()
    {
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "echo one& echo two 1>&2& exit 3"])
            : new ProcessSpec("/bin/sh", ["-c", "echo one; echo two 1>&2; exit 3"]);
        var lines = new List<OutputLine>();
        var result = await ProcessRunner.RunAsync(spec, l => { lock (lines) lines.Add(l); }, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.Contains(lines, l => l.Stream == OutputStream.StandardOutput && l.Text.Trim() == "one");
        Assert.Contains(lines, l => l.Stream == OutputStream.StandardError && l.Text.Trim() == "two");
    }

    [Fact]
    public async Task Cancellation_kills_the_process()
    {
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "ping -n 30 127.0.0.1"])
            : new ProcessSpec("/bin/sh", ["-c", "sleep 30"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await ProcessRunner.RunAsync(spec, null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
    }
}
