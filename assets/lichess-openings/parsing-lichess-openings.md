# Best Practices For Parsing Lichess Openings

This guide concerns the five-column, generated `all.tsv`-style distribution of the [lichess-org/chess-openings](https://github.com/lichess-org/chess-openings) project. It is based on an audit of the supplied `chess-openings.tsv` copy (3,815 data rows). Its central rule is simple: **parse moves with a chess library, index opening positions by the complete four-field EPD, and classify a game by its last matched position.** Treat opening names and ECO codes as labels, not unique identifiers or proof that a line is strategically sound.

## What the file contains

The source repository keeps editable opening data in `a.tsv` through `e.tsv`, divided by ECO volume. Its build process generates `dist/all.tsv` with additional computed fields. The supplied file has this generated five-column shape:

| Column | Meaning | Parsing guidance |
| --- | --- | --- |
| `eco` | ECO classification, such as `B10` | Preserve it as a string. It is a category, not a row key. |
| `name` | English opening or variation label | Preserve spelling and punctuation. It is not unique. |
| `pgn` | An illustrative SAN move sequence from the initial position | Parse as chess moves; do not infer a position by splitting words. |
| `uci` | The same move sequence in UCI coordinate notation | Useful for deterministic replay and integrity checks. |
| `epd` | The named position: piece placement, side to move, castling rights, legal en passant square | Use the **whole field** as the position lookup key. |

Lichess describes the `pgn` sequence as a well-known line or a common route to the position, rather than a required literal prefix of every game that reaches it. It explicitly suggests walking a game's moves backward until a named position is found. [Repository README](https://github.com/lichess-org/chess-openings#readme)

### Snapshot audit

The supplied copy has 3,815 records spanning all 500 ECO codes (`A00`â€“`E99`); 3,174 distinct names; and 3,815 distinct complete EPDs. There are no missing cells, malformed ECO codes, duplicate complete EPDs, or duplicate complete rows. Each UCI line was replayed from the standard starting position; its moves were legal and its resulting complete EPD matched the file. The number of SAN moves in each `pgn` also matched its UCI move count. That last check is only a **count** check; it does not independently prove that every SAN token denotes the same move as the corresponding UCI token.

There are 297 names appearing on more than one row, accounting for 641 additional occurrences. There are 108 names associated with more than one ECO code. Those counts are **not** reasons to delete rows: a label may apply at several positions, and the repository deliberately includes additional lines to improve coverage of move orders and transpositions. For example, `Zukertort Opening` appears at `1. Nf3`, `1. Nf3 Nf6`, and other positions.

This audit establishes the structural properties of the **attached copy**. It does not establish that every name is accepted by chess historians, that every line is good play, that all possible transpositions are represented, or that the copy is byte-for-byte identical to the newest upstream build.

## Use the complete EPD as the lookup key

An opening EPD here consists of four space-separated fields:

```text
piece-placement side-to-move castling-rights legal-en-passant-square
```

A conventional full FEN adds a halfmove clock and fullmove number. Those counters vary between histories and are not part of this dataset's position key. Conversely, **dropping the castling or en passant fields loses information** relevant to the legal position and can create false collisions. Lichess includes an en passant square only when a capture is legal. [Repository README](https://github.com/lichess-org/chess-openings#readme) [python-chess `Board.epd()` documentation](https://python-chess.readthedocs.io/en/latest/core.html)

The attached file provides an unusually clear example. These rows have the same board placement, side to move, and castling rights:

| ECO and name | Illustrative moves | Last EPD field |
| --- | --- | --- |
| `A00` Van Geet Opening: Nowokunski Gambit | `1. Nc3 e5 2. f4 exf4 3. e4` | `e3` |
| `C33` King's Gambit Accepted: Mason-Keres Gambit | `1. e4 e5 2. f4 exf4 3. Nc3` | `-` |

After the first line, Black can legally capture the just-advanced e-pawn en passant from f4. After the second line, White's last move was `Nc3`, so that capture is unavailable. If an importer discards the fourth field, it merges two distinct positions and two labels. In the supplied file, there is exactly one collision when indexing by piece placement plus turn; retaining the complete EPD resolves it.

Use the chess library's **legal** en passant setting when generating the key. Some FEN writers include an en passant target after any two-square pawn advance, even when nobody can capture; that convention will fail otherwise valid matches against this file. `python-chess` defaults `Board.epd()` to `en_passant="legal"`, but specifying it makes the intent explicit. [python-chess core documentation](https://python-chess.readthedocs.io/en/latest/core.html)

## Classify games by positions, not text prefixes

For a standard chess game:

1. Load the TSV with a tab-aware reader and preserve its Unicode text.
2. Index each complete `epd` to its row. Check for duplicate keys and handle them explicitly if a future release introduces any.
3. Parse the game's PGN with a chess parser; use its mainline unless a different variation is deliberately being analyzed.
4. Starting from the game's initial board, make each move and compute the four-field EPD with legal en passant handling.
5. Whenever the EPD has a row, remember that row and its ply number. Return the **last** match after processing the game.

This is the repository's backward-from-the-end rule expressed as one forward pass. A position reached through another move order can match even if the game's text never begins with the row's `pgn`. An exact literal `pgn` or `uci` prefix test alone misses such cases. Still, the dataset lists a finite set of positions: it does **not** guarantee a label for every theoretically equivalent line or every transposition. [Repository README](https://github.com/lichess-org/chess-openings#readme)

The result is the **deepest named position encountered along that game**, not necessarily the opening one would use for a repertoire lesson or an engine's strategic assessment. If no position matches, return an explicit `unknown` or null result; do not guess an ECO code from the first move.

### Compact Python reference implementation

This example requires `python-chess` and the supplied five-column TSV. It classifies standard-start PGNs conservatively, records the ply of the last match, and rejects parse errors and nonstandard starting positions.

```python
import csv
import chess
import chess.pgn


EXPECTED_COLUMNS = ["eco", "name", "pgn", "uci", "epd"]


def load_openings(tsv_path):
    by_epd = {}
    with open(tsv_path, "r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        if reader.fieldnames != EXPECTED_COLUMNS:
            raise ValueError(f"Unexpected columns: {reader.fieldnames!r}")

        for line_number, row in enumerate(reader, start=2):
            if None in row or any(not row[column] for column in EXPECTED_COLUMNS):
                raise ValueError(f"Missing or extra data at line {line_number}")
            key = row["epd"]
            if key in by_epd:
                raise ValueError(f"Duplicate EPD at line {line_number}: {key}")
            by_epd[key] = row
    return by_epd


def classify_game(game, by_epd):
    if game.errors:
        raise ValueError(f"Invalid PGN: {game.errors}")

    board = game.board()
    if board.chess960 or board.fen() != chess.STARTING_FEN:
        raise ValueError("This index expects standard chess from the initial position")

    last_match = None
    for ply, move in enumerate(game.mainline_moves(), start=1):
        board.push(move)
        opening = by_epd.get(board.epd(en_passant="legal"))
        if opening is not None:
            last_match = {"ply": ply, **opening}
    return last_match


openings = load_openings("chess-openings.tsv")
with open("games.pgn", "r", encoding="utf-8") as handle:
    while (game := chess.pgn.read_game(handle)) is not None:
        result = classify_game(game, openings)
        print(result or {"name": "Unknown opening"})
```

`chess.pgn.read_game()` and `mainline_moves()` are documented by [python-chess](https://python-chess.readthedocs.io/en/latest/pgn.html). The example does not silently classify Chess960, a custom `SetUp`/`FEN` position, or a partial score as though it were a complete game from the usual starting array. For very large PGN collections, avoid printing each result and stream the classification into the intended database or output file; retain parse-error counts.

### Validation during import

For an ingestion pipeline, test the generated fields against the replayed moves. This catches damaged copies or edits made after generation:

```python
def check_row(row):
    board = chess.Board()
    moves = []
    for token in row["uci"].split():
        move = chess.Move.from_uci(token)
        if move not in board.legal_moves:
            raise ValueError(f"Illegal move {token} in {row['name']}")
        moves.append(move)
        board.push(move)
    if board.epd(en_passant="legal") != row["epd"]:
        raise ValueError(f"EPD disagreement in {row['name']}")
    return moves
```

When independently checking `pgn` against `uci`, parse its SAN using a PGN parser and compare the actual move objects. Merely counting SAN tokens, as in the initial audit, cannot detect a substituted legal move. Keep validation separate from classification so a single bad row can be reported with its source line rather than silently changing the result.

## Data model and operational choices

| Concern | Recommended choice | Common failure |
| --- | --- | --- |
| Row identity | Complete four-field `epd` within a pinned dataset version | Treating `name`, `eco`, or board placement alone as unique |
| Lookup | Compute a legal-EP EPD after each legal move | Matching only a literal SAN/UCI prefix |
| Game result | Last matched row, with its matched ply | Replacing it with the game's final board or earliest named line |
| PGN input | Parse the mainline and inspect parse errors | Splitting on spaces or trusting a supplied `[ECO]` tag as a computed result |
| Missing match | Record `unknown` with enough information to investigate | Assigning a guessed name or the first superficially similar family |
| Provenance | Record source repository, download date or commit, and file hash | Mixing labels from different snapshots without knowing which won |
| Local corrections | Keep a small documented override or exclusion table | Editing upstream text in place and losing the original evidence |

In SQLite, `epd TEXT PRIMARY KEY` is a natural lookup column for this particular snapshot; store `eco`, `name`, `pgn`, `uci`, and perhaps a derived ply count beside it. Preserve the raw TSV separately. If you combine this source with a different opening book, define an explicit precedence rule, since the other source may use different names or ECO assignments. If you need historical reproducibility, pin a commit or release and keep a checksum: the repository accepts changes and its downstream services update on different schedules. [Repository README](https://github.com/lichess-org/chess-openings#readme)

## Accuracy limits and a concrete exception

The moves and EPD can be internally consistent while the **label** is debatable or wrong. In the attached copy, line 1088 is:

```text
B10  Caro-Kann Defense: Accelerated Panov Attack, Pseudo-Scandinavian
1. e4 c6 2. c4 d5 3. exd5 Qxd5
```

White can play `4. cxd5`, taking Black's queen. The line is legal, so a legality or EPD audit correctly passes it, but its use of the opening name has been challenged in [upstream issue #312](https://github.com/lichess-org/chess-openings/issues/312). Treat this as a **flagged naming/line judgment**, not as evidence that the TSV is corrupt or that a global cleanup rule should delete similar rows. The repository also has an [open discussion about parent lines and transpositions](https://github.com/lichess-org/chess-openings/issues/66).

For published opening names, a sensible policy is to retain the original row and annotate specific exceptions with a reason, evidence link, reviewer, and date. An override can replace a display label or suppress one mapping without destroying the source copy. Revisit those decisions on refresh, since upstream may incorporate a correction.

## Refresh checklist

- Confirm whether the downloaded file is a generated five-column `dist/all.tsv` or a three-column editable source volume; do not assume identical schemas.
- Preserve the raw file and record its source version and checksum.
- Parse as UTF-8 TSV and validate required columns, missing values, ECO shape, and duplicate complete EPDs.
- Replay `uci` from the standard start and compare the resulting legal-EP EPD; optionally parse and compare the SAN `pgn` moves too.
- Classify games by complete EPD at each ply and return the last matched row, with an explicit no-match state.
- Review known exceptions separately from structural errors. Do not deduplicate by name or automatically choose one ECO for every occurrence of a name.

**Practical conclusion:** The attached file needs no bulk format cleanup. It is a strong, internally consistent position-to-label index. Good parsing and a small, reviewable layer for disputed labels matter more than rewriting its rows.
