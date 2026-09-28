namespace FastchessDesktop.Core.Tools;

public enum TournamentType
{
    RoundRobin,
    Gauntlet,
}

/// <summary>Search limit applied to every engine through -each. tc and st are mutually exclusive in fastchess.</summary>
public enum LimitKind
{
    TimeControl,
    FixedTimePerMove,
    Nodes,
    Depth,
}

public enum OpeningFormat
{
    Epd,
    Pgn,
}

public enum OpeningOrder
{
    Sequential,
    Random,
}

public enum PgnNotation
{
    San,
    Lan,
    Uci,
}

public enum SprtModel
{
    Normalized,
    Logistic,
    Bayesian,
}

public enum FastchessLogLevel
{
    Trace,
    Info,
    Warn,
    Err,
    Fatal,
}

public sealed record EngineOption(string Name, string Value);

public sealed record EngineSettings
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyList<EngineOption> Options { get; init; } = [];
}

/// <summary>Everything the Tournament page configures. Serialized into the settings file.</summary>
public sealed record TournamentSettings
{
    public IReadOnlyList<EngineSettings> Engines { get; init; } = [];

    public TournamentType Type { get; init; } = TournamentType.RoundRobin;
    public int Seeds { get; init; } = 1;
    public int Rounds { get; init; } = 10;
    public int GamesPerEncounter { get; init; } = 2;
    public int Concurrency { get; init; } = 1;
    public bool ForceConcurrency { get; init; }

    public LimitKind Limit { get; init; } = LimitKind.TimeControl;
    public string TimeControl { get; init; } = "10+0.1";
    public double MoveTimeSeconds { get; init; } = 1;
    public long Nodes { get; init; } = 100_000;
    public int Depth { get; init; } = 10;
    public int TimeMarginMs { get; init; }

    /// <summary>Applied as option.Threads through -each when greater than zero.</summary>
    public int Threads { get; init; } = 1;

    /// <summary>Applied as option.Hash (MB) through -each when greater than zero.</summary>
    public int HashMb { get; init; } = 16;

    public string OpeningsFile { get; init; } = "";
    public OpeningFormat OpeningsFormat { get; init; } = OpeningFormat.Epd;
    public OpeningOrder OpeningsOrder { get; init; } = OpeningOrder.Random;

    /// <summary>Plies to use from each PGN opening; 0 or less means all.</summary>
    public int OpeningPlies { get; init; }

    public int OpeningStart { get; init; } = 1;

    /// <summary>Shuffle seed; empty lets fastchess choose.</summary>
    public string Srand { get; init; } = "";

    public bool DrawAdjudication { get; init; }
    public int DrawMoveNumber { get; init; } = 40;
    public int DrawMoveCount { get; init; } = 8;
    public int DrawScore { get; init; } = 10;

    public bool ResignAdjudication { get; init; }
    public int ResignMoveCount { get; init; } = 3;
    public int ResignScore { get; init; } = 600;
    public bool ResignTwoSided { get; init; }

    /// <summary>0 disables -maxmoves.</summary>
    public int MaxMoves { get; init; }

    public string TablebasePaths { get; init; } = "";
    public int TablebasePieces { get; init; }

    public bool Sprt { get; init; }
    public double SprtElo0 { get; init; }
    public double SprtElo1 { get; init; } = 2;
    public double SprtAlpha { get; init; } = 0.05;
    public double SprtBeta { get; init; } = 0.05;
    public SprtModel SprtModel { get; init; } = SprtModel.Normalized;

    public string Event { get; init; } = "fastchess-desktop tournament";
    public string Site { get; init; } = "";

    public string PgnOut { get; init; } = "";
    public PgnNotation PgnNotation { get; init; } = PgnNotation.San;
    public bool PgnAppend { get; init; } = true;
    public bool PgnMinimal { get; init; }
    public bool PgnSearchInfo { get; init; }
    public string EpdOut { get; init; } = "";

    /// <summary>JSON state file (-config outname=...). Empty keeps the fastchess default.</summary>
    public string StateFile { get; init; } = "";
    public int AutosaveInterval { get; init; } = 20;
    public bool Recover { get; init; } = true;

    public string LogFile { get; init; } = "";
    public FastchessLogLevel LogLevel { get; init; } = FastchessLogLevel.Warn;
    public bool LogEngineTraffic { get; init; }

    public bool CutechessOutput { get; init; }
    public int RatingInterval { get; init; } = 10;
    public bool ReportPenta { get; init; } = true;

    /// <summary>Additional raw arguments appended at the end, split with Windows rules.</summary>
    public string ExtraArguments { get; init; } = "";

    /// <summary>Games the schedule will play, when it can be computed.</summary>
    public long? ExpectedGames
    {
        get
        {
            var n = Engines.Count;
            if (n < 2 || Rounds <= 0 || GamesPerEncounter <= 0) return null;
            long pairs = Type == TournamentType.Gauntlet
                ? (long)Math.Clamp(Seeds, 1, n - 1) * (n - Math.Clamp(Seeds, 1, n - 1))
                : (long)n * (n - 1) / 2;
            return pairs * Rounds * GamesPerEncounter;
        }
    }
}
