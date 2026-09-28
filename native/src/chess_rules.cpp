#include "chess_rules.hpp"

#include <algorithm>
#include <fstream>
#include <streambuf>

#include "chess.hpp"
#include "util.hpp"

namespace fcd {

namespace {

bool is_result_token(std::string_view t) { return t == "1-0" || t == "0-1" || t == "1/2-1/2" || t == "*"; }

// Strips move-quality suffixes such as "!", "?!" that some PGN writers attach.
std::string strip_annotation(std::string_view san) {
    std::string s(san);
    while (!s.empty() && (s.back() == '!' || s.back() == '?')) s.pop_back();
    return s;
}

bool is_chess960_variant(const std::string& v) {
    return iequals(v, "chess960") || iequals(v, "fischerandom") || iequals(v, "fischer random") ||
           iequals(v, "chess 960") || iequals(v, "frc");
}

// Finds the legal move matching a UCI string, or NO_MOVE.
chess::Move legal_from_uci(const chess::Board& board, std::string_view uci) {
    chess::Movelist moves;
    chess::movegen::legalmoves(moves, board);
    for (const auto& m : moves)
        if (chess::uci::moveToUci(m, board.chess960()) == uci) return m;
    return chess::Move::NO_MOVE;
}

bool setup_board(chess::Board& board, const std::string& fen, bool chess960) {
    board.set960(chess960);
    return board.setFen(fen.empty() ? std::string(chess::constants::STARTPOS) : fen);
}

// Input buffer that drops carriage returns, so CRLF and malformed CR-runs
// (for example "\r\r\r\r\n" from repeated line-ending conversion) parse like LF.
class NoCarriageReturnBuf : public std::streambuf {
   public:
    explicit NoCarriageReturnBuf(std::istream& source) : source_(source) {}

   protected:
    int_type underflow() override {
        while (true) {
            source_.read(buffer_, sizeof buffer_);
            const auto got = source_.gcount();
            if (got <= 0) return traits_type::eof();
            char* end = std::remove(buffer_, buffer_ + got, '\r');
            if (end != buffer_) {
                setg(buffer_, buffer_, end);
                return traits_type::to_int_type(*gptr());
            }
        }
    }

   private:
    std::istream& source_;
    char buffer_[1 << 16];
};

class Collector : public chess::pgn::Visitor {
   public:
    explicit Collector(const std::function<bool(PgnGame&)>& on_game) : on_game_(on_game) {}

    bool stopped() const { return stopped_; }

    void startPgn() override { current_ = PgnGame{}; }

    void header(std::string_view key, std::string_view value) override {
        current_.headers.emplace_back(to_utf8_lenient(key), to_utf8_lenient(value));
    }

    void startMoves() override {}

    void move(std::string_view move, std::string_view comment) override {
        if (is_result_token(move)) return;
        current_.moves.emplace_back(move);
        current_.comments.emplace_back(to_utf8_lenient(trim(comment)));
    }

    void endPgn() override {
        if (stopped_) return;
        if (!on_game_(current_)) {
            stopped_ = true;
            skipPgn(true);
        }
    }

   private:
    const std::function<bool(PgnGame&)>& on_game_;
    PgnGame current_;
    bool stopped_ = false;
};

}  // namespace

void read_pgn_file(const std::string& path, const std::function<bool(PgnGame&)>& on_game) {
    std::ifstream file(utf8_path(path), std::ios::binary);
    if (!file) throw Error(FCD_ERR_IO, "cannot open PGN file: " + path);
    NoCarriageReturnBuf filtered(file);
    std::istream in(&filtered);
    Collector collector(on_game);
    chess::pgn::StreamParser parser(in);
    const auto err = parser.readGames(collector);
    if (err.hasError() && err.code() != chess::pgn::StreamParserError::NotEnoughData)
        throw Error(FCD_ERR_PARSE, "PGN parse error in " + path + ": " + err.message());
}

bool build_game(const PgnGame& raw, Game& g, std::string& error) {
    g = Game{};
    for (const auto& [name, value] : raw.headers) {
        if (name == "FEN") {
            g.start_fen = trim(value);
        } else if (name == "Variant") {
            g.chess960 = is_chess960_variant(value);
            g.extra_tags.emplace_back(name, value);
        } else if (is_derived_tag(name)) {
            continue;
        } else if (!assign_known_tag(g, name, value)) {
            g.extra_tags.emplace_back(name, value);
        }
    }
    if (g.start_fen == chess::constants::STARTPOS && !g.chess960) g.start_fen.clear();

    chess::Board board;
    if (!setup_board(board, g.start_fen, g.chess960)) {
        error = "invalid FEN: " + g.start_fen;
        return false;
    }

    g.san.reserve(raw.moves.size());
    g.uci.reserve(raw.moves.size());
    for (size_t i = 0; i < raw.moves.size(); ++i) {
        const std::string token = strip_annotation(raw.moves[i]);
        chess::Move move = chess::Move::NO_MOVE;
        try {
            move = chess::uci::parseSan(board, token);
        } catch (const std::exception&) {
            move = chess::Move::NO_MOVE;
        }
        if (move == chess::Move::NO_MOVE) {
            error = "illegal or unreadable move '" + raw.moves[i] + "' at ply " + std::to_string(i + 1);
            return false;
        }
        g.san.push_back(chess::uci::moveToSan(board, move));
        g.uci.push_back(chess::uci::moveToUci(move, board.chess960()));
        board.makeMove(move);
    }
    g.comments = raw.comments;
    g.comments.resize(g.san.size());
    g.final_fen = board.getFen();
    if (g.result.empty()) g.result = "*";
    return true;
}

TerminalInfo terminal_state(const Game& game) {
    chess::Board board;
    if (!setup_board(board, game.start_fen, game.chess960)) throw Error(FCD_ERR_PARSE, "invalid FEN");
    for (const auto& u : game.uci) {
        const auto m = legal_from_uci(board, u);
        if (m == chess::Move::NO_MOVE) throw Error(FCD_ERR_PARSE, "illegal stored move " + u);
        board.makeMove(m);
    }
    TerminalInfo info;
    info.white_to_move = board.sideToMove() == chess::Color::WHITE;
    switch (board.isGameOver().first) {
        case chess::GameResultReason::CHECKMATE: info.kind = Terminal::Checkmate; break;
        case chess::GameResultReason::STALEMATE: info.kind = Terminal::Stalemate; break;
        case chess::GameResultReason::INSUFFICIENT_MATERIAL: info.kind = Terminal::InsufficientMaterial; break;
        case chess::GameResultReason::FIFTY_MOVE_RULE: info.kind = Terminal::FiftyMoves; break;
        case chess::GameResultReason::THREEFOLD_REPETITION: info.kind = Terminal::Repetition; break;
        default: info.kind = Terminal::None;
    }
    return info;
}

std::vector<std::string> epds_after_each_ply(const std::vector<std::string>& uci) {
    chess::Board board;
    std::vector<std::string> out;
    out.reserve(uci.size());
    for (const auto& u : uci) {
        const auto m = legal_from_uci(board, u);
        if (m == chess::Move::NO_MOVE) throw Error(FCD_ERR_PARSE, "illegal move " + u);
        board.makeMove(m);
        out.push_back(board.getFen(false));
    }
    return out;
}

}  // namespace fcd
