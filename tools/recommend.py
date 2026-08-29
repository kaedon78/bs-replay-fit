"""Search whole-degree controller settings, using the conversion the game actually performs.

The measured part of this -- how far each hand's cut plane sits from where it should -- comes
from the replay pipeline in ../beat-saber-mapper and is independent of any of this. What is
searched here is the last step, turning that measurement into two numbers per hand, and that
step has been wrong twice for the same reason: the settings screen's numbers are not what the
game feeds to ``Quaternion.Euler``.

An in-game probe pinned most of it. The left hand's Y and Z are negated, the position offset
is rotated by the resulting rotation, and the platform's root pose left-multiplies and so
cancels out of any difference between two settings. What the probe could not answer is the
per-manufacturer legacy offset, because it ran under ``fpfc`` where no controller is present
and the platform reports nothing; the game's own source gives -16.3 degrees of X for a Valve
Index. So both are searched, and if they pick the same settings the question is moot.
"""

from __future__ import annotations

import argparse
import collections
import datetime
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, r"C:\Dev\beat-saber-mapper\tools\scripts")
from offset_convert import turn_between  # noqa: E402
from saber_offset import accuracy_points, load_cache, merge  # noqa: E402
from saber_offset_apply import Scored  # noqa: E402

#: The rotation in force while the fitted replays were played.
BASELINE = (47.0, -5.0, 0.0)


def candidates(hand: str, cap_deg: float, span: int, **kw) -> list[tuple]:
    base = tuple(int(round(v)) for v in BASELINE)
    out = []
    for dx in range(-span, span + 1):
        for dy in range(-span, span + 1):
            for dz in range(-span, span + 1):
                trial = (base[0] + dx, base[1] + dy, base[2] + dz)
                turn = turn_between(BASELINE, trial, hand, **kw)
                if np.degrees(np.linalg.norm(turn)) <= cap_deg + 1e-9:
                    out.append((trial, turn))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("cache", type=Path)
    ap.add_argument("--cap", type=float, default=5.0)
    ap.add_argument("--span", type=int, default=9)
    ap.add_argument("--before", default="2026-08-28 22:49")
    ap.add_argument("--top", type=int, default=4)
    args = ap.parse_args()

    rows = load_cache(args.cache, until=datetime.datetime.fromisoformat(args.before))
    sessions: dict[str, dict[int, list]] = collections.defaultdict(lambda: {0: [], 1: []})
    for row in rows:
        for colour, cuts in row["hands"].items():
            sessions[row["played"].strftime("%Y-%m-%d")][colour].append(cuts)
    days = sorted(sessions)
    print(f"{len(rows)} replays over {len(days)} sessions\n")

    for colour, hand in ((0, "left"), (1, "right")):
        group = {d: sessions[d][colour] for d in days if sessions[d][colour]}
        pooled = Scored(merge([c for g in group.values() for c in g]))
        by_day = {d: Scored(merge(g)) for d, g in group.items()}
        worth = sum(s.worth for s in by_day.values())
        print(f"=== {hand} hand: {sum(len(g) for g in group.values())} replays ===")

        for label, kw in (("legacy 0 (what fpfc showed)", dict(controller="none")),
                          ("legacy -16.3 X (Valve, real VR)", dict(controller="valve"))):
            grid = candidates(hand, args.cap, args.span, **kw)
            held = {t: sum(by_day[d].points(turn)[0] for d in group) / worth for t, turn in grid}
            order = sorted(held, key=lambda t: -held[t])[: args.top]
            best = ", ".join(f"{t} {held[t]:+.3%}" for t in order)
            print(f"  {label:<34s} {best}")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
