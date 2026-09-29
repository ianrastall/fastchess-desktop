// Minimal UCI client: one engine process, one search at a time.
#pragma once

#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "process.hpp"

namespace fcd::uci {

// Score from the side to move's point of view.
struct Score {
    std::optional<int> cp, mate;
};

struct SearchResult {
    Score score;
    int depth = 0;
    std::string best_move;
    std::vector<std::string> pv;
};

struct Limit {
    int depth = 0;
    int movetime_ms = 0;
    long long nodes = 0;
    std::string go_command() const;  // "go depth 12" when no limit is set
};

// Parses the score, depth and pv of an info line. False for lines without a final main-line
// score (bounds, multipv > 1, strings, currmove updates).
bool parse_info(const std::string& line, Score& score, int& depth, std::vector<std::string>& pv);

class Engine {
   public:
    // Starts the engine, completes uci/isready (timeouts 15 s and 30 s) and applies the options.
    static std::unique_ptr<Engine> start(const std::string& path,
                                         const std::vector<std::pair<std::string, std::string>>& options,
                                         const process::Cancel* cancel);
    ~Engine();  // sends quit, waits two seconds, then stops the process

    const std::string& name() const { return name_; }

    void new_game(const process::Cancel* cancel);

    // Searches the position after the given UCI moves from start_fen (empty for the start position).
    // Cancelling sends "stop" and throws Error(FCD_ERR_CANCELLED).
    SearchResult search(const std::string& start_fen, const std::vector<std::string>& moves, const Limit& limit,
                        const process::Cancel* cancel);

   private:
    Engine() = default;
    std::unique_ptr<process::Child> child_;
    std::string name_;
    std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<std::string> lines_;

    void send(const std::string& command);
    void sync(const process::Cancel* cancel);
    // Next output line; timeout_ms < 0 waits indefinitely. Throws FCD_ERR_TIMEOUT, FCD_ERR_CANCELLED
    // or FCD_ERR_PROCESS (the engine exited).
    std::string next_line(int timeout_ms, const process::Cancel* cancel);
};

}  // namespace fcd::uci
