#include "database.hpp"

#include <sqlite3.h>

#include <algorithm>
#include <chrono>
#include <format>
#include <fstream>
#include <map>

#include "chess_rules.hpp"
#include "openings.hpp"
#include "sqlite.hpp"
#include "util.hpp"

namespace fcd {

namespace {

constexpr int kSchemaVersion = 1;

constexpr const char* kSchema = R"sql(
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS games (
    id           INTEGER PRIMARY KEY,
    event        TEXT NOT NULL DEFAULT '',
    site         TEXT NOT NULL DEFAULT '',
    date         TEXT NOT NULL DEFAULT '',
    round        TEXT NOT NULL DEFAULT '',
    white        TEXT NOT NULL DEFAULT '',
    black        TEXT NOT NULL DEFAULT '',
    result       TEXT NOT NULL DEFAULT '*',
    white_elo    INTEGER,
    black_elo    INTEGER,
    eco          TEXT NOT NULL DEFAULT '',
    opening      TEXT NOT NULL DEFAULT '',
    variation    TEXT NOT NULL DEFAULT '',
    time_control TEXT NOT NULL DEFAULT '',
    termination  TEXT NOT NULL DEFAULT '',
    start_fen    TEXT,
    chess960     INTEGER NOT NULL DEFAULT 0,
    san_moves    TEXT NOT NULL DEFAULT '',
    uci_moves    TEXT NOT NULL DEFAULT '',
    comments     TEXT,
    ply_count    INTEGER NOT NULL DEFAULT 0,
    final_fen    TEXT NOT NULL DEFAULT '',
    analysis     TEXT,
    dup_key      TEXT NOT NULL,
    source       TEXT NOT NULL DEFAULT '',
    imported_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS games_dup_key ON games(dup_key);
CREATE INDEX IF NOT EXISTS games_white ON games(white);
CREATE INDEX IF NOT EXISTS games_black ON games(black);
CREATE TABLE IF NOT EXISTS game_tags (
    game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
    name    TEXT NOT NULL,
    value   TEXT NOT NULL,
    ord     INTEGER NOT NULL,
    PRIMARY KEY (game_id, name)
);
CREATE TABLE IF NOT EXISTS ratings (
    player  TEXT PRIMARY KEY,
    rating  REAL NOT NULL,
    error   REAL,
    points  REAL NOT NULL,
    played  INTEGER NOT NULL,
    percent REAL NOT NULL,
    ord     INTEGER NOT NULL
);
)sql";

constexpr const char* kGameColumns =
    "id, event, site, date, round, white, black, result, white_elo, black_elo, eco, opening, variation, "
    "time_control, termination, start_fen, chess960, san_moves, uci_moves, comments, final_fen, analysis, source, "
    "ply_count";

constexpr char kCommentSeparator = '\x1e';

constexpr const char* kSelectTags = "SELECT name, value FROM game_tags WHERE game_id = ?1 ORDER BY ord";

std::string select_game_sql() { return std::string("SELECT ") + kGameColumns + " FROM games WHERE id = ?1"; }

// Column name for a well-known tag, or nullptr.
const char* column_for_tag(const std::string& tag) {
    static const std::map<std::string, const char*> map = {
        {"Event", "event"},     {"Site", "site"},         {"Date", "date"},          {"Round", "round"},
        {"White", "white"},     {"Black", "black"},       {"Result", "result"},      {"WhiteElo", "white_elo"},
        {"BlackElo", "black_elo"}, {"ECO", "eco"},        {"Opening", "opening"},    {"Variation", "variation"},
        {"TimeControl", "time_control"}, {"Termination", "termination"}};
    const auto it = map.find(tag);
    return it == map.end() ? nullptr : it->second;
}

std::string now_iso() {
    const auto t = std::chrono::system_clock::now();
    return std::format("{:%Y-%m-%dT%H:%M:%SZ}", std::chrono::floor<std::chrono::seconds>(t));
}

std::string join_comments(const std::vector<std::string>& comments) {
    const bool any = std::any_of(comments.begin(), comments.end(), [](const auto& c) { return !c.empty(); });
    if (!any) return {};
    std::string out;
    for (size_t i = 0; i < comments.size(); ++i) {
        if (i) out += kCommentSeparator;
        out += comments[i];
    }
    return out;
}

std::vector<std::string> split_moves(const std::string& text) {
    if (text.empty()) return {};
    return split(text, ' ');
}

void summary_json(std::string& out, const Game& g, std::int64_t ply_count, bool has_analysis) {
    auto field = [&](const char* name, const std::string& value) {
        out += ',';
        json_string(out, name);
        out += ':';
        json_string(out, value);
    };
    auto opt = [&](const char* name, const std::optional<int>& value) {
        out += ',';
        json_string(out, name);
        out += ':';
        out += value ? std::to_string(*value) : "null";
    };
    out += "{\"id\":" + std::to_string(g.id);
    field("event", g.event);
    field("site", g.site);
    field("date", g.date);
    field("round", g.round);
    field("white", g.white);
    field("black", g.black);
    field("result", g.result);
    opt("whiteElo", g.white_elo);
    opt("blackElo", g.black_elo);
    field("eco", g.eco);
    field("opening", g.opening);
    field("variation", g.variation);
    field("timeControl", g.time_control);
    field("termination", g.termination);
    out += ",\"plyCount\":" + std::to_string(ply_count);
    out += std::string(",\"hasAnalysis\":") + (has_analysis ? "true" : "false");
}

// Reads the summary columns of a row selected with kGameColumns.
Game row_to_game(const Statement& s, bool with_moves) {
    Game g;
    g.id = s.i64(0);
    g.event = s.text(1);
    g.site = s.text(2);
    g.date = s.text(3);
    g.round = s.text(4);
    g.white = s.text(5);
    g.black = s.text(6);
    g.result = s.text(7);
    g.white_elo = s.opt_int(8);
    g.black_elo = s.opt_int(9);
    g.eco = s.text(10);
    g.opening = s.text(11);
    g.variation = s.text(12);
    g.time_control = s.text(13);
    g.termination = s.text(14);
    g.start_fen = s.text(15);
    g.chess960 = s.i64(16) != 0;
    if (with_moves) {
        g.san = split_moves(s.text(17));
        g.uci = split_moves(s.text(18));
        g.comments = s.is_null(19) ? std::vector<std::string>() : split(s.text(19), kCommentSeparator);
        g.comments.resize(g.san.size());
        g.final_fen = s.text(20);
    }
    g.analysis = s.text(21);
    g.source = s.text(22);
    return g;
}

std::vector<std::string> parse_csv_line(const std::string& line) {
    std::vector<std::string> fields;
    std::string cur;
    bool quoted = false;
    for (size_t i = 0; i < line.size(); ++i) {
        const char c = line[i];
        if (quoted) {
            if (c == '"' && i + 1 < line.size() && line[i + 1] == '"') {
                cur += '"';
                ++i;
            } else if (c == '"') {
                quoted = false;
            } else {
                cur += c;
            }
        } else if (c == '"') {
            quoted = true;
        } else if (c == ',') {
            fields.push_back(trim(cur));
            cur.clear();
        } else if (c != '\r') {
            cur += c;
        }
    }
    fields.push_back(trim(cur));
    return fields;
}

std::optional<double> parse_double(const std::string& text) {
    try {
        size_t used = 0;
        const double v = std::stod(text, &used);
        if (used != text.size()) return std::nullopt;
        return v;
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

}  // namespace

std::string dup_key_for(const Game& g) {
    std::string key = g.white + '\x1f' + g.black + '\x1f' + g.date + '\x1f' + g.round + '\x1f' + g.result + '\x1f' +
                      g.start_fen + '\x1f' + join(g.uci, ' ');
    return fnv1a_hex(key);
}

Database::Database(const std::string& path) {
    const int flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX;
    if (sqlite3_open_v2(path.c_str(), &db_, flags, nullptr) != SQLITE_OK) {
        std::string msg = db_ ? sqlite3_errmsg(db_) : "cannot open database";
        sqlite3_close(db_);
        db_ = nullptr;
        throw Error(FCD_ERR_DATABASE, msg + ": " + path);
    }
    try {
        exec(db_, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        exec(db_, kSchema);
        Statement version(db_, "SELECT value FROM meta WHERE key = 'schema_version'");
        if (version.step()) {
            const int v = std::stoi(version.text(0));
            if (v > kSchemaVersion)
                throw Error(FCD_ERR_DATABASE, "database schema version " + std::to_string(v) +
                                                  " is newer than this program supports");
        } else {
            Statement insert(db_, "INSERT INTO meta(key, value) VALUES ('schema_version', ?1)");
            insert.bind(1, std::to_string(kSchemaVersion)).run();
        }
        exec(db_, "CREATE TEMP TABLE IF NOT EXISTS selection (id INTEGER PRIMARY KEY)");
    } catch (...) {
        sqlite3_close(db_);
        db_ = nullptr;
        throw;
    }
}

Database::~Database() { sqlite3_close(db_); }

fcd_import_result Database::import_pgn(const std::string& pgn_path, bool skip_duplicates, const Progress& progress) {
    fcd_import_result result{0, 0, 0};
    Transaction tx(db_);
    Statement find_dup(db_, "SELECT 1 FROM games WHERE dup_key = ?1 LIMIT 1");
    Statement insert(db_,
                     "INSERT INTO games (event, site, date, round, white, black, result, white_elo, black_elo, eco, "
                     "opening, variation, time_control, termination, start_fen, chess960, san_moves, uci_moves, "
                     "comments, ply_count, final_fen, dup_key, source, imported_at) VALUES "
                     "(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, "
                     "?21, ?22, ?23, ?24)");
    Statement insert_tag(db_, "INSERT OR REPLACE INTO game_tags (game_id, name, value, ord) VALUES (?1, ?2, ?3, ?4)");
    const std::string imported_at = now_iso();
    const auto file_name = utf8_path(pgn_path).filename().u8string();
    const std::string source(file_name.begin(), file_name.end());
    bool cancelled = false;
    std::int64_t processed = 0;

    read_pgn_file(pgn_path, [&](PgnGame& raw) {
        Game g;
        std::string error;
        ++processed;
        if (!build_game(raw, g, error)) {
            ++result.failed;
        } else {
            const std::string key = dup_key_for(g);
            bool duplicate = false;
            // A game without moves carries no evidence of duplication (rating-pool PGNs
            // legitimately repeat the same pairing and result), so it is always imported.
            if (skip_duplicates && !g.uci.empty()) {
                find_dup.bind(1, key);
                duplicate = find_dup.step();
                find_dup.reset();
            }
            if (duplicate) {
                ++result.duplicates;
            } else {
                insert.bind(1, g.event).bind(2, g.site).bind(3, g.date).bind(4, g.round).bind(5, g.white);
                insert.bind(6, g.black).bind(7, g.result).bind(8, g.white_elo).bind(9, g.black_elo);
                insert.bind(10, g.eco).bind(11, g.opening).bind(12, g.variation).bind(13, g.time_control);
                insert.bind(14, g.termination).bind_text_or_null(15, g.start_fen);
                insert.bind(16, static_cast<std::int64_t>(g.chess960)).bind(17, join(g.san, ' '));
                insert.bind(18, join(g.uci, ' ')).bind_text_or_null(19, join_comments(g.comments));
                insert.bind(20, static_cast<std::int64_t>(g.uci.size())).bind(21, g.final_fen).bind(22, key);
                insert.bind(23, source).bind(24, imported_at);
                insert.run();
                const std::int64_t id = sqlite3_last_insert_rowid(db_);
                std::int64_t ord = 0;
                for (const auto& [name, value] : g.extra_tags) {
                    insert_tag.bind(1, id).bind(2, name).bind(3, value).bind(4, ord++);
                    insert_tag.run();
                }
                ++result.imported;
            }
        }
        if (progress && processed % 250 == 0 && !progress(processed, 0)) {
            cancelled = true;
            return false;
        }
        return true;
    });

    if (cancelled) throw Error(FCD_ERR_CANCELLED, "import cancelled");
    tx.commit();
    if (progress) progress(processed, processed);
    return result;
}

Database::Where Database::build_where(const fcd_query* q) {
    Where w;
    std::vector<std::string> clauses;
    if (q && q->search && *q->search) {
        std::string pattern = "%";
        for (const char* p = q->search; *p; ++p) {
            if (*p == '%' || *p == '_' || *p == '\\') pattern += '\\';
            pattern += *p;
        }
        pattern += '%';
        w.params.push_back(pattern);
        clauses.push_back(
            "(white LIKE ?1 ESCAPE '\\' OR black LIKE ?1 ESCAPE '\\' OR event LIKE ?1 ESCAPE '\\' OR "
            "site LIKE ?1 ESCAPE '\\' OR eco LIKE ?1 ESCAPE '\\' OR opening LIKE ?1 ESCAPE '\\' OR "
            "variation LIKE ?1 ESCAPE '\\')");
    }
    if (q && q->id_count > 0) {
        if (!q->ids) throw Error(FCD_ERR_ARGUMENT, "ids is NULL but id_count > 0");
        exec(db_, "DELETE FROM temp.selection");
        Statement ins(db_, "INSERT OR IGNORE INTO temp.selection (id) VALUES (?1)");
        for (std::int64_t i = 0; i < q->id_count; ++i) {
            ins.bind(1, q->ids[i]);
            ins.run();
        }
        clauses.push_back("id IN (SELECT id FROM temp.selection)");
    }
    if (!clauses.empty()) {
        w.sql = " WHERE ";
        for (size_t i = 0; i < clauses.size(); ++i) w.sql += (i ? " AND " : "") + clauses[i];
    }
    return w;
}

std::string Database::order_clause(const fcd_query* q) const {
    static const std::map<std::string, std::string> allowed = {
        {"id", "id"},         {"date", "date"},       {"event", "event"},         {"white", "white"},
        {"black", "black"},   {"result", "result"},   {"white_elo", "white_elo"}, {"black_elo", "black_elo"},
        {"eco", "eco"},       {"opening", "opening"}, {"ply_count", "ply_count"}, {"round", "round"},
        {"site", "site"}};
    std::string column = "id";
    if (q && q->order_by && *q->order_by) {
        const auto it = allowed.find(q->order_by);
        if (it == allowed.end()) throw Error(FCD_ERR_ARGUMENT, std::string("cannot order by ") + q->order_by);
        column = it->second;
    }
    const char* dir = (q && q->descending) ? " DESC" : " ASC";
    std::string sql = " ORDER BY " + column + dir;
    if (column != "id") sql += std::string(", id") + dir;
    if (q && q->limit > 0) sql += " LIMIT " + std::to_string(q->limit) + " OFFSET " + std::to_string(std::max<std::int64_t>(0, q->offset));
    else if (q && q->offset > 0) sql += " LIMIT -1 OFFSET " + std::to_string(q->offset);
    return sql;
}

std::int64_t Database::count(const fcd_query* q) {
    const Where w = build_where(q);
    Statement s(db_, "SELECT COUNT(*) FROM games" + w.sql);
    for (size_t i = 0; i < w.params.size(); ++i) s.bind(static_cast<int>(i + 1), w.params[i]);
    s.step();
    return s.i64(0);
}

std::string Database::query_json(const fcd_query* q) {
    const Where w = build_where(q);
    Statement s(db_, std::string("SELECT ") + kGameColumns + " FROM games" + w.sql + order_clause(q));
    for (size_t i = 0; i < w.params.size(); ++i) s.bind(static_cast<int>(i + 1), w.params[i]);
    std::string out = "[";
    bool first = true;
    while (s.step()) {
        const Game g = row_to_game(s, false);
        if (!first) out += ',';
        first = false;
        summary_json(out, g, s.i64(23), !s.is_null(21));
        out += '}';
    }
    out += ']';
    return out;
}

std::vector<std::int64_t> Database::select_ids(const fcd_query* q) {
    const Where w = build_where(q);
    Statement s(db_, "SELECT id FROM games" + w.sql + order_clause(q));
    for (size_t i = 0; i < w.params.size(); ++i) s.bind(static_cast<int>(i + 1), w.params[i]);
    std::vector<std::int64_t> ids;
    while (s.step()) ids.push_back(s.i64(0));
    return ids;
}

Game Database::load_game(std::int64_t id) {
    Statement game_stmt(db_, select_game_sql());
    Statement tag_stmt(db_, kSelectTags);
    return load_game(id, game_stmt, tag_stmt);
}

Game Database::load_game(std::int64_t id, Statement& game_stmt, Statement& tag_stmt) {
    game_stmt.bind(1, id);
    if (!game_stmt.step()) {
        game_stmt.reset();
        throw Error(FCD_ERR_NOT_FOUND, "no game with id " + std::to_string(id));
    }
    Game g = row_to_game(game_stmt, true);
    game_stmt.reset();
    tag_stmt.bind(1, id);
    while (tag_stmt.step()) g.extra_tags.emplace_back(tag_stmt.text(0), tag_stmt.text(1));
    tag_stmt.reset();
    return g;
}

void Database::for_each_game(const fcd_query* q, const std::function<void(const Game&)>& fn) {
    Statement game_stmt(db_, select_game_sql());
    Statement tag_stmt(db_, kSelectTags);
    for (const auto id : select_ids(q)) fn(load_game(id, game_stmt, tag_stmt));
}

void Database::for_each_result(
    const fcd_query* q, const std::function<void(const std::string&, const std::string&, const std::string&)>& fn) {
    const Where w = build_where(q);
    Statement s(db_, "SELECT white, black, result FROM games" + w.sql + order_clause(q));
    for (size_t i = 0; i < w.params.size(); ++i) s.bind(static_cast<int>(i + 1), w.params[i]);
    while (s.step()) fn(s.text(0), s.text(1), s.text(2));
}

std::string Database::game_json(std::int64_t id) {
    const Game g = load_game(id);
    std::string out;
    summary_json(out, g, static_cast<std::int64_t>(g.uci.size()), !g.analysis.empty());
    out += ",\"tags\":{";
    for (size_t i = 0; i < g.extra_tags.size(); ++i) {
        if (i) out += ',';
        json_string(out, g.extra_tags[i].first);
        out += ':';
        json_string(out, g.extra_tags[i].second);
    }
    out += "},\"startFen\":";
    if (g.start_fen.empty())
        out += "null";
    else
        json_string(out, g.start_fen);
    out += ",\"chess960\":";
    out += g.chess960 ? "true" : "false";
    auto array = [&](const char* name, const std::vector<std::string>& items) {
        out += ",\"";
        out += name;
        out += "\":[";
        for (size_t i = 0; i < items.size(); ++i) {
            if (i) out += ',';
            json_string(out, items[i]);
        }
        out += ']';
    };
    array("sanMoves", g.san);
    array("uciMoves", g.uci);
    array("comments", g.comments);
    out += ",\"finalFen\":";
    json_string(out, g.final_fen);
    out += ",\"source\":";
    json_string(out, g.source);
    out += ",\"analysis\":";
    out += g.analysis.empty() ? "null" : g.analysis;
    out += '}';
    return out;
}

void Database::update_dup_key(std::int64_t id) {
    const Game g = load_game(id);
    Statement s(db_, "UPDATE games SET dup_key = ?1 WHERE id = ?2");
    s.bind(1, dup_key_for(g)).bind(2, id).run();
}

void Database::set_tag(std::int64_t id, const std::string& tag, const std::string& value) {
    if (tag.empty()) throw Error(FCD_ERR_ARGUMENT, "tag name is empty");
    if (is_derived_tag(tag)) throw Error(FCD_ERR_ARGUMENT, tag + " is derived from the moves and cannot be set");
    load_game(id);  // existence check

    if (const char* column = column_for_tag(tag)) {
        Statement s(db_, std::string("UPDATE games SET ") + column + " = ?1 WHERE id = ?2");
        if (tag == "WhiteElo" || tag == "BlackElo") {
            if (!value.empty() && !parse_elo(value)) throw Error(FCD_ERR_ARGUMENT, "Elo must be a positive integer");
            s.bind(1, value.empty() ? std::optional<int>() : parse_elo(value));
        } else if (tag == "Result") {
            const std::string r = value.empty() ? "*" : value;
            if (r != "1-0" && r != "0-1" && r != "1/2-1/2" && r != "*")
                throw Error(FCD_ERR_ARGUMENT, "Result must be 1-0, 0-1, 1/2-1/2 or *");
            s.bind(1, r);
        } else {
            s.bind(1, value);
        }
        s.bind(2, id).run();
        if (tag == "White" || tag == "Black" || tag == "Date" || tag == "Round" || tag == "Result") update_dup_key(id);
        return;
    }

    if (value.empty()) {
        Statement s(db_, "DELETE FROM game_tags WHERE game_id = ?1 AND name = ?2");
        s.bind(1, id).bind(2, tag).run();
        return;
    }
    Statement s(db_,
                "INSERT INTO game_tags (game_id, name, value, ord) VALUES (?1, ?2, ?3, "
                "(SELECT COALESCE(MAX(ord), -1) + 1 FROM game_tags WHERE game_id = ?1)) "
                "ON CONFLICT(game_id, name) DO UPDATE SET value = excluded.value");
    s.bind(1, id).bind(2, tag).bind(3, value).run();
}

void Database::delete_games(const std::vector<std::int64_t>& ids) {
    Transaction tx(db_);
    Statement s(db_, "DELETE FROM games WHERE id = ?1");
    for (const auto id : ids) {
        s.bind(1, id);
        s.run();
    }
    tx.commit();
}

void Database::set_analysis(std::int64_t id, const std::string& json) {
    if (!json.empty() && !json_is_valid(json)) throw Error(FCD_ERR_PARSE, "analysis is not valid JSON");
    load_game(id);
    Statement s(db_, "UPDATE games SET analysis = ?1 WHERE id = ?2");
    s.bind_text_or_null(1, json).bind(2, id).run();
}

std::int64_t Database::fill_missing(const fcd_query* q, const OpeningBook* book, std::uint32_t flags,
                                    const Progress& progress) {
    if ((flags & FCD_FILL_OPENING) && !book) throw Error(FCD_ERR_ARGUMENT, "opening fill requires an opening book");
    const bool overwrite_opening = (flags & FCD_FILL_OVERWRITE_OPENING) != 0;
    const auto ids = select_ids(q);

    Transaction tx(db_);
    Statement set_opening(db_, "UPDATE games SET eco = ?1, opening = ?2, variation = ?3 WHERE id = ?4");
    Statement set_result(db_, "UPDATE games SET result = ?1, termination = ?2, dup_key = ?3 WHERE id = ?4");
    Statement game_stmt(db_, select_game_sql());
    Statement tag_stmt(db_, kSelectTags);
    std::int64_t updated = 0, done = 0;
    const auto total = static_cast<std::int64_t>(ids.size());

    for (const auto id : ids) {
        Game g = load_game(id, game_stmt, tag_stmt);
        bool changed = false;

        if ((flags & FCD_FILL_OPENING) && g.start_fen.empty() && !g.chess960 &&
            (overwrite_opening || g.eco.empty() || g.opening.empty())) {
            std::optional<OpeningMatch> match;
            try {
                match = book->classify(g.uci);
            } catch (const Error&) {
                match.reset();
            }
            if (match) {
                const auto [family, rest] = OpeningBook::split_name(match->entry->name);
                if (g.eco != match->entry->eco || g.opening != family || g.variation != rest) {
                    set_opening.bind(1, match->entry->eco).bind(2, family).bind(3, rest).bind(4, id);
                    set_opening.run();
                    changed = true;
                }
            }
        }

        if ((flags & FCD_FILL_RESULT) && (g.result.empty() || g.result == "*")) {
            TerminalInfo t;
            try {
                t = terminal_state(g);
            } catch (const Error&) {
                t = TerminalInfo{};
            }
            std::string result, reason;
            switch (t.kind) {
                case Terminal::Checkmate:
                    result = t.white_to_move ? "0-1" : "1-0";
                    reason = "checkmate";
                    break;
                case Terminal::Stalemate: result = "1/2-1/2"; reason = "stalemate"; break;
                case Terminal::InsufficientMaterial: result = "1/2-1/2"; reason = "insufficient material"; break;
                case Terminal::FiftyMoves: result = "1/2-1/2"; reason = "fifty-move rule"; break;
                case Terminal::Repetition: result = "1/2-1/2"; reason = "threefold repetition"; break;
                case Terminal::None: break;
            }
            if (!result.empty()) {
                g.result = result;
                set_result.bind(1, result).bind(2, g.termination.empty() ? reason : g.termination);
                set_result.bind(3, dup_key_for(g)).bind(4, id);
                set_result.run();
                changed = true;
            }
        }

        if (changed) ++updated;
        ++done;
        if (progress && done % 100 == 0 && !progress(done, total)) throw Error(FCD_ERR_CANCELLED, "fill cancelled");
    }
    tx.commit();
    if (progress) progress(total, total);
    return updated;
}

std::pair<std::int64_t, std::int64_t> Database::apply_ordo_csv(const std::string& csv_path, bool fill_elo,
                                                               bool overwrite) {
    std::ifstream in(utf8_path(csv_path), std::ios::binary);
    if (!in) throw Error(FCD_ERR_IO, "cannot open Ordo CSV: " + csv_path);
    std::string line;
    if (!std::getline(in, line)) throw Error(FCD_ERR_PARSE, "Ordo CSV is empty");
    const auto header = parse_csv_line(line);
    auto column = [&](const char* name) -> int {
        for (size_t i = 0; i < header.size(); ++i)
            if (iequals(header[i], name)) return static_cast<int>(i);
        return -1;
    };
    const int c_player = column("PLAYER"), c_rating = column("RATING"), c_error = column("ERROR");
    const int c_points = column("POINTS"), c_played = column("PLAYED"), c_percent = column("(%)");
    if (c_player < 0 || c_rating < 0) throw Error(FCD_ERR_PARSE, "Ordo CSV lacks PLAYER or RATING columns");

    std::vector<Rating> rows;
    while (std::getline(in, line)) {
        if (trim(line).empty()) continue;
        const auto f = parse_csv_line(line);
        auto at = [&](int c) { return c >= 0 && c < static_cast<int>(f.size()) ? f[c] : std::string(); };
        Rating r;
        r.player = to_utf8_lenient(at(c_player));
        const auto rating = parse_double(at(c_rating));
        if (r.player.empty() || !rating) throw Error(FCD_ERR_PARSE, "bad Ordo CSV row: " + line);
        r.rating = *rating;
        r.error = parse_double(at(c_error));
        r.points = parse_double(at(c_points)).value_or(0);
        r.played = static_cast<std::int64_t>(parse_double(at(c_played)).value_or(0));
        r.percent = parse_double(at(c_percent)).value_or(0);
        rows.push_back(std::move(r));
    }

    Transaction tx(db_);
    exec(db_, "DELETE FROM ratings");
    Statement ins(db_,
                  "INSERT OR REPLACE INTO ratings (player, rating, error, points, played, percent, ord) "
                  "VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)");
    std::int64_t ord = 0;
    for (const auto& r : rows) {
        ins.bind(1, r.player).bind(2, r.rating);
        if (r.error)
            ins.bind(3, *r.error);
        else
            ins.bind_null(3);
        ins.bind(4, r.points).bind(5, r.played).bind(6, r.percent).bind(7, ord++);
        ins.run();
    }
    std::int64_t written = 0;
    if (fill_elo) {
        for (const char* side : {"white", "black"}) {
            const std::string col = std::string(side) + "_elo";
            std::string sql = "UPDATE games SET " + col + " = (SELECT CAST(ROUND(rating) AS INTEGER) FROM ratings " +
                              "WHERE player = games." + side + ") WHERE " + side +
                              " IN (SELECT player FROM ratings)";
            if (!overwrite) sql += " AND " + col + " IS NULL";
            exec(db_, sql);
            written += sqlite3_changes(db_);
        }
    }
    tx.commit();
    return {static_cast<std::int64_t>(rows.size()), written};
}

std::vector<Rating> Database::ratings() {
    Statement s(db_, "SELECT player, rating, error, points, played, percent FROM ratings ORDER BY ord");
    std::vector<Rating> out;
    while (s.step()) {
        Rating r;
        r.player = s.text(0);
        r.rating = s.f64(1);
        if (!s.is_null(2)) r.error = s.f64(2);
        r.points = s.f64(3);
        r.played = s.i64(4);
        r.percent = s.f64(5);
        out.push_back(std::move(r));
    }
    return out;
}

std::string Database::ratings_json() {
    std::string out = "[";
    bool first = true;
    for (const auto& r : ratings()) {
        if (!first) out += ',';
        first = false;
        out += "{\"player\":";
        json_string(out, r.player);
        out += std::format(",\"rating\":{:.1f},\"error\":{},\"points\":{:.1f},\"played\":{},\"percent\":{:.1f}}}",
                           r.rating, r.error ? std::format("{:.1f}", *r.error) : "null", r.points, r.played,
                           r.percent);
    }
    out += ']';
    return out;
}

}  // namespace fcd
