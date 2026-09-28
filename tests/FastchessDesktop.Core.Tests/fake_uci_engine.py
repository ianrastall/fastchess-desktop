#!/usr/bin/env python3
"""Deterministic stand-in for a UCI engine, used by the C# tests on Linux and macOS.

Score (side-to-move point of view) is 30 - 20 * number_of_moves_played, so
evaluations alternate in a predictable way. After the move "d8h4" it reports a
forced mate for the side to move, which exercises mate handling.
"""
import sys

moves = []
for raw in sys.stdin:
    line = raw.strip()
    if line == "uci":
        print("id name FakeEngine 1.0")
        print("id author tests")
        print("option name Hash type spin default 16 min 1 max 1024")
        print("uciok")
    elif line == "isready":
        print("readyok")
    elif line.startswith("position"):
        parts = line.split()
        moves = parts[parts.index("moves") + 1:] if "moves" in parts else []
    elif line.startswith("go"):
        if moves and moves[-1] == "d8h4":
            print("info depth 1 score mate 0")
            print("bestmove (none)")
        else:
            cp = 30 - 20 * len(moves)
            print("info depth 1 multipv 1 score cp 9999 lowerbound")
            print("info depth 5 seldepth 6 multipv 1 score cp %d nodes 100 pv e2e4 e7e5" % cp)
            print("info string not a score")
            print("bestmove e2e4 ponder e7e5")
    elif line == "quit":
        break
    sys.stdout.flush()
