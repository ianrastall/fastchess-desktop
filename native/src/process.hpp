// Child processes with redirected output: used to run fastchess, Ordo, Ordoprep, pgn-extract
// and UCI engines. Windows: CreateProcessW with an explicit inherited-handle list, and a job
// object so the whole process tree can be stopped. POSIX: fork/exec in a new process group.
#pragma once

#include <atomic>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace fcd::process {

// Set from any thread to stop a running operation.
class Cancel {
   public:
    void request() { requested_.store(true); }
    bool requested() const { return requested_.load(); }

   private:
    std::atomic<bool> requested_{false};
};

inline bool cancelled(const Cancel* c) { return c && c->requested(); }

enum class Stream { Stdout = 0, Stderr = 1 };

// Called with each complete output line (without the line ending), one call at a time.
using LineHandler = std::function<void(Stream, const std::string&)>;

struct Options {
    std::string program;             // UTF-8 path of the executable
    std::vector<std::string> args;   // passed individually; quoting is handled here
    std::string working_directory;   // empty: the program's directory
    bool pipe_stdin = false;         // otherwise stdin is the null device
};

class Child {
   public:
    // Throws Error(FCD_ERR_NOT_FOUND) when the program does not exist, FCD_ERR_IO when it cannot start.
    static std::unique_ptr<Child> start(const Options& options, LineHandler on_line);
    virtual ~Child() = default;

    // Writes a line to stdin (pipe_stdin only). Throws Error(FCD_ERR_PROCESS) when the pipe is closed.
    virtual void write_line(const std::string& line) = 0;
    // Waits up to timeout_ms (negative: forever) for the process to exit. True when it has exited.
    virtual bool wait(int timeout_ms) = 0;
    virtual int exit_code() const = 0;  // after wait() returned true
    // Stops the process and everything it started.
    virtual void kill_tree() = 0;
    // True once both output pipes have reached end of file and every line was delivered.
    virtual bool output_finished() const = 0;
    // Waits for the output to finish; after timeout_ms stops the remaining process tree (for example
    // engines left behind by a crashed fastchess, which keep the pipes open) and waits again. If a
    // process outside the tree still holds the pipes after that, reading stops (see output_abandoned),
    // so this always returns.
    virtual void finish_output(int timeout_ms) = 0;
    // True when finish_output stopped reading output that was still open.
    virtual bool output_abandoned() const = 0;
};

struct RunResult {
    int exit_code = 0;
    bool cancelled = false;
    long long duration_ms = 0;
};

// Runs a program to completion. Requesting cancel stops the whole process tree. If the output
// stays open after the tree was stopped, a line saying so is passed to on_line on Stderr.
RunResult run(const Options& options, const LineHandler& on_line, const Cancel* cancel);

// Splits a byte stream into lines at "\n", "\r\n" or a lone "\r", like .NET's ReadLine.
class LineSplitter {
   public:
    explicit LineSplitter(std::function<void(const std::string&)> emit) : emit_(std::move(emit)) {}
    void feed(const char* data, size_t size);
    void finish();  // emits a final unterminated line

   private:
    std::function<void(const std::string&)> emit_;
    std::string line_;
    bool pending_cr_ = false;
};

}  // namespace fcd::process
