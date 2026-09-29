#include "formats.hpp"

#include <algorithm>

#include "util.hpp"

namespace fcd::formats {

namespace {

// Upper bound on backtracking steps before rematches are allowed.
constexpr int kSearchBudget = 200'000;

template <typename K, typename V>
V value_or(const std::map<K, V>& m, const K& key, V fallback) {
    const auto it = m.find(key);
    return it == m.end() ? fallback : it->second;
}

bool try_pair(const std::vector<std::string>& ranked, const std::map<std::string, std::set<std::string>>& opponents,
              bool allow_rematch, int& budget, std::vector<std::pair<std::string, std::string>>& out) {
    if (ranked.empty()) return true;
    if (--budget < 0) return false;
    const std::string& first = ranked[0];
    const auto met = opponents.find(first);
    for (size_t j = 1; j < ranked.size(); ++j) {
        const std::string& candidate = ranked[j];
        if (!allow_rematch && met != opponents.end() && met->second.count(candidate)) continue;
        std::vector<std::string> rest;
        rest.reserve(ranked.size() - 2);
        for (size_t k = 1; k < ranked.size(); ++k)
            if (k != j) rest.push_back(ranked[k]);
        const size_t mark = out.size();
        out.emplace_back(first, candidate);
        if (try_pair(rest, opponents, allow_rematch, budget, out)) return true;
        out.resize(mark);
        if (budget < 0) return false;
    }
    return false;
}

}  // namespace

std::vector<int> bracket_order(int size) {
    if (size < 2 || (size & (size - 1)) != 0) throw Error(FCD_ERR_ARGUMENT, "bracket size must be a power of two >= 2");
    std::vector<int> seeds{1};
    while (static_cast<int>(seeds.size()) < size) {
        std::vector<int> next;
        const int sum = static_cast<int>(seeds.size()) * 2 + 1;
        for (const int s : seeds) {
            next.push_back(s);
            next.push_back(sum - s);
        }
        seeds = std::move(next);
    }
    return seeds;
}

std::vector<Pairing> knockout_first_round(const std::vector<std::string>& seeded) {
    if (seeded.size() < 2) throw Error(FCD_ERR_ARGUMENT, "a knockout needs at least two players");
    int size = 2;
    while (size < static_cast<int>(seeded.size())) size *= 2;
    const auto order = bracket_order(size);
    const int n = static_cast<int>(seeded.size());
    std::vector<Pairing> pairs;
    for (size_t i = 0; i < order.size(); i += 2) {
        const bool has_a = order[i] <= n, has_b = order[i + 1] <= n;
        if (!has_a) pairs.push_back({seeded[order[i + 1] - 1], std::nullopt});
        else if (!has_b) pairs.push_back({seeded[order[i] - 1], std::nullopt});
        else pairs.push_back({seeded[order[i] - 1], seeded[order[i + 1] - 1]});
    }
    return pairs;
}

std::vector<Pairing> knockout_next_round(const std::vector<std::string>& winners) {
    std::vector<Pairing> pairs;
    for (size_t i = 0; i + 1 < winners.size(); i += 2) pairs.push_back({winners[i], winners[i + 1]});
    return pairs;
}

SwissRound swiss_round(const SwissState& state) {
    std::map<std::string, size_t> seed;
    for (size_t i = 0; i < state.seeded.size(); ++i) seed[state.seeded[i]] = i;
    std::vector<std::string> ranked = state.seeded;
    std::stable_sort(ranked.begin(), ranked.end(), [&](const std::string& a, const std::string& b) {
        const double sa = value_or(state.scores, a, 0.0), sb = value_or(state.scores, b, 0.0);
        if (sa != sb) return sa > sb;
        return seed[a] < seed[b];
    });

    SwissRound round;
    if (ranked.size() % 2 == 1) {
        auto it = std::find_if(ranked.rbegin(), ranked.rend(), [&](const std::string& p) { return !state.had_bye.count(p); });
        round.bye = it != ranked.rend() ? *it : ranked.back();
        ranked.erase(std::find(ranked.begin(), ranked.end(), *round.bye));
    }

    std::vector<std::pair<std::string, std::string>> pairs;
    int budget = kSearchBudget;
    if (!try_pair(ranked, state.opponents, false, budget, pairs)) {
        pairs.clear();
        budget = 1 << 30;  // with rematches allowed the first attempt always succeeds
        try_pair(ranked, state.opponents, true, budget, pairs);
    }
    for (const auto& [a, b] : pairs) {
        const int wa = value_or(state.white_counts, a, 0), wb = value_or(state.white_counts, b, 0);
        round.pairs.push_back(wb < wa ? Pairing{b, a} : Pairing{a, b});
    }
    return round;
}

}  // namespace fcd::formats
