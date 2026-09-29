// Command lines for Ordoprep, Ordo and pgn-extract. Syntax: assets/ordo/ordo-help.xml,
// assets/ordoprep/ordoprep-help.xml, assets/pgn-extract/pgn-extract-help.xml.
#pragma once

#include <string>
#include <vector>

#include "json.hpp"

namespace fcd::tools {

// Ordoprep accepts at most one structural filter per run.
enum class OrdoprepFilter { None, RemovePerfectScores, MinGames, MajorGroupOnly };

// Mirrors FastchessDesktop.Core.Tools.RatingSettings; read from its camelCase JSON form.
struct RatingSettings {
    double average = 2300;
    std::string anchor_player;
    bool white_advantage_auto = true;
    bool draw_rate_auto = true;
    int simulations = 200;
    int cpus = 1;
    int decimals = 1;
    OrdoprepFilter ordoprep_filter = OrdoprepFilter::RemovePerfectScores;
    int min_games = 10;

    static RatingSettings parse(const char* json_text);
};

std::vector<std::string> ordoprep_args(const RatingSettings& s, const std::string& input_pgn, const std::string& output_pgn);

std::vector<std::string> ordo_args(const RatingSettings& s, const std::string& input_pgn, const std::string& report_txt,
                                   const std::string& ratings_csv);

enum class PgnExtractPreset { ClassifyOpenings, RemoveDuplicates, FixResultTags, Custom };

// Arguments for one pgn-extract run over input_pgn writing output_pgn. custom_args are appended for
// every preset, so a preset can be refined (for example with -t tag criteria).
std::vector<std::string> pgn_extract_args(PgnExtractPreset preset, const std::string& custom_args,
                                          const std::string& eco_pgn, const std::string& input_pgn,
                                          const std::string& output_pgn);

}  // namespace fcd::tools
