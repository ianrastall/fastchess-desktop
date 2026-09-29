#include "ratings.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <fstream>
#include <iterator>
#include <unordered_set>

#include "util.hpp"

namespace fcd::ratings {

namespace {

bool is_digit(char c) { return c >= '0' && c <= '9'; }

bool is_space(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

// Compiler and CPU build tags that say nothing about the engine version. Bare numbers such as
// "64" are kept: they can be versions ("ShashChess 32").
const std::unordered_set<std::string_view>& build_tags() {
    static const std::unordered_set<std::string_view> tags = {
        "x64",    "x86",     "x86-64",  "amd64",  "64bit",  "64-bit",  "32bit", "32-bit", "w32",    "w64",
        "win32",  "win64",   "windows", "linux",  "avx",    "avx2",    "avx512", "avx-512", "avxvnni", "vnni",
        "vnni256", "vnni512", "bmi",    "bmi2",   "popcnt", "nopopcnt", "pext",  "sse",    "sse2",   "sse3",
        "ssse3",  "sse41",   "sse4.1",  "sse42",  "sse4.2", "modern",  "native", "ja",     "arm64",  "neon"};
    return tags;
}

// "v6.8" -> "6.8", "16.0" -> "16", "0.10.0" -> "0.10", "2909.00" -> "2909", "16.0v" -> "16v".
std::string normalize_version(std::string token) {
    if (token.size() > 1 && token[0] == 'v' && is_digit(token[1])) token.erase(0, 1);
    if (token.empty() || !is_digit(token[0])) return token;
    size_t end = 0;
    while (end < token.size() && (is_digit(token[end]) || token[end] == '.')) ++end;
    std::string number = token.substr(0, end);
    const std::string rest = token.substr(end);
    while (!number.empty() && number.back() == '.') number.pop_back();
    for (;;) {
        const auto dot = number.rfind('.');
        if (dot == std::string::npos) break;
        const auto last = std::string_view(number).substr(dot + 1);
        if (last.empty() || last.find_first_not_of('0') != std::string_view::npos) break;
        number.erase(dot);
    }
    return number + rest;
}

std::vector<std::string> tokens(std::string_view name) {
    std::string text;
    text.reserve(name.size());
    int depth = 0;  // inside (...) or [...]
    for (const char raw : name) {
        const char c = raw >= 'A' && raw <= 'Z' ? static_cast<char>(raw - 'A' + 'a') : raw;
        if (c == '(' || c == '[') ++depth;
        else if ((c == ')' || c == ']') && depth > 0) --depth;
        else if (depth == 0) text += c == '_' ? ' ' : c;
    }
    std::vector<std::string> out;
    std::string current;
    for (size_t i = 0; i <= text.size(); ++i) {
        if (i == text.size() || is_space(text[i])) {
            if (!current.empty() && !build_tags().contains(current)) out.push_back(normalize_version(current));
            current.clear();
        } else {
            current += text[i];
        }
    }
    return out;
}

std::string joined(const std::vector<std::string>& parts, size_t from, size_t to) {
    std::string out;
    for (size_t i = from; i < to; ++i) {
        if (i > from) out += ' ';
        out += parts[i];
    }
    return out;
}

// "<engine> <version> [<suffix>]" where the version is dotted digits, such as "stockfish 17.1" or
// "plentychess 7.0.19 se". The engine part is everything before the first token that starts with a digit.
struct VersionedName {
    std::string family;  // "<engine>|<suffix>"
    std::vector<long long> version;
};

std::optional<VersionedName> versioned(const std::vector<std::string>& parts) {
    for (size_t i = 1; i < parts.size(); ++i) {
        const auto& token = parts[i];
        if (!is_digit(token[0])) continue;
        if (token.find_first_not_of("0123456789.") != std::string::npos) return std::nullopt;
        VersionedName v;
        for (const auto& component : split(token, '.')) {
            long long n = 0;
            const auto r = std::from_chars(component.data(), component.data() + component.size(), n);
            if (r.ec != std::errc() || r.ptr != component.data() + component.size()) return std::nullopt;
            v.version.push_back(n);
        }
        v.family = joined(parts, 0, i) + "|" + joined(parts, i + 1, parts.size());
        return v;
    }
    return std::nullopt;
}

int compare_versions(const std::vector<long long>& a, const std::vector<long long>& b) {
    for (size_t i = 0; i < std::max(a.size(), b.size()); ++i) {
        const long long x = i < a.size() ? a[i] : 0, y = i < b.size() ? b[i] : 0;
        if (x != y) return x < y ? -1 : 1;
    }
    return 0;
}

// One CSV record; fields may be quoted, with "" for a quote inside.
std::vector<std::string> csv_fields(std::string_view line) {
    std::vector<std::string> fields(1);
    bool quoted = false;
    for (size_t i = 0; i < line.size(); ++i) {
        const char c = line[i];
        if (quoted) {
            if (c == '"' && i + 1 < line.size() && line[i + 1] == '"') {
                fields.back() += '"';
                ++i;
            } else if (c == '"') {
                quoted = false;
            } else {
                fields.back() += c;
            }
        } else if (c == '"') {
            quoted = true;
        } else if (c == ',') {
            fields.emplace_back();
        } else {
            fields.back() += c;
        }
    }
    for (auto& f : fields) f = trim(f);
    return fields;
}

std::optional<double> to_number(const std::string& text) {
    double v = 0;
    const auto r = std::from_chars(text.data(), text.data() + text.size(), v);
    if (r.ec != std::errc() || r.ptr != text.data() + text.size() || !std::isfinite(v)) return std::nullopt;
    return v;
}

}  // namespace

std::string normalize(std::string_view name) {
    const auto parts = tokens(name);
    return joined(parts, 0, parts.size());
}

List List::load(const std::string& csv_path) {
    std::ifstream in(utf8_path(csv_path), std::ios::binary);
    if (!in) throw Error(FCD_ERR_NOT_FOUND, "Rating list not found: " + csv_path);
    const std::string text{std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
    try {
        return parse(to_utf8_lenient(text));
    } catch (const Error& e) {
        throw Error(e.status(), csv_path + ": " + e.what());
    }
}

List List::parse(std::string_view csv) {
    if (csv.substr(0, 3) == "\xEF\xBB\xBF") csv.remove_prefix(3);
    List list;
    int player = -1, rating = -1, games = -1;
    for (const auto& raw : split(csv, '\n')) {
        const auto line = trim(raw);
        if (line.empty()) continue;
        const auto fields = csv_fields(line);
        if (player < 0) {
            for (size_t i = 0; i < fields.size(); ++i) {
                if (iequals(fields[i], "PLAYER")) player = static_cast<int>(i);
                else if (iequals(fields[i], "RATING")) rating = static_cast<int>(i);
                else if (iequals(fields[i], "PLAYED")) games = static_cast<int>(i);
            }
            if (player < 0 || rating < 0)
                throw Error(FCD_ERR_PARSE, "not an Ordo CSV rating list (the first line has no PLAYER and RATING columns)");
            continue;
        }
        if (static_cast<int>(fields.size()) <= std::max(player, rating)) continue;
        const auto value = to_number(fields[rating]);
        if (!value || fields[player].empty()) continue;
        Entry e{fields[player], *value, 0};
        if (games >= 0 && games < static_cast<int>(fields.size()))
            if (const auto n = to_number(fields[games])) e.games = static_cast<long long>(*n);
        list.add(std::move(e));
    }
    if (list.entries_.empty()) throw Error(FCD_ERR_PARSE, "the rating list has no ratings");
    return list;
}

void List::add(Entry entry) {
    const auto parts = tokens(entry.player);
    if (parts.empty()) return;
    const size_t index = entries_.size();
    const auto key = joined(parts, 0, parts.size());
    const auto [it, inserted] = exact_.emplace(key, index);
    if (!inserted && entry.games > entries_[it->second].games) it->second = index;
    if (auto v = versioned(parts)) families_[v->family].push_back({std::move(v->version), index});
    entries_.push_back(std::move(entry));
}

std::optional<Match> List::lookup(std::string_view engine_name) const {
    const auto parts = tokens(engine_name);
    if (parts.empty()) return std::nullopt;
    if (const auto it = exact_.find(joined(parts, 0, parts.size())); it != exact_.end()) {
        const auto& e = entries_[it->second];
        return Match{e.player, e.rating, e.games, false, e.rating};
    }
    const auto v = versioned(parts);
    if (!v) return std::nullopt;
    const auto family = families_.find(v->family);
    if (family == families_.end()) return std::nullopt;
    auto newest_older = [&](long long min_games) -> const Versioned* {
        const Versioned* best = nullptr;
        for (const auto& candidate : family->second) {
            if (compare_versions(candidate.version, v->version) >= 0 || entries_[candidate.index].games < min_games)
                continue;
            const int order = best ? compare_versions(candidate.version, best->version) : 1;
            if (order > 0 || (order == 0 && entries_[candidate.index].games > entries_[best->index].games)) best = &candidate;
        }
        return best;
    };
    const Versioned* best = newest_older(kEstimateMinGames);
    if (!best) best = newest_older(0);
    if (!best) return std::nullopt;
    const auto& e = entries_[best->index];
    const double estimate = std::round((e.rating + kNewerVersionBonus) * 10) / 10;
    return Match{e.player, estimate, e.games, true, e.rating};
}

}  // namespace fcd::ratings
