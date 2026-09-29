// Deterministic stand-in for a UCI engine, used by the native and C# tests.
//
// The score (side to move's point of view) is 30 - 20 * number_of_moves_played, so evaluations
// alternate in a predictable way. After the move "d8h4" it reports a checkmated side to move,
// which exercises mate handling. Option names it receives are echoed as "info string" lines.
#include <cstdio>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

int main() {
    std::vector<std::string> moves;
    std::string line;
    while (std::getline(std::cin, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line == "uci") {
            std::printf("id name FakeEngine 1.0\nid author tests\n");
            std::printf("option name Hash type spin default 16 min 1 max 1024\nuciok\n");
        } else if (line == "isready") {
            std::printf("readyok\n");
        } else if (line.rfind("setoption ", 0) == 0) {
            std::printf("info string %s\n", line.c_str());
        } else if (line.rfind("position", 0) == 0) {
            moves.clear();
            std::istringstream in(line);
            std::string token;
            bool after_moves = false;
            while (in >> token) {
                if (after_moves) moves.push_back(token);
                else if (token == "moves") after_moves = true;
            }
        } else if (line.rfind("go", 0) == 0) {
            if (!moves.empty() && moves.back() == "d8h4") {
                std::printf("info depth 1 score mate 0\nbestmove (none)\n");
            } else {
                const int cp = 30 - 20 * static_cast<int>(moves.size());
                std::printf("info depth 1 multipv 1 score cp 9999 lowerbound\n");
                std::printf("info depth 5 seldepth 6 multipv 1 score cp %d nodes 100 pv e2e4 e7e5\n", cp);
                std::printf("info string not a score\n");
                std::printf("bestmove e2e4 ponder e7e5\n");
            }
        } else if (line == "quit") {
            break;
        }
        std::fflush(stdout);
    }
    return 0;
}
