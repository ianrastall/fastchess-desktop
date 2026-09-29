// Engine rating lists: an Ordo CSV file ("#","PLAYER","RATING","ERROR","POINTS","PLAYED","(%)"),
// such as the UCERL list, looked up by the name an engine reports over UCI.
#pragma once

#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace fcd::ratings {

struct Entry {
    std::string player;
    double rating = 0;
    long long games = 0;
};

struct Match {
    std::string player;         // the list entry the rating comes from
    double rating = 0;          // estimated: the entry's rating plus kNewerVersionBonus
    long long games = 0;        // games behind the entry's rating
    bool estimated = false;     // the engine is a newer version of `player`, which is in the list
    double base_rating = 0;     // the entry's own rating
};

// An unlisted version of a listed engine is estimated this much above the nearest older listed version.
constexpr double kNewerVersionBonus = 10;
// Older versions with fewer games than this are used for an estimate only when no other is listed:
// ratings from a handful of games are noise.
constexpr long long kEstimateMinGames = 100;

// Comparison key for engine names: lower case; parenthesized parts, build tags (x64, avx2, bmi2 ...)
// and a leading "v" on version numbers removed; trailing ".0" components of a version removed, so
// "Obsidian 16.0 (x64 avx2)" and "obsidian v16" give the same key.
std::string normalize(std::string_view name);

class List {
   public:
    // Throws Error(FCD_ERR_NOT_FOUND) for a missing file, FCD_ERR_PARSE when no rating row is found.
    static List load(const std::string& csv_path);
    static List parse(std::string_view csv);

    size_t size() const { return entries_.size(); }

    // Exact match on the normalized name. Otherwise, when the name is "<engine> <version> [<suffix>]"
    // and older versions of that engine with the same suffix are listed, an estimate from the
    // newest of them that has at least kEstimateMinGames games (or the newest of all, if none has).
    // When several entries share a key, the one with the most games is used.
    std::optional<Match> lookup(std::string_view engine_name) const;

   private:
    struct Versioned {
        std::vector<long long> version;
        size_t index;
    };

    std::vector<Entry> entries_;
    std::unordered_map<std::string, size_t> exact_;
    std::unordered_map<std::string, std::vector<Versioned>> families_;  // "<engine>|<suffix>"

    void add(Entry entry);
};

}  // namespace fcd::ratings
