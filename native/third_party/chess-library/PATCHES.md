# Local patches to chess.hpp

`chess.hpp` is chess-library 0.9.4 by Disservin (MIT, see `LICENSE`), copied
from `assets/fastchess/fastchess-1.8.2-alpha.zip` (`app/third_party/chess.hpp`).

## Patch 1: whitespace before comments, NAGs and variations

`StreamParser::parseMoveAppendix` recognized only a literal space between a
move and a following `{comment}`, `$NAG` or `(variation)`. When one of those
began a new line, the parser emitted it as a move token (for example
`{+M1/1 0.01s}`), and the game failed to import. This affects PGN written by
pgn-extract `--commentlines`, by fastchess-desktop's own PGN export (which wraps
lines at 79 columns), and by many other tools.

The patch adds `'\t'`, `'\n'` and `'\r'` as cases next to `' '`. It is marked
in the source with the comment `fastchess-desktop patch`.

When upgrading chess.hpp, check whether upstream has fixed this and re-apply
the patch if not. `native/tests/fcd_tests.cpp` (PGN round trip) detects a
regression.
