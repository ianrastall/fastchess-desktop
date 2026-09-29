// Runs a tournament of any format. Round robin and gauntlet are a single fastchess run; pyramid,
// knockout and Swiss are a series of fastchess runs whose results decide the next pairings. All
// runs append to the same PGN file, and game numbers reported in events are continuous across runs.
#pragma once

#include <functional>
#include <string>
#include <vector>

#include "fastchess.hpp"
#include "process.hpp"

namespace fcd::tournament {

struct Outcome {
    bool cancelled = false;
    int exit_code = 0;
    long long duration_ms = 0;
    std::vector<std::string> ranking;  // empty for single-run formats and for failed or stopped runs
};

// Events as JSON objects:
//   {"type":"commandStarted","commandLine"}   a fastchess process is about to start
//   {"type":"stageStarted","stage","description"}
//   {"type":"gameStarted","number","total","white","black"}
//   {"type":"gameFinished","number","white","black","result","reason"}
//   {"type":"tournamentFinished","message"}
//   {"type":"note","message"}                 pairings, byes, tiebreaks, final ranking
//   {"type":"engineWarning","engine","message","count","file"}
//       fastchess flagged an engine's search output; count is the occurrences of this message for
//       this engine so far. The warning's lines are written to file (engine-warnings.log in the
//       working directory) and not passed to on_line.
using EventHandler = std::function<void(const std::string&)>;

Outcome run(const std::string& fastchess_path, const std::string& working_directory,
            const fastchess::TournamentSettings& settings, const process::LineHandler& on_line,
            const EventHandler& on_event, const process::Cancel* cancel);

std::string outcome_json(const Outcome& o);

}  // namespace fcd::tournament
