"""Run the offline pipeline over exactly the files the mod read, and compare.

The mod's answer already matched the offline one on the left hand and differed by about a
degree on the right. That is the kind of difference that has two very different causes --
a real defect in the port, or the two having simply read different replays -- and the only
way to tell them apart is to stop letting the populations differ.

So this reproduces the mod's file selection rather than approximating it: both folders, the
newest N by modification time, Standard only, the strict `clean` test the mod applies, at
least a hundred cuts per hand, and only the epoch before the settings changed. If the cut
counts come out equal, the selection and extraction agree and any remaining difference is
in the search. If they do not, the populations were never the same and the earlier
comparison meant nothing.
"""

from __future__ import annotations

import argparse
import datetime
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, r"C:\Dev\beat-saber-mapper\tools\scripts")
from offset_convert import turn_between  # noqa: E402
from saber_offset import merge, read  # noqa: E402
from saber_offset_apply import Scored  # noqa: E402

#: Matching Recommender.MostRecentReplays and MinCutsPerHand.
MOST_RECENT = 400
MIN_CUTS = 100

#: The settings these replays were played on, and when they stopped being in force.
BASELINE = (47.0, -5.0, 0.0)
EPOCH_ENDS = datetime.datetime(2026, 8, 29, 2, 49, 0, tzinfo=datetime.timezone.utc)

FOLDERS = [
    Path(r"D:\BeatSaberCustom\BSModUpdater\game\1.45.0-modded\UserData\BeatLeader\Replays"),
    Path(r"D:\BeatSaberCustom\BSManager\BSInstances\1.40.5\UserData\BeatLeader\Replays"),
]


def candidates(cap_deg: float, span: int, hand: str) -> list[tuple]:
    base = tuple(int(round(v)) for v in BASELINE)
    out = []
    for dx in range(-span, span + 1):
        for dy in range(-span, span + 1):
            for dz in range(-span, span + 1):
                trial = (base[0] + dx, base[1] + dy, base[2] + dz)
                turn = turn_between(BASELINE, trial, hand, controller="none")
                if np.degrees(np.linalg.norm(turn)) <= cap_deg + 1e-9:
                    out.append((trial, turn))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cap", type=float, default=5.0)
    ap.add_argument("--span", type=int, default=9)
    args = ap.parse_args()

    files = [p for folder in FOLDERS if folder.is_dir() for p in folder.glob("*.bsor")]
    files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    files = files[:MOST_RECENT]
    print(f"{len(files)} files, newest first, across {len(FOLDERS)} folders")

    parts: dict[int, list] = {0: [], 1: []}
    used = skipped = 0
    for path in files:
        when = datetime.datetime.fromtimestamp(
            path.stat().st_mtime, tz=datetime.timezone.utc)
        if when >= EPOCH_ENDS:
            skipped += 1
            continue
        try:
            hands = read(path, standard_only=True, min_cuts=MIN_CUTS, require_clean=True)
        except Exception:
            skipped += 1
            continue
        if not hands or 0 not in hands:
            skipped += 1
            continue
        for colour, cuts in hands.items():
            parts[colour].append(cuts)
        used += 1

    print(f"{used} used, {skipped} skipped\n")
    print(f"  {'hand':<6s}{'cuts':>10s}{'mean cut':>11s}{'setting':>16s}"
          f"{'turn (deg)':>18s}{'gain':>10s}")
    for colour, hand in ((0, "left"), (1, "right")):
        if not parts[colour]:
            continue
        pooled = merge(parts[colour])
        scored = Scored(pooled)
        grid = candidates(args.cap, args.span, hand)
        best, best_turn, best_score = None, None, -1e18
        for trial, turn in grid:
            got, _ = scored.points(turn)
            if got > best_score:
                best_score, best, best_turn = got, trial, turn
        gain = best_score / scored.worth
        print(f"  {hand:<6s}{len(pooled):>10,d}{pooled.distance.mean()*100:>10.2f}cm"
              f"{str(best):>16s}"
              f"{np.degrees(best_turn[0]):>+10.2f},{np.degrees(best_turn[1]):>+6.2f}"
              f"{gain:>+10.3%}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
