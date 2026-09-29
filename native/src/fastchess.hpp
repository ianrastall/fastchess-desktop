// Tournament settings, the fastchess command line, and fastchess output parsing.
// Command-line syntax: assets/fastchess/fastchess-help.xml. Output formats: fastchess
// app/src/matchmaking/output/output_fastchess.hpp and output_cutechess.hpp.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "json.hpp"

namespace fcd::fastchess {

enum class TournamentType { RoundRobin, Gauntlet, Pyramid, Knockout, Swiss };
enum class LimitKind { TimeControl, FixedTimePerMove, Nodes, Depth };
enum class OpeningFormat { Epd, Pgn };
enum class OpeningOrder { Sequential, Random };
enum class PgnNotation { San, Lan, Uci };
enum class SprtModel { Normalized, Logistic, Bayesian };
enum class LogLevel { Trace, Info, Warn, Err, Fatal };

struct EngineOption {
    std::string name, value;
};

struct EngineSettings {
    std::string name, command, arguments, working_directory;
    std::vector<EngineOption> options;
};

// Mirrors FastchessDesktop.Core.Tools.TournamentSettings; read from its camelCase JSON form.
struct TournamentSettings {
    std::vector<EngineSettings> engines;
    TournamentType type = TournamentType::RoundRobin;
    int seeds = 1;
    int swiss_rounds = 5;
    int knockout_tiebreak_pairs = 2;
    int rounds = 10;
    int games_per_encounter = 2;
    int concurrency = 1;
    bool force_concurrency = false;
    LimitKind limit = LimitKind::TimeControl;
    std::string time_control = "10+0.1";
    double move_time_seconds = 1;
    long long nodes = 100000;
    int depth = 10;
    int time_margin_ms = 0;
    int threads = 1;
    int hash_mb = 16;
    std::string openings_file;
    OpeningFormat openings_format = OpeningFormat::Epd;
    OpeningOrder openings_order = OpeningOrder::Random;
    int opening_plies = 0;
    int opening_start = 1;
    std::string srand;
    bool draw_adjudication = false;
    int draw_move_number = 40, draw_move_count = 8, draw_score = 10;
    bool resign_adjudication = false;
    int resign_move_count = 3, resign_score = 600;
    bool resign_two_sided = false;
    int max_moves = 0;
    std::string tablebase_paths;
    int tablebase_pieces = 0;
    bool sprt = false;
    double sprt_elo0 = 0, sprt_elo1 = 2, sprt_alpha = 0.05, sprt_beta = 0.05;
    SprtModel sprt_model = SprtModel::Normalized;
    std::string event = "fastchess-desktop tournament", site;
    std::string pgn_out;
    PgnNotation pgn_notation = PgnNotation::San;
    bool pgn_append = true, pgn_minimal = false, pgn_search_info = false;
    std::string epd_out, state_file;
    int autosave_interval = 20;
    bool recover = true;
    std::string log_file;
    LogLevel log_level = LogLevel::Warn;
    bool log_engine_traffic = false;
    bool cutechess_output = false;
    int rating_interval = 10;
    bool report_penta = true;
    std::string extra_arguments;

    static TournamentSettings from_json(const json::Value& v);
    static TournamentSettings parse(const char* json_text);
};

bool is_run_by_fastchess(TournamentType type);
std::string type_name(TournamentType type);

// Configured name, or the executable's file name without extension (as fastchess does).
std::string engine_name(const EngineSettings& e);
std::string engine_name(const std::string& name, const std::string& command);

// Problems that would make fastchess refuse to start. Empty when the settings are usable.
std::vector<std::string> validate(const TournamentSettings& s);

// Arguments for one fastchess run. Throws for pyramid, knockout and Swiss, which run in stages.
std::vector<std::string> build(const TournamentSettings& s);

// Games the schedule will play, when it can be computed (knockout excludes tiebreaks).
std::optional<long long> expected_games(const TournamentSettings& s);

// Settings for one stage (a single fastchess run) of a staged tournament.
TournamentSettings stage_settings(const TournamentSettings& s, std::vector<EngineSettings> engines, TournamentType type,
                                  int rounds, int stage);

// The fastchess arguments of the first run, for the command preview.
std::vector<std::string> first_stage_arguments(const TournamentSettings& s);

// ---- Output ----------------------------------------------------------------

enum class LineKind { Normal, Warning, EngineFailure };

struct GameStarted {
    int number, total;
    std::string white, black;
};
struct GameFinished {
    int number;
    std::string white, black, result, reason;
};

struct ParsedLine {
    LineKind kind = LineKind::Normal;
    std::optional<GameStarted> started;
    std::optional<GameFinished> finished;
    std::optional<std::string> tournament_finished;  // "Tournament finished" or an SPRT conclusion
    std::optional<std::string> warning_engine;       // the engine a "Warning;" line is about
    std::optional<std::string> warning_message;      // its text without the move and engine: "Illegal PV move"
};

ParsedLine parse_line(const std::string& line);

// {"kind":"normal|warning|engineFailure","event":null|{...},"warningEngine":null|"name"}
std::string parsed_line_json(const ParsedLine& p);

}  // namespace fcd::fastchess
