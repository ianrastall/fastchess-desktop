// Game and standings exports: PGN, JSON, XML, text crosstable.
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "database.hpp"
#include "game.hpp"

namespace fcd {

struct PlayerStanding {
    std::string name;
    std::int64_t games = 0, wins = 0, draws = 0, losses = 0;
    double score = 0;
    std::optional<double> rating;
};

// Scores from finished games (Result "*" is ignored), ordered by score,
// then by fewer games, then by name.
class Standings {
   public:
    void add(const std::string& white, const std::string& black, const std::string& result);
    void attach_ratings(const std::vector<Rating>& ratings);
    std::vector<PlayerStanding> ordered() const;
    // Score of a against b and number of games between them.
    std::pair<double, std::int64_t> head_to_head(const std::string& a, const std::string& b) const;
    std::int64_t finished_games() const { return finished_; }

   private:
    std::map<std::string, PlayerStanding> players_;
    std::map<std::pair<std::string, std::string>, std::pair<double, std::int64_t>> pairs_;
    std::int64_t finished_ = 0;
};

std::string game_to_pgn(const Game& game);
std::string crosstable_text(const Standings& standings, std::int64_t total_games);

// Writes an export of the selected games; returns the number of games written.
std::int64_t export_games(Database& db, const fcd_query* query, fcd_export_format format, const std::string& path);

}  // namespace fcd
