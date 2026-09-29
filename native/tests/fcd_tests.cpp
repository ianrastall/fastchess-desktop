// Tests for the fcd_core C ABI: database, openings, exports. Uses only the public header.
// Other groups: tools_tests.cpp (command lines, statistics, pairings), process_tests.cpp
// (processes, UCI engines, analysis, tournaments).
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <sstream>
#include <string>
#include <vector>

#include "fcd/fcd.h"

#include "check.hpp"

namespace {

using fcd_test::contains;
using fcd_test::take;

std::string read_file(const std::filesystem::path& p) {
    std::ifstream in(p, std::ios::binary);
    std::stringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

std::filesystem::path temp_dir() {
    auto dir = std::filesystem::temp_directory_path() / "fcd_tests";
    std::filesystem::remove_all(dir);
    std::filesystem::create_directories(dir);
    return dir;
}

void test_import_and_query(const std::filesystem::path& dir) {
    fcd_db* db = nullptr;
    const std::string db_path = (dir / "games.fcdb").string();
    CHECK_OK(fcd_db_open(db_path.c_str(), &db));
    CHECK(db != nullptr);

    const std::string pgn = std::string(FCD_TEST_DATA_DIR) + "/sample.pgn";
    fcd_import_result r{};
    int progress_calls = 0;
    auto progress = [](void* user, int64_t, int64_t) -> int {
        ++*static_cast<int*>(user);
        return 0;
    };
    CHECK_OK(fcd_db_import_pgn(db, pgn.c_str(), 1, progress, &progress_calls, &r));
    CHECK(r.imported == 4);
    CHECK(r.duplicates == 1);
    CHECK(r.failed == 1);
    CHECK(progress_calls >= 1);

    int64_t count = 0;
    CHECK_OK(fcd_db_count(db, nullptr, &count));
    CHECK(count == 4);

    fcd_query q{};
    q.search = "ren";
    CHECK_OK(fcd_db_count(db, &q, &count));
    CHECK(count == 1);

    char* json = nullptr;
    CHECK_OK(fcd_db_query(db, &q, &json));
    const std::string rows = take(json);
    CHECK(contains(rows, "\"white\":\"Ren\xc3\xa9\""));  // Latin-1 input converted to UTF-8

    fcd_query sorted{};
    sorted.order_by = "white";
    sorted.descending = 1;
    sorted.limit = 2;
    CHECK_OK(fcd_db_query(db, &sorted, &json));
    const std::string page = take(json);
    CHECK(page.find("\"white\":\"Ren") < page.find("\"white\":\"Gamma"));

    sorted.order_by = "white; DROP TABLE games";
    CHECK(fcd_db_query(db, &sorted, &json) == FCD_ERR_ARGUMENT);

    CHECK_OK(fcd_db_get_game(db, 1, &json));
    const std::string game = take(json);
    CHECK(contains(game, "\"uciMoves\":[\"e2e4\",\"e7e5\",\"f1c4\",\"b8c6\",\"d1h5\",\"g8f6\",\"h5f7\"]"));
    CHECK(contains(game, "\"sanMoves\":[\"e4\",\"e5\",\"Bc4\",\"Nc6\",\"Qh5\",\"Nf6\",\"Qxf7#\"]"));
    CHECK(contains(game, "+0.30/12 0.10s"));
    CHECK(contains(game, "\"GameDuration\":\"00:00:05\""));
    CHECK(!contains(game, "PlyCount\":\"7"));  // derived, not stored as a tag
    CHECK(contains(game, "\"plyCount\":7"));

    CHECK(fcd_db_get_game(db, 999, &json) == FCD_ERR_NOT_FOUND);

    fcd_db_close(db);
}

void test_editing(const std::filesystem::path& dir) {
    fcd_db* db = nullptr;
    CHECK_OK(fcd_db_open((dir / "games.fcdb").string().c_str(), &db));
    CHECK_OK(fcd_db_set_tag(db, 1, "WhiteElo", "2750"));
    CHECK_OK(fcd_db_set_tag(db, 1, "Annotator", "Tester"));
    CHECK(fcd_db_set_tag(db, 1, "WhiteElo", "strong") == FCD_ERR_ARGUMENT);
    CHECK(fcd_db_set_tag(db, 1, "Result", "2-0") == FCD_ERR_ARGUMENT);
    CHECK(fcd_db_set_tag(db, 1, "PlyCount", "3") == FCD_ERR_ARGUMENT);

    char* json = nullptr;
    CHECK_OK(fcd_db_get_game(db, 1, &json));
    std::string game = take(json);
    CHECK(contains(game, "\"whiteElo\":2750"));
    CHECK(contains(game, "\"Annotator\":\"Tester\""));

    CHECK_OK(fcd_db_set_tag(db, 1, "Annotator", nullptr));
    CHECK_OK(fcd_db_get_game(db, 1, &json));
    game = take(json);
    CHECK(!contains(game, "Annotator"));

    CHECK_OK(fcd_db_set_analysis(db, 1, "{\"engine\":\"Stockfish\",\"evals\":[30,-25,null]}"));
    CHECK(fcd_db_set_analysis(db, 1, "{not json") == FCD_ERR_PARSE);
    CHECK_OK(fcd_db_get_game(db, 1, &json));
    game = take(json);
    CHECK(contains(game, "\"analysis\":{\"engine\":\"Stockfish\""));
    CHECK(contains(game, "\"hasAnalysis\":true"));
    fcd_db_close(db);
}

void test_openings_and_fill(const std::filesystem::path& dir) {
    fcd_openings* book = nullptr;
    CHECK_OK(fcd_openings_load_tsv(FCD_TEST_OPENINGS_TSV, &book));
    CHECK(fcd_openings_count(book) == 3815);

    char* json = nullptr;
    CHECK_OK(fcd_openings_classify_uci(book, "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4 f3d4 g8f6 b1c3 a7a6", &json));
    const std::string najdorf = take(json);
    CHECK(contains(najdorf, "\"eco\":\"B90\""));
    CHECK(contains(najdorf, "\"opening\":\"Sicilian Defense\""));
    CHECK(contains(najdorf, "\"variation\":\"Najdorf Variation\""));
    CHECK(contains(najdorf, "\"ply\":10"));

    // Transposition: reached by a different move order than the book line.
    CHECK_OK(fcd_openings_classify_uci(book, "g1f3 d7d5 d2d4", &json));
    CHECK(contains(take(json), "\"name\":\"Queen's Pawn Game: Zukertort Variation\""));

    // En passant distinguishes these two otherwise identical positions (see parsing guide).
    CHECK_OK(fcd_openings_classify_uci(book, "b1c3 e7e5 f2f4 e5f4 e2e4", &json));
    CHECK(contains(take(json), "Nowokunski Gambit"));
    CHECK_OK(fcd_openings_classify_uci(book, "e2e4 e7e5 f2f4 e5f4 b1c3", &json));
    CHECK(contains(take(json), "Mason-Keres Gambit"));

    CHECK_OK(fcd_openings_classify_uci(book, "", &json));
    CHECK(take(json) == "null");
    CHECK(fcd_openings_classify_uci(book, "e2e5", &json) == FCD_ERR_PARSE);

    const std::string eco_path = (dir / "eco.pgn").string();
    CHECK_OK(fcd_openings_write_eco_pgn(book, eco_path.c_str()));
    const std::string eco = read_file(eco_path);
    CHECK(contains(eco, "[ECO \"A00\"]\n[Opening \"Amar Opening\"]\n[Variation \"Paris Gambit\"]\n"
                        "[SubVariation \"Gent Gambit\"]\n\n1. Nh3 d5 2. g3 e5 3. f4 Bxh3"));

    fcd_db* db = nullptr;
    CHECK_OK(fcd_db_open((dir / "games.fcdb").string().c_str(), &db));
    int64_t updated = 0;
    CHECK_OK(fcd_db_fill_missing(db, nullptr, book, FCD_FILL_OPENING | FCD_FILL_RESULT, nullptr, nullptr, &updated));
    CHECK(updated >= 3);
    CHECK_OK(fcd_db_get_game(db, 2, &json));
    const std::string fools = take(json);
    CHECK(contains(fools, "\"result\":\"0-1\""));
    CHECK(contains(fools, "\"termination\":\"checkmate\""));
    CHECK_OK(fcd_db_get_game(db, 1, &json));
    const std::string scholars = take(json);
    CHECK(contains(scholars, "\"eco\":\"C23\""));
    CHECK(contains(scholars, "\"opening\":\"Bishop's Opening\""));
    CHECK(fcd_db_fill_missing(db, nullptr, nullptr, FCD_FILL_OPENING, nullptr, nullptr, &updated) ==
          FCD_ERR_ARGUMENT);
    fcd_db_close(db);
    fcd_openings_free(book);
}

void test_ratings(const std::filesystem::path& dir) {
    const auto csv = dir / "ratings.csv";
    {
        std::ofstream out(csv, std::ios::binary);
        out << "\"#\",\"PLAYER\",\"RATING\",\"ERROR\",\"POINTS\",\"PLAYED\",\"(%)\"\n"
            << "1,\"Alpha\",2612.4,35.1,3.5,4,87.5\n"
            << "2,\"Beta\",2400.0,-,1.0,2,50.0\n";
    }
    fcd_db* db = nullptr;
    CHECK_OK(fcd_db_open((dir / "games.fcdb").string().c_str(), &db));
    int64_t players = 0, written = 0;
    CHECK_OK(fcd_db_apply_ordo_csv(db, csv.string().c_str(), 1, 0, &players, &written));
    CHECK(players == 2);
    CHECK(written >= 3);  // game 1 White keeps its manual 2750 (no overwrite)

    char* json = nullptr;
    CHECK_OK(fcd_db_get_ratings(db, &json));
    const std::string ratings = take(json);
    CHECK(contains(ratings, "{\"player\":\"Alpha\",\"rating\":2612.4,\"error\":35.1"));
    CHECK(contains(ratings, "\"player\":\"Beta\",\"rating\":2400.0,\"error\":null"));

    CHECK_OK(fcd_db_get_game(db, 1, &json));
    const std::string g1 = take(json);
    CHECK(contains(g1, "\"whiteElo\":2750"));
    CHECK(contains(g1, "\"blackElo\":2400"));
    fcd_db_close(db);
}

void test_exports(const std::filesystem::path& dir) {
    fcd_db* db = nullptr;
    CHECK_OK(fcd_db_open((dir / "games.fcdb").string().c_str(), &db));
    int64_t n = 0;

    const auto pgn = (dir / "out.pgn").string();
    CHECK_OK(fcd_db_export(db, nullptr, FCD_EXPORT_PGN, pgn.c_str(), &n));
    CHECK(n == 4);
    const std::string pgn_text = read_file(pgn);
    CHECK(contains(pgn_text, "[White \"Alpha\"]\n[Black \"Beta\"]\n[Result \"1-0\"]\n[WhiteElo \"2750\"]"));
    // PGN export format: a Black move directly after a comment repeats its move number.
    CHECK(contains(pgn_text, "1. e4 {+0.30/12 0.10s} 1... e5 {-0.25/11 0.09s} 2. Bc4 Nc6"));
    CHECK(contains(pgn_text, "[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/4P3/4K3 b - - 0 40\"]"));
    CHECK(contains(pgn_text, "40... Kd7 41. Kd2 Kd6 42. Kd3 1/2-1/2"));

    // Round trip: the exported PGN imports cleanly into a new database.
    fcd_db* db2 = nullptr;
    CHECK_OK(fcd_db_open((dir / "roundtrip.fcdb").string().c_str(), &db2));
    fcd_import_result r{};
    CHECK_OK(fcd_db_import_pgn(db2, pgn.c_str(), 1, nullptr, nullptr, &r));
    CHECK(r.imported == 4 && r.failed == 0);
    fcd_db_close(db2);

    const auto json_path = (dir / "out.json").string();
    CHECK_OK(fcd_db_export(db, nullptr, FCD_EXPORT_JSON, json_path.c_str(), &n));
    const std::string json_text = read_file(json_path);
    CHECK(contains(json_text, "\"standings\":[{\"rank\":1,\"player\":\"Alpha\""));
    CHECK(contains(json_text, "\"moves\":[{\"san\":\"e4\",\"uci\":\"e2e4\",\"comment\":\"+0.30/12 0.10s\"}"));

    const auto xml_path = (dir / "out.xml").string();
    CHECK_OK(fcd_db_export(db, nullptr, FCD_EXPORT_XML, xml_path.c_str(), &n));
    const std::string xml = read_file(xml_path);
    CHECK(contains(xml, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"));
    CHECK(contains(xml, "<tag name=\"White\">Ren\xc3\xa9</tag>"));
    CHECK(contains(xml, "<move ply=\"1\" san=\"e4\" uci=\"e2e4\">+0.30/12 0.10s</move>"));

    const auto txt_path = (dir / "crosstable.txt").string();
    CHECK_OK(fcd_db_export(db, nullptr, FCD_EXPORT_CROSSTABLE_TXT, txt_path.c_str(), &n));
    const std::string txt = read_file(txt_path);
    CHECK(contains(txt, "Crosstable: 4 players, 4 finished games of 4 selected"));
    CHECK(contains(txt, "Alpha"));

    const int64_t ids[] = {2};
    fcd_query sel{};
    sel.ids = ids;
    sel.id_count = 1;
    CHECK_OK(fcd_db_export(db, &sel, FCD_EXPORT_PGN, pgn.c_str(), &n));
    CHECK(n == 1);

    CHECK_OK(fcd_db_delete_games(db, ids, 1));
    CHECK_OK(fcd_db_count(db, nullptr, &n));
    CHECK(n == 3);
    fcd_db_close(db);

    std::printf("\n--- crosstable.txt ---\n%s\n", txt.c_str());
}

void test_errors() {
    fcd_db* db = nullptr;
    CHECK(fcd_db_open(nullptr, &db) == FCD_ERR_ARGUMENT);
    CHECK(std::strlen(fcd_last_error()) > 0);
    CHECK(fcd_abi_version() == FCD_ABI_VERSION);
    fcd_openings* book = nullptr;
    CHECK(fcd_openings_load_tsv("does-not-exist.tsv", &book) == FCD_ERR_IO);
    CHECK(book == nullptr);
}

}  // namespace

int main() {
    const auto dir = temp_dir();
    test_errors();
    test_import_and_query(dir);
    test_editing(dir);
    test_openings_and_fill(dir);
    test_ratings(dir);
    test_exports(dir);
    run_tools_tests();
    run_process_tests();
    std::printf("%d checks, %d failures\n", fcd_test::g_checks, fcd_test::g_failures);
    return fcd_test::g_failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
