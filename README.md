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

The panel has three tabs. **Fit** is where the work happens, **Ranges** shows what you have
told it about your history, and **Progress** says whether any of it helped.

The Fit tab is three numbered steps, top to bottom, with the history controls beneath them.

**1. Read replays.** Parses your replay files. Slow the first time and near-instant
afterwards, because the reduced form is cached. A very large library is reduced a chunk at a
time rather than in one sitting, so the first few reads each do a share of the work and get
quicker as the cache fills; every replay is covered in the end, however far back it goes.
Nothing happens until you press it; the mod never reads on startup.

**2. Fit both hands.** Fast, so change a range and refit freely.

**3. Apply.** Pick which profile to write into, and press. It writes the fitted rotation,
keeps the position you already had, switches to that profile, and records the change so
later replays are tied to the new grip.

**Below those: tell it which settings your old replays were played on.** A replay does not
record the controller offsets it was played with, and nothing in the file can recover them.
From the moment you install this mod it keeps its own record, but everything before that
needs your answer. Set the range with the two sliders, pick the controller profile you were
using, and press **Assign range**. If you changed your grip partway through your history,
assign each stretch separately; the start slider then follows the range you just made, so you
can walk forward through your history a stretch at a time. Ranges may not overlap — assigning
one that would starts it after the range already covering it, and says so — because a replay
claimed by two grips at once would be counted under whichever the fit happened to reach
first.

The **Ranges** tab lists what you have assigned, each line showing the dates, the grip, and
how many replays it actually holds. That count is worth a glance before fitting: a range can
easily span months and still pick up far fewer runs than you expected. A range over play the
mod recorded at the time is harmless but does nothing, because what was recorded as you
played outranks anything assigned afterwards.

Runs that no range covers are left out of the fit entirely. Guessing at them would mix cuts
from an unknown grip into a group that claims to know its own, which is the one error worth
avoiding here.

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

## Progress

The Progress tab answers the question the rest of the mod cannot: is any of this working?

It splits your history at the first entry in the mod's own record — the moment it first knew
what your settings were — and compares how far your cuts landed from the centre of the note
before and since. The comparison is **paired within each map**: only maps you played on both
sides of that line are counted, and each is compared against itself. That matters, because
your average cut distance across a library says more about which maps you happened to play
than about your grip.

    47 maps played both before and since
    Replay Fit started recording, 29 Aug 26

    Left  1.6 cm closer (34/47)      Right  1.5 cm closer (31/47)

    About +0.52% accuracy

The bracketed pair is how many of those maps improved. The percentage is what the change is
worth in scoring terms: the accuracy component of a note is 15 of its 115 points and falls
off with distance from the centre, so a centimetre is worth a fraction of a percent, not a
grade. The chart below plots the same measure over time, one column per slice of your
history, so a change that helped shows as a step rather than a number you have to trust.

Beneath it is where your current settings stand — what the cuts played on them are still
asking for, in degrees. Small numbers there mean the fit has converged and there is little
left to win.

**One caveat worth stating plainly.** Later runs are also more practised runs. Pairing within
a map controls for which maps you played, not for the fact that you have played them more by
the time the second half comes around. Some of any improvement shown here is you getting
better at the game rather than the mod getting your grip right. Treat it as evidence, not as
a measurement — and note that if the number is flat or negative, that reading is the more
trustworthy one, since practice would have pushed it the other way.

The tab needs replays read first, and stays quiet until there is enough on both sides of the
split to say anything: at least five maps in common, and a few hundred cuts in each era.

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

## If something goes wrong

The log is `Logs/_latest.log` in your Beat Saber folder, and every line from this mod is
tagged `ReplayFit`. That log is the most useful thing you can attach to a bug
report.

## Known limits

- **One Saber, speed and practice modifiers are excluded.** They change the geometry or the
  scoring, so their cuts would bias the fit.
- **Chains are ignored.** Chain links do not sit where their grid position claims, so their
  cut distances cannot be placed reliably.
- **Whole degrees only**, because that is what the settings screen accepts.
- **On 1.40.5**, a grip change you make in the game's own settings screen is noticed within
  30 seconds rather than immediately. That version has no event to hook. It makes no
  difference unless you change settings and start a map inside the same half-minute.

## Building

No game binary is committed here, and none should be: Beat Saber's assemblies are not
redistributable. The build reads them from an install you already have.

```powershell
dotnet build -c Release -p:BeatSaberDir="C:\Path\To\Beat Saber"
```

## Releasing

Tag and branch naming is documented in [RELEASING.md](RELEASING.md), and is shared with the other
Beat Saber mods alongside this one.

## License

MIT. See [LICENSE](LICENSE).
