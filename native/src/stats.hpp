// Match statistics as fastchess computes them (fastchess 1.8.2: stats.hpp, elo_wdl.cpp,
// elo_pentanomial.cpp, sprt.cpp, scoreboard.hpp), so live values match its reports.
#pragma once

#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "fcd/fcd.h"

namespace fcd::stats {

// Results from one engine's point of view. A pair is the two games played on one opening
// with colors swapped; ld is one loss and one draw, wl one win and one loss, and so on.
struct MatchStats {
    int wins = 0, draws = 0, losses = 0;
    int ll = 0, ld = 0, wl = 0, dd = 0, wd = 0, ww = 0;

    int games() const { return wins + draws + losses; }
    int pairs() const { return ll + ld + wl + dd + wd + ww; }
    double points() const { return wins + 0.5 * draws; }
    MatchStats inverted() const { return {losses, draws, wins, ww, wd, wl, dd, ld, ll}; }
    MatchStats& operator+=(const MatchStats& o);
    bool operator==(const MatchStats&) const = default;
};

MatchStats from_abi(const fcd_match_stats& s);
fcd_match_stats to_abi(const MatchStats& s);

// Elo with a 95% margin, normalized Elo and likelihood of superiority (percent). Values can be
// infinite or NaN for a 0% or 100% score, as in fastchess.
struct EloEstimate {
    double elo, error, nelo, nelo_error, los;
};

std::optional<EloEstimate> elo_from_games(const MatchStats& s);  // fastchess EloWDL
std::optional<EloEstimate> elo_from_pairs(const MatchStats& s);  // fastchess EloPentanomial
double score_to_elo(double score);

enum class SprtModel { Normalized = 0, Logistic = 1, Bayesian = 2 };
enum class SprtOutcome { Continue = 0, AcceptH0 = 1, AcceptH1 = 2 };

class Sprt {
   public:
    Sprt(double alpha, double beta, double elo0, double elo1, SprtModel model);

    double llr(const MatchStats& s, bool pentanomial) const;
    double lower_bound() const { return lower_; }
    double upper_bound() const { return upper_; }
    // Progress toward the bound on the LLR's side, negative toward H0 (fastchess getFraction).
    double fraction(double llr) const { return llr >= 0 ? llr / upper_ : -llr / lower_; }
    SprtOutcome outcome(double llr) const;

   private:
    double elo0_, elo1_, lower_, upper_;
    SprtModel model_;
    double llr_games(int win, int draw, int loss) const;
    double llr_pairs(const MatchStats& s) const;
};

// True when a fastchess game end reason means an engine failed (time loss, crash, stall, illegal move).
bool is_engine_failure_reason(const std::string& reason);

// Live tournament results from fastchess "Finished game" lines. Game counts update with every game;
// pentanomial counts when both games of a pair are done. Pairs are games (n - 1) / games_per_encounter,
// as fastchess schedules them (base_scheduler.hpp).
class Scoreboard {
   public:
    Scoreboard(int games_per_encounter, bool pentanomial);

    bool pentanomial() const { return pentanomial_; }
    const std::vector<std::string>& engines() const { return engines_; }

    void add_engine(const std::string& name);
    // Unfinished results ("*") are ignored.
    void add_game(int number, const std::string& white, const std::string& black, const std::string& result,
                  const std::string& reason);
    // Counts one of fastchess's engine-output warnings against an engine it knows.
    void add_warning(const std::string& engine);

    MatchStats stats_of(const std::string& engine) const;
    MatchStats head_to_head(const std::string& engine, const std::string& opponent) const;
    int failures(const std::string& engine) const;
    int warnings(const std::string& engine) const;
    std::optional<EloEstimate> elo_of(const std::string& engine) const;

   private:
    struct OpenPair {
        std::string white, black;
        double white_score;
    };
    int games_per_encounter_;
    bool pentanomial_;
    std::vector<std::string> engines_;
    std::map<std::string, MatchStats> stats_;
    std::map<std::string, int> failures_, warnings_;
    std::map<std::pair<std::string, std::string>, MatchStats> head_to_head_;
    std::map<int, OpenPair> open_pairs_;

    void add(const std::string& engine, const std::string& opponent, const MatchStats& s);
};

}  // namespace fcd::stats
