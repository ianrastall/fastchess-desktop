// In-memory game record shared by import, storage and export.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace fcd {

struct Game {
    std::int64_t id = 0;
    std::string event, site, date, round, white, black, result;
    std::optional<int> white_elo, black_elo;
    std::string eco, opening, variation, time_control, termination;

    std::string start_fen;  // empty for the standard start position
    bool chess960 = false;
    std::vector<std::string> san, uci, comments;  // comments aligned with moves
    std::string final_fen;

    std::vector<std::pair<std::string, std::string>> extra_tags;  // file order
    std::string analysis;                                         // raw JSON, empty if none
    std::string source;
};

// Assigns a well-known tag to its field. Returns false for other tags.
// Derived tags (PlyCount, SetUp, FEN) are handled by the caller.
bool assign_known_tag(Game& game, const std::string& name, const std::string& value);

// Returns the value of a well-known tag, or std::nullopt if name is not well-known.
std::optional<std::string> known_tag_value(const Game& game, const std::string& name);

// Canonical order of well-known tags when writing PGN.
const std::vector<std::string>& known_tag_names();

// Tags that are derived from the move data and never stored as extra tags.
bool is_derived_tag(const std::string& name);

std::optional<int> parse_elo(const std::string& value);

}  // namespace fcd
