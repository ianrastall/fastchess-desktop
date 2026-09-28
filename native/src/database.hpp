// SQLite-backed game database.
#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "fcd/fcd.h"
#include "game.hpp"

struct sqlite3;

namespace fcd {

class OpeningBook;
class Statement;

// Return false to cancel.
using Progress = std::function<bool(std::int64_t done, std::int64_t total)>;

struct Rating {
    std::string player;
    double rating = 0;
    std::optional<double> error;
    double points = 0;
    std::int64_t played = 0;
    double percent = 0;
};

class Database {
   public:
    explicit Database(const std::string& utf8_path);
    ~Database();
    Database(const Database&) = delete;
    Database& operator=(const Database&) = delete;

    fcd_import_result import_pgn(const std::string& pgn_path, bool skip_duplicates, const Progress& progress);

    std::int64_t count(const fcd_query* query);
    std::string query_json(const fcd_query* query);
    std::string game_json(std::int64_t id);

    // Streams full games matching the query in query order.
    void for_each_game(const fcd_query* query, const std::function<void(const Game&)>& fn);

    // Streams only white, black and result of matching games (cheap standings pass).
    void for_each_result(const fcd_query* query,
                         const std::function<void(const std::string&, const std::string&, const std::string&)>& fn);

    void set_tag(std::int64_t id, const std::string& tag, const std::string& value);
    void delete_games(const std::vector<std::int64_t>& ids);
    void set_analysis(std::int64_t id, const std::string& json);

    std::int64_t fill_missing(const fcd_query* query, const OpeningBook* book, std::uint32_t flags,
                              const Progress& progress);

    // Returns {players, Elo tag values written}.
    std::pair<std::int64_t, std::int64_t> apply_ordo_csv(const std::string& csv_path, bool fill_elo, bool overwrite);
    std::vector<Rating> ratings();
    std::string ratings_json();

   private:
    struct Where {
        std::string sql;
        std::vector<std::string> params;
    };
    Where build_where(const fcd_query* query);
    std::string order_clause(const fcd_query* query) const;
    std::vector<std::int64_t> select_ids(const fcd_query* query);
    Game load_game(std::int64_t id);
    Game load_game(std::int64_t id, Statement& game_stmt, Statement& tag_stmt);
    void update_dup_key(std::int64_t id);

    sqlite3* db_ = nullptr;
};

std::string dup_key_for(const Game& game);

}  // namespace fcd
