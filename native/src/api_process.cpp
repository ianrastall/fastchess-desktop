// C ABI entry points for cancellation, processes, UCI engines, analysis and tournaments.
#include "abi.hpp"
#include "analysis.hpp"
#include "fastchess.hpp"
#include "json.hpp"
#include "process.hpp"
#include "tournament.hpp"
#include "uci.hpp"

struct fcd_cancel {
    fcd::process::Cancel impl;
};

struct fcd_uci {
    std::unique_ptr<fcd::uci::Engine> impl;
};

namespace {

using fcd::abi::give;
using fcd::abi::guarded;
using fcd::abi::require;
namespace json = fcd::json;

const fcd::process::Cancel* token(const fcd_cancel* c) { return c ? &c->impl : nullptr; }

fcd::process::LineHandler line_handler(fcd_line_fn fn, void* user) {
    if (!fn) return {};
    return [fn, user](fcd::process::Stream stream, const std::string& line) {
        fn(user, static_cast<int32_t>(stream), line.c_str());
    };
}

std::vector<std::string> moves_of(const char* uci_moves) {
    const std::string text = fcd::trim(uci_moves ? uci_moves : "");
    std::vector<std::string> moves;
    for (auto& m : fcd::split(text, ' '))
        if (!m.empty()) moves.push_back(std::move(m));
    return moves;
}

std::string optional_int(const std::optional<int>& v) { return v ? std::to_string(*v) : "null"; }

std::string score_json(const fcd::uci::Score& score, int depth, const std::vector<std::string>& pv) {
    return "{\"cp\":" + optional_int(score.cp) + ",\"mate\":" + optional_int(score.mate) +
           ",\"depth\":" + std::to_string(depth) + ",\"pv\":" + json::string_array(pv);
}

}  // namespace

extern "C" {

FCD_API fcd_status fcd_cancel_new(fcd_cancel** out_cancel) {
    return guarded([&] {
        require(out_cancel, "out_cancel");
        *out_cancel = new fcd_cancel();
    });
}

FCD_API void fcd_cancel_request(fcd_cancel* cancel) {
    if (cancel) cancel->impl.request();
}

FCD_API void fcd_cancel_free(fcd_cancel* cancel) { delete cancel; }

FCD_API fcd_status fcd_process_run(const char* program, const char* args_json, const char* working_directory,
                                   fcd_line_fn on_line, void* user, fcd_cancel* cancel,
                                   fcd_process_result* out_result) {
    return guarded([&] {
        require(program, "program");
        require(out_result, "out_result");
        fcd::process::Options o;
        o.program = program;
        if (args_json) o.args = json::Value::parse(args_json).string_items();
        o.working_directory = working_directory ? working_directory : "";
        const auto r = fcd::process::run(o, line_handler(on_line, user), token(cancel));
        *out_result = {r.exit_code, r.cancelled ? 1 : 0, r.duration_ms};
    });
}

FCD_API fcd_status fcd_uci_start(const char* path, const char* options_json, fcd_cancel* cancel, fcd_uci** out_engine) {
    return guarded([&] {
        require(path, "path");
        require(out_engine, "out_engine");
        *out_engine = nullptr;
        std::vector<std::pair<std::string, std::string>> options;
        if (options_json) {
            const auto parsed = json::Value::parse(options_json);
            for (const auto& [name, value] : parsed.members())
                options.emplace_back(name, value.is_string() ? value.as_string() : json::number(value.as_number()));
        }
        auto engine = fcd::uci::Engine::start(path, options, token(cancel));
        *out_engine = new fcd_uci{std::move(engine)};
    });
}

FCD_API void fcd_uci_close(fcd_uci* engine) { delete engine; }

FCD_API fcd_status fcd_uci_name(const fcd_uci* engine, char** out_text) {
    return guarded([&] {
        require(engine, "engine");
        give(out_text, engine->impl->name());
    });
}

FCD_API fcd_status fcd_uci_new_game(fcd_uci* engine, fcd_cancel* cancel) {
    return guarded([&] {
        require(engine, "engine");
        engine->impl->new_game(token(cancel));
    });
}

FCD_API fcd_status fcd_uci_search(fcd_uci* engine, const char* start_fen, const char* uci_moves,
                                  const fcd_uci_limit* limit, fcd_cancel* cancel, char** out_json) {
    return guarded([&] {
        require(engine, "engine");
        require(out_json, "out_json");
        const fcd::uci::Limit l = limit ? fcd::uci::Limit{limit->depth, limit->movetime_ms, limit->nodes}
                                        : fcd::uci::Limit{};
        const auto r = engine->impl->search(start_fen ? start_fen : "", moves_of(uci_moves), l, token(cancel));
        std::string out = score_json(r.score, r.depth, r.pv) + ",\"bestMove\":";
        fcd::json_string(out, r.best_move);
        give(out_json, out + "}");
    });
}

FCD_API fcd_status fcd_uci_parse_info(const char* line, char** out_json) {
    return guarded([&] {
        require(line, "line");
        fcd::uci::Score score;
        int depth = 0;
        std::vector<std::string> pv;
        give(out_json, fcd::uci::parse_info(line, score, depth, pv) ? score_json(score, depth, pv) + "}" : "null");
    });
}

FCD_API fcd_status fcd_uci_analyze_game(fcd_uci* engine, const char* start_fen, const char* uci_moves,
                                        const fcd_analysis_settings* settings, fcd_progress_fn progress, void* user,
                                        fcd_cancel* cancel, char** out_json) {
    return guarded([&] {
        require(engine, "engine");
        require(settings, "settings");
        require(out_json, "out_json");
        fcd::analysis::Progress report;
        if (progress) report = [progress, user](int ply, int total) { return progress(user, ply, total) == 0; };
        give(out_json, fcd::analysis::analyze(*engine->impl, start_fen ? start_fen : "", moves_of(uci_moves),
                                              {settings->depth, settings->movetime_ms}, report, token(cancel)));
    });
}

FCD_API fcd_status fcd_tournament_run(const char* fastchess_path, const char* working_directory,
                                      const char* settings_json, fcd_line_fn on_line, fcd_event_fn on_event,
                                      void* user, fcd_cancel* cancel, char** out_outcome_json) {
    return guarded([&] {
        require(fastchess_path, "fastchess_path");
        require(out_outcome_json, "out_outcome_json");
        const auto settings = fcd::fastchess::TournamentSettings::parse(settings_json);
        fcd::tournament::EventHandler events;
        if (on_event) events = [on_event, user](const std::string& e) { on_event(user, e.c_str()); };
        const auto outcome = fcd::tournament::run(fastchess_path, working_directory ? working_directory : "", settings,
                                                  line_handler(on_line, user), events, token(cancel));
        give(out_outcome_json, fcd::tournament::outcome_json(outcome));
    });
}

}  // extern "C"
