#include "exporters.hpp"

#include <algorithm>
#include <cmath>
#include <format>
#include <fstream>

#include "util.hpp"

#ifndef FCD_VERSION_STRING
#define FCD_VERSION_STRING "0.0.0"
#endif

namespace fcd {

namespace {

std::string pgn_escape(const std::string& value) {
    std::string out;
    for (const char c : value) {
        if (c == '"' || c == '\\') out += '\\';
        out += c;
    }
    return out;
}

// PGN comments cannot contain '}'.
std::string comment_text(const std::string& c) {
    std::string out = c;
    std::replace(out.begin(), out.end(), '}', ')');
    return out;
}

bool black_moves_first(const std::string& fen) {
    const auto parts = split(fen, ' ');
    return parts.size() > 1 && parts[1] == "b";
}

int start_move_number(const std::string& fen) {
    const auto parts = split(fen, ' ');
    if (parts.size() > 5) {
        try {
            return std::max(1, std::stoi(parts[5]));
        } catch (const std::exception&) {
        }
    }
    return 1;
}

// Code points in UTF-8 text; used for column widths because std::format may pad by bytes.
size_t char_count(const std::string& text) {
    size_t n = 0;
    for (const char c : text)
        if ((static_cast<unsigned char>(c) & 0xC0) != 0x80) ++n;
    return n;
}

std::string pad_right(const std::string& text, size_t width) {
    const size_t n = char_count(text);
    return n >= width ? text : text + std::string(width - n, ' ');
}

std::string format_score(double score) {
    return std::abs(score - static_cast<std::int64_t>(score)) < 1e-9 ? std::format("{:.0f}", score)
                                                                       : std::format("{:.1f}", score);
}

class Output {
   public:
    explicit Output(const std::string& path) : out_(utf8_path(path), std::ios::binary | std::ios::trunc), path_(path) {
        if (!out_) throw Error(FCD_ERR_IO, "cannot write " + path);
    }
    Output& operator<<(const std::string& s) {
        out_ << s;
        return *this;
    }
    void finish() {
        out_.flush();
        if (!out_) throw Error(FCD_ERR_IO, "write failed: " + path_);
    }

   private:
    std::ofstream out_;
    std::string path_;
};

void standings_json(std::string& out, const Standings& st) {
    out += "\"standings\":[";
    const auto rows = st.ordered();
    for (size_t i = 0; i < rows.size(); ++i) {
        const auto& p = rows[i];
        if (i) out += ',';
        out += std::format("{{\"rank\":{},\"player\":", i + 1);
        json_string(out, p.name);
        out += std::format(",\"rating\":{},\"games\":{},\"wins\":{},\"draws\":{},\"losses\":{},\"score\":{},",
                           p.rating ? std::format("{:.1f}", *p.rating) : "null", p.games, p.wins, p.draws, p.losses,
                           format_score(p.score));
        out += std::format("\"percent\":{:.1f}}}", p.games ? 100.0 * p.score / p.games : 0.0);
    }
    out += ']';
}

void game_json(std::string& out, const Game& g) {
    out += std::format("{{\"id\":{},\"tags\":{{", g.id);
    bool first = true;
    auto tag = [&](const std::string& name, const std::string& value) {
        if (value.empty()) return;
        if (!first) out += ',';
        first = false;
        json_string(out, name);
        out += ':';
        json_string(out, value);
    };
    for (const auto& name : known_tag_names()) tag(name, *known_tag_value(g, name));
    for (const auto& [name, value] : g.extra_tags) tag(name, value);
    out += "},\"startFen\":";
    if (g.start_fen.empty())
        out += "null";
    else
        json_string(out, g.start_fen);
    out += ",\"moves\":[";
    for (size_t i = 0; i < g.san.size(); ++i) {
        if (i) out += ',';
        out += "{\"san\":";
        json_string(out, g.san[i]);
        out += ",\"uci\":";
        json_string(out, g.uci[i]);
        if (i < g.comments.size() && !g.comments[i].empty()) {
            out += ",\"comment\":";
            json_string(out, g.comments[i]);
        }
        out += '}';
    }
    out += "],\"result\":";
    json_string(out, g.result);
    out += ",\"analysis\":";
    out += g.analysis.empty() ? "null" : g.analysis;
    out += '}';
}

std::string xml_attr(const char* name, const std::string& value) {
    return std::string(" ") + name + "=\"" + xml_escape(value) + "\"";
}

std::string game_xml(const Game& g) {
    std::string s = "    <game" + xml_attr("id", std::to_string(g.id)) + ">\n";
    for (const auto& name : known_tag_names()) {
        const auto value = *known_tag_value(g, name);
        if (!value.empty()) s += "      <tag" + xml_attr("name", name) + ">" + xml_escape(value) + "</tag>\n";
    }
    for (const auto& [name, value] : g.extra_tags)
        s += "      <tag" + xml_attr("name", name) + ">" + xml_escape(value) + "</tag>\n";
    if (!g.start_fen.empty()) s += "      <startFen>" + xml_escape(g.start_fen) + "</startFen>\n";
    s += "      <moves>\n";
    for (size_t i = 0; i < g.san.size(); ++i) {
        s += "        <move" + xml_attr("ply", std::to_string(i + 1)) + xml_attr("san", g.san[i]) +
             xml_attr("uci", g.uci[i]);
        if (i < g.comments.size() && !g.comments[i].empty())
            s += ">" + xml_escape(g.comments[i]) + "</move>\n";
        else
            s += "/>\n";
    }
    s += "      </moves>\n";
    if (!g.analysis.empty()) s += "      <analysis>" + xml_escape(g.analysis) + "</analysis>\n";
    s += "    </game>\n";
    return s;
}

}  // namespace

void Standings::add(const std::string& white, const std::string& black, const std::string& result) {
    double white_points;
    if (result == "1-0")
        white_points = 1;
    else if (result == "0-1")
        white_points = 0;
    else if (result == "1/2-1/2")
        white_points = 0.5;
    else
        return;
    ++finished_;
    auto& w = players_[white];
    auto& b = players_[black];
    w.name = white;
    b.name = black;
    ++w.games;
    ++b.games;
    w.score += white_points;
    b.score += 1 - white_points;
    if (white_points == 1) {
        ++w.wins;
        ++b.losses;
    } else if (white_points == 0) {
        ++w.losses;
        ++b.wins;
    } else {
        ++w.draws;
        ++b.draws;
    }
    auto& wb = pairs_[{white, black}];
    wb.first += white_points;
    ++wb.second;
    auto& bw = pairs_[{black, white}];
    bw.first += 1 - white_points;
    ++bw.second;
}

void Standings::attach_ratings(const std::vector<Rating>& ratings) {
    for (const auto& r : ratings) {
        const auto it = players_.find(r.player);
        if (it != players_.end()) it->second.rating = r.rating;
    }
}

std::vector<PlayerStanding> Standings::ordered() const {
    std::vector<PlayerStanding> rows;
    for (const auto& [name, p] : players_) rows.push_back(p);
    std::sort(rows.begin(), rows.end(), [](const auto& a, const auto& b) {
        if (a.score != b.score) return a.score > b.score;
        if (a.games != b.games) return a.games < b.games;
        return a.name < b.name;
    });
    return rows;
}

std::pair<double, std::int64_t> Standings::head_to_head(const std::string& a, const std::string& b) const {
    const auto it = pairs_.find({a, b});
    return it == pairs_.end() ? std::pair<double, std::int64_t>{0, 0} : it->second;
}

std::string game_to_pgn(const Game& g) {
    std::string out;
    for (const auto& name : known_tag_names()) {
        const std::string value = *known_tag_value(g, name);
        const bool roster = name == "Event" || name == "Site" || name == "Date" || name == "Round" ||
                            name == "White" || name == "Black" || name == "Result";
        if (!roster && value.empty()) continue;
        std::string v = value;
        if (roster && v.empty()) v = name == "Date" ? "????.??.??" : name == "Result" ? "*" : "?";
        out += "[" + name + " \"" + pgn_escape(v) + "\"]\n";
    }
    if (!g.start_fen.empty()) {
        out += "[SetUp \"1\"]\n";
        out += "[FEN \"" + pgn_escape(g.start_fen) + "\"]\n";
    }
    out += "[PlyCount \"" + std::to_string(g.san.size()) + "\"]\n";
    for (const auto& [name, value] : g.extra_tags) out += "[" + name + " \"" + pgn_escape(value) + "\"]\n";
    out += '\n';

    std::string line;
    auto emit = [&](const std::string& token) {
        if (!line.empty() && line.size() + 1 + token.size() > 79) {
            out += line + '\n';
            line.clear();
        }
        if (!line.empty()) line += ' ';
        line += token;
    };
    const bool black_first = !g.start_fen.empty() && black_moves_first(g.start_fen);
    int number = g.start_fen.empty() ? 1 : start_move_number(g.start_fen);
    bool previous_had_comment = false;
    for (size_t i = 0; i < g.san.size(); ++i) {
        const bool white_to_move = ((i % 2 == 0) != black_first);
        if (white_to_move)
            emit(std::to_string(number) + ".");
        else if (i == 0 || previous_had_comment)
            emit(std::to_string(number) + "...");
        emit(g.san[i]);
        previous_had_comment = i < g.comments.size() && !g.comments[i].empty();
        if (previous_had_comment) {
            // Comments are emitted word by word so line wrapping stays under 80 columns.
            const auto words = split(comment_text(g.comments[i]), ' ');
            for (size_t w = 0; w < words.size(); ++w) {
                std::string word = words[w];
                if (w == 0) word = "{" + word;
                if (w + 1 == words.size()) word += "}";
                emit(word);
            }
        }
        if (!white_to_move) ++number;
    }
    emit(g.result.empty() ? "*" : g.result);
    out += line + "\n\n";
    return out;
}

std::string crosstable_text(const Standings& st, std::int64_t total_games) {
    const auto rows = st.ordered();
    size_t name_width = 6;
    for (const auto& r : rows) name_width = std::max(name_width, char_count(r.name));
    size_t cell_width = 4;
    for (size_t i = 0; i < rows.size(); ++i)
        for (size_t j = 0; j < rows.size(); ++j)
            if (i != j) {
                const auto [s, n] = st.head_to_head(rows[i].name, rows[j].name);
                if (n) cell_width = std::max(cell_width, (format_score(s) + "/" + std::to_string(n)).size());
            }

    std::string out;
    out += std::format("Crosstable: {} players, {} finished games of {} selected\n", rows.size(),
                       st.finished_games(), total_games);
    out += std::format("Generated by fastchess-desktop {}\n\n", FCD_VERSION_STRING);
    std::string header = std::format("{:>3}  {}  {:>7}  {:>5}  {:>6}  {:>6}  {:>4}  {:>4}  {:>4} |", "#",
                                     pad_right("Player", name_width), "Rating", "Games", "Score", "%", "W", "D", "L");
    for (size_t j = 0; j < rows.size(); ++j) header += std::format(" {:>{}}", j + 1, cell_width);
    out += header + '\n' + std::string(header.size(), '-') + '\n';

    for (size_t i = 0; i < rows.size(); ++i) {
        const auto& p = rows[i];
        const std::string rating = p.rating ? std::format("{:.0f}", *p.rating) : "-";
        out += std::format("{:>3}  {}  {:>7}  {:>5}  {:>6}  {:>6.1f}  {:>4}  {:>4}  {:>4} |", i + 1,
                           pad_right(p.name, name_width), rating, p.games, format_score(p.score),
                           p.games ? 100.0 * p.score / p.games : 0.0, p.wins, p.draws, p.losses);
        for (size_t j = 0; j < rows.size(); ++j) {
            std::string cell;
            if (i == j) {
                cell = "x";
            } else {
                const auto [s, n] = st.head_to_head(p.name, rows[j].name);
                cell = n ? format_score(s) + "/" + std::to_string(n) : ".";
            }
            out += std::format(" {:>{}}", cell, cell_width);
        }
        out += '\n';
    }
    return out;
}

std::int64_t export_games(Database& db, const fcd_query* query, fcd_export_format format, const std::string& path) {
    // Ordering and paging apply to exports too, so a query can export exactly what the table shows.
    Output out(path);
    std::int64_t written = 0;

    if (format == FCD_EXPORT_PGN) {
        db.for_each_game(query, [&](const Game& g) {
            out << game_to_pgn(g);
            ++written;
        });
        out.finish();
        return written;
    }
    if (format != FCD_EXPORT_JSON && format != FCD_EXPORT_XML && format != FCD_EXPORT_CROSSTABLE_TXT)
        throw Error(FCD_ERR_ARGUMENT, "unknown export format");

    // First pass: standings from results only. Second pass: stream full games.
    Standings standings;
    db.for_each_result(query, [&](const std::string& white, const std::string& black, const std::string& result) {
        standings.add(white, black, result);
        ++written;
    });
    standings.attach_ratings(db.ratings());

    if (format == FCD_EXPORT_CROSSTABLE_TXT) {
        out << crosstable_text(standings, written);
    } else if (format == FCD_EXPORT_JSON) {
        std::string s = std::format("{{\"generator\":\"fastchess-desktop\",\"version\":\"{}\",", FCD_VERSION_STRING);
        standings_json(s, standings);
        s += ",\"games\":[";
        out << s;
        bool first = true;
        db.for_each_game(query, [&](const Game& g) {
            s.clear();
            if (!first) s += ',';
            first = false;
            game_json(s, g);
            out << s;
        });
        out << "]}\n";
    } else {
        out << "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
        out << std::string("<chessDatabase") + xml_attr("generator", "fastchess-desktop") +
                   xml_attr("version", FCD_VERSION_STRING) + ">\n";
        out << "  <standings>\n";
        const auto rows = standings.ordered();
        for (size_t i = 0; i < rows.size(); ++i) {
            const auto& p = rows[i];
            out << "    <player" + xml_attr("rank", std::to_string(i + 1)) + xml_attr("name", p.name) +
                       (p.rating ? xml_attr("rating", std::format("{:.1f}", *p.rating)) : std::string()) +
                       xml_attr("games", std::to_string(p.games)) + xml_attr("wins", std::to_string(p.wins)) +
                       xml_attr("draws", std::to_string(p.draws)) + xml_attr("losses", std::to_string(p.losses)) +
                       xml_attr("score", format_score(p.score)) +
                       xml_attr("percent", std::format("{:.1f}", p.games ? 100.0 * p.score / p.games : 0.0)) +
                       "/>\n";
        }
        out << "  </standings>\n  <games>\n";
        db.for_each_game(query, [&](const Game& g) { out << game_xml(g); });
        out << "  </games>\n</chessDatabase>\n";
    }
    out.finish();
    return written;
}

}  // namespace fcd
