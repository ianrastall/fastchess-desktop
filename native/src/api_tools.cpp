// C ABI entry points for command lines, tournament settings, fastchess output,
// statistics and pairings.
#include <cmath>

#include "abi.hpp"
#include "cmdline.hpp"
#include "fastchess.hpp"
#include "formats.hpp"
#include "json.hpp"
#include "stats.hpp"
#include "tools.hpp"

struct fcd_scoreboard {
    fcd::stats::Scoreboard impl;
};

namespace {

using fcd::abi::give;
using fcd::abi::guarded;
using fcd::abi::require;
namespace json = fcd::json;

std::vector<std::string> string_list(const char* json_text, const char* what) {
    require(json_text, what);
    const auto v = json::Value::parse(json_text);
    if (!v.is_array()) throw fcd::Error(FCD_ERR_ARGUMENT, std::string(what) + " must be a JSON array");
    return v.string_items();
}

std::string pairings_json(const std::vector<fcd::formats::Pairing>& pairs) {
    std::string out = "[";
    for (size_t i = 0; i < pairs.size(); ++i) {
        if (i) out += ',';
        out += "{\"white\":";
        fcd::json_string(out, pairs[i].white);
        out += ",\"black\":";
        if (pairs[i].black) fcd::json_string(out, *pairs[i].black);
        else out += "null";
        out += '}';
    }
    return out + "]";
}

}  // namespace

extern "C" {

// ---- Command lines -------------------------------------------------------------

FCD_API fcd_status fcd_cmdline_quote(const char* argument, char** out_text) {
    return guarded([&] {
        require(argument, "argument");
        give(out_text, fcd::cmdline::quote(argument));
    });
}

FCD_API fcd_status fcd_cmdline_format(const char* args_json, char** out_text) {
    return guarded([&] { give(out_text, fcd::cmdline::format(string_list(args_json, "args_json"))); });
}

FCD_API fcd_status fcd_cmdline_split(const char* text, char** out_json) {
    return guarded([&] { give(out_json, json::string_array(fcd::cmdline::split(text ? text : ""))); });
}

// ---- Tournament settings -------------------------------------------------------

FCD_API fcd_status fcd_tournament_validate(const char* settings_json, char** out_errors_json) {
    return guarded([&] {
        const auto s = fcd::fastchess::TournamentSettings::parse(settings_json);
        give(out_errors_json, json::string_array(fcd::fastchess::validate(s)));
    });
}

FCD_API fcd_status fcd_tournament_build_args(const char* settings_json, char** out_args_json) {
    return guarded([&] {
        const auto s = fcd::fastchess::TournamentSettings::parse(settings_json);
        give(out_args_json, json::string_array(fcd::fastchess::build(s)));
    });
}

FCD_API fcd_status fcd_tournament_first_stage_args(const char* settings_json, char** out_args_json) {
    return guarded([&] {
        const auto s = fcd::fastchess::TournamentSettings::parse(settings_json);
        give(out_args_json, json::string_array(fcd::fastchess::first_stage_arguments(s)));
    });
}

FCD_API fcd_status fcd_tournament_expected_games(const char* settings_json, int64_t* out_games) {
    return guarded([&] {
        require(out_games, "out_games");
        const auto s = fcd::fastchess::TournamentSettings::parse(settings_json);
        *out_games = fcd::fastchess::expected_games(s).value_or(-1);
    });
}

FCD_API fcd_status fcd_engine_display_name(const char* name, const char* command, char** out_text) {
    return guarded([&] { give(out_text, fcd::fastchess::engine_name(name ? name : "", command ? command : "")); });
}

FCD_API fcd_status fcd_fastchess_parse_line(const char* line, char** out_json) {
    return guarded([&] {
        require(line, "line");
        give(out_json, fcd::fastchess::parsed_line_json(fcd::fastchess::parse_line(line)));
    });
}

FCD_API int32_t fcd_is_engine_failure_reason(const char* reason) {
    return reason && fcd::stats::is_engine_failure_reason(reason) ? 1 : 0;
}

// ---- Rating and pgn-extract command lines ---------------------------------------

FCD_API fcd_status fcd_ordoprep_args(const char* rating_settings_json, const char* input_pgn, const char* output_pgn,
                                     char** out_args_json) {
    return guarded([&] {
        require(input_pgn, "input_pgn");
        require(output_pgn, "output_pgn");
        const auto s = fcd::tools::RatingSettings::parse(rating_settings_json);
        give(out_args_json, json::string_array(fcd::tools::ordoprep_args(s, input_pgn, output_pgn)));
    });
}

FCD_API fcd_status fcd_ordo_args(const char* rating_settings_json, const char* input_pgn, const char* report_txt,
                                 const char* ratings_csv, char** out_args_json) {
    return guarded([&] {
        require(input_pgn, "input_pgn");
        require(report_txt, "report_txt");
        require(ratings_csv, "ratings_csv");
        const auto s = fcd::tools::RatingSettings::parse(rating_settings_json);
        give(out_args_json, json::string_array(fcd::tools::ordo_args(s, input_pgn, report_txt, ratings_csv)));
    });
}

FCD_API fcd_status fcd_pgn_extract_args(int32_t preset, const char* custom_args, const char* eco_pgn,
                                        const char* input_pgn, const char* output_pgn, char** out_args_json) {
    return guarded([&] {
        require(input_pgn, "input_pgn");
        require(output_pgn, "output_pgn");
        if (preset < 0 || preset > 3) throw fcd::Error(FCD_ERR_ARGUMENT, "unknown pgn-extract preset");
        give(out_args_json,
             json::string_array(fcd::tools::pgn_extract_args(static_cast<fcd::tools::PgnExtractPreset>(preset),
                                                             custom_args ? custom_args : "", eco_pgn ? eco_pgn : "",
                                                             input_pgn, output_pgn)));
    });
}

// ---- Statistics -------------------------------------------------------------------

FCD_API fcd_status fcd_elo_estimate(const fcd_match_stats* stats, int32_t pentanomial, fcd_elo* out_elo) {
    return guarded([&] {
        require(stats, "stats");
        require(out_elo, "out_elo");
        const auto s = fcd::stats::from_abi(*stats);
        const auto e = pentanomial ? fcd::stats::elo_from_pairs(s) : fcd::stats::elo_from_games(s);
        if (!e) throw fcd::Error(FCD_ERR_NOT_FOUND, pentanomial ? "no completed game pairs" : "no games");
        *out_elo = {e->elo, e->error, e->nelo, e->nelo_error, e->los};
    });
}

FCD_API fcd_status fcd_sprt_evaluate(const fcd_sprt_params* params, const fcd_match_stats* stats, int32_t pentanomial,
                                     fcd_sprt_state* out_state) {
    return guarded([&] {
        require(params, "params");
        require(stats, "stats");
        require(out_state, "out_state");
        if (params->model < 0 || params->model > 2) throw fcd::Error(FCD_ERR_ARGUMENT, "unknown SPRT model");
        const fcd::stats::Sprt sprt(params->alpha, params->beta, params->elo0, params->elo1,
                                    static_cast<fcd::stats::SprtModel>(params->model));
        const double llr = sprt.llr(fcd::stats::from_abi(*stats), pentanomial != 0);
        *out_state = {llr, sprt.lower_bound(), sprt.upper_bound(), sprt.fraction(llr),
                      static_cast<int32_t>(sprt.outcome(llr))};
    });
}

FCD_API fcd_status fcd_scoreboard_new(int32_t games_per_encounter, int32_t pentanomial, fcd_scoreboard** out_board) {
    return guarded([&] {
        require(out_board, "out_board");
        *out_board = nullptr;
        *out_board = new fcd_scoreboard{fcd::stats::Scoreboard(games_per_encounter, pentanomial != 0)};
    });
}

FCD_API void fcd_scoreboard_free(fcd_scoreboard* board) { delete board; }

FCD_API int32_t fcd_scoreboard_is_pentanomial(const fcd_scoreboard* board) {
    return board && board->impl.pentanomial() ? 1 : 0;
}

FCD_API fcd_status fcd_scoreboard_add_engine(fcd_scoreboard* board, const char* name) {
    return guarded([&] {
        require(board, "board");
        require(name, "name");
        board->impl.add_engine(name);
    });
}

FCD_API fcd_status fcd_scoreboard_add_game(fcd_scoreboard* board, int32_t number, const char* white, const char* black,
                                           const char* result, const char* reason) {
    return guarded([&] {
        require(board, "board");
        require(white, "white");
        require(black, "black");
        require(result, "result");
        board->impl.add_game(number, white, black, result, reason ? reason : "");
    });
}

FCD_API fcd_status fcd_scoreboard_add_warning(fcd_scoreboard* board, const char* engine) {
    return guarded([&] {
        require(board, "board");
        require(engine, "engine");
        board->impl.add_warning(engine);
    });
}

FCD_API fcd_status fcd_scoreboard_engines(const fcd_scoreboard* board, char** out_json) {
    return guarded([&] {
        require(board, "board");
        give(out_json, json::string_array(board->impl.engines()));
    });
}

FCD_API fcd_status fcd_scoreboard_engine(const fcd_scoreboard* board, const char* engine, fcd_engine_totals* out_totals) {
    return guarded([&] {
        require(board, "board");
        require(engine, "engine");
        require(out_totals, "out_totals");
        *out_totals = {fcd::stats::to_abi(board->impl.stats_of(engine)), board->impl.failures(engine),
                       board->impl.warnings(engine)};
    });
}

FCD_API fcd_status fcd_scoreboard_head_to_head(const fcd_scoreboard* board, const char* engine, const char* opponent,
                                               fcd_match_stats* out_stats) {
    return guarded([&] {
        require(board, "board");
        require(engine, "engine");
        require(opponent, "opponent");
        require(out_stats, "out_stats");
        *out_stats = fcd::stats::to_abi(board->impl.head_to_head(engine, opponent));
    });
}

// ---- Pairings -----------------------------------------------------------------------

FCD_API fcd_status fcd_pairing_bracket_order(int32_t size, char** out_json) {
    return guarded([&] {
        std::string out = "[";
        const auto order = fcd::formats::bracket_order(size);
        for (size_t i = 0; i < order.size(); ++i) out += (i ? "," : "") + std::to_string(order[i]);
        give(out_json, out + "]");
    });
}

FCD_API fcd_status fcd_pairing_knockout_first_round(const char* seeded_json, char** out_json) {
    return guarded([&] {
        give(out_json, pairings_json(fcd::formats::knockout_first_round(string_list(seeded_json, "seeded_json"))));
    });
}

FCD_API fcd_status fcd_pairing_knockout_next_round(const char* winners_json, char** out_json) {
    return guarded([&] {
        give(out_json, pairings_json(fcd::formats::knockout_next_round(string_list(winners_json, "winners_json"))));
    });
}

FCD_API fcd_status fcd_pairing_swiss_round(const char* state_json, char** out_json) {
    return guarded([&] {
        require(state_json, "state_json");
        const auto v = json::Value::parse(state_json);
        fcd::formats::SwissState state;
        if (const auto* seeded = v.find("seeded")) state.seeded = seeded->string_items();
        if (const auto* scores = v.find("scores"))
            for (const auto& [name, points] : scores->members()) state.scores[name] = points.as_number();
        if (const auto* opponents = v.find("opponents"))
            for (const auto& [name, list] : opponents->members())
                for (auto& o : list.string_items()) state.opponents[name].insert(std::move(o));
        if (const auto* had_bye = v.find("hadBye"))
            for (auto& name : had_bye->string_items()) state.had_bye.insert(std::move(name));
        if (const auto* whites = v.find("whiteCounts"))
            for (const auto& [name, count] : whites->members())
                state.white_counts[name] = static_cast<int>(std::lround(count.as_number()));
        const auto round = fcd::formats::swiss_round(state);
        std::string out = "{\"pairs\":" + pairings_json(round.pairs) + ",\"bye\":";
        if (round.bye) fcd::json_string(out, *round.bye);
        else out += "null";
        give(out_json, out + "}");
    });
}

}  // extern "C"
