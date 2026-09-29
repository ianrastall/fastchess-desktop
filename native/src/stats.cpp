#include "stats.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>

#include "util.hpp"

namespace fcd::stats {

namespace {

constexpr double kCi95Z = 1.959963984540054;
const double kNEloScale = 800.0 / std::log(10.0);

double sq(double x) { return x * x; }

EloEstimate build(double score, double variance_per_unit, double nelo_sigma) {
    const double upper = score + kCi95Z * std::sqrt(variance_per_unit);
    const double lower = score - kCi95Z * std::sqrt(variance_per_unit);
    auto nelo = [&](double s) { return (s - 0.5) / nelo_sigma * kNEloScale; };
    const double los = (1 - std::erf(-(score - 0.5) / std::sqrt(2.0 * variance_per_unit))) / 2.0;
    return {score_to_elo(score), (score_to_elo(upper) - score_to_elo(lower)) / 2.0, nelo(score),
            (nelo(upper) - nelo(lower)) / 2.0, los * 100.0};
}

double regularize(int value) { return value == 0 ? 1e-3 : value; }

template <size_t N>
double mean(const std::array<double, N>& x, const std::array<double, N>& p) {
    double r = 0.0;
    for (size_t i = 0; i < N; ++i) r += x[i] * p[i];
    return r;
}

// I. F. D. Oliveira and R. H. C. Takahashi, "An Enhancement of the Bisection Method Average
// Performance Preserving Minmax Optimality", ACM TOMS 47(1), 2021. Same code as fastchess.
template <typename F>
double itp(F f, double a, double b, double f_a, double f_b, double k_1, double k_2, double n_0, double epsilon) {
    if (f_a > 0) {
        std::swap(a, b);
        std::swap(f_a, f_b);
    }
    const double n_half = std::ceil(std::log2(std::abs(b - a) / (2.0 * epsilon)));
    const double n_max = n_half + n_0;
    for (size_t i = 0; std::abs(b - a) > 2.0 * epsilon; i++) {
        const double x_half = (a + b) / 2.0;
        const double r = epsilon * std::pow(2.0, n_max - static_cast<double>(i)) - (b - a) / 2.0;
        const double delta = k_1 * std::pow(b - a, k_2);
        const double x_f = (f_b * a - f_a * b) / (f_b - f_a);
        const double sigma = (x_half - x_f) / std::abs(x_half - x_f);
        const double x_t = delta <= std::abs(x_half - x_f) ? x_f + sigma * delta : x_half;
        const double x_itp = std::abs(x_t - x_half) <= r ? x_t : x_half - sigma * r;
        const double f_itp = f(x_itp);
        if (std::fpclassify(f_itp) == FP_ZERO) {
            a = x_itp;
            b = x_itp;
        } else if (std::signbit(f_itp)) {
            a = x_itp;
            f_a = f_itp;
        } else {
            b = x_itp;
            f_b = f_itp;
        }
    }
    return (a + b) / 2.0;
}

// Van den Bergh, "Comparing the approximations for the generalized log likelihood ratio of a
// multinomial distribution", Proposition 1.1.
template <size_t N>
double llr_logistic(double total, const std::array<double, N>& scores, const std::array<double, N>& probs, double s0,
                    double s1) {
    const auto mle = [&](double s) {
        const double min_theta = -1.0 / (scores[N - 1] - s);
        const double max_theta = -1.0 / (scores[0] - s);
        const double theta = itp(
            [&](double x) {
                double r = 0.0;
                for (size_t i = 0; i < N; i++) r += probs[i] * (scores[i] - s) / (1.0 + x * (scores[i] - s));
                return r;
            },
            min_theta, max_theta, INFINITY, -INFINITY, 0.1, 2.0, 0.99, 1e-3);
        std::array<double, N> p;
        for (size_t i = 0; i < N; i++) p[i] = probs[i] / (1 + theta * (scores[i] - s));
        return p;
    };
    const auto p0 = mle(s0);
    const auto p1 = mle(s1);
    std::array<double, N> lpr;
    for (size_t i = 0; i < N; i++) lpr[i] = std::log(p1[i]) - std::log(p0[i]);
    return total * mean(lpr, probs);
}

// Van den Bergh, "Comments on normalized Elo", section 4.1.
template <size_t N>
double llr_normalized(double total, const std::array<double, N>& scores, const std::array<double, N>& probs, double t0,
                      double t1) {
    const auto mle = [&](double mu_ref, double t_star) {
        std::array<double, N> p;
        p.fill(1.0 / N);
        for (int iteration = 0; iteration < 10; iteration++) {
            const double mu = mean(scores, p);
            double var = 0.0;
            for (size_t i = 0; i < N; i++) var += p[i] * (scores[i] - mu) * (scores[i] - mu);
            const double sigma = std::sqrt(var);
            std::array<double, N> phi;
            for (size_t i = 0; i < N; i++) {
                const double z = (scores[i] - mu) / sigma;
                phi[i] = scores[i] - mu_ref - 0.5 * t_star * sigma * (1.0 + z * z);
            }
            const double u = *std::min_element(phi.begin(), phi.end());
            const double v = *std::max_element(phi.begin(), phi.end());
            const double theta = itp(
                [&](double x) {
                    double r = 0.0;
                    for (size_t i = 0; i < N; i++) r += probs[i] * phi[i] / (1.0 + x * phi[i]);
                    return r;
                },
                -1.0 / v, -1.0 / u, INFINITY, -INFINITY, 0.1, 2.0, 0.99, 1e-7);
            double max_diff = 0.0;
            for (size_t i = 0; i < N; i++) {
                const double next = probs[i] / (1.0 + theta * phi[i]);
                max_diff = std::max(max_diff, std::abs(next - p[i]));
                p[i] = next;
            }
            if (max_diff < 1e-4) break;
        }
        return p;
    };
    const auto p0 = mle(0.5, t0);
    const auto p1 = mle(0.5, t1);
    std::array<double, N> lpr;
    for (size_t i = 0; i < N; i++) lpr[i] = std::log(p1[i]) - std::log(p0[i]);
    return total * mean(lpr, probs);
}

double logistic_elo_to_score(double elo) { return 1 / (1 + std::pow(10, -elo / 400)); }

double bayes_elo_to_score(double bayes_elo, double draw_elo) {
    const double pwin = 1.0 / (1.0 + std::pow(10.0, (-bayes_elo + draw_elo) / 400.0));
    const double ploss = 1.0 / (1.0 + std::pow(10.0, (bayes_elo + draw_elo) / 400.0));
    return pwin + 0.5 * (1.0 - pwin - ploss);
}

}  // namespace

MatchStats& MatchStats::operator+=(const MatchStats& o) {
    wins += o.wins;
    draws += o.draws;
    losses += o.losses;
    ll += o.ll;
    ld += o.ld;
    wl += o.wl;
    dd += o.dd;
    wd += o.wd;
    ww += o.ww;
    return *this;
}

MatchStats from_abi(const fcd_match_stats& s) {
    return {s.wins, s.draws, s.losses, s.ll, s.ld, s.wl, s.dd, s.wd, s.ww};
}

fcd_match_stats to_abi(const MatchStats& s) {
    return {s.wins, s.draws, s.losses, s.ll, s.ld, s.wl, s.dd, s.wd, s.ww};
}

double score_to_elo(double score) { return -400.0 * std::log10(1.0 / score - 1.0); }

std::optional<EloEstimate> elo_from_games(const MatchStats& s) {
    if (s.games() == 0) return std::nullopt;
    const double n = s.games(), w = s.wins / n, d = s.draws / n, l = s.losses / n;
    const double score = w + 0.5 * d;
    const double variance = w * sq(1 - score) + d * sq(0.5 - score) + l * sq(0 - score);
    return build(score, variance / n, std::sqrt(variance));
}

std::optional<EloEstimate> elo_from_pairs(const MatchStats& s) {
    if (s.pairs() == 0) return std::nullopt;
    const double n = s.pairs();
    const double ww = s.ww / n, wd = s.wd / n, wldd = (s.wl + s.dd) / n, ld = s.ld / n, ll = s.ll / n;
    const double score = ww + 0.75 * wd + 0.5 * wldd + 0.25 * ld;
    const double variance =
        ww * sq(1 - score) + wd * sq(0.75 - score) + wldd * sq(0.5 - score) + ld * sq(0.25 - score) + ll * sq(0 - score);
    return build(score, variance / n, std::sqrt(2 * variance));
}

Sprt::Sprt(double alpha, double beta, double elo0, double elo1, SprtModel model)
    : elo0_(elo0),
      elo1_(elo1),
      lower_(std::log(beta / (1 - alpha))),
      upper_(std::log((1 - beta) / alpha)),
      model_(model) {}

SprtOutcome Sprt::outcome(double llr) const {
    if (llr >= upper_) return SprtOutcome::AcceptH1;
    if (llr <= lower_) return SprtOutcome::AcceptH0;
    return SprtOutcome::Continue;
}

double Sprt::llr(const MatchStats& s, bool pentanomial) const {
    return pentanomial ? llr_pairs(s) : llr_games(s.wins, s.draws, s.losses);
}

double Sprt::llr_games(int win, int draw, int loss) const {
    const double l = regularize(loss), d = regularize(draw), w = regularize(win);
    const double total = l + d + w;
    const std::array<double, 3> probs{l / total, d / total, w / total};
    const std::array<double, 3> scores{0.0, 0.5, 1.0};
    switch (model_) {
        case SprtModel::Normalized:
            return llr_normalized(total, scores, probs, elo0_ / kNEloScale, elo1_ / kNEloScale);
        case SprtModel::Bayesian: {
            if (win == 0 || loss == 0) return 0.0;
            const double draw_elo = 200 * std::log10((1 - probs[0]) / probs[0] * (1 - probs[2]) / probs[2]);
            return llr_logistic(total, scores, probs, bayes_elo_to_score(elo0_, draw_elo),
                                bayes_elo_to_score(elo1_, draw_elo));
        }
        default:
            return llr_logistic(total, scores, probs, logistic_elo_to_score(elo0_), logistic_elo_to_score(elo1_));
    }
}

double Sprt::llr_pairs(const MatchStats& s) const {
    const double ll = regularize(s.ll), ld = regularize(s.ld), wldd = regularize(s.dd + s.wl), wd = regularize(s.wd),
                 ww = regularize(s.ww);
    const double total = ww + wd + wldd + ld + ll;
    const std::array<double, 5> probs{ll / total, ld / total, wldd / total, wd / total, ww / total};
    const std::array<double, 5> scores{0.0, 0.25, 0.5, 0.75, 1.0};
    if (model_ == SprtModel::Normalized)
        return llr_normalized(total, scores, probs, std::sqrt(2.0) * elo0_ / kNEloScale,
                              std::sqrt(2.0) * elo1_ / kNEloScale);
    return llr_logistic(total, scores, probs, logistic_elo_to_score(elo0_), logistic_elo_to_score(elo1_));
}

bool is_engine_failure_reason(const std::string& reason) {
    // Game end reasons from fastchess match.hpp that mean an engine failed.
    for (const char* r : {" loses on time", " disconnects", "'s connection stalls", " makes an illegal move",
                          "Game interrupted"})
        if (reason.find(r) != std::string::npos) return true;
    return false;
}

Scoreboard::Scoreboard(int games_per_encounter, bool pentanomial)
    : games_per_encounter_(std::max(1, games_per_encounter)), pentanomial_(pentanomial && games_per_encounter == 2) {}

void Scoreboard::add_engine(const std::string& name) {
    if (stats_.count(name)) return;
    engines_.push_back(name);
    stats_[name] = {};
}

void Scoreboard::add(const std::string& engine, const std::string& opponent, const MatchStats& s) {
    stats_[engine] += s;
    stats_[opponent] += s.inverted();
    head_to_head_[{engine, opponent}] += s;
    head_to_head_[{opponent, engine}] += s.inverted();
}

void Scoreboard::add_game(int number, const std::string& white, const std::string& black, const std::string& result,
                          const std::string& reason) {
    double white_score;
    if (result == "1-0") white_score = 1;
    else if (result == "0-1") white_score = 0;
    else if (result == "1/2-1/2") white_score = 0.5;
    else return;

    add_engine(white);
    add_engine(black);
    MatchStats game;
    if (white_score == 1) game.wins = 1;
    else if (white_score == 0) game.losses = 1;
    else game.draws = 1;
    add(white, black, game);

    if (is_engine_failure_reason(reason)) {
        const std::string* culprit = reason.rfind("White", 0) == 0 ? &white : reason.rfind("Black", 0) == 0 ? &black : nullptr;
        if (culprit) ++failures_[*culprit];
    }

    if (!pentanomial_ || number < 1) return;
    const int pair = (number - 1) / games_per_encounter_;
    const auto open = open_pairs_.find(pair);
    if (open == open_pairs_.end()) {
        open_pairs_[pair] = {white, black, white_score};
        return;
    }
    const OpenPair first = open->second;
    open_pairs_.erase(open);
    if (first.white != black || first.black != white) return;  // not the color-swapped partner

    // Both games from the point of view of the engine that had White in the first game.
    const double sum = first.white_score + (1 - white_score);
    MatchStats p;
    if (sum == 2.0) p.ww = 1;
    else if (sum == 1.5) p.wd = 1;
    else if (sum == 1.0 && first.white_score == 0.5) p.dd = 1;
    else if (sum == 1.0) p.wl = 1;
    else if (sum == 0.5) p.ld = 1;
    else p.ll = 1;
    add(first.white, first.black, p);
}

void Scoreboard::add_warning(const std::string& engine) {
    if (stats_.count(engine)) ++warnings_[engine];
}

MatchStats Scoreboard::stats_of(const std::string& engine) const {
    const auto it = stats_.find(engine);
    return it == stats_.end() ? MatchStats{} : it->second;
}

MatchStats Scoreboard::head_to_head(const std::string& engine, const std::string& opponent) const {
    const auto it = head_to_head_.find({engine, opponent});
    return it == head_to_head_.end() ? MatchStats{} : it->second;
}

int Scoreboard::failures(const std::string& engine) const {
    const auto it = failures_.find(engine);
    return it == failures_.end() ? 0 : it->second;
}

int Scoreboard::warnings(const std::string& engine) const {
    const auto it = warnings_.find(engine);
    return it == warnings_.end() ? 0 : it->second;
}

std::optional<EloEstimate> Scoreboard::elo_of(const std::string& engine) const {
    return pentanomial_ ? elo_from_pairs(stats_of(engine)) : elo_from_games(stats_of(engine));
}

}  // namespace fcd::stats
