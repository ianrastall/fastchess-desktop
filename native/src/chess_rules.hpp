// Chess rules and PGN parsing. This is the only module that includes the
// third-party chess library; everything else goes through this interface.
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <utility>
#include <vector>

#include "game.hpp"

namespace fcd {

// Raw game as read from PGN text, before the moves are validated.
struct PgnGame {
    std::vector<std::pair<std::string, std::string>> headers;
    std::vector<std::string> moves;     // SAN tokens as written
    std::vector<std::string> comments;  // comment following each move
};

// Streams games from a PGN file. The callback returns false to stop early.
// Throws Error on I/O failure.
void read_pgn_file(const std::string& utf8_path, const std::function<bool(PgnGame&)>& on_game);

// Validates and normalizes a raw PGN game into a Game. On failure returns
// false and sets error.
bool build_game(const PgnGame& raw, Game& out, std::string& error);

enum class Terminal { None, Checkmate, Stalemate, InsufficientMaterial, FiftyMoves, Repetition };

struct TerminalInfo {
    Terminal kind = Terminal::None;
    bool white_to_move = true;
};

// Replays UCI moves and reports whether the final position ends the game.
TerminalInfo terminal_state(const Game& game);

// Four-field EPD (placement, side, castling, legal en passant) after each ply
// of a UCI line from the standard start. Throws Error on an illegal move.
std::vector<std::string> epds_after_each_ply(const std::vector<std::string>& uci);

}  // namespace fcd
