// Deterministic stand-in for fastchess, used by the native and C# tests of the process layer and
// the tournament runner. It reads the real fastchess arguments (-engine cmd=... name=..., -rounds,
// -games, -tournament) and "plays" every game: the engine with the higher strength wins, equal
// strengths draw. An engine's cmd= value is its strength, or a behavior:
//   cmd=fail     exit with code 1 before playing
//   cmd=hang     print the first "Started game" line, then never finish (for cancellation)
//   cmd=orphan   play normally, but leave a child process running that holds the output pipes
//                open (as engines of a crashed fastchess would)
//   cmd=escape   first start a child outside the process tree (a new session on POSIX, a job
//                breakaway on Windows) that holds the output pipes open for 15 s; then play
//                normally. Stopping the tree does not reach it.
//   cmd=warn     strength 0, and a fastchess engine warning block (Warning;, Info;, Position;,
//                Moves;) about this engine in every game it plays
//                (and, once, a fastchess warning that names no engine)
// It also writes one line to standard error. "--sleep" runs the orphan child, "--linger" the
// escaped one.
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace {

void sleep_forever() {
    for (;;) std::this_thread::sleep_for(std::chrono::seconds(60));
}

void start_orphan(const char* self) {
#ifdef _WIN32
    (void)self;
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring command = L"\"" + std::wstring(path) + L"\" --sleep";
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    // Inherits this process's handles, including the output pipe.
    if (CreateProcessW(path, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
#else
    if (fork() == 0) {
        execl(self, self, "--sleep", static_cast<char*>(nullptr));
        _exit(0);
    }
#endif
}

// Our job does not allow breakaway, so on Windows this child normally stays in the job (and the
// tests then only see the ordinary path); on POSIX setsid() always takes it out of the group.
void start_escaped(const char* self) {
#ifdef _WIN32
    (void)self;
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring command = L"\"" + std::wstring(path) + L"\" --linger";
    STARTUPINFOW si{};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(path, command.data(), nullptr, nullptr, TRUE, CREATE_BREAKAWAY_FROM_JOB, nullptr, nullptr, &si,
                       &pi) ||
        CreateProcessW(path, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
#else
    if (fork() == 0) {
        setsid();
        execl(self, self, "--linger", static_cast<char*>(nullptr));
        _exit(0);
    }
#endif
}

}  // namespace

int main(int argc, char** argv) {
    std::vector<std::string> args(argv + 1, argv + argc);
    if (!args.empty() && args[0] == "--sleep") sleep_forever();
    if (!args.empty() && args[0] == "--linger") {
        std::this_thread::sleep_for(std::chrono::seconds(15));
        return 0;
    }
#ifdef _WIN32
    // Line endings exactly as written below (the test covers both "\n" and "\r\n").
    _setmode(_fileno(stdout), _O_BINARY);
#endif

    std::vector<std::pair<std::string, std::string>> engines;  // name, cmd
    int rounds = 1, games = 2;
    bool gauntlet = false;
    for (size_t i = 0; i < args.size(); ++i) {
        const std::string& a = args[i];
        if (a == "-engine") engines.emplace_back();
        else if (a.rfind("cmd=", 0) == 0 && !engines.empty()) engines.back().second = a.substr(4);
        else if (a.rfind("name=", 0) == 0 && !engines.empty()) engines.back().first = a.substr(5);
        else if (a == "-rounds" && i + 1 < args.size()) rounds = std::atoi(args[++i].c_str());
        else if (a == "-games" && i + 1 < args.size()) games = std::atoi(args[++i].c_str());
        else if (a == "-tournament" && i + 1 < args.size()) gauntlet = args[++i] == "gauntlet";
    }
    std::fprintf(stderr, "fake fastchess: %d engines\n", static_cast<int>(engines.size()));
    std::fflush(stderr);
    for (const auto& e : engines)
        if (e.second == "warn") {
            std::printf("Warning; Failed to set CPU affinity for the tournament thread.\n");
            break;
        }

    bool hang = false, orphan = false;
    for (const auto& e : engines) {
        if (e.second == "fail") return 1;
        hang = hang || e.second == "hang";
        orphan = orphan || e.second == "orphan";
        if (e.second == "escape") start_escaped(argv[0]);
    }

    std::vector<std::pair<size_t, size_t>> pairs;
    for (size_t i = 0; i < engines.size(); ++i)
        for (size_t j = i + 1; j < engines.size(); ++j)
            if (!gauntlet || i == 0) pairs.emplace_back(i, j);
    std::vector<std::pair<size_t, size_t>> schedule;
    for (int r = 0; r < rounds; ++r)
        for (const auto& [x, y] : pairs)
            for (int g = 0; g < games; ++g) schedule.push_back(g % 2 == 0 ? std::make_pair(x, y) : std::make_pair(y, x));

    auto strength = [&](size_t i) { return std::atoi(engines[i].second.c_str()); };
    const size_t total = schedule.size();
    for (size_t k = 0; k < total; ++k) {
        const auto& [w, b] = schedule[k];
        std::printf("Started game %d of %d (%s vs %s)\n", static_cast<int>(k + 1), static_cast<int>(total),
                    engines[w].first.c_str(), engines[b].first.c_str());
        std::fflush(stdout);
        if (hang) sleep_forever();
        for (const size_t side : {w, b}) {
            if (engines[side].second != "warn") continue;
            std::printf("Warning; Bestmove does not match beginning of last PV - move e7e5 from %s\n"
                        "Info; info depth 12 score cp 20 nodes 1000 time 5 pv c7c5 g1f3\n"
                        "Position; startpos\n"
                        "Moves; e2e4\n",
                        engines[side].first.c_str());
        }
        const char* result = strength(w) > strength(b) ? "1-0" : strength(w) < strength(b) ? "0-1" : "1/2-1/2";
        // A Windows-style line ending, as fastchess writes on Windows.
        std::printf("Finished game %d (%s vs %s): %s {test}\r\n", static_cast<int>(k + 1), engines[w].first.c_str(),
                    engines[b].first.c_str(), result);
        std::fflush(stdout);
    }
    if (orphan) start_orphan(argv[0]);
    std::printf("Tournament finished\n");
    std::fflush(stdout);
    return 0;
}
