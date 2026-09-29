#include "tournament.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <map>
#include <mutex>
#include <set>

#include "cmdline.hpp"
#include "formats.hpp"
#include "json.hpp"
#include "util.hpp"

namespace fcd::tournament {

namespace {

using fastchess::EngineSettings;
using fastchess::TournamentSettings;
using fastchess::TournamentType;
using Clock = std::chrono::steady_clock;

// Points are multiples of 0.5: "2", "1.5".
std::string fmt(double v) {
    if (v == std::floor(v)) return std::to_string(static_cast<long long>(v));
    char buf[32];
    std::snprintf(buf, sizeof buf, "%.1f", v);
    return buf;
}

std::string table(const std::vector<std::pair<std::string, std::string>>& rows) {
    std::string out;
    for (size_t i = 0; i < rows.size(); ++i) {
        std::string rank = std::to_string(i + 1);
        if (rank.size() < 3) rank.insert(0, 3 - rank.size(), ' ');
        if (i) out += '\n';
        out += rank + ". " + rows[i].first + "  " + rows[i].second;
    }
    while (!out.empty() && (out.back() == ' ' || out.back() == '\n')) out.pop_back();
    return out;
}

std::string event(const char* type, std::initializer_list<std::pair<const char*, std::string>> strings,
                  std::initializer_list<std::pair<const char*, long long>> numbers = {}) {
    std::string out = "{\"type\":\"";
    out += type;
    out += '"';
    for (const auto& [key, value] : numbers) out += ",\"" + std::string(key) + "\":" + std::to_string(value);
    for (const auto& [key, value] : strings) {
        out += ",\"" + std::string(key) + "\":";
        json_string(out, value);
    }
    return out + "}";
}

// A fastchess run that failed or was stopped ends the tournament.
struct StageFailed {
    process::RunResult result;
};

class Run {
   public:
    Run(std::string fastchess, std::string working_directory, const TournamentSettings& settings,
        const process::LineHandler& on_line, const EventHandler& on_event, const process::Cancel* cancel)
        : fastchess_(std::move(fastchess)),
          working_directory_(std::move(working_directory)),
          settings_(settings),
          on_line_(on_line),
          on_event_(on_event),
          cancel_(cancel),
          expected_(fastchess::expected_games(settings)) {
        for (const auto& e : settings.engines) {
            const auto name = fastchess::engine_name(e);
            seeded_.push_back(name);
            engines_.emplace(name, e);
        }
    }

    Outcome execute() {
        try {
            switch (settings_.type) {
                case TournamentType::Pyramid: return pyramid();
                case TournamentType::Knockout: return knockout();
                case TournamentType::Swiss: return swiss();
                default: return single();
            }
        } catch (const StageFailed& f) {
            return {f.result.cancelled, f.result.exit_code, elapsed(), {}};
        }
    }

   private:
    std::string fastchess_, working_directory_;
    const TournamentSettings& settings_;
    const process::LineHandler& on_line_;
    const EventHandler& on_event_;
    const process::Cancel* cancel_;
    const std::optional<long long> expected_;
    const Clock::time_point started_ = Clock::now();
    std::vector<std::string> seeded_;
    std::map<std::string, EngineSettings> engines_;
    int stage_ = 0;
    int games_before_ = 0;

    long long elapsed() const {
        return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started_).count();
    }

    void emit(const std::string& json) {
        if (on_event_) on_event_(json);
    }

    void note(const std::string& message) { emit(event("note", {{"message", message}})); }

    Outcome done(std::vector<std::string> ranking) { return {false, 0, elapsed(), std::move(ranking)}; }

    int seed_of(const std::string& name) const {
        return static_cast<int>(std::find(seeded_.begin(), seeded_.end(), name) - seeded_.begin());
    }

    process::Options options(const TournamentSettings& s) const {
        process::Options o;
        o.program = fastchess_;
        o.args = fastchess::build(s);
        o.working_directory = working_directory_;
        return o;
    }

    void command_started(const process::Options& o) {
        std::vector<std::string> command{o.program};
        command.insert(command.end(), o.args.begin(), o.args.end());
        emit(event("commandStarted", {{"commandLine", cmdline::format(command)}}));
    }

    Outcome single() {
        const auto o = options(settings_);
        command_started(o);
        const auto result = process::run(
            o,
            [this](process::Stream stream, const std::string& line) {
                if (on_line_) on_line_(stream, line);
                const auto p = fastchess::parse_line(line);
                if (p.started)
                    emit(event("gameStarted", {{"white", p.started->white}, {"black", p.started->black}},
                               {{"number", p.started->number}, {"total", p.started->total}}));
                else if (p.finished) emit(finished_event(*p.finished, p.finished->number));
                else if (p.tournament_finished) emit(event("tournamentFinished", {{"message", *p.tournament_finished}}));
            },
            cancel_);
        return {result.cancelled, result.exit_code, elapsed(), {}};
    }

    static std::string finished_event(const fastchess::GameFinished& f, int number) {
        return event("gameFinished",
                     {{"white", f.white}, {"black", f.black}, {"result", f.result}, {"reason", f.reason}},
                     {{"number", number}});
    }

    // Runs one fastchess process and returns the points each engine scored in it.
    std::map<std::string, double> stage(const std::string& description, const TournamentSettings& stage_settings) {
        ++stage_;
        emit(event("stageStarted", {{"description", description}}, {{"stage", stage_}}));
        const auto o = options(stage_settings);
        command_started(o);

        std::map<std::string, double> points;
        int finished = 0;
        const auto result = process::run(
            o,
            [&](process::Stream stream, const std::string& line) {
                if (on_line_) on_line_(stream, line);
                const auto p = fastchess::parse_line(line);
                if (p.started) {
                    const long long total = expected_ ? *expected_ : games_before_ + p.started->total;
                    emit(event("gameStarted", {{"white", p.started->white}, {"black", p.started->black}},
                               {{"number", games_before_ + p.started->number}, {"total", total}}));
                } else if (p.finished) {
                    ++finished;
                    const auto& f = *p.finished;
                    const double w = f.result == "1-0" ? 1 : f.result == "1/2-1/2" ? 0.5 : 0;
                    const double b = f.result == "0-1" ? 1 : f.result == "1/2-1/2" ? 0.5 : 0;
                    points[f.white] += w;
                    points[f.black] += b;
                    emit(finished_event(f, games_before_ + f.number));
                }
                // A single run's "Tournament finished" is not the end of a multi-stage tournament.
            },
            cancel_);
        games_before_ += finished;
        if (result.cancelled || result.exit_code != 0) throw StageFailed{result};
        return points;
    }

    TournamentSettings match(const std::string& a, const std::string& b, int rounds) {
        return fastchess::stage_settings(settings_, {engines_.at(a), engines_.at(b)}, TournamentType::RoundRobin, rounds,
                                         stage_ + 1);
    }

    Outcome pyramid() {
        std::map<std::string, double> totals;
        for (const auto& n : seeded_) totals[n] = 0;
        for (size_t k = 1; k < seeded_.size(); ++k) {
            const std::string& newcomer = seeded_[k];
            std::vector<EngineSettings> field{engines_.at(newcomer)};
            std::vector<std::string> earlier(seeded_.begin(), seeded_.begin() + static_cast<long>(k));
            for (const auto& n : earlier) field.push_back(engines_.at(n));
            std::string names;
            for (size_t i = 0; i < earlier.size(); ++i) names += (i ? ", " : "") + earlier[i];
            const auto points =
                stage("Pyramid stage " + std::to_string(k) + ": " + newcomer + " joins and plays " + names,
                      fastchess::stage_settings(settings_, field, TournamentType::Gauntlet, settings_.rounds, stage_ + 1));
            for (const auto& [name, p] : points) totals[name] += p;
        }
        auto ranking = seeded_;
        std::stable_sort(ranking.begin(), ranking.end(),
                         [&](const std::string& a, const std::string& b) { return totals[a] > totals[b]; });
        std::vector<std::pair<std::string, std::string>> rows;
        for (const auto& n : ranking) rows.emplace_back(n, fmt(totals[n]) + " points");
        note("Pyramid finished. Final ranking:\n" + table(rows));
        emit(event("tournamentFinished", {{"message", "Pyramid finished: " + ranking[0] + " first"}}));
        return done(ranking);
    }

    std::string knockout_match(int round, const std::string& a, const std::string& b) {
        const std::string label = "Knockout round " + std::to_string(round) + ": " + a + " vs " + b;
        auto points = stage(label, match(a, b, settings_.rounds));
        double pa = points[a], pb = points[b];
        for (int t = 1; pa == pb && t <= settings_.knockout_tiebreak_pairs; ++t) {
            points = stage(label + ", tiebreak " + std::to_string(t), match(a, b, 1));
            pa += points[a];
            pb += points[b];
        }
        const std::string& higher_seed = seed_of(a) < seed_of(b) ? a : b;
        const std::string winner = pa > pb ? a : pb > pa ? b : higher_seed;
        note(pa == pb ? a + " " + fmt(pa) + " - " + fmt(pb) + " " + b + ": still tied after tiebreaks; " + winner +
                            " advances as the higher seed."
                      : a + " " + fmt(pa) + " - " + fmt(pb) + " " + b + ": " + winner + " advances.");
        return winner;
    }

    Outcome knockout() {
        auto round = formats::knockout_first_round(seeded_);
        std::vector<std::string> eliminated;  // in order of elimination
        for (int r = 1;; ++r) {
            std::vector<std::string> winners;
            for (const auto& pair : round) {
                if (!pair.black) {
                    note("Knockout round " + std::to_string(r) + ": " + pair.white + " has a bye.");
                    winners.push_back(pair.white);
                    continue;
                }
                const auto winner = knockout_match(r, pair.white, *pair.black);
                winners.push_back(winner);
                eliminated.push_back(winner == pair.white ? *pair.black : pair.white);
            }
            if (winners.size() == 1) {
                std::vector<std::string> ranking{winners[0]};
                ranking.insert(ranking.end(), eliminated.rbegin(), eliminated.rend());
                std::vector<std::pair<std::string, std::string>> rows;
                for (const auto& n : ranking) rows.emplace_back(n, "");
                note("Knockout finished. Winner: " + winners[0] + ". Order of elimination (last out first):\n" +
                     table(rows));
                emit(event("tournamentFinished", {{"message", "Knockout finished: " + winners[0] + " wins"}}));
                return done(ranking);
            }
            round = formats::knockout_next_round(winners);
        }
    }

    Outcome swiss() {
        formats::SwissState state;
        state.seeded = seeded_;
        for (const auto& n : seeded_) {
            state.scores[n] = 0;
            state.white_counts[n] = 0;
            state.opponents[n];
        }
        const double bye_points = static_cast<double>(settings_.rounds) * settings_.games_per_encounter;

        for (int r = 1; r <= settings_.swiss_rounds; ++r) {
            const auto round = formats::swiss_round(state);
            std::string pairings;
            for (size_t i = 0; i < round.pairs.size(); ++i)
                pairings += (i ? "; " : "") + round.pairs[i].white + " - " + round.pairs[i].black.value_or("");
            if (round.bye) pairings += "; bye: " + *round.bye + " (+" + fmt(bye_points) + ")";
            note("Swiss round " + std::to_string(r) + " pairings: " + pairings);
            if (round.bye) {
                state.had_bye.insert(*round.bye);
                state.scores[*round.bye] += bye_points;
            }
            for (const auto& pair : round.pairs) {
                const std::string& white = pair.white;
                const std::string& black = *pair.black;
                auto points = stage("Swiss round " + std::to_string(r) + ": " + white + " vs " + black,
                                    match(white, black, settings_.rounds));
                state.scores[white] += points[white];
                state.scores[black] += points[black];
                state.opponents[white].insert(black);
                state.opponents[black].insert(white);
                ++state.white_counts[white];
            }
        }

        auto buchholz = [&](const std::string& n) {
            double sum = 0;
            for (const auto& o : state.opponents[n]) sum += state.scores[o];
            return sum;
        };
        auto ranking = seeded_;
        std::stable_sort(ranking.begin(), ranking.end(), [&](const std::string& a, const std::string& b) {
            if (state.scores[a] != state.scores[b]) return state.scores[a] > state.scores[b];
            return buchholz(a) > buchholz(b);
        });
        std::vector<std::pair<std::string, std::string>> rows;
        for (const auto& n : ranking) rows.emplace_back(n, fmt(state.scores[n]) + "  (" + fmt(buchholz(n)) + ")");
        note("Swiss finished. Final ranking (score, Buchholz):\n" + table(rows));
        emit(event("tournamentFinished", {{"message", "Swiss finished: " + ranking[0] + " first"}}));
        return done(ranking);
    }
};

}  // namespace

Outcome run(const std::string& fastchess_path, const std::string& working_directory, const TournamentSettings& settings,
            const process::LineHandler& on_line, const EventHandler& on_event, const process::Cancel* cancel) {
    return Run(fastchess_path, working_directory, settings, on_line, on_event, cancel).execute();
}

std::string outcome_json(const Outcome& o) {
    return std::string("{\"cancelled\":") + (o.cancelled ? "true" : "false") +
           ",\"exitCode\":" + std::to_string(o.exit_code) + ",\"durationMs\":" + std::to_string(o.duration_ms) +
           ",\"ranking\":" + json::string_array(o.ranking) + "}";
}

}  // namespace fcd::tournament
