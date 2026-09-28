#include "game.hpp"

#include <charconv>

#include "util.hpp"

namespace fcd {

namespace {
std::string elo_text(const std::optional<int>& elo) { return elo ? std::to_string(*elo) : std::string(); }
}  // namespace

std::optional<int> parse_elo(const std::string& value) {
    const std::string t = trim(value);
    int v = 0;
    const auto [ptr, ec] = std::from_chars(t.data(), t.data() + t.size(), v);
    if (ec != std::errc() || ptr != t.data() + t.size() || v <= 0) return std::nullopt;
    return v;
}

bool assign_known_tag(Game& g, const std::string& name, const std::string& value) {
    if (name == "Event") g.event = value;
    else if (name == "Site") g.site = value;
    else if (name == "Date") g.date = value;
    else if (name == "Round") g.round = value;
    else if (name == "White") g.white = value;
    else if (name == "Black") g.black = value;
    else if (name == "Result") g.result = value;
    else if (name == "WhiteElo") g.white_elo = parse_elo(value);
    else if (name == "BlackElo") g.black_elo = parse_elo(value);
    else if (name == "ECO") g.eco = value;
    else if (name == "Opening") g.opening = value;
    else if (name == "Variation") g.variation = value;
    else if (name == "TimeControl") g.time_control = value;
    else if (name == "Termination") g.termination = value;
    else return false;
    return true;
}

std::optional<std::string> known_tag_value(const Game& g, const std::string& name) {
    if (name == "Event") return g.event;
    if (name == "Site") return g.site;
    if (name == "Date") return g.date;
    if (name == "Round") return g.round;
    if (name == "White") return g.white;
    if (name == "Black") return g.black;
    if (name == "Result") return g.result;
    if (name == "WhiteElo") return elo_text(g.white_elo);
    if (name == "BlackElo") return elo_text(g.black_elo);
    if (name == "ECO") return g.eco;
    if (name == "Opening") return g.opening;
    if (name == "Variation") return g.variation;
    if (name == "TimeControl") return g.time_control;
    if (name == "Termination") return g.termination;
    return std::nullopt;
}

const std::vector<std::string>& known_tag_names() {
    static const std::vector<std::string> names = {"Event",    "Site",     "Date", "Round",   "White",
                                                   "Black",    "Result",   "WhiteElo", "BlackElo", "ECO",
                                                   "Opening",  "Variation", "TimeControl", "Termination"};
    return names;
}

bool is_derived_tag(const std::string& name) { return name == "PlyCount" || name == "SetUp" || name == "FEN"; }

}  // namespace fcd
