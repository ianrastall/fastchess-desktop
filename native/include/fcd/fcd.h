/*
 * fcd.h - C ABI of the fastchess-desktop native core (fcd_core).
 *
 * Conventions
 *   - All strings are UTF-8 and NUL-terminated.
 *   - Every function that can fail returns fcd_status. On failure a message is
 *     available from fcd_last_error() on the same thread until the next call.
 *   - Strings returned through char** out-parameters are owned by the caller
 *     and must be released with fcd_free().
 *   - Handles are not thread-safe. Use one handle per thread, or serialize access.
 *   - Progress callbacks return 0 to continue, non-zero to cancel. Cancelling
 *     returns FCD_ERR_CANCELLED and rolls back the current transaction.
 *   - Structured input and output that has no fixed layout (tournament settings,
 *     argument lists, events) is exchanged as UTF-8 JSON text. Settings objects
 *     use the camelCase property names and enum member names of the C# records
 *     they mirror (TournamentSettings, RatingSettings).
 *
 * The library performs no networking and no UI work.
 */
#ifndef FCD_H
#define FCD_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(FCD_BUILDING)
#    define FCD_API __declspec(dllexport)
#  else
#    define FCD_API __declspec(dllimport)
#  endif
#else
#  define FCD_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define FCD_ABI_VERSION 2

typedef enum fcd_status {
    FCD_OK = 0,
    FCD_ERR_ARGUMENT = 1,
    FCD_ERR_IO = 2,
    FCD_ERR_DATABASE = 3,
    FCD_ERR_PARSE = 4,
    FCD_ERR_NOT_FOUND = 5,
    FCD_ERR_CANCELLED = 6,
    FCD_ERR_INTERNAL = 99
} fcd_status;

typedef struct fcd_db fcd_db;
typedef struct fcd_openings fcd_openings;

typedef int (*fcd_progress_fn)(void* user, int64_t done, int64_t total);

/* ---- Library ------------------------------------------------------------ */

FCD_API int32_t fcd_abi_version(void);
FCD_API const char* fcd_version(void);
FCD_API const char* fcd_last_error(void);
FCD_API void fcd_free(void* p);

/* ---- Game database ------------------------------------------------------ */

/* Selects games. All members are optional (NULL / 0).
 *   search     case-insensitive substring over players, event, site, ECO, opening
 *   order_by   one of: id, date, event, white, black, result, white_elo,
 *              black_elo, eco, opening, ply_count, round
 *   ids        explicit id list; when id_count > 0 only these games are used
 *   limit      0 means no limit */
typedef struct fcd_query {
    const char* search;
    const char* order_by;
    int32_t descending;
    int64_t offset;
    int64_t limit;
    const int64_t* ids;
    int64_t id_count;
} fcd_query;

typedef struct fcd_import_result {
    int64_t imported;
    int64_t duplicates;
    int64_t failed;
} fcd_import_result;

FCD_API fcd_status fcd_db_open(const char* path, fcd_db** out_db);
FCD_API void fcd_db_close(fcd_db* db);

/* Imports every game of a PGN file. Games whose moves cannot be replayed are
 * counted as failed and skipped. When skip_duplicates is non-zero, a game with
 * the same players, date, round, result and move sequence as a stored game is
 * skipped. Games without moves are never treated as duplicates. */
FCD_API fcd_status fcd_db_import_pgn(fcd_db* db, const char* pgn_path, int32_t skip_duplicates,
                                     fcd_progress_fn progress, void* user, fcd_import_result* out_result);

FCD_API fcd_status fcd_db_count(fcd_db* db, const fcd_query* query, int64_t* out_count);

/* JSON array of game summary objects:
 * {"id","event","site","date","round","white","black","result","whiteElo",
 *  "blackElo","eco","opening","variation","timeControl","termination",
 *  "plyCount","hasAnalysis"} (Elo values are null when unknown). */
FCD_API fcd_status fcd_db_query(fcd_db* db, const fcd_query* query, char** out_json);

/* JSON object with the summary fields plus "tags" (object of extra tags),
 * "startFen" (null for the standard start), "sanMoves" and "uciMoves"
 * (arrays), "comments" (array aligned with moves), "analysis" (raw JSON or null). */
FCD_API fcd_status fcd_db_get_game(fcd_db* db, int64_t id, char** out_json);

/* Sets a PGN tag. Well-known tags (Event, Site, Date, Round, White, Black,
 * Result, WhiteElo, BlackElo, ECO, Opening, Variation, TimeControl,
 * Termination) update their column; anything else is stored as an extra tag.
 * A NULL or empty value clears the tag. PlyCount, FEN and SetUp are derived
 * from the moves and cannot be set. */
FCD_API fcd_status fcd_db_set_tag(fcd_db* db, int64_t id, const char* tag, const char* value);

FCD_API fcd_status fcd_db_delete_games(fcd_db* db, const int64_t* ids, int64_t id_count);

/* Stores an analysis document (any JSON text) for a game. NULL clears it. */
FCD_API fcd_status fcd_db_set_analysis(fcd_db* db, int64_t id, const char* analysis_json);

#define FCD_FILL_OPENING            0x01u /* ECO/Opening/Variation from the opening book */
#define FCD_FILL_RESULT             0x02u /* Result/Termination when the final position is terminal */
#define FCD_FILL_OVERWRITE_OPENING  0x10u /* replace existing ECO/Opening/Variation */

/* Fills missing values on the selected games (NULL query = all games).
 * FCD_FILL_OPENING requires a non-NULL opening book. */
FCD_API fcd_status fcd_db_fill_missing(fcd_db* db, const fcd_query* query, const fcd_openings* book, uint32_t flags,
                                       fcd_progress_fn progress, void* user, int64_t* out_updated);

/* Reads an Ordo CSV (-c) file, replaces the stored rating list, and when
 * fill_elo is non-zero writes the rounded ratings into WhiteElo/BlackElo
 * (overwrite_elo: also replace existing values). out_elo_values_written counts
 * individual WhiteElo/BlackElo values written. */
FCD_API fcd_status fcd_db_apply_ordo_csv(fcd_db* db, const char* csv_path, int32_t fill_elo, int32_t overwrite_elo,
                                         int64_t* out_players, int64_t* out_elo_values_written);

/* JSON array: {"player","rating","error","points","played","percent"}. */
FCD_API fcd_status fcd_db_get_ratings(fcd_db* db, char** out_json);

typedef enum fcd_export_format {
    FCD_EXPORT_PGN = 0,
    FCD_EXPORT_JSON = 1,
    FCD_EXPORT_XML = 2,
    FCD_EXPORT_CROSSTABLE_TXT = 3
} fcd_export_format;

/* Exports the selected games (NULL query = all games). JSON and XML contain
 * the standings as well as the games. */
FCD_API fcd_status fcd_db_export(fcd_db* db, const fcd_query* query, fcd_export_format format, const char* out_path,
                                 int64_t* out_games);

/* ---- Opening book (Lichess chess-openings TSV) -------------------------- */

/* Loads and validates a five-column eco/name/pgn/uci/epd TSV. */
FCD_API fcd_status fcd_openings_load_tsv(const char* tsv_path, fcd_openings** out_book);
FCD_API void fcd_openings_free(fcd_openings* book);
FCD_API int64_t fcd_openings_count(const fcd_openings* book);

/* Writes the book as a pgn-extract ECO file (usable with -e<path>). The name
 * "Family: Variation, Sub, ..." maps to Opening / Variation / SubVariation. */
FCD_API fcd_status fcd_openings_write_eco_pgn(const fcd_openings* book, const char* out_path);

/* Classifies a UCI move sequence from the standard start position by the last
 * matched complete EPD. Writes {"eco","name","opening","variation","ply"} or
 * the literal null when nothing matches. */
FCD_API fcd_status fcd_openings_classify_uci(const fcd_openings* book, const char* uci_moves, char** out_json);

/* ---- Command lines (Windows CommandLineToArgvW rules) ------------------- */

FCD_API fcd_status fcd_cmdline_quote(const char* argument, char** out_text);
/* args_json: JSON array of strings. */
FCD_API fcd_status fcd_cmdline_format(const char* args_json, char** out_text);
/* Splits user-typed text into arguments; writes a JSON array of strings. */
FCD_API fcd_status fcd_cmdline_split(const char* text, char** out_json);

/* ---- Tournament settings and the fastchess command line ------------------ */

/* Problems that would make fastchess refuse to start, as a JSON array of messages (empty when usable). */
FCD_API fcd_status fcd_tournament_validate(const char* settings_json, char** out_errors_json);
/* fastchess arguments (JSON array) for a round robin or gauntlet. FCD_ERR_ARGUMENT for the staged
 * formats (pyramid, knockout, Swiss), which run as several fastchess runs. */
FCD_API fcd_status fcd_tournament_build_args(const char* settings_json, char** out_args_json);
/* Arguments of the first fastchess run of any format, for the command preview. */
FCD_API fcd_status fcd_tournament_first_stage_args(const char* settings_json, char** out_args_json);
/* Games the schedule plays (knockout: without tiebreaks); -1 when it cannot be computed. */
FCD_API fcd_status fcd_tournament_expected_games(const char* settings_json, int64_t* out_games);
/* The configured name, or the executable's file name without extension (as fastchess does). */
FCD_API fcd_status fcd_engine_display_name(const char* name, const char* command, char** out_text);

/* Classifies and parses one line of fastchess output:
 * {"kind":"normal"|"warning"|"engineFailure", "event":null|EVENT, "warningEngine":null|"name"}
 * EVENT is {"type":"gameStarted","number","total","white","black"},
 * {"type":"gameFinished","number","white","black","result","reason"} or
 * {"type":"tournamentFinished","message"}. "warning" marks fastchess's checks on engine output
 * (Warning;/Info;/Position;/Moves; lines); "engineFailure" a game lost to a time forfeit, crash,
 * stall or illegal move, or a nonzero timeout or crash count. */
FCD_API fcd_status fcd_fastchess_parse_line(const char* line, char** out_json);
/* 1 when a game end reason means an engine failed, else 0. */
FCD_API int32_t fcd_is_engine_failure_reason(const char* reason);

/* ---- Rating and pgn-extract command lines --------------------------------- */

FCD_API fcd_status fcd_ordoprep_args(const char* rating_settings_json, const char* input_pgn, const char* output_pgn,
                                     char** out_args_json);
FCD_API fcd_status fcd_ordo_args(const char* rating_settings_json, const char* input_pgn, const char* report_txt,
                                 const char* ratings_csv, char** out_args_json);
/* preset: 0 classify openings (-e<eco_pgn>), 1 remove duplicates, 2 fix result tags, 3 custom only.
 * custom_args are appended for every preset. */
FCD_API fcd_status fcd_pgn_extract_args(int32_t preset, const char* custom_args, const char* eco_pgn,
                                        const char* input_pgn, const char* output_pgn, char** out_args_json);

/* ---- Match statistics (the formulas of fastchess 1.8.2) ------------------- */

/* Results from one engine's point of view. The pentanomial counts are game pairs (the two games
 * on one opening): ll = two losses, ld = a loss and a draw, wl = a win and a loss, ... */
typedef struct fcd_match_stats {
    int32_t wins, draws, losses;
    int32_t ll, ld, wl, dd, wd, ww;
} fcd_match_stats;

/* Elo difference with its 95% margin, normalized Elo with margin, and likelihood of superiority
 * in percent. Values are infinite or NaN for a 0% or 100% score, as in fastchess. */
typedef struct fcd_elo {
    double elo, error, nelo, nelo_error, los;
} fcd_elo;

/* From game results, or from completed pairs when pentanomial is non-zero.
 * FCD_ERR_NOT_FOUND when there are no games (pairs). */
FCD_API fcd_status fcd_elo_estimate(const fcd_match_stats* stats, int32_t pentanomial, fcd_elo* out_elo);

typedef enum fcd_sprt_model { FCD_SPRT_NORMALIZED = 0, FCD_SPRT_LOGISTIC = 1, FCD_SPRT_BAYESIAN = 2 } fcd_sprt_model;
typedef enum fcd_sprt_outcome { FCD_SPRT_CONTINUE = 0, FCD_SPRT_H0 = 1, FCD_SPRT_H1 = 2 } fcd_sprt_outcome;

typedef struct fcd_sprt_params {
    double alpha, beta, elo0, elo1;
    int32_t model; /* fcd_sprt_model */
} fcd_sprt_params;

typedef struct fcd_sprt_state {
    double llr, lower_bound, upper_bound;
    double fraction; /* progress toward the bound on the LLR's side; negative toward H0 */
    int32_t outcome; /* fcd_sprt_outcome */
} fcd_sprt_state;

FCD_API fcd_status fcd_sprt_evaluate(const fcd_sprt_params* params, const fcd_match_stats* stats, int32_t pentanomial,
                                     fcd_sprt_state* out_state);

/* Live results of a tournament, fed with fastchess's finished games. Game counts update with every
 * game; pentanomial counts when both games of a pair are done (games (n-1)/games_per_encounter).
 * Pentanomial statistics are used only with games_per_encounter == 2. */
typedef struct fcd_scoreboard fcd_scoreboard;

typedef struct fcd_engine_totals {
    fcd_match_stats stats;
    int32_t failures; /* games lost by time forfeit, disconnect, stall or illegal move */
    int32_t warnings; /* fastchess warnings about the engine's search output */
} fcd_engine_totals;

FCD_API fcd_status fcd_scoreboard_new(int32_t games_per_encounter, int32_t pentanomial, fcd_scoreboard** out_board);
FCD_API void fcd_scoreboard_free(fcd_scoreboard* board);
FCD_API int32_t fcd_scoreboard_is_pentanomial(const fcd_scoreboard* board);
FCD_API fcd_status fcd_scoreboard_add_engine(fcd_scoreboard* board, const char* name);
/* result: "1-0", "0-1", "1/2-1/2" (anything else is ignored); reason: fastchess's game end reason. */
FCD_API fcd_status fcd_scoreboard_add_game(fcd_scoreboard* board, int32_t number, const char* white, const char* black,
                                           const char* result, const char* reason);
FCD_API fcd_status fcd_scoreboard_add_warning(fcd_scoreboard* board, const char* engine);
/* Engines in the order they were added or first played, as a JSON array. */
FCD_API fcd_status fcd_scoreboard_engines(const fcd_scoreboard* board, char** out_json);
FCD_API fcd_status fcd_scoreboard_engine(const fcd_scoreboard* board, const char* engine, fcd_engine_totals* out_totals);
FCD_API fcd_status fcd_scoreboard_head_to_head(const fcd_scoreboard* board, const char* engine, const char* opponent,
                                               fcd_match_stats* out_stats);

/* ---- Pairings of the staged formats --------------------------------------- */

/* Pairings are JSON arrays of {"white":"name","black":"name"|null}; black null is a bye. */
FCD_API fcd_status fcd_pairing_bracket_order(int32_t size, char** out_json);
/* seeded_json: names in seed order. Pads to a power of two; the top seeds get the byes. */
FCD_API fcd_status fcd_pairing_knockout_first_round(const char* seeded_json, char** out_json);
FCD_API fcd_status fcd_pairing_knockout_next_round(const char* winners_json, char** out_json);
/* state_json: {"seeded":[names],"scores":{name:points},"opponents":{name:[names]},
 *              "hadBye":[names],"whiteCounts":{name:count}}
 * Writes {"pairs":[pairing...],"bye":null|"name"}. */
FCD_API fcd_status fcd_pairing_swiss_round(const char* state_json, char** out_json);

#ifdef __cplusplus
}
#endif

#endif /* FCD_H */
