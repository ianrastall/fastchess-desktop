using System.Globalization;

namespace FastchessDesktop.Core.Tools;

/// <summary>Translates <see cref="TournamentSettings"/> into fastchess arguments (see assets/fastchess/fastchess-help.xml).</summary>
public static class FastchessCommandBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Problems that would make fastchess refuse to start. Empty when the settings are usable.</summary>
    public static IReadOnlyList<string> Validate(TournamentSettings s)
    {
        var errors = new List<string>();
        if (s.Engines.Count < 2) errors.Add("At least two engines are required.");
        for (var i = 0; i < s.Engines.Count; i++)
        {
            var e = s.Engines[i];
            if (string.IsNullOrWhiteSpace(e.Command)) errors.Add($"Engine {i + 1} has no executable.");
            foreach (var o in e.Options)
                if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Contains('='))
                    errors.Add($"Engine {i + 1} has an invalid UCI option name '{o.Name}'.");
        }
        var names = s.Engines.Select(EngineName).Where(n => n.Length > 0).ToList();
        if (names.Count != names.Distinct(StringComparer.Ordinal).Count())
            errors.Add("Engine names must be unique, otherwise results cannot be told apart.");

        switch (s.Limit)
        {
            case LimitKind.TimeControl when string.IsNullOrWhiteSpace(s.TimeControl):
                errors.Add("Time control is empty (format [moves/]seconds[+increment], e.g. 10+0.1).");
                break;
            case LimitKind.FixedTimePerMove when s.MoveTimeSeconds <= 0:
                errors.Add("Time per move must be positive.");
                break;
            case LimitKind.Nodes when s.Nodes <= 0:
                errors.Add("Node limit must be positive.");
                break;
            case LimitKind.Depth when s.Depth <= 0:
                errors.Add("Depth limit must be positive.");
                break;
        }
        if (s.Rounds <= 0) errors.Add("Rounds must be positive.");
        if (s.GamesPerEncounter is < 1 or > 2) errors.Add("Games per encounter must be 1 or 2.");
        if (s.Type == TournamentType.Gauntlet && (s.Seeds < 1 || s.Seeds >= s.Engines.Count))
            errors.Add("Gauntlet seeds must be at least 1 and fewer than the number of engines.");
        if (s.Sprt && s.Type is not (TournamentType.RoundRobin or TournamentType.Gauntlet))
            errors.Add("SPRT is only available for round robin and gauntlet tournaments.");
        if (s.Type == TournamentType.Swiss && (s.SwissRounds < 1 || s.SwissRounds >= s.Engines.Count))
            errors.Add("Swiss rounds must be at least 1 and fewer than the number of engines.");
        if (s.Type == TournamentType.Knockout && s.KnockoutTiebreakPairs < 0)
            errors.Add("Knockout tiebreak pairs cannot be negative.");
        if (s.Sprt && s.Engines.Count != 2) errors.Add("SPRT requires exactly two engines.");
        if (s.Sprt && s.SprtElo1 <= s.SprtElo0) errors.Add("SPRT elo1 must be greater than elo0.");
        if (s.Sprt && (s.SprtAlpha is <= 0 or >= 1 || s.SprtBeta is <= 0 or >= 1))
            errors.Add("SPRT alpha and beta must be between 0 and 1.");
        return errors;
    }

    /// <summary>Arguments for one fastchess run. Only round robin and gauntlet are fastchess formats.</summary>
    public static IReadOnlyList<string> Build(TournamentSettings s)
    {
        if (s.Type is not (TournamentType.RoundRobin or TournamentType.Gauntlet))
            throw new ArgumentException($"{s.Type} is run in stages by TournamentRunner, not by a single fastchess command.", nameof(s));
        var a = new List<string>();

        foreach (var e in s.Engines)
        {
            a.Add("-engine");
            a.Add("cmd=" + e.Command);
            a.Add("name=" + EngineName(e));
            if (!string.IsNullOrWhiteSpace(e.Arguments)) a.Add("args=" + e.Arguments.Trim());
            if (!string.IsNullOrWhiteSpace(e.WorkingDirectory)) a.Add("dir=" + e.WorkingDirectory);
            foreach (var o in e.Options) a.Add($"option.{o.Name.Trim()}={o.Value}");
        }

        a.Add("-each");
        a.Add(s.Limit switch
        {
            LimitKind.TimeControl => "tc=" + s.TimeControl.Trim(),
            LimitKind.FixedTimePerMove => "st=" + s.MoveTimeSeconds.ToString(Inv),
            LimitKind.Nodes => "nodes=" + s.Nodes.ToString(Inv),
            LimitKind.Depth => "depth=" + s.Depth.ToString(Inv),
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.Limit, "unknown limit"),
        });
        if (s.TimeMarginMs > 0) a.Add("timemargin=" + s.TimeMarginMs.ToString(Inv));
        if (s.Threads > 0) a.Add("option.Threads=" + s.Threads.ToString(Inv));
        if (s.HashMb > 0) a.Add("option.Hash=" + s.HashMb.ToString(Inv));

        a.Add("-tournament");
        a.Add(s.Type == TournamentType.Gauntlet ? "gauntlet" : "roundrobin");
        if (s.Type == TournamentType.Gauntlet) a.AddRange(["-seeds", s.Seeds.ToString(Inv)]);
        a.AddRange(["-rounds", s.Rounds.ToString(Inv), "-games", s.GamesPerEncounter.ToString(Inv)]);
        a.AddRange(["-concurrency", s.Concurrency.ToString(Inv)]);
        if (s.ForceConcurrency) a.Add("-force-concurrency");

        if (!string.IsNullOrWhiteSpace(s.OpeningsFile))
        {
            a.Add("-openings");
            a.Add("file=" + s.OpeningsFile);
            a.Add("format=" + (s.OpeningsFormat == OpeningFormat.Pgn ? "pgn" : "epd"));
            a.Add("order=" + (s.OpeningsOrder == OpeningOrder.Random ? "random" : "sequential"));
            if (s.OpeningPlies > 0) a.Add("plies=" + s.OpeningPlies.ToString(Inv));
            if (s.OpeningStart > 1) a.Add("start=" + s.OpeningStart.ToString(Inv));
        }
        if (!string.IsNullOrWhiteSpace(s.Srand)) a.AddRange(["-srand", s.Srand.Trim()]);

        if (s.DrawAdjudication)
            a.AddRange(["-draw", $"movenumber={s.DrawMoveNumber}", $"movecount={s.DrawMoveCount}", $"score={s.DrawScore}"]);
        if (s.ResignAdjudication)
        {
            a.AddRange(["-resign", $"movecount={s.ResignMoveCount}", $"score={s.ResignScore}"]);
            if (s.ResignTwoSided) a.Add("twosided=true");
        }
        if (s.MaxMoves > 0) a.AddRange(["-maxmoves", s.MaxMoves.ToString(Inv)]);
        if (!string.IsNullOrWhiteSpace(s.TablebasePaths))
        {
            a.AddRange(["-tb", s.TablebasePaths.Trim()]);
            if (s.TablebasePieces > 0) a.AddRange(["-tbpieces", s.TablebasePieces.ToString(Inv)]);
        }

        if (s.Sprt)
        {
            a.AddRange(["-sprt",
                "elo0=" + s.SprtElo0.ToString(Inv), "elo1=" + s.SprtElo1.ToString(Inv),
                "alpha=" + s.SprtAlpha.ToString(Inv), "beta=" + s.SprtBeta.ToString(Inv),
                "model=" + s.SprtModel.ToString().ToLowerInvariant()]);
        }

        if (!string.IsNullOrWhiteSpace(s.Event)) a.AddRange(["-event", s.Event.Trim()]);
        if (!string.IsNullOrWhiteSpace(s.Site)) a.AddRange(["-site", s.Site.Trim()]);

        if (!string.IsNullOrWhiteSpace(s.PgnOut))
        {
            a.AddRange(["-pgnout", "file=" + s.PgnOut, "notation=" + s.PgnNotation.ToString().ToLowerInvariant(),
                "append=" + Bool(s.PgnAppend)]);
            if (s.PgnMinimal) a.Add("min=true");
            if (s.PgnSearchInfo) a.AddRange(["nodes=true", "seldepth=true", "nps=true"]);
        }
        if (!string.IsNullOrWhiteSpace(s.EpdOut)) a.AddRange(["-epdout", "file=" + s.EpdOut]);

        if (!string.IsNullOrWhiteSpace(s.StateFile)) a.AddRange(["-config", "outname=" + s.StateFile]);
        a.AddRange(["-autosaveinterval", s.AutosaveInterval.ToString(Inv)]);
        if (s.Recover) a.Add("-recover");

        if (!string.IsNullOrWhiteSpace(s.LogFile))
        {
            a.AddRange(["-log", "file=" + s.LogFile, "level=" + s.LogLevel.ToString().ToLowerInvariant()]);
            if (s.LogEngineTraffic) a.Add("engine=true");
        }

        if (s.CutechessOutput) a.AddRange(["-output", "format=cutechess"]);
        a.AddRange(["-ratinginterval", s.RatingInterval.ToString(Inv)]);
        if (!s.ReportPenta) a.AddRange(["-report", "penta=false"]);

        a.AddRange(CommandLine.Split(s.ExtraArguments));
        return a;
    }

    /// <summary>Configured name, or the executable's file name without extension (as fastchess does).</summary>
    public static string EngineName(EngineSettings e) =>
        !string.IsNullOrWhiteSpace(e.Name) ? e.Name.Trim()
        : string.IsNullOrWhiteSpace(e.Command) ? ""
        : Path.GetFileNameWithoutExtension(e.Command.Replace('\\', Path.DirectorySeparatorChar));

    private static string Bool(bool v) => v ? "true" : "false";
}
