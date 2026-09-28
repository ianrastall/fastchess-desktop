// Lichess chess-openings index, keyed by the complete four-field EPD.
// See assets/lichess-openings/parsing-lichess-openings.md for the rules applied.
#pragma once

#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace fcd {

struct OpeningEntry {
    std::string eco, name, pgn, uci, epd;
};

struct OpeningMatch {
    const OpeningEntry* entry = nullptr;
    int ply = 0;
};

class OpeningBook {
   public:
    // Loads and validates the TSV: exact header, five non-empty fields,
    // unique EPDs, and every UCI line must replay to its EPD.
    static OpeningBook load_tsv(const std::string& utf8_path);

    size_t size() const { return entries_.size(); }

    // Last matched position along a game from the standard start.
    std::optional<OpeningMatch> classify(const std::vector<std::string>& uci) const;

    // pgn-extract ECO file (ECO/Opening/Variation/SubVariation tags).
    void write_eco_pgn(const std::string& utf8_path) const;

    // "Family: A, B, C" -> {"Family", "A, B, C"}.
    static std::pair<std::string, std::string> split_name(const std::string& name);

   private:
    std::vector<OpeningEntry> entries_;
    std::unordered_map<std::string, size_t> by_epd_;
};

}  // namespace fcd
