# ReplayFit

[![Latest release](https://img.shields.io/github/v/release/kaedon78/bs-replay-fit?label=latest%20release)](https://github.com/kaedon78/bs-replay-fit/releases/latest)
[![License: MIT](https://img.shields.io/github/license/kaedon78/bs-replay-fit)](LICENSE)

Beat Saber scores every note out of 115: 70 for winding up far enough, 30 for following
through, and 15 for how close to the note's centre your blade passed. The first two come
from how far your controller *turned*, so no controller offset can touch them. The third is
pure placement — and placement is exactly what a controller offset moves.

This mod reads the replays you have already played, measures where your sabers actually
crossed each note, and works out the whole-degree controller rotation that would have put
them closer to centre. It suggests; you decide. When you accept, it writes the numbers into
a controller profile you choose, the same values you could type into the settings screen
yourself.

## What to expect

**A small, real improvement, or an honest "nothing to gain".**

On the player it was developed against, measured over 300 runs, the best available offset was
worth about **+0.24% accuracy**. That is small *because that player's grip was already close*.
Someone genuinely misaligned has more to gain. The mod is built to say plainly when there is
nothing worth changing rather than inventing a reason to adjust.

Two things it will not do:

- **It will not change how you swing.** It re-aims a swing you already own.
- **It will not fix inconsistency.** On that same history, **95% of the accuracy lost to cut
  placement was per-swing scatter** that no fixed setting reaches: the cuts land in a wide
  spread around a centre that is already nearly right, and an offset can only move the centre.
  If your centring falls apart on dense passages, that is a swing problem and this mod will
  tell you so rather than pretend a number fixes it.

Your two hands will usually want different numbers. Applying one hand's answer to the other
costs about as much as the right one gains, so it always fits them separately.

## Requirements

| | |
|---|---|
| Beat Saber | 1.40.5 or 1.45.0 (download the matching build) |
| BSIPA | 4.3 or newer |
| BSML | 1.12 or newer |
| Replays | BeatLeader, or any folder of `.bsor` files |

Replays are where all the measurement comes from. Without them the mod has nothing to read.
You need roughly **20 runs** on one set of controller settings before it will recommend
anything — below that it shows what it found but refuses to advise on it.

## Installing

Extract the zip into your Beat Saber folder so that `ReplayFit.dll` lands in
`Plugins`. Start the game once, then find **ReplayFit** under Mod Settings.

## Using it

The panel is four numbered steps, top to bottom.

**1. Read replays.** Parses your replay files. Slow the first time and near-instant
afterwards, because the reduced form is cached. A very large library is reduced a chunk at a
time rather than in one sitting, so the first few reads each do a share of the work and get
quicker as the cache fills; every replay is covered in the end, however far back it goes.
Nothing happens until you press it; the mod never reads on startup.

**2. Tell it which settings your old replays were played on.** A replay does not record the
controller offsets it was played with, and nothing in the file can recover them. From the
moment you install this mod it keeps its own record, but everything before that needs your
answer. Set the date range with the two sliders, pick the controller profile you were using,
and press **Assign range**. If you changed your grip partway through your history, assign
each stretch separately — the list shows how many runs each range actually covers, so you
can check a range holds what you meant before fitting on it. A range over play the mod
already recorded at the time says *already recorded* rather than a count: it is harmless, but
it is doing nothing, because what was recorded as you played outranks anything assigned
afterwards.

Runs that no range covers are left out of the fit entirely. Guessing at them would mix cuts
from an unknown grip into a group that claims to know its own, which is the one error worth
avoiding here.

**3. Fit both hands.** Fast, so change the range and refit freely.

**4. Apply.** Pick which profile to write into, and press. It writes the fitted rotation,
keeps the position you already had, switches to that profile, and records the change so
later replays are tied to the new grip.

## Reading the result

    left: (42.00, -5.00, 0.00) -> (45.00, 0.00, 0.00), worth 0.31% accuracy

Your current setting, the suggested one, and what the change is worth as a percentage of
accuracy on the cuts it was measured on. Small numbers are normal. A large one usually means
the range you assigned describes settings you were not actually playing on.

If it says no group has enough runs, either widen the range or play more.

## After you apply

The change is recorded, so the next fit knows your history has a boundary in it and will not
average across it. Play 10–20 runs on the new grip and fit again: if the first fit was right,
the second should ask for a much smaller correction.

## Fair play

The mod only ever writes the same per-hand values the settings screen writes. It does not
move your sabers at runtime, does not patch scoring, and does nothing during a map. What it
produces is a controller configuration you could have typed in yourself.

## Where its data lives

`UserData/ReplayFit/`:

| file | what it is |
|---|---|
| `cuts.cache` | parsed replays, so a second read is fast |
| `offsets.jsonl` | when your controller settings changed |
| `preferences.txt` | the ranges you assigned |
| `replay-folders.txt` | extra replay folders, one path per line (optional) |

`replay-folders.txt` is worth knowing about if you keep several game installs — a separate
Beat Saber version has its own replay folder, and listing it here lets the mod use that
history too.

Deleting any of these is safe. The cache rebuilds, and the other two only lose answers you
gave it.

## Known limits

- **One Saber, speed and practice modifiers are excluded.** They change the geometry or the
  scoring, so their cuts would bias the fit.
- **Chains are ignored.** Chain links do not sit where their grid position claims, so their
  cut distances cannot be placed reliably.
- **Whole degrees only**, because that is what the settings screen accepts.
- **On 1.40.5**, a grip change you make in the game's own settings screen is noticed within
  30 seconds rather than immediately. That version has no event to hook. It makes no
  difference unless you change settings and start a map inside the same half-minute.

## If something goes wrong

The log is `Logs/_latest.log` in your Beat Saber folder, and every line from this mod is
tagged `ReplayFit`. That log is the most useful thing you can attach to a bug
report.

## License

MIT. See [LICENSE](LICENSE).
