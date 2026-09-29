#include "fastchess.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdio>
#include <regex>
#include <set>

#include "cmdline.hpp"
#include "formats.hpp"
#include "stats.hpp"
#include "util.hpp"

namespace fcd::fastchess {

namespace {

// Enum members arrive as names ("RoundRobin") from System.Text.Json, or as numbers.
template <typename E, size_t N>
E read_enum(const json::Value& v, std::string_view key, const std::array<std::string_view, N>& names, E fallback) {
    const json::Value* m = v.find(key);
    if (!m) return fallback;
    if (m->type() == json::Value::Type::Number) {
        const auto i = static_cast<long long>(m->as_number());
        return i >= 0 && i < static_cast<long long>(N) ? static_cast<E>(i) : fallback;
    }
    for (size_t i = 0; i < N; ++i)
        if (iequals(m->as_string(), names[i])) return static_cast<E>(i);
    return fallback;
}

constexpr std::array<std::string_view, 5> kTypeNames{"RoundRobin", "Gauntlet", "Pyramid", "Knockout", "Swiss"};
constexpr std::array<std::string_view, 4> kLimitNames{"TimeControl", "FixedTimePerMove", "Nodes", "Depth"};
constexpr std::array<std::string_view, 2> kOpeningFormatNames{"Epd", "Pgn"};
constexpr std::array<std::string_view, 2> kOpeningOrderNames{"Sequential", "Random"};
constexpr std::array<std::string_view, 3> kNotationNames{"San", "Lan", "Uci"};
constexpr std::array<std::string_view, 3> kSprtModelNames{"Normalized", "Logistic", "Bayesian"};
constexpr std::array<std::string_view, 5> kLogLevelNames{"Trace", "Info", "Warn", "Err", "Fatal"};

std::string lower(std::string_view s) {
    std::string out(s);
    for (auto& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

bool blank(const std::string& s) { return trim(s).empty(); }

int to_int(const json::Value& v, std::string_view key, int fallback) {
    return static_cast<int>(v.get_integer(key, fallback));
}

std::string num(double v) { return json::number(v); }

}  // namespace

TournamentSettings TournamentSettings::from_json(const json::Value& v) {
    if (!v.is_object()) throw Error(FCD_ERR_ARGUMENT, "tournament settings must be a JSON object");
    TournamentSettings s;
    if (const auto* engines = v.find("engines")) {
        for (const auto& e : engines->items()) {
            EngineSettings es;
            es.name = e.get_string("name");
            es.command = e.get_string("command");
            es.arguments = e.get_string("arguments");
            es.working_directory = e.get_string("workingDirectory");
            if (const auto* options = e.find("options"))
                for (const auto& o : options->items()) es.options.push_back({o.get_string("name"), o.get_string("value")});
            s.engines.push_back(std::move(es));
        }
    }
    s.type = read_enum(v, "type", kTypeNames, s.type);
    s.seeds = to_int(v, "seeds", s.seeds);
    s.swiss_rounds = to_int(v, "swissRounds", s.swiss_rounds);
    s.knockout_tiebreak_pairs = to_int(v, "knockoutTiebreakPairs", s.knockout_tiebreak_pairs);
    s.rounds = to_int(v, "rounds", s.rounds);
    s.games_per_encounter = to_int(v, "gamesPerEncounter", s.games_per_encounter);
    s.concurrency = to_int(v, "concurrency", s.concurrency);
    s.force_concurrency = v.get_bool("forceConcurrency", s.force_concurrency);
    s.limit = read_enum(v, "limit", kLimitNames, s.limit);
    s.time_control = v.get_string("timeControl", s.time_control);
    s.move_time_seconds = v.get_number("moveTimeSeconds", s.move_time_seconds);
    s.nodes = v.get_integer("nodes", s.nodes);
    s.depth = to_int(v, "depth", s.depth);
    s.time_margin_ms = to_int(v, "timeMarginMs", s.time_margin_ms);
    s.threads = to_int(v, "threads", s.threads);
    s.hash_mb = to_int(v, "hashMb", s.hash_mb);
    s.openings_file = v.get_string("openingsFile");
    s.openings_format = read_enum(v, "openingsFormat", kOpeningFormatNames, s.openings_format);
    s.openings_order = read_enum(v, "openingsOrder", kOpeningOrderNames, s.openings_order);
    s.opening_plies = to_int(v, "openingPlies", s.opening_plies);
    s.opening_start = to_int(v, "openingStart", s.opening_start);
    s.srand = v.get_string("srand");
    s.draw_adjudication = v.get_bool("drawAdjudication", s.draw_adjudication);
    s.draw_move_number = to_int(v, "drawMoveNumber", s.draw_move_number);
    s.draw_move_count = to_int(v, "drawMoveCount", s.draw_move_count);
    s.draw_score = to_int(v, "drawScore", s.draw_score);
    s.resign_adjudication = v.get_bool("resignAdjudication", s.resign_adjudication);
    s.resign_move_count = to_int(v, "resignMoveCount", s.resign_move_count);
    s.resign_score = to_int(v, "resignScore", s.resign_score);
    s.resign_two_sided = v.get_bool("resignTwoSided", s.resign_two_sided);
    s.max_moves = to_int(v, "maxMoves", s.max_moves);
    s.tablebase_paths = v.get_string("tablebasePaths");
    s.tablebase_pieces = to_int(v, "tablebasePieces", s.tablebase_pieces);
    s.sprt = v.get_bool("sprt", s.sprt);
    s.sprt_elo0 = v.get_number("sprtElo0", s.sprt_elo0);
    s.sprt_elo1 = v.get_number("sprtElo1", s.sprt_elo1);
    s.sprt_alpha = v.get_number("sprtAlpha", s.sprt_alpha);
    s.sprt_beta = v.get_number("sprtBeta", s.sprt_beta);
    s.sprt_model = read_enum(v, "sprtModel", kSprtModelNames, s.sprt_model);
    s.event = v.get_string("event", s.event);
    s.site = v.get_string("site");
    s.pgn_out = v.get_string("pgnOut");
    s.pgn_notation = read_enum(v, "pgnNotation", kNotationNames, s.pgn_notation);
    s.pgn_append = v.get_bool("pgnAppend", s.pgn_append);
    s.pgn_minimal = v.get_bool("pgnMinimal", s.pgn_minimal);
    s.pgn_search_info = v.get_bool("pgnSearchInfo", s.pgn_search_info);
    s.epd_out = v.get_string("epdOut");
    s.state_file = v.get_string("stateFile");
    s.autosave_interval = to_int(v, "autosaveInterval", s.autosave_interval);
    s.recover = v.get_bool("recover", s.recover);
    s.log_file = v.get_string("logFile");
    s.log_level = read_enum(v, "logLevel", kLogLevelNames, s.log_level);
    s.log_engine_traffic = v.get_bool("logEngineTraffic", s.log_engine_traffic);
    s.cutechess_output = v.get_bool("cutechessOutput", s.cutechess_output);
    s.rating_interval = to_int(v, "ratingInterval", s.rating_interval);
    s.report_penta = v.get_bool("reportPenta", s.report_penta);
    s.extra_arguments = v.get_string("extraArguments");
    return s;
}

TournamentSettings TournamentSettings::parse(const char* json_text) {
    if (!json_text) throw Error(FCD_ERR_ARGUMENT, "settings_json is NULL");
    return from_json(json::Value::parse(json_text));
}

bool is_run_by_fastchess(TournamentType type) {
    return type == TournamentType::RoundRobin || type == TournamentType::Gauntlet;
}

std::string type_name(TournamentType type) { return std::string(kTypeNames[static_cast<size_t>(type)]); }

std::string engine_name(const std::string& name, const std::string& command) {
    if (!blank(name)) return trim(name);
    if (blank(command)) return "";
    std::string file = command;
    if (const auto slash = file.find_last_of("/\\"); slash != std::string::npos) file = file.substr(slash + 1);
    if (const auto dot = file.rfind('.'); dot != std::string::npos) file = file.substr(0, dot);
    return file;
}

std::string engine_name(const EngineSettings& e) { return engine_name(e.name, e.command); }

std::vector<std::string> validate(const TournamentSettings& s) {
    std::vector<std::string> errors;
    const auto n = static_cast<int>(s.engines.size());
    if (n < 2) errors.push_back("At least two engines are required.");
    for (int i = 0; i < n; ++i) {
        const auto& e = s.engines[i];
        if (blank(e.command)) errors.push_back("Engine " + std::to_string(i + 1) + " has no executable.");
        for (const auto& o : e.options)
            if (blank(o.name) || o.name.find('=') != std::string::npos)
                errors.push_back("Engine " + std::to_string(i + 1) + " has an invalid UCI option name '" + o.name + "'.");
    }
    std::vector<std::string> names;
    for (const auto& e : s.engines)
        if (auto name = engine_name(e); !name.empty()) names.push_back(std::move(name));
    if (std::set<std::string>(names.begin(), names.end()).size() != names.size())
        errors.push_back("Engine names must be unique, otherwise results cannot be told apart.");

    switch (s.limit) {
        case LimitKind::TimeControl:
            if (blank(s.time_control))
                errors.push_back("Time control is empty (format [moves/]seconds[+increment], e.g. 10+0.1).");
            break;
        case LimitKind::FixedTimePerMove:
            if (s.move_time_seconds <= 0) errors.push_back("Time per move must be positive.");
            break;
        case LimitKind::Nodes:
            if (s.nodes <= 0) errors.push_back("Node limit must be positive.");
            break;
        case LimitKind::Depth:
            if (s.depth <= 0) errors.push_back("Depth limit must be positive.");
            break;
    }
    if (s.rounds <= 0) errors.push_back("Rounds must be positive.");
    if (s.games_per_encounter < 1 || s.games_per_encounter > 2) errors.push_back("Games per encounter must be 1 or 2.");
    if (s.type == TournamentType::Gauntlet && (s.seeds < 1 || s.seeds >= n))
        errors.push_back("Gauntlet seeds must be at least 1 and fewer than the number of engines.");
    if (s.sprt && !is_run_by_fastchess(s.type))
        errors.push_back("SPRT is only available for round robin and gauntlet tournaments.");
    if (s.type == TournamentType::Swiss && (s.swiss_rounds < 1 || s.swiss_rounds >= n))
        errors.push_back("Swiss rounds must be at least 1 and fewer than the number of engines.");
    if (s.type == TournamentType::Knockout && s.knockout_tiebreak_pairs < 0)
        errors.push_back("Knockout tiebreak pairs cannot be negative.");
    if (s.sprt && n != 2) errors.push_back("SPRT requires exactly two engines.");
    if (s.sprt && s.sprt_elo1 <= s.sprt_elo0) errors.push_back("SPRT elo1 must be greater than elo0.");
    if (s.sprt && (s.sprt_alpha <= 0 || s.sprt_alpha >= 1 || s.sprt_beta <= 0 || s.sprt_beta >= 1))
        errors.push_back("SPRT alpha and beta must be between 0 and 1.");
    return errors;
}

std::vector<std::string> build(const TournamentSettings& s) {
    if (!is_run_by_fastchess(s.type))
        throw Error(FCD_ERR_ARGUMENT,
                    type_name(s.type) + " is run in stages by TournamentRunner, not by a single fastchess command.");
    std::vector<std::string> a;
    auto add = [&a](std::initializer_list<std::string> items) { a.insert(a.end(), items); };

    for (const auto& e : s.engines) {
        add({"-engine", "cmd=" + e.command, "name=" + engine_name(e)});
        if (!blank(e.arguments)) a.push_back("args=" + trim(e.arguments));
        if (!blank(e.working_directory)) a.push_back("dir=" + e.working_directory);
        for (const auto& o : e.options) a.push_back("option." + trim(o.name) + "=" + o.value);
    }

    a.push_back("-each");
    switch (s.limit) {
        case LimitKind::TimeControl: a.push_back("tc=" + trim(s.time_control)); break;
        case LimitKind::FixedTimePerMove: a.push_back("st=" + num(s.move_time_seconds)); break;
        case LimitKind::Nodes: a.push_back("nodes=" + std::to_string(s.nodes)); break;
        case LimitKind::Depth: a.push_back("depth=" + std::to_string(s.depth)); break;
    }
    if (s.time_margin_ms > 0) a.push_back("timemargin=" + std::to_string(s.time_margin_ms));
    if (s.threads > 0) a.push_back("option.Threads=" + std::to_string(s.threads));
    if (s.hash_mb > 0) a.push_back("option.Hash=" + std::to_string(s.hash_mb));

    add({"-tournament", s.type == TournamentType::Gauntlet ? "gauntlet" : "roundrobin"});
    if (s.type == TournamentType::Gauntlet) add({"-seeds", std::to_string(s.seeds)});
    add({"-rounds", std::to_string(s.rounds), "-games", std::to_string(s.games_per_encounter)});
    add({"-concurrency", std::to_string(s.concurrency)});
    if (s.force_concurrency) a.push_back("-force-concurrency");

    if (!blank(s.openings_file)) {
        add({"-openings", "file=" + s.openings_file,
             std::string("format=") + (s.openings_format == OpeningFormat::Pgn ? "pgn" : "epd"),
             std::string("order=") + (s.openings_order == OpeningOrder::Random ? "random" : "sequential")});
        if (s.opening_plies > 0) a.push_back("plies=" + std::to_string(s.opening_plies));
        if (s.opening_start > 1) a.push_back("start=" + std::to_string(s.opening_start));
    }
    if (!blank(s.srand)) add({"-srand", trim(s.srand)});

    if (s.draw_adjudication)
        add({"-draw", "movenumber=" + std::to_string(s.draw_move_number), "movecount=" + std::to_string(s.draw_move_count),
             "score=" + std::to_string(s.draw_score)});
    if (s.resign_adjudication) {
        add({"-resign", "movecount=" + std::to_string(s.resign_move_count), "score=" + std::to_string(s.resign_score)});
        if (s.resign_two_sided) a.push_back("twosided=true");
    }
    if (s.max_moves > 0) add({"-maxmoves", std::to_string(s.max_moves)});
    if (!blank(s.tablebase_paths)) {
        add({"-tb", trim(s.tablebase_paths)});
        if (s.tablebase_pieces > 0) add({"-tbpieces", std::to_string(s.tablebase_pieces)});
    }

    if (s.sprt)
        add({"-sprt", "elo0=" + num(s.sprt_elo0), "elo1=" + num(s.sprt_elo1), "alpha=" + num(s.sprt_alpha),
             "beta=" + num(s.sprt_beta), "model=" + lower(kSprtModelNames[static_cast<size_t>(s.sprt_model)])});

    if (!blank(s.event)) add({"-event", trim(s.event)});
    if (!blank(s.site)) add({"-site", trim(s.site)});

    if (!blank(s.pgn_out)) {
        add({"-pgnout", "file=" + s.pgn_out, "notation=" + lower(kNotationNames[static_cast<size_t>(s.pgn_notation)]),
             std::string("append=") + (s.pgn_append ? "true" : "false")});
        if (s.pgn_minimal) a.push_back("min=true");
        if (s.pgn_search_info) add({"nodes=true", "seldepth=true", "nps=true"});
    }
    if (!blank(s.epd_out)) add({"-epdout", "file=" + s.epd_out});

    if (!blank(s.state_file)) add({"-config", "outname=" + s.state_file});
    add({"-autosaveinterval", std::to_string(s.autosave_interval)});
    if (s.recover) a.push_back("-recover");

    if (!blank(s.log_file)) {
        add({"-log", "file=" + s.log_file, "level=" + lower(kLogLevelNames[static_cast<size_t>(s.log_level)])});
        if (s.log_engine_traffic) a.push_back("engine=true");
    }

    if (s.cutechess_output) add({"-output", "format=cutechess"});
    add({"-ratinginterval", std::to_string(s.rating_interval)});
    if (!s.report_penta) add({"-report", "penta=false"});

    for (auto& extra : cmdline::split(s.extra_arguments)) a.push_back(std::move(extra));
    return a;
}

std::optional<long long> expected_games(const TournamentSettings& s) {
    const long long n = static_cast<long long>(s.engines.size());
    if (n < 2 || s.rounds <= 0 || s.games_per_encounter <= 0) return std::nullopt;
    long long pairs;
    switch (s.type) {
        case TournamentType::Gauntlet: {
            const long long seeds = std::clamp<long long>(s.seeds, 1, n - 1);
            pairs = seeds * (n - seeds);
            break;
        }
        case TournamentType::Knockout: pairs = n - 1; break;  // excluding tiebreaks
        case TournamentType::Swiss: pairs = std::max(0, s.swiss_rounds) * (n / 2); break;
        default: pairs = n * (n - 1) / 2; break;  // round robin and pyramid play the same pairings
    }
    return pairs * s.rounds * s.games_per_encounter;
}

TournamentSettings stage_settings(const TournamentSettings& s, std::vector<EngineSettings> engines, TournamentType type,
                                  int rounds, int stage) {
    TournamentSettings t = s;
    t.engines = std::move(engines);
    t.type = type;
    t.seeds = 1;
    t.rounds = rounds;
    t.sprt = false;
    // Later stages must not truncate the games written by earlier ones.
    t.pgn_append = stage == 1 ? s.pgn_append : true;
    char name[32];
    std::snprintf(name, sizeof name, "stage-%03d.json", stage);
    t.state_file = name;
    return t;
}

std::vector<std::string> first_stage_arguments(const TournamentSettings& s) {
    if (is_run_by_fastchess(s.type) || s.engines.size() < 2) {
        TournamentSettings single = s;
        if (!is_run_by_fastchess(single.type)) single.type = TournamentType::RoundRobin;
        return build(single);
    }
    switch (s.type) {
        case TournamentType::Pyramid:
            return build(stage_settings(s, {s.engines[1], s.engines[0]}, TournamentType::Gauntlet, s.rounds, 1));
        case TournamentType::Knockout: {
            std::vector<std::string> names;
            for (const auto& e : s.engines) names.push_back(engine_name(e));
            for (const auto& p : formats::knockout_first_round(names)) {
                if (!p.black) continue;
                auto by_name = [&](const std::string& n) {
                    return *std::find_if(s.engines.begin(), s.engines.end(),
                                         [&](const EngineSettings& e) { return engine_name(e) == n; });
                };
                return build(stage_settings(s, {by_name(p.white), by_name(*p.black)}, TournamentType::RoundRobin,
                                            s.rounds, 1));
            }
            throw Error(FCD_ERR_INTERNAL, "no knockout match in the first round");
        }
        default:
            return build(stage_settings(s, {s.engines[0], s.engines[1]}, TournamentType::RoundRobin, s.rounds, 1));
    }
}

// ---- Output ------------------------------------------------------------------

ParsedLine parse_line(const std::string& line) {
    // Same patterns as the C# parser had; regex_match anchors at both ends, so long unrelated
    // lines (engine PVs, move lists) fail on their first characters.
    static const std::regex started(R"(Started game (\d+) of (\d+) \((.+) vs (.+)\)\s*)");
    static const std::regex finished(R"(Finished game (\d+) \((.+) vs (.+)\): (1-0|0-1|1/2-1/2|\*) \{(.*)\}\s*)");
    static const std::regex tracker(R"(\s*(Timeouts|Crashed): (\d+)\s*)");

    ParsedLine p;
    std::smatch m;
    if (std::regex_match(line, m, started)) {
        p.started = GameStarted{std::stoi(m[1]), std::stoi(m[2]), m[3], m[4]};
        return p;
    }
    if (std::regex_match(line, m, finished)) {
        p.finished = GameFinished{std::stoi(m[1]), m[2], m[3], m[4], m[5]};
        if (stats::is_engine_failure_reason(m[5])) p.kind = LineKind::EngineFailure;
        return p;
    }
    if (std::regex_match(line, m, tracker)) {
        if (m[2] != "0") p.kind = LineKind::EngineFailure;
        return p;
    }
    const std::string trimmed = trim(line);
    // "Tournament finished", or "SPRT (...) completed - H0 was accepted" (roundrobin.cpp).
    if (trimmed == "Tournament finished" ||
        (trimmed.rfind("SPRT (", 0) == 0 && trimmed.size() >= 13 &&
         trimmed.compare(trimmed.size() - 13, 13, " was accepted") == 0)) {
        p.tournament_finished = trimmed;
        return p;
    }
    // One engine-output warning is a "Warning;" line and the "Info;", "Position;", "Moves;" lines after it (match.cpp).
    for (const char* prefix : {"Warning;", "Info;", "Infos;", "Position;", "Moves;"}) {
        if (trimmed.rfind(prefix, 0) != 0) continue;
        p.kind = LineKind::Warning;
        if (std::string_view(prefix) == "Warning;") {
            // These end with "from <engine name>".
            if (const auto at = trimmed.rfind(" from "); at != std::string::npos) p.warning_engine = trimmed.substr(at + 6);
        }
        break;
    }
    return p;
}

std::string parsed_line_json(const ParsedLine& p) {
    std::string out = "{\"kind\":";
    out += p.kind == LineKind::Warning ? "\"warning\"" : p.kind == LineKind::EngineFailure ? "\"engineFailure\"" : "\"normal\"";
    out += ",\"event\":";
    if (p.started) {
        out += "{\"type\":\"gameStarted\",\"number\":" + std::to_string(p.started->number) +
               ",\"total\":" + std::to_string(p.started->total) + ",\"white\":";
        json_string(out, p.started->white);
        out += ",\"black\":";
        json_string(out, p.started->black);
        out += '}';
    } else if (p.finished) {
        out += "{\"type\":\"gameFinished\",\"number\":" + std::to_string(p.finished->number) + ",\"white\":";
        json_string(out, p.finished->white);
        out += ",\"black\":";
        json_string(out, p.finished->black);
        out += ",\"result\":";
        json_string(out, p.finished->result);
        out += ",\"reason\":";
        json_string(out, p.finished->reason);
        out += '}';
    } else if (p.tournament_finished) {
        out += "{\"type\":\"tournamentFinished\",\"message\":";
        json_string(out, *p.tournament_finished);
        out += '}';
    } else {
        out += "null";
    }
    out += ",\"warningEngine\":";
    if (p.warning_engine) json_string(out, *p.warning_engine);
    else out += "null";
    return out + "}";
}

}  // namespace fcd::fastchess
