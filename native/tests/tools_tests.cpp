// Tests for command lines, tournament settings, fastchess output, statistics and pairings.
#include <cstring>
#include <string>

#include "check.hpp"

namespace {

using fcd_test::contains;
using fcd_test::near;
using fcd_test::take;

// Two engines, most options at their defaults (camelCase JSON as the C# side sends it).
const char* kTwoEngines = R"({
  "engines": [
    {"name": "Alpha", "command": "C:\\Engines\\alpha.exe", "options": [{"name": "Threads", "value": "2"}]},
    {"name": "", "command": "C:\\Engines\\Beta Engine.exe", "arguments": "--nnue big.nnue"}
  ],
  "type": "RoundRobin", "rounds": 5, "gamesPerEncounter": 2, "concurrency": 2,
  "limit": "TimeControl", "timeControl": "10+0.1", "threads": 1, "hashMb": 16,
  "openingsFile": "C:\\books\\UHO.epd", "openingsFormat": "Epd", "openingsOrder": "Random",
  "drawAdjudication": true, "pgnOut": "C:\\out\\games.pgn", "pgnAppend": true, "recover": true,
  "sprt": false, "sprtElo0": 0, "sprtElo1": 2.5, "sprtAlpha": 0.05, "sprtBeta": 0.05, "sprtModel": "Normalized",
  "event": "Test", "ratingInterval": 10, "reportPenta": true, "autosaveInterval": 20
})";

std::string replace(std::string text, const std::string& from, const std::string& to) {
    const auto at = text.find(from);
    if (at != std::string::npos) text.replace(at, from.size(), to);
    return text;
}

void test_command_lines() {
    char* out = nullptr;
    CHECK_OK(fcd_cmdline_quote("with space", &out));
    CHECK(take(out) == "\"with space\"");
    CHECK_OK(fcd_cmdline_quote("", &out));
    CHECK(take(out) == "\"\"");
    CHECK_OK(fcd_cmdline_quote("say \"hi\"", &out));
    CHECK(take(out) == "\"say \\\"hi\\\"\"");
    CHECK_OK(fcd_cmdline_quote("C:\\Program Files\\x\\", &out));
    CHECK(take(out) == "\"C:\\Program Files\\x\\\\\"");

    // Split undoes Format, including quotes, trailing backslashes and empty arguments.
    const char* args = R"(["-t","C:\\My Files\\tags.txt","say \"hi\"","trailing\\","","--plycount"])";
    CHECK_OK(fcd_cmdline_format(args, &out));
    const std::string line = take(out);
    CHECK_OK(fcd_cmdline_split(line.c_str(), &out));
    CHECK(take(out) == args);
    CHECK_OK(fcd_cmdline_split("   ", &out));
    CHECK(take(out) == "[]");
}

void test_fastchess_command() {
    char* out = nullptr;
    CHECK_OK(fcd_tournament_build_args(kTwoEngines, &out));
    const std::string args = take(out);
    CHECK(contains(args, R"("-engine","cmd=C:\\Engines\\alpha.exe","name=Alpha","option.Threads=2")"));
    // The name falls back to the executable stem, as in fastchess itself.
    CHECK(contains(args, R"("cmd=C:\\Engines\\Beta Engine.exe","name=Beta Engine","args=--nnue big.nnue")"));
    CHECK(contains(args, R"("-each","tc=10+0.1","option.Threads=1","option.Hash=16")"));
    CHECK(contains(args, R"("-tournament","roundrobin","-rounds","5","-games","2","-concurrency","2")"));
    CHECK(contains(args, R"("-openings","file=C:\\books\\UHO.epd","format=epd","order=random")"));
    CHECK(contains(args, R"("-draw","movenumber=40","movecount=8","score=10")"));
    CHECK(contains(args, R"("-pgnout","file=C:\\out\\games.pgn","notation=san","append=true")"));
    CHECK(contains(args, R"("-recover")"));
    CHECK(!contains(args, "-sprt"));

    const std::string st = replace(kTwoEngines, R"("limit": "TimeControl")", R"("limit": "FixedTimePerMove", "moveTimeSeconds": 0.5)");
    CHECK_OK(fcd_tournament_build_args(st.c_str(), &out));
    const std::string st_args = take(out);
    CHECK(contains(st_args, "\"st=0.5\""));
    CHECK(!contains(st_args, "\"tc="));

    const std::string sprt = replace(kTwoEngines, R"("sprt": false)", R"("sprt": true)");
    CHECK_OK(fcd_tournament_build_args(sprt.c_str(), &out));
    const std::string sprt_args = take(out);
    CHECK(contains(sprt_args, R"("-sprt","elo0=0","elo1=2.5","alpha=0.05","beta=0.05","model=normalized")"));

    int64_t games = 0;
    CHECK_OK(fcd_tournament_expected_games(kTwoEngines, &games));
    CHECK(games == 10);  // one pairing x 5 rounds x 2 games
    CHECK_OK(fcd_tournament_expected_games(R"({"engines":[]})", &games));
    CHECK(games == -1);

    CHECK_OK(fcd_engine_display_name("", "/opt/engines/stockfish-17.exe", &out));
    CHECK(take(out) == "stockfish-17");
    CHECK_OK(fcd_engine_display_name(" Named ", "x.exe", &out));
    CHECK(take(out) == "Named");

    // The staged formats run in several fastchess runs.
    const std::string swiss = replace(kTwoEngines, R"("type": "RoundRobin")", R"("type": "Swiss")");
    CHECK(fcd_tournament_build_args(swiss.c_str(), &out) == FCD_ERR_ARGUMENT);
    CHECK(contains(fcd_last_error(), "Swiss is run in stages"));
    CHECK_OK(fcd_tournament_first_stage_args(swiss.c_str(), &out));
    CHECK(contains(take(out), "\"roundrobin\""));
    CHECK(fcd_tournament_build_args("{not json", &out) == FCD_ERR_PARSE);
}

void test_validation() {
    char* out = nullptr;
    const char* bad = R"({"engines":[{"name":"A","command":"a.exe"},{"name":"A"},{"name":"C","command":"c.exe"}],
                         "sprt":true,"limit":"Nodes","nodes":0,"type":"Swiss","swissRounds":3})";
    CHECK_OK(fcd_tournament_validate(bad, &out));
    const std::string errors = take(out);
    CHECK(contains(errors, "Engine 2 has no executable."));
    CHECK(contains(errors, "Engine names must be unique"));
    CHECK(contains(errors, "Node limit must be positive."));
    CHECK(contains(errors, "SPRT requires exactly two engines."));
    CHECK(contains(errors, "SPRT is only available for round robin and gauntlet"));
    CHECK(contains(errors, "Swiss rounds must be at least 1 and fewer than the number of engines."));
    CHECK_OK(fcd_tournament_validate(kTwoEngines, &out));
    CHECK(take(out) == "[]");
}

void test_output_parser() {
    char* out = nullptr;
    CHECK_OK(fcd_fastchess_parse_line("Started game 3 of 40 (Alpha vs Beta Engine)", &out));
    CHECK(take(out) == R"({"kind":"normal","event":{"type":"gameStarted","number":3,"total":40,"white":"Alpha","black":"Beta Engine"},"warningEngine":null})");
    CHECK_OK(fcd_fastchess_parse_line("Finished game 3 (Alpha vs Beta Engine): 1/2-1/2 {Draw by 3-fold repetition}", &out));
    CHECK(contains(take(out), R"("type":"gameFinished","number":3,"white":"Alpha","black":"Beta Engine","result":"1/2-1/2","reason":"Draw by 3-fold repetition")"));
    CHECK_OK(fcd_fastchess_parse_line("Finished game 2 (A vs B): 0-1 {White loses on time (12ms overrun)}", &out));
    CHECK(contains(take(out), R"("kind":"engineFailure")"));
    CHECK_OK(fcd_fastchess_parse_line("SPRT ([0.00, 2.00]) completed - H1 was accepted", &out));
    CHECK(contains(take(out), R"("type":"tournamentFinished","message":"SPRT ([0.00, 2.00]) completed - H1 was accepted")"));
    CHECK_OK(fcd_fastchess_parse_line("Warning; Bestmove does not match beginning of last PV - move f7e6 from Berserk 14", &out));
    const std::string warning = take(out);
    CHECK(contains(warning, R"("kind":"warning")"));
    CHECK(contains(warning, R"("warningEngine":"Berserk 14")"));
    CHECK_OK(fcd_fastchess_parse_line("Moves; d1d2 c6d4 e3d4", &out));
    CHECK(contains(take(out), R"("kind":"warning","event":null,"warningEngine":null)"));
    CHECK_OK(fcd_fastchess_parse_line("  Timeouts: 3", &out));
    CHECK(contains(take(out), R"("kind":"engineFailure")"));
    CHECK_OK(fcd_fastchess_parse_line("  Crashed: 0", &out));
    CHECK(contains(take(out), R"("kind":"normal")"));
    CHECK_OK(fcd_fastchess_parse_line("Elo: 12.3 +/- 4.5, nElo: 20.1 +/- 7.7", &out));
    CHECK(take(out) == R"({"kind":"normal","event":null,"warningEngine":null})");
    CHECK(fcd_is_engine_failure_reason("Black's connection stalls") == 1);
    CHECK(fcd_is_engine_failure_reason("White mates") == 0);
}

// A real 20-game match (fastchess 1.8.2, -games 2, SPRT elo0=0 elo1=2 alpha=beta=0.05 normalized):
// game number and result in the order the games finished; B has White in odd games.
void test_statistics() {
    const struct {
        int number;
        const char* result;
    } match[] = {{3, "1/2-1/2"}, {1, "1/2-1/2"},  {2, "1/2-1/2"},  {4, "0-1"},     {6, "1-0"},     {5, "1/2-1/2"},
                 {7, "1/2-1/2"}, {9, "1/2-1/2"},  {11, "1/2-1/2"}, {8, "1-0"},     {10, "1-0"},    {13, "1/2-1/2"},
                 {12, "1/2-1/2"}, {16, "1/2-1/2"}, {14, "1/2-1/2"}, {15, "1/2-1/2"}, {19, "1/2-1/2"}, {18, "1-0"},
                 {17, "1/2-1/2"}, {20, "1-0"}};
    fcd_scoreboard* board = nullptr;
    CHECK_OK(fcd_scoreboard_new(2, 1, &board));
    CHECK(fcd_scoreboard_is_pentanomial(board) == 1);
    for (const auto& g : match)
        CHECK_OK(fcd_scoreboard_add_game(board, g.number, g.number % 2 ? "B" : "R", g.number % 2 ? "R" : "B", g.result,
                                         "test"));

    // fastchess: "Games: 20, Wins: 1, Losses: 5, Draws: 14" and "Ptnml(0-2): [0, 5, 4, 1, 0], WL/DD Ratio: 0.00"
    fcd_engine_totals b{};
    CHECK_OK(fcd_scoreboard_engine(board, "B", &b));
    CHECK(b.stats.wins == 1 && b.stats.draws == 14 && b.stats.losses == 5);
    CHECK(b.stats.ll == 0 && b.stats.ld == 5 && b.stats.wl == 0 && b.stats.dd == 4 && b.stats.wd == 1 && b.stats.ww == 0);
    fcd_match_stats h2h{};
    CHECK_OK(fcd_scoreboard_head_to_head(board, "R", "B", &h2h));
    CHECK(h2h.wins == 5 && h2h.losses == 1 && h2h.wd == 5 && h2h.ld == 1);

    // fastchess: "Elo: -70.44 +/- 75.72, nElo: -148.15 +/- 152.27" and "LOS: 2.83 %"
    fcd_elo elo{};
    CHECK_OK(fcd_elo_estimate(&b.stats, 1, &elo));
    CHECK(near(elo.elo, -70.44, 0.005));
    CHECK(near(elo.error, 75.72, 0.005));
    CHECK(near(elo.nelo, -148.15, 0.005));
    CHECK(near(elo.nelo_error, 152.27, 0.005));
    CHECK(near(elo.los, 2.83, 0.005));

    // fastchess: "LLR: -0.04 (-1.2%) (-2.94, 2.94) [0.00, 2.00]"
    const fcd_sprt_params params{0.05, 0.05, 0, 2, FCD_SPRT_NORMALIZED};
    fcd_sprt_state sprt{};
    CHECK_OK(fcd_sprt_evaluate(&params, &b.stats, 1, &sprt));
    CHECK(near(sprt.llr, -0.04, 0.005));
    CHECK(near(sprt.fraction * 100, -1.2, 0.05));
    CHECK(near(sprt.lower_bound, -2.94, 0.005) && near(sprt.upper_bound, 2.94, 0.005));
    CHECK(sprt.outcome == FCD_SPRT_CONTINUE);

    char* out = nullptr;
    CHECK_OK(fcd_scoreboard_engines(board, &out));
    CHECK(take(out) == R"(["B","R"])");  // in order of first appearance: game 3 (B has White) finished first
    fcd_scoreboard_free(board);

    // Clear results reach a decision under every model.
    const fcd_match_stats strong{600, 900, 300, 20, 150, 80, 300, 250, 100};
    const fcd_match_stats weak{300, 900, 600, 100, 250, 80, 300, 150, 20};
    for (int model = 0; model <= 2; ++model) {
        const fcd_sprt_params p{0.05, 0.05, 0, 5, model};
        for (int penta = 0; penta <= (model == FCD_SPRT_BAYESIAN ? 0 : 1); ++penta) {
            CHECK_OK(fcd_sprt_evaluate(&p, &strong, penta, &sprt));
            CHECK(sprt.outcome == FCD_SPRT_H1);
            CHECK_OK(fcd_sprt_evaluate(&p, &weak, penta, &sprt));
            CHECK(sprt.outcome == FCD_SPRT_H0);
        }
    }

    const fcd_match_stats even{10, 20, 10, 0, 0, 0, 0, 0, 0};
    CHECK_OK(fcd_elo_estimate(&even, 0, &elo));
    CHECK(near(elo.elo, 0, 1e-9) && near(elo.los, 50, 1e-9) && elo.error > 0);
    const fcd_match_stats none{};
    CHECK(fcd_elo_estimate(&none, 0, &elo) == FCD_ERR_NOT_FOUND);

    // Failures are counted against the side named in the reason; warnings only for known engines.
    CHECK_OK(fcd_scoreboard_new(2, 0, &board));
    CHECK_OK(fcd_scoreboard_add_game(board, 1, "A", "B", "1-0", "Black loses on time (12ms overrun)"));
    CHECK_OK(fcd_scoreboard_add_game(board, 2, "B", "A", "0-1", "White disconnects"));
    CHECK_OK(fcd_scoreboard_add_game(board, 3, "A", "B", "*", "Game interrupted"));
    CHECK_OK(fcd_scoreboard_add_warning(board, "A"));
    CHECK_OK(fcd_scoreboard_add_warning(board, "Nobody"));
    fcd_engine_totals a{}, bb{};
    CHECK_OK(fcd_scoreboard_engine(board, "A", &a));
    CHECK_OK(fcd_scoreboard_engine(board, "B", &bb));
    CHECK(bb.failures == 2 && a.failures == 0 && a.warnings == 1);
    CHECK(a.stats.wins == 2 && a.stats.losses == 0 && a.stats.draws == 0);  // the unfinished game is ignored
    fcd_scoreboard_free(board);
}

void test_pairings() {
    char* out = nullptr;
    CHECK_OK(fcd_pairing_bracket_order(8, &out));
    CHECK(take(out) == "[1,8,4,5,2,7,3,6]");
    CHECK(fcd_pairing_bracket_order(6, &out) == FCD_ERR_ARGUMENT);

    CHECK_OK(fcd_pairing_knockout_first_round(R"(["A","B","C","D","E"])", &out));
    CHECK(take(out) == R"([{"white":"A","black":null},{"white":"D","black":"E"},{"white":"B","black":null},{"white":"C","black":null}])");
    CHECK_OK(fcd_pairing_knockout_next_round(R"(["A","D","B","C"])", &out));
    CHECK(take(out) == R"([{"white":"A","black":"D"},{"white":"B","black":"C"}])");

    CHECK_OK(fcd_pairing_swiss_round(R"({"seeded":["A","B","C","D"]})", &out));
    CHECK(take(out) == R"({"pairs":[{"white":"A","black":"B"},{"white":"C","black":"D"}],"bye":null})");
    // Round two: winners meet, losers meet, no rematches; fewer whites plays White.
    CHECK_OK(fcd_pairing_swiss_round(R"({"seeded":["A","B","C","D"],"scores":{"A":2,"C":2,"B":0,"D":0},
        "opponents":{"A":["B"],"B":["A"],"C":["D"],"D":["C"]},"whiteCounts":{"A":1,"C":1}})", &out));
    CHECK(take(out) == R"({"pairs":[{"white":"A","black":"C"},{"white":"B","black":"D"}],"bye":null})");
    // The bye goes to the lowest-ranked player who has not had one.
    CHECK_OK(fcd_pairing_swiss_round(R"({"seeded":["A","B","C"],"hadBye":["C"]})", &out));
    CHECK(contains(take(out), R"("bye":"B")"));
    // A rematch only when nothing else is possible.
    CHECK_OK(fcd_pairing_swiss_round(R"({"seeded":["A","B"],"opponents":{"A":["B"],"B":["A"]}})", &out));
    CHECK(take(out) == R"({"pairs":[{"white":"A","black":"B"}],"bye":null})");
}

void test_rating_tools() {
    const char* settings = R"({"average":2300,"anchorPlayer":" Stockfish ","whiteAdvantageAuto":true,"drawRateAuto":true,
                              "simulations":200,"cpus":4,"decimals":1,"ordoprepFilter":"MinGames","minGames":0})";
    char* out = nullptr;
    CHECK_OK(fcd_ordoprep_args(settings, "in.pgn", "out.pgn", &out));
    CHECK(take(out) == R"(["-p","in.pgn","-o","out.pgn","-M","1"])");
    CHECK_OK(fcd_ordo_args(settings, "in.pgn", "r.txt", "r.csv", &out));
    CHECK(take(out) == R"(["-p","in.pgn","-o","r.txt","-c","r.csv","-a","2300","-A","Stockfish","-W","-D","-s","200","-n","4","-N","1"])");
    CHECK_OK(fcd_pgn_extract_args(0, "-t \"my tags.txt\"", "C:\\eco.pgn", "in.pgn", "out.pgn", &out));
    CHECK(take(out) == R"(["-eC:\\eco.pgn","-t","my tags.txt","--quiet","-o","out.pgn","in.pgn"])");
    CHECK(fcd_pgn_extract_args(0, "", "", "in.pgn", "out.pgn", &out) == FCD_ERR_ARGUMENT);
}

}  // namespace

void run_tools_tests() {
    test_command_lines();
    test_fastchess_command();
    test_validation();
    test_output_parser();
    test_statistics();
    test_pairings();
    test_rating_tools();
}
