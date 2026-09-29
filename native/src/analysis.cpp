#include "analysis.hpp"

#include <algorithm>
#include <chrono>
#include <ctime>

#include "json.hpp"
#include "util.hpp"

namespace fcd::analysis {

namespace {

bool black_to_move(const std::string& fen) {
    const auto parts = split(trim(fen), ' ');
    return parts.size() > 1 && parts[1] == "b";
}

std::string utc_now_iso8601() {
    const std::time_t now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
#ifdef _WIN32
    gmtime_s(&tm, &now);
#else
    gmtime_r(&now, &tm);
#endif
    char buf[32];
    std::strftime(buf, sizeof buf, "%Y-%m-%dT%H:%M:%SZ", &tm);
    return buf;
}

}  // namespace

std::optional<int> capped_cp(const PlyEvaluation& e) {
    if (e.mate && *e.mate != 0) return *e.mate > 0 ? kCap : -kCap;
    if (e.cp) return std::clamp(*e.cp, -kCap, kCap);
    return std::nullopt;
}

std::pair<std::optional<double>, std::optional<double>> average_loss(const std::vector<PlyEvaluation>& evals,
                                                                     bool white_moves_first) {
    double white_sum = 0, black_sum = 0;
    int white_moves = 0, black_moves = 0;
    for (size_t i = 1; i < evals.size(); ++i) {
        const auto before = capped_cp(evals[i - 1]);
        const auto after = capped_cp(evals[i]);
        if (!before || !after) continue;
        const bool mover_is_white = ((i - 1) % 2 == 0) == white_moves_first;
        const int loss = mover_is_white ? *before - *after : *after - *before;
        if (mover_is_white) {
            white_sum += std::max(0, loss);
            ++white_moves;
        } else {
            black_sum += std::max(0, loss);
            ++black_moves;
        }
    }
    return {white_moves > 0 ? std::optional<double>(white_sum / white_moves) : std::nullopt,
            black_moves > 0 ? std::optional<double>(black_sum / black_moves) : std::nullopt};
}

std::string analyze(uci::Engine& engine, const std::string& start_fen, const std::vector<std::string>& moves,
                    const Settings& settings, const Progress& progress, const process::Cancel* cancel) {
    engine.new_game(cancel);
    const uci::Limit limit{settings.depth, settings.movetime_ms, 0};
    const bool white_first = start_fen.empty() || !black_to_move(start_fen);
    const int total = static_cast<int>(moves.size());
    std::vector<PlyEvaluation> evals;
    evals.reserve(moves.size() + 1);

    for (int ply = 0; ply <= total; ++ply) {
        if (process::cancelled(cancel) || (progress && !progress(ply, total)))
            throw Error(FCD_ERR_CANCELLED, "cancelled");
        const bool white_to_move = (ply % 2 == 0) == white_first;
        const auto r = engine.search(start_fen, std::vector<std::string>(moves.begin(), moves.begin() + ply), limit, cancel);
        uci::Score score = r.score;
        if (!white_to_move) {
            if (score.cp) score.cp = -*score.cp;
            if (score.mate) score.mate = -*score.mate;
        }
        PlyEvaluation e;
        e.ply = ply;
        e.mate = score.mate;
        // "mate 0" means the side to move is checkmated; its sign is lost by negation, so the loser
        // is recorded through a capped centipawn value instead.
        e.cp = score.mate && *score.mate == 0 ? std::optional<int>(white_to_move ? -kCap : kCap) : score.cp;
        e.depth = r.depth;
        // A terminal position yields "bestmove (none)".
        if (!r.best_move.empty() && r.best_move != "(none)") e.best_move = r.best_move;
        evals.push_back(std::move(e));
    }

    const auto [white, black] = average_loss(evals, white_first);
    std::string out = "{\"format\":1,\"engine\":";
    json_string(out, engine.name());
    out += ",\"depth\":" + std::to_string(settings.depth) + ",\"moveTimeMs\":" + std::to_string(settings.movetime_ms) +
           ",\"analyzedAt\":\"" + utc_now_iso8601() + "\",\"evals\":[";
    for (size_t i = 0; i < evals.size(); ++i) {
        const auto& e = evals[i];
        if (i) out += ',';
        out += "{\"ply\":" + std::to_string(e.ply);
        if (e.cp) out += ",\"cp\":" + std::to_string(*e.cp);
        if (e.mate) out += ",\"mate\":" + std::to_string(*e.mate);
        out += ",\"depth\":" + std::to_string(e.depth);
        if (e.best_move) {
            out += ",\"bestMove\":";
            json_string(out, *e.best_move);
        }
        out += '}';
    }
    out += ']';
    if (white) out += ",\"whiteAcpl\":" + json::number(*white);
    if (black) out += ",\"blackAcpl\":" + json::number(*black);
    return out + "}";
}

}  // namespace fcd::analysis
