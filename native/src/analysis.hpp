// Game analysis with a UCI engine: an evaluation for every position of a game, from White's
// point of view, and the average centipawn loss per side. The result is the analysis document
// stored per game (fcd_db_set_analysis), format version 1.
#pragma once

#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "uci.hpp"

namespace fcd::analysis {

// Evaluation after a ply (ply 0 is the start position), from White's point of view. mate is moves
// to mate (positive: White mates); mate 0 marks a checkmated position, and cp then holds +/-1000.
struct PlyEvaluation {
    int ply = 0;
    std::optional<int> cp, mate;
    int depth = 0;
    std::optional<std::string> best_move;
};

struct Settings {
    int depth = 16;
    int movetime_ms = 0;
};

// Evaluations are capped at +/-1000 cp; a forced mate counts as the cap.
constexpr int kCap = 1000;

std::optional<int> capped_cp(const PlyEvaluation& e);

// Average centipawn loss of the side that moved into each position.
std::pair<std::optional<double>, std::optional<double>> average_loss(const std::vector<PlyEvaluation>& evals,
                                                                     bool white_moves_first);

// Progress: (ply, total plies); return false to cancel.
using Progress = std::function<bool(int, int)>;

// Analyzes the game and returns the analysis document as JSON:
// {"format":1,"engine","depth","moveTimeMs","analyzedAt","evals":[{"ply","cp","mate","depth","bestMove"}],
//  "whiteAcpl","blackAcpl"} (absent values are omitted).
std::string analyze(uci::Engine& engine, const std::string& start_fen, const std::vector<std::string>& moves,
                    const Settings& settings, const Progress& progress, const process::Cancel* cancel);

}  // namespace fcd::analysis
