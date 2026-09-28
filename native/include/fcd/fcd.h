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
 *
 * The library performs no process launching, no networking and no UI work.
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

#define FCD_ABI_VERSION 1

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

#ifdef __cplusplus
}
#endif

#endif /* FCD_H */
