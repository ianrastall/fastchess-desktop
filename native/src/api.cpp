// C ABI entry points for the library, the game database and the opening book.
// Every function catches all exceptions and converts them to fcd_status plus a
// thread-local message (abi.hpp). Tools, statistics and processes: api_tools.cpp.
#include <cstdlib>

#include "abi.hpp"
#include "database.hpp"
#include "exporters.hpp"
#include "fcd/fcd.h"
#include "openings.hpp"
#include "util.hpp"

struct fcd_db {
    fcd::Database impl;
    explicit fcd_db(const std::string& path) : impl(path) {}
};

struct fcd_openings {
    fcd::OpeningBook impl;
};

namespace {

using fcd::abi::guarded;
using fcd::abi::require;

fcd::Progress wrap(fcd_progress_fn fn, void* user) {
    if (!fn) return {};
    return [fn, user](std::int64_t done, std::int64_t total) { return fn(user, done, total) == 0; };
}

}  // namespace

extern "C" {

FCD_API fcd_status fcd_db_open(const char* path, fcd_db** out_db) {
    return guarded([&] {
        require(path, "path");
        require(out_db, "out_db");
        *out_db = nullptr;
        *out_db = new fcd_db(path);
    });
}

FCD_API void fcd_db_close(fcd_db* db) { delete db; }

FCD_API fcd_status fcd_db_import_pgn(fcd_db* db, const char* pgn_path, int32_t skip_duplicates,
                                     fcd_progress_fn progress, void* user, fcd_import_result* out_result) {
    return guarded([&] {
        require(db, "db");
        require(pgn_path, "pgn_path");
        const auto r = db->impl.import_pgn(pgn_path, skip_duplicates != 0, wrap(progress, user));
        if (out_result) *out_result = r;
    });
}

FCD_API fcd_status fcd_db_count(fcd_db* db, const fcd_query* query, int64_t* out_count) {
    return guarded([&] {
        require(db, "db");
        require(out_count, "out_count");
        *out_count = db->impl.count(query);
    });
}

FCD_API fcd_status fcd_db_query(fcd_db* db, const fcd_query* query, char** out_json) {
    return guarded([&] {
        require(db, "db");
        require(out_json, "out_json");
        *out_json = nullptr;
        *out_json = fcd::dup_for_caller(db->impl.query_json(query));
    });
}

FCD_API fcd_status fcd_db_get_game(fcd_db* db, int64_t id, char** out_json) {
    return guarded([&] {
        require(db, "db");
        require(out_json, "out_json");
        *out_json = nullptr;
        *out_json = fcd::dup_for_caller(db->impl.game_json(id));
    });
}

FCD_API fcd_status fcd_db_set_tag(fcd_db* db, int64_t id, const char* tag, const char* value) {
    return guarded([&] {
        require(db, "db");
        require(tag, "tag");
        db->impl.set_tag(id, tag, value ? fcd::trim(value) : std::string());
    });
}

FCD_API fcd_status fcd_db_delete_games(fcd_db* db, const int64_t* ids, int64_t id_count) {
    return guarded([&] {
        require(db, "db");
        if (id_count < 0 || (id_count > 0 && !ids)) throw fcd::Error(FCD_ERR_ARGUMENT, "invalid id list");
        db->impl.delete_games(std::vector<std::int64_t>(ids, ids + id_count));
    });
}

FCD_API fcd_status fcd_db_set_analysis(fcd_db* db, int64_t id, const char* analysis_json) {
    return guarded([&] {
        require(db, "db");
        db->impl.set_analysis(id, analysis_json ? analysis_json : "");
    });
}

FCD_API fcd_status fcd_db_fill_missing(fcd_db* db, const fcd_query* query, const fcd_openings* book, uint32_t flags,
                                       fcd_progress_fn progress, void* user, int64_t* out_updated) {
    return guarded([&] {
        require(db, "db");
        const auto n = db->impl.fill_missing(query, book ? &book->impl : nullptr, flags, wrap(progress, user));
        if (out_updated) *out_updated = n;
    });
}

FCD_API fcd_status fcd_db_apply_ordo_csv(fcd_db* db, const char* csv_path, int32_t fill_elo, int32_t overwrite_elo,
                                         int64_t* out_players, int64_t* out_elo_values_written) {
    return guarded([&] {
        require(db, "db");
        require(csv_path, "csv_path");
        const auto [players, written] = db->impl.apply_ordo_csv(csv_path, fill_elo != 0, overwrite_elo != 0);
        if (out_players) *out_players = players;
        if (out_elo_values_written) *out_elo_values_written = written;
    });
}

FCD_API fcd_status fcd_db_get_ratings(fcd_db* db, char** out_json) {
    return guarded([&] {
        require(db, "db");
        require(out_json, "out_json");
        *out_json = nullptr;
        *out_json = fcd::dup_for_caller(db->impl.ratings_json());
    });
}

FCD_API fcd_status fcd_db_export(fcd_db* db, const fcd_query* query, fcd_export_format format, const char* out_path,
                                 int64_t* out_games) {
    return guarded([&] {
        require(db, "db");
        require(out_path, "out_path");
        const auto n = fcd::export_games(db->impl, query, format, out_path);
        if (out_games) *out_games = n;
    });
}

FCD_API fcd_status fcd_openings_load_tsv(const char* tsv_path, fcd_openings** out_book) {
    return guarded([&] {
        require(tsv_path, "tsv_path");
        require(out_book, "out_book");
        *out_book = nullptr;
        *out_book = new fcd_openings{fcd::OpeningBook::load_tsv(tsv_path)};
    });
}

FCD_API void fcd_openings_free(fcd_openings* book) { delete book; }

FCD_API int64_t fcd_openings_count(const fcd_openings* book) {
    return book ? static_cast<int64_t>(book->impl.size()) : 0;
}

FCD_API fcd_status fcd_openings_write_eco_pgn(const fcd_openings* book, const char* out_path) {
    return guarded([&] {
        require(book, "book");
        require(out_path, "out_path");
        book->impl.write_eco_pgn(out_path);
    });
}

FCD_API fcd_status fcd_openings_classify_uci(const fcd_openings* book, const char* uci_moves, char** out_json) {
    return guarded([&] {
        require(book, "book");
        require(uci_moves, "uci_moves");
        require(out_json, "out_json");
        *out_json = nullptr;
        const std::string moves = fcd::trim(uci_moves);
        const auto match = book->impl.classify(moves.empty() ? std::vector<std::string>() : fcd::split(moves, ' '));
        std::string out;
        if (!match) {
            out = "null";
        } else {
            const auto [family, rest] = fcd::OpeningBook::split_name(match->entry->name);
            out = "{\"eco\":";
            fcd::json_string(out, match->entry->eco);
            out += ",\"name\":";
            fcd::json_string(out, match->entry->name);
            out += ",\"opening\":";
            fcd::json_string(out, family);
            out += ",\"variation\":";
            fcd::json_string(out, rest);
            out += ",\"ply\":" + std::to_string(match->ply) + "}";
        }
        *out_json = fcd::dup_for_caller(out);
    });
}

}  // extern "C"
