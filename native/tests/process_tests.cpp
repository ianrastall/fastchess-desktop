// Tests for processes, UCI engines, analysis and the tournament runner. They run the fake
// programs built with the tests (fake_fastchess, fake_uci_engine).
#include <atomic>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "check.hpp"

#ifndef FCD_FAKE_FASTCHESS
#error FCD_FAKE_FASTCHESS must name the fake_fastchess executable
#endif
#ifndef FCD_FAKE_UCI
#error FCD_FAKE_UCI must name the fake_uci_engine executable
#endif

namespace {

using fcd_test::contains;
using fcd_test::take;

struct Collected {
    std::mutex mutex;
    std::vector<std::pair<int, std::string>> lines;
    std::vector<std::string> events;
    fcd_cancel* cancel_on_first_line = nullptr;
};

void on_line(void* user, int32_t stream, const char* line) {
    auto* c = static_cast<Collected*>(user);
    std::lock_guard lock(c->mutex);
    c->lines.emplace_back(stream, line);
    if (c->cancel_on_first_line) fcd_cancel_request(c->cancel_on_first_line);
}

void on_event(void* user, const char* json) {
    auto* c = static_cast<Collected*>(user);
    std::lock_guard lock(c->mutex);
    c->events.emplace_back(json);
}

size_t count_containing(const std::vector<std::string>& items, const std::string& needle) {
    size_t n = 0;
    for (const auto& s : items) n += contains(s, needle) ? 1 : 0;
    return n;
}

std::filesystem::path work_dir() {
    auto dir = std::filesystem::temp_directory_path() / "fcd_process_tests";
    std::filesystem::create_directories(dir);
    return dir;
}

void test_process_run() {
    Collected c;
    fcd_process_result r{};
    const char* args = R"(["-engine","cmd=2","name=Alpha One","-engine","cmd=1","name=B","-rounds","1","-games","2"])";
    CHECK_OK(fcd_process_run(FCD_FAKE_FASTCHESS, args, nullptr, on_line, &c, nullptr, &r));
    CHECK(r.exit_code == 0 && r.cancelled == 0);
    std::vector<std::string> out, err;
    for (const auto& [stream, text] : c.lines) (stream == 0 ? out : err).push_back(text);
    // "\n" and "\r\n" both end a line, with no empty lines in between.
    CHECK(out.size() == 5);
    CHECK(out.size() == 5 && out[0] == "Started game 1 of 2 (Alpha One vs B)");
    CHECK(out.size() == 5 && out[1] == "Finished game 1 (Alpha One vs B): 1-0 {test}");
    CHECK(out.size() == 5 && out[4] == "Tournament finished");
    CHECK(err.size() == 1 && err[0] == "fake fastchess: 2 engines");

    const char* failing = R"(["-engine","cmd=fail","name=A","-engine","cmd=1","name=B"])";
    CHECK_OK(fcd_process_run(FCD_FAKE_FASTCHESS, failing, nullptr, nullptr, nullptr, nullptr, &r));
    CHECK(r.exit_code == 1);

    CHECK(fcd_process_run("/no/such/program.exe", "[]", nullptr, nullptr, nullptr, nullptr, &r) == FCD_ERR_NOT_FOUND);
    CHECK(contains(fcd_last_error(), "Program not found"));
}

void test_process_cancel_and_orphans() {
    fcd_cancel* cancel = nullptr;
    CHECK_OK(fcd_cancel_new(&cancel));
    Collected c;
    c.cancel_on_first_line = cancel;
    fcd_process_result r{};
    const auto started = std::chrono::steady_clock::now();
    const char* hang = R"(["-engine","cmd=hang","name=A","-engine","cmd=1","name=B"])";
    CHECK_OK(fcd_process_run(FCD_FAKE_FASTCHESS, hang, nullptr, on_line, &c, cancel, &r));
    CHECK(r.cancelled == 1);
    CHECK(std::chrono::steady_clock::now() - started < std::chrono::seconds(20));
    fcd_cancel_free(cancel);

    // The fake leaves a child running that holds the output pipes open. The run still ends, because
    // the remaining process tree is stopped after a grace period.
    const auto orphan_started = std::chrono::steady_clock::now();
    const char* orphan = R"(["-engine","cmd=orphan","name=A","-engine","cmd=1","name=B","-games","1"])";
    CHECK_OK(fcd_process_run(FCD_FAKE_FASTCHESS, orphan, nullptr, nullptr, nullptr, nullptr, &r));
    CHECK(r.exit_code == 0 && r.cancelled == 0);
    CHECK(std::chrono::steady_clock::now() - orphan_started < std::chrono::seconds(30));
}

void test_uci_engine() {
    char* out = nullptr;
    CHECK_OK(fcd_uci_parse_info("info depth 20 seldepth 30 multipv 1 score cp -35 nodes 1 nps 2 pv e2e4 e7e5", &out));
    CHECK(take(out) == R"({"cp":-35,"mate":null,"depth":20,"pv":["e2e4","e7e5"]})");
    CHECK_OK(fcd_uci_parse_info("info depth 9 score mate -3 pv h7h8q", &out));
    CHECK(contains(take(out), R"("cp":null,"mate":-3)"));
    for (const char* ignored : {"info depth 5 score cp 20 lowerbound pv e2e4", "info depth 5 multipv 2 score cp 20 pv e2e4",
                                "info string NNUE evaluation using nn.nnue", "info depth 5 currmove e2e4 currmovenumber 1"}) {
        CHECK_OK(fcd_uci_parse_info(ignored, &out));
        CHECK(take(out) == "null");
    }

    fcd_uci* engine = nullptr;
    CHECK_OK(fcd_uci_start(FCD_FAKE_UCI, R"({"Hash":"32"})", nullptr, &engine));
    CHECK(engine != nullptr);
    if (!engine) return;
    CHECK_OK(fcd_uci_name(engine, &out));
    CHECK(take(out) == "FakeEngine 1.0");

    const fcd_uci_limit limit{5, 0, 0};
    CHECK_OK(fcd_uci_search(engine, nullptr, "", &limit, nullptr, &out));
    CHECK(take(out) == R"({"cp":30,"mate":null,"depth":5,"pv":["e2e4","e7e5"],"bestMove":"e2e4"})");

    // Every ply of 1. f3 e5 2. g4 Qh4#, from White's point of view.
    int progress_calls = 0;
    auto progress = [](void* user, int64_t, int64_t) -> int {
        ++*static_cast<int*>(user);
        return 0;
    };
    const fcd_analysis_settings settings{5, 0};
    CHECK_OK(fcd_uci_analyze_game(engine, nullptr, "f2f3 e7e5 g2g4 d8h4", &settings, progress, &progress_calls, nullptr, &out));
    const std::string analysis = take(out);
    CHECK(progress_calls == 5);
    CHECK(contains(analysis, R"("format":1,"engine":"FakeEngine 1.0","depth":5,"moveTimeMs":0,"analyzedAt":")"));
    // Side-to-move scores are 30, 10, -10, -30; White's view flips the sign on Black's turns.
    CHECK(contains(analysis, R"({"ply":0,"cp":30,"depth":5,"bestMove":"e2e4"},{"ply":1,"cp":-10,"depth":5,"bestMove":"e2e4"})"));
    CHECK(contains(analysis, R"({"ply":2,"cp":-10,"depth":5,"bestMove":"e2e4"},{"ply":3,"cp":30,"depth":5,"bestMove":"e2e4"})"));
    // The final position: White to move and checkmated.
    CHECK(contains(analysis, R"({"ply":4,"cp":-1000,"mate":0,"depth":1})"));
    // White moves 30 -> -10 (loses 40) and -10 -> 30 (gains, counts 0): 20. Black moves -10 -> -10
    // and 30 -> -1000 (mates, from White's view a drop, which is Black's gain): 0.
    CHECK(contains(analysis, R"("whiteAcpl":20,"blackAcpl":0)"));

    auto stop = [](void*, int64_t ply, int64_t) -> int { return ply >= 2 ? 1 : 0; };
    CHECK(fcd_uci_analyze_game(engine, nullptr, "f2f3 e7e5 g2g4", &settings, stop, nullptr, nullptr, &out) ==
          FCD_ERR_CANCELLED);
    fcd_uci_close(engine);

    CHECK(fcd_uci_start("/no/such/engine.exe", nullptr, nullptr, &engine) == FCD_ERR_NOT_FOUND);
    // A program that is not a UCI engine exits without answering.
    CHECK(fcd_uci_start(FCD_FAKE_FASTCHESS, nullptr, nullptr, &engine) == FCD_ERR_PROCESS);
}

std::string engines_json(std::initializer_list<std::pair<const char*, const char*>> engines) {
    std::string out = "[";
    for (const auto& [name, strength] : engines) {
        if (out.size() > 1) out += ',';
        out += std::string("{\"name\":\"") + name + "\",\"command\":\"" + strength + "\"}";
    }
    return out + "]";
}

std::string settings(const char* type, const std::string& engines) {
    return std::string("{\"type\":\"") + type + "\",\"engines\":" + engines +
           ",\"rounds\":1,\"gamesPerEncounter\":2,\"pgnOut\":\"games.pgn\",\"pgnAppend\":false,\"swissRounds\":3}";
}

// Runs a tournament with the fake fastchess. Engine commands are strengths (see fake_fastchess.cpp).
std::string run_tournament(const std::string& settings_json, Collected& c) {
    char* out = nullptr;
    const auto dir = work_dir().string();
    // The fake is the "fastchess"; its engines' cmd= values are their strengths.
    CHECK_OK(fcd_tournament_run(FCD_FAKE_FASTCHESS, dir.c_str(), settings_json.c_str(), on_line, on_event, &c, nullptr,
                                &out));
    return take(out);
}

void test_tournaments() {
    {
        Collected c;
        const auto outcome = run_tournament(settings("Knockout", engines_json({{"A", "5"}, {"B", "4"}, {"C", "3"}, {"D", "2"}, {"E", "1"}})), c);
        CHECK(contains(outcome, R"("cancelled":false,"exitCode":0)"));
        CHECK(contains(outcome, R"("ranking":["A","B","C","D","E"])"));
        CHECK(count_containing(c.events, "\"commandStarted\"") == 4);  // D-E, then A-D and B-C, then the final
        // Game numbers continue across the runs: 1..8.
        std::vector<std::string> finished;
        for (const auto& e : c.events)
            if (contains(e, "\"gameFinished\"")) finished.push_back(e);
        CHECK(finished.size() == 8);
        CHECK(finished.size() == 8 && contains(finished[7], "\"number\":8"));
        CHECK(count_containing(c.events, R"("message":"Knockout finished: A wins")") == 1);
        // Only the first run may truncate the PGN; later runs append.
        std::vector<std::string> commands;
        for (const auto& e : c.events)
            if (contains(e, "\"commandStarted\"")) commands.push_back(e);
        CHECK(!commands.empty() && contains(commands[0], "append=false"));
        CHECK(commands.size() == 4 && contains(commands[1], "append=true") && contains(commands[3], "append=true"));
    }
    {
        Collected c;
        const auto outcome = run_tournament(settings("Knockout", engines_json({{"A", "1"}, {"B", "1"}})), c);
        CHECK(count_containing(c.events, "\"commandStarted\"") == 3);  // the match and two tiebreaks
        CHECK(contains(outcome, R"("ranking":["A","B"])"));
        CHECK(count_containing(c.events, "higher seed") == 1);
    }
    {
        Collected c;
        const auto outcome = run_tournament(settings("Swiss", engines_json({{"A", "4"}, {"B", "3"}, {"C", "2"}, {"D", "1"}})), c);
        CHECK(count_containing(c.events, "\"commandStarted\"") == 6);  // 3 rounds x 2 pairings
        CHECK(contains(outcome, R"("ranking":["A","B","C","D"])"));
        CHECK(count_containing(c.events, "Swiss round 1 pairings: A - B; C - D") == 1);
    }
    {
        Collected c;
        const auto outcome = run_tournament(settings("Pyramid", engines_json({{"A", "1"}, {"B", "3"}, {"C", "2"}})), c);
        std::vector<std::string> commands;
        for (const auto& e : c.events)
            if (contains(e, "\"commandStarted\"")) commands.push_back(e);
        CHECK(commands.size() == 2);
        CHECK(commands.size() == 2 && commands[0].find("name=B") < commands[0].find("name=A"));
        CHECK(commands.size() == 2 && commands[1].find("name=C") < commands[1].find("name=A") &&
              commands[1].find("name=A") < commands[1].find("name=B"));
        CHECK(commands.size() == 2 && contains(commands[0], "gauntlet") && contains(commands[1], "gauntlet"));
        CHECK(contains(outcome, R"("ranking":["B","C","A"])"));
        CHECK(count_containing(c.events, "\"stageStarted\"") == 2);
    }
    {
        Collected c;
        const auto outcome = run_tournament(settings("Swiss", engines_json({{"A", "fail"}, {"B", "1"}, {"C", "3"}, {"D", "0"}})), c);
        CHECK(contains(outcome, R"("exitCode":1)"));
        CHECK(contains(outcome, R"("ranking":[])"));
        CHECK(count_containing(c.events, "\"commandStarted\"") == 1);
    }
    {
        Collected c;
        const auto outcome = run_tournament(settings("RoundRobin", engines_json({{"A", "1"}, {"B", "2"}, {"C", "3"}})), c);
        CHECK(count_containing(c.events, "\"commandStarted\"") == 1);
        CHECK(count_containing(c.events, "\"gameFinished\"") == 6);
        CHECK(count_containing(c.events, R"("message":"Tournament finished")") == 1);
        CHECK(contains(outcome, R"("ranking":[])"));
        CHECK(c.lines.size() >= 13);  // every output line reaches on_line as well
    }
    {
        // Engine warning blocks go to engine-warnings.log and engineWarning events, not to on_line.
        const auto log = work_dir() / "engine-warnings.log";
        std::filesystem::remove(log);
        Collected c;
        run_tournament(settings("RoundRobin", engines_json({{"Peace Keeper", "warn"}, {"B", "1"}})), c);
        std::vector<std::string> warnings;
        for (const auto& e : c.events)
            if (contains(e, "\"engineWarning\"")) warnings.push_back(e);
        CHECK(warnings.size() == 2);
        CHECK(warnings.size() == 2 && contains(warnings[0], R"("count":1)") && contains(warnings[1], R"("count":2)"));
        CHECK(warnings.size() == 2 && contains(warnings[0], R"("engine":"Peace Keeper")") &&
              contains(warnings[0], R"("message":"Bestmove does not match beginning of last PV")") &&
              contains(warnings[0], "engine-warnings.log"));
        std::vector<std::string> texts;
        for (const auto& [stream, text] : c.lines) texts.push_back(text);
        CHECK(count_containing(texts, "Bestmove") == 0);
        CHECK(count_containing(texts, "Info;") == 0 && count_containing(texts, "Position;") == 0 &&
              count_containing(texts, "Moves;") == 0);
        CHECK(count_containing(texts, "Failed to set CPU affinity") == 1);  // names no engine: stays
        CHECK(count_containing(texts, "Finished game") == 2);
        std::ifstream in(log, std::ios::binary);
        std::vector<std::string> logged;
        for (std::string l; std::getline(in, l);) logged.push_back(l);
        CHECK(count_containing(logged, "Warning; Bestmove") == 2);
        CHECK(count_containing(logged, "Moves; e2e4") == 2);
        CHECK(count_containing(logged, "affinity") == 0);
    }
}

}  // namespace

void run_process_tests() {
    test_process_run();
    test_process_cancel_and_orphans();
    test_uci_engine();
    test_tournaments();
}

// Entry point used by the Windows (Wine) test driver.
void run_process_tests_if_present() { run_process_tests(); }
