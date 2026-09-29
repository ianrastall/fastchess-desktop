// Pairing rules for the tournament formats fastchess does not schedule itself.
#pragma once

#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

namespace fcd::formats {

// White is listed first to fastchess; black is empty for a bye.
struct Pairing {
    std::string white;
    std::optional<std::string> black;
    bool operator==(const Pairing&) const = default;
};

// Standard bracket order for a power-of-two field: 1, N, N/2, N/2+1, ... so that seeds 1 and 2
// can only meet in the final (for 8: 1 8 4 5 2 7 3 6). Throws for sizes that are not a power of two.
std::vector<int> bracket_order(int size);

// First knockout round for players in seed order. The bracket is padded to a power of two;
// the top seeds receive the byes.
std::vector<Pairing> knockout_first_round(const std::vector<std::string>& seeded);

// Next knockout round: winners of adjacent matches meet.
std::vector<Pairing> knockout_next_round(const std::vector<std::string>& winners_in_bracket_order);

struct SwissState {
    std::vector<std::string> seeded;
    std::map<std::string, double> scores;
    std::map<std::string, std::set<std::string>> opponents;
    std::set<std::string> had_bye;
    std::map<std::string, int> white_counts;
};

struct SwissRound {
    std::vector<Pairing> pairs;
    std::optional<std::string> bye;
};

// One Swiss round. Players are ranked by score (then by seed) and paired top-down with the nearest
// opponent they have not met; backtracking resolves dead ends, and rematches are allowed only when
// no rematch-free pairing exists. With an odd count, the lowest-ranked player without a bye sits out.
// Within a pair, the player with fewer games as White (then the higher-ranked one) is listed first.
SwissRound swiss_round(const SwissState& state);

}  // namespace fcd::formats
