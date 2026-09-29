#include "uci.hpp"

#include <charconv>
#include <chrono>

#include "util.hpp"

namespace fcd::uci {

namespace {

bool to_int(const std::string& s, int& out) {
    const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
    return r.ec == std::errc() && r.ptr == s.data() + s.size();
}

std::vector<std::string> tokens(const std::string& line) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i < line.size()) {
        while (i < line.size() && line[i] == ' ') ++i;
        const size_t start = i;
        while (i < line.size() && line[i] != ' ') ++i;
        if (i > start) out.push_back(line.substr(start, i - start));
    }
    return out;
}

}  // namespace

std::string Limit::go_command() const {
    std::string go = "go";
    if (depth > 0) go += " depth " + std::to_string(depth);
    if (movetime_ms > 0) go += " movetime " + std::to_string(movetime_ms);
    if (nodes > 0) go += " nodes " + std::to_string(nodes);
    return go == "go" ? "go depth 12" : go;
}

bool parse_info(const std::string& line, Score& score, int& depth, std::vector<std::string>& pv) {
    score = {};
    depth = 0;
    pv.clear();
    const auto t = tokens(line);
    bool has_score = false;
    for (size_t i = 1; i < t.size(); ++i) {
        const std::string& key = t[i];
        if (key == "depth" && i + 1 < t.size()) {
            to_int(t[++i], depth);
        } else if (key == "multipv" && i + 1 < t.size()) {
            if (t[++i] != "1") return false;
        } else if (key == "lowerbound" || key == "upperbound") {
            return false;
        } else if (key == "score" && i + 2 < t.size()) {
            const std::string kind = t[++i];
            int v = 0;
            if (!to_int(t[++i], v)) return false;
            score = kind == "mate" ? Score{std::nullopt, v} : Score{v, std::nullopt};
            has_score = kind == "cp" || kind == "mate";
        } else if (key == "pv") {
            pv.assign(t.begin() + static_cast<long>(i) + 1, t.end());
            break;
        } else if (key == "string") {
            break;
        }
    }
    return has_score;
}

std::unique_ptr<Engine> Engine::start(const std::string& path,
                                      const std::vector<std::pair<std::string, std::string>>& options,
                                      const process::Cancel* cancel) {
    std::unique_ptr<Engine> engine(new Engine());
    Engine* e = engine.get();
    process::Options o;
    o.program = path;
    o.pipe_stdin = true;
    e->child_ = process::Child::start(o, [e](process::Stream stream, const std::string& line) {
        if (stream != process::Stream::Stdout) return;  // engines' stderr is not part of the protocol
        {
            std::lock_guard lock(e->mutex_);
            e->lines_.push_back(trim(line));
        }
        e->ready_.notify_one();
    });

    e->send("uci");
    for (;;) {
        const std::string line = e->next_line(15000, cancel);
        if (line.rfind("id name ", 0) == 0) e->name_ = trim(line.substr(8));
        if (line == "uciok") break;
    }
    for (const auto& [name, value] : options) e->send("setoption name " + name + " value " + value);
    e->sync(cancel);
    return engine;
}

Engine::~Engine() {
    if (!child_) return;
    try {
        send("quit");
    } catch (...) {
        // The engine already went away.
    }
    if (!child_->wait(2000)) child_->kill_tree();
    child_.reset();  // waits for the output threads; they use this object
}

void Engine::new_game(const process::Cancel* cancel) {
    send("ucinewgame");
    sync(cancel);
}

SearchResult Engine::search(const std::string& start_fen, const std::vector<std::string>& moves, const Limit& limit,
                            const process::Cancel* cancel) {
    std::string position = start_fen.empty() ? "position startpos" : "position fen " + start_fen;
    if (!moves.empty()) position += " moves " + join(moves, ' ');
    send(position);
    send(limit.go_command());

    SearchResult result;
    try {
        for (;;) {
            const std::string line = next_line(-1, cancel);
            if (line.rfind("info ", 0) == 0) {
                Score s;
                int d = 0;
                std::vector<std::string> pv;
                if (parse_info(line, s, d, pv)) {
                    result.score = s;
                    result.depth = d;
                    if (!pv.empty()) result.pv = std::move(pv);
                }
            } else if (line.rfind("bestmove", 0) == 0) {
                const auto parts = tokens(line);
                result.best_move = parts.size() > 1 ? parts[1] : "";
                return result;
            }
        }
    } catch (const Error& e) {
        // Stop the search so the engine is usable (or at least quiet) afterwards.
        if (e.status() == FCD_ERR_CANCELLED) {
            try {
                send("stop");
            } catch (...) {
            }
        }
        throw;
    }
}

void Engine::send(const std::string& command) { child_->write_line(command); }

void Engine::sync(const process::Cancel* cancel) {
    send("isready");
    while (next_line(30000, cancel) != "readyok") {
    }
}

std::string Engine::next_line(int timeout_ms, const process::Cancel* cancel) {
    using Clock = std::chrono::steady_clock;
    const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms < 0 ? 0 : timeout_ms);
    std::unique_lock lock(mutex_);
    for (;;) {
        if (!lines_.empty()) {
            std::string line = std::move(lines_.front());
            lines_.pop_front();
            return line;
        }
        if (process::cancelled(cancel)) throw Error(FCD_ERR_CANCELLED, "cancelled");
        if (child_->output_finished()) throw Error(FCD_ERR_PROCESS, "Engine process exited unexpectedly.");
        if (timeout_ms >= 0 && Clock::now() >= deadline)
            throw Error(FCD_ERR_TIMEOUT,
                        "Engine did not respond within " + std::to_string(timeout_ms / 1000) + " s.");
        ready_.wait_for(lock, std::chrono::milliseconds(20));
    }
}

}  // namespace fcd::uci
