using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastchessDesktop.Core.Tools;

namespace FastchessDesktop.Core.Native;

/// <summary>
/// P/Invoke declarations for the command-line, tournament, statistics and pairing parts of
/// native/include/fcd/fcd.h. Keep in sync with the header.
/// </summary>
internal static unsafe partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FcdMatchStats
    {
        public int Wins, Draws, Losses;
        public int LL, LD, WL, DD, WD, WW;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdElo
    {
        public double Elo, Error, NElo, NEloError, Los;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdSprtParams
    {
        public double Alpha, Beta, Elo0, Elo1;
        public int Model;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdSprtState
    {
        public double Llr, LowerBound, UpperBound, Fraction;
        public int Outcome;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FcdEngineTotals
    {
        public FcdMatchStats Stats;
        public int Failures;
        public int Warnings;
    }

    [LibraryImport(Library, EntryPoint = "fcd_cmdline_quote", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus CmdlineQuote(string argument, out nint text);

    [LibraryImport(Library, EntryPoint = "fcd_cmdline_format", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus CmdlineFormat(string argsJson, out nint text);

    [LibraryImport(Library, EntryPoint = "fcd_cmdline_split", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus CmdlineSplit(string? text, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_tournament_validate", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus TournamentValidate(string settingsJson, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_tournament_build_args", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus TournamentBuildArgs(string settingsJson, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_tournament_first_stage_args", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus TournamentFirstStageArgs(string settingsJson, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_tournament_expected_games", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus TournamentExpectedGames(string settingsJson, out long games);

    [LibraryImport(Library, EntryPoint = "fcd_engine_display_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus EngineDisplayName(string? name, string? command, out nint text);

    [LibraryImport(Library, EntryPoint = "fcd_fastchess_parse_line", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus FastchessParseLine(string line, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_is_engine_failure_reason", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int IsEngineFailureReason(string reason);

    [LibraryImport(Library, EntryPoint = "fcd_ordoprep_args", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus OrdoprepArgs(string settingsJson, string inputPgn, string outputPgn, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_ordo_args", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus OrdoArgs(string settingsJson, string inputPgn, string reportTxt, string ratingsCsv,
        out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_pgn_extract_args", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus PgnExtractArgs(int preset, string? customArgs, string? ecoPgn, string inputPgn,
        string outputPgn, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_elo_estimate")]
    public static partial FcdStatus EloEstimate(in FcdMatchStats stats, int pentanomial, out FcdElo elo);

    [LibraryImport(Library, EntryPoint = "fcd_sprt_evaluate")]
    public static partial FcdStatus SprtEvaluate(in FcdSprtParams parameters, in FcdMatchStats stats, int pentanomial,
        out FcdSprtState state);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_new")]
    public static partial FcdStatus ScoreboardNew(int gamesPerEncounter, int pentanomial, out nint board);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_free")]
    public static partial void ScoreboardFree(nint board);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_is_pentanomial")]
    public static partial int ScoreboardIsPentanomial(ScoreboardHandle board);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_add_engine", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ScoreboardAddEngine(ScoreboardHandle board, string name);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_add_game", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ScoreboardAddGame(ScoreboardHandle board, int number, string white, string black,
        string result, string? reason);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_add_warning", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ScoreboardAddWarning(ScoreboardHandle board, string engine);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_engines")]
    public static partial FcdStatus ScoreboardEngines(ScoreboardHandle board, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_engine", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ScoreboardEngine(ScoreboardHandle board, string engine, out FcdEngineTotals totals);

    [LibraryImport(Library, EntryPoint = "fcd_scoreboard_head_to_head", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus ScoreboardHeadToHead(ScoreboardHandle board, string engine, string opponent,
        out FcdMatchStats stats);

    [LibraryImport(Library, EntryPoint = "fcd_pairing_bracket_order")]
    public static partial FcdStatus PairingBracketOrder(int size, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_pairing_knockout_first_round", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus PairingKnockoutFirstRound(string seededJson, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_pairing_knockout_next_round", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus PairingKnockoutNextRound(string winnersJson, out nint json);

    [LibraryImport(Library, EntryPoint = "fcd_pairing_swiss_round", StringMarshalling = StringMarshalling.Utf8)]
    public static partial FcdStatus PairingSwissRound(string stateJson, out nint json);

    /// <summary>Checks the status and returns the library-owned string, freeing it.</summary>
    public static string TakeChecked(FcdStatus status, nint text)
    {
        if (status != FcdStatus.Ok)
        {
            if (text != 0) Free(text);
            Check(status);
        }
        return TakeString(text);
    }

    /// <summary>Checks the status and returns a JSON array of strings, freeing it.</summary>
    public static IReadOnlyList<string> TakeStringList(FcdStatus status, nint json) =>
        JsonSerializer.Deserialize(TakeChecked(status, json), ToolJsonContext.Default.ListString) ?? [];

    /// <summary>Like <see cref="Check"/>, but invalid input is reported as ArgumentException.</summary>
    public static void CheckArgument(FcdStatus status)
    {
        if (status == FcdStatus.Argument)
            throw new ArgumentException(Marshal.PtrToStringUTF8(LastError()) ?? "invalid argument");
        Check(status);
    }

    public static string ToJson(TournamentSettings settings) =>
        JsonSerializer.Serialize(settings, ToolJsonContext.Default.TournamentSettings);

    public static string ToJson(RatingSettings settings) =>
        JsonSerializer.Serialize(settings, ToolJsonContext.Default.RatingSettings);

    public static string ToJson(IEnumerable<string> items) =>
        JsonSerializer.Serialize(items.ToList(), ToolJsonContext.Default.ListString);
}

internal sealed class ScoreboardHandle : SafeHandle
{
    public ScoreboardHandle() : base(0, ownsHandle: true) { }

    public ScoreboardHandle(nint handle) : this() => SetHandle(handle);

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.ScoreboardFree(handle);
        return true;
    }
}

/// <summary>A pairing as the native pairing functions write it; Black is null for a bye.</summary>
internal sealed record PairingDto(string White, string? Black);

internal sealed record SwissRoundDto(List<PairingDto> Pairs, string? Bye);

internal sealed record SwissStateDto(
    IReadOnlyList<string> Seeded,
    IReadOnlyDictionary<string, double> Scores,
    IReadOnlyDictionary<string, List<string>> Opponents,
    IReadOnlyList<string> HadBye,
    IReadOnlyDictionary<string, int> WhiteCounts);

/// <summary>A parsed fastchess output line (fcd_fastchess_parse_line).</summary>
internal sealed record ParsedLineDto(string Kind, FastchessEventDto? Event, string? WarningEngine);

internal sealed record FastchessEventDto(
    string Type, int Number, int Total, string? White, string? Black, string? Result, string? Reason, string? Message,
    int Stage, string? Description, string? CommandLine, string? Engine = null, int Count = 0, string? File = null);

/// <summary>JSON exchanged with the native tool functions: camelCase properties, enum member names.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TournamentSettings))]
[JsonSerializable(typeof(RatingSettings))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<PairingDto>))]
[JsonSerializable(typeof(SwissRoundDto))]
[JsonSerializable(typeof(SwissStateDto))]
[JsonSerializable(typeof(ParsedLineDto))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
