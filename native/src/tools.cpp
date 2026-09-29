#include "tools.hpp"

#include <algorithm>

#include "cmdline.hpp"
#include "util.hpp"

namespace fcd::tools {

RatingSettings RatingSettings::parse(const char* json_text) {
    if (!json_text) throw Error(FCD_ERR_ARGUMENT, "rating settings JSON is NULL");
    const auto v = json::Value::parse(json_text);
    if (!v.is_object()) throw Error(FCD_ERR_ARGUMENT, "rating settings must be a JSON object");
    RatingSettings s;
    s.average = v.get_number("average", s.average);
    s.anchor_player = v.get_string("anchorPlayer");
    s.white_advantage_auto = v.get_bool("whiteAdvantageAuto", s.white_advantage_auto);
    s.draw_rate_auto = v.get_bool("drawRateAuto", s.draw_rate_auto);
    s.simulations = static_cast<int>(v.get_integer("simulations", s.simulations));
    s.cpus = static_cast<int>(v.get_integer("cpus", s.cpus));
    s.decimals = static_cast<int>(v.get_integer("decimals", s.decimals));
    s.min_games = static_cast<int>(v.get_integer("minGames", s.min_games));
    if (const auto* f = v.find("ordoprepFilter")) {
        const std::string name = f->as_string();
        if (f->type() == json::Value::Type::Number) {
            const auto i = static_cast<int>(f->as_number());
            if (i >= 0 && i <= 3) s.ordoprep_filter = static_cast<OrdoprepFilter>(i);
        } else if (iequals(name, "None")) s.ordoprep_filter = OrdoprepFilter::None;
        else if (iequals(name, "RemovePerfectScores")) s.ordoprep_filter = OrdoprepFilter::RemovePerfectScores;
        else if (iequals(name, "MinGames")) s.ordoprep_filter = OrdoprepFilter::MinGames;
        else if (iequals(name, "MajorGroupOnly")) s.ordoprep_filter = OrdoprepFilter::MajorGroupOnly;
    }
    return s;
}

std::vector<std::string> ordoprep_args(const RatingSettings& s, const std::string& input_pgn, const std::string& output_pgn) {
    std::vector<std::string> a{"-p", input_pgn, "-o", output_pgn};
    switch (s.ordoprep_filter) {
        case OrdoprepFilter::RemovePerfectScores: a.push_back("-d"); break;
        case OrdoprepFilter::MinGames:
            a.push_back("-M");
            a.push_back(std::to_string(std::max(1, s.min_games)));
            break;
        case OrdoprepFilter::MajorGroupOnly: a.push_back("--major-only"); break;
        case OrdoprepFilter::None: break;
    }
    return a;
}

std::vector<std::string> ordo_args(const RatingSettings& s, const std::string& input_pgn, const std::string& report_txt,
                                   const std::string& ratings_csv) {
    std::vector<std::string> a{"-p", input_pgn, "-o", report_txt, "-c", ratings_csv, "-a", json::number(s.average)};
    if (!trim(s.anchor_player).empty()) {
        a.push_back("-A");
        a.push_back(trim(s.anchor_player));
    }
    if (s.white_advantage_auto) a.push_back("-W");
    if (s.draw_rate_auto) a.push_back("-D");
    if (s.simulations > 0) {
        a.push_back("-s");
        a.push_back(std::to_string(s.simulations));
        if (s.cpus > 1) {
            a.push_back("-n");
            a.push_back(std::to_string(s.cpus));
        }
    }
    a.push_back("-N");
    a.push_back(std::to_string(s.decimals));
    return a;
}

std::vector<std::string> pgn_extract_args(PgnExtractPreset preset, const std::string& custom_args,
                                          const std::string& eco_pgn, const std::string& input_pgn,
                                          const std::string& output_pgn) {
    std::vector<std::string> a;
    switch (preset) {
        case PgnExtractPreset::ClassifyOpenings:
            if (trim(eco_pgn).empty()) throw Error(FCD_ERR_ARGUMENT, "Opening classification needs an ECO file.");
            a.push_back("-e" + eco_pgn);  // pgn-extract requires the path attached to -e
            break;
        case PgnExtractPreset::RemoveDuplicates: a.push_back("-D"); break;
        case PgnExtractPreset::FixResultTags:
            a.push_back("--fixresulttags");
            a.push_back("--plycount");
            break;
        case PgnExtractPreset::Custom: break;
    }
    for (auto& extra : cmdline::split(custom_args)) a.push_back(std::move(extra));
    for (const auto& tail : {std::string("--quiet"), std::string("-o"), output_pgn, input_pgn}) a.push_back(tail);
    return a;
}

}  // namespace fcd::tools
