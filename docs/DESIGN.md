# ReplayFit

A Beat Saber mod that watches where your sabers actually cut and suggests a controller
offset that lands them closer to the centre of the note.

## What it is fixing

A note is worth 115 points: 70 for winding up far enough, 30 for following through, and 15
for how close to the note's centre the blade passed. The first two are measured from how far
the controller *turned*, not where it was or which way it pointed, so a fixed offset cannot
touch them. Only the third moves. That is the whole safety argument for this mod — it
re-aims a swing you already own rather than asking you to swing differently.

On the player this was developed against, measured over 273 replays and 37 sessions:

| | |
|---|---|
| swing points | 99.95% of maximum |
| accuracy points (cut distance) | 60.68% |

Every point missing was placement, none of it swinging.

## What it will and will not buy you

*Superseded in part. The figures here were measured offline, before the cut model's sign was
corrected, and on a smaller set. "Where the accuracy actually goes" below is the current
measurement, from the mod's own cache over 300 runs; it agrees on the headline (+0.24 pp
against +0.3%) and is more precise about the split.*

Be honest with users about this. On the developing player a rigid offset explains only
**8–11%** of the signed cut distance; mean distance is 11–14 cm, of which about 6 cm is
fixed bias and 13–17 cm is per-swing scatter no setting reaches. Held out across sessions,
the best offset is worth about **+0.3% of final score**.

That number is personal, not universal. It is small here *because this player's grip is
already close*. Someone genuinely misaligned has much more to gain, and the mod should say
plainly when a player has nothing to gain rather than inventing a reason to adjust.

Two facts that shape the design:

- **The hands want different settings.** Applying either hand's fitted offset to the other
  costs about as much as the right one gains. Fit and apply per hand, always.
- **The residual drifts**, around 1–2 degrees a month in yaw. A one-time fit goes stale.
  That, not the one-off gain, is what justifies automating this.

## The problem this repo exists to solve first

Everything above was measured outside the game, from BeatLeader replays. That works, and the
measurement is sound — the scoring model was verified by rebuilding a replay's final score
exactly (582,075 against 582,075).

What did *not* work outside the game was the last step: converting a measured saber-frame
correction into numbers for the settings screen. The settings screen's numbers are not what
the game feeds to `Quaternion.Euler`, and two defensible readings of
`VRController.TryGetControllerOffset` disagreed by 3–8° in the saber's frame. Replay data
could not separate them — the run-to-run spread of the fitted angle is ~1.8°, so telling
them apart would have taken more sessions than it takes to ask the game directly.

That cost a wrong recommendation before it was caught, and the failure mode is the reason
this is worth writing down: the left hand's correction came out sign-flipped on the term
doing most of the work, and a sign-flipped offset does not look wrong. It looks like a
setting that did nothing. The measured gain fell from an available +0.373% to +0.059% while
every number in the pipeline stayed plausible.

The next section is what an in-game probe returned, which settled it in one launch.

In-process the question disappears. So does most of the rest of the offline pipeline:

| needed for replays | in-process |
|---|---|
| BSOR parsing | subscribe to the cut event |
| reconstructing each note's centre from the grid and a fitted note speed | `noteTransform.position` — exact |
| excluding chain links as off-grid; the `time_deviation` sign trap | gone with the reconstruction |
| interpolating frames to find the saber pose at the cut | the transform is right there |
| inferring which settings each replay was played on | stamp the active offsets on each cut |
| Euler → saber-frame conversion | ask `TryGetControllerOffset` |

What remains per cut is five floats — signed distance, the plane normal's two saber-frame
components, the lever, the combo multiplier. 100k cuts is 2 MB, and the fit is a grid search
over candidate integer settings.

## The conversion, as measured

**Milestone 1 is done.** `OffsetProbe` logged, for each hand, the settings in force and the
offset pose the game computed from them. The mapping is now pinned, and the position
transform reproduces to four decimals on both hands:

    appliedRotation = Euler(mirror(legacyRotationOffset + typedRotation))
    appliedPosition = appliedRotation * mirror(legacyPositionOffset + typedPosition)
    mirror(v) = (v.x, -v.y, -v.z) for rotation, (-v.x, v.y, v.z) for position, left hand only

The platform's root pose left-multiplies the rotation and therefore cancels out of any
difference between two settings, which is what makes the search computable at all.

`legacyRotationOffset` is **zero for this player**, but note carefully where that comes from.
Under `fpfc` the probe reports it zero via `DevicelessVRHelper`, which says nothing about VR
— with no device present the helper returns zeros whatever the hardware would have done. The
evidence for the VR case is separate and stronger: every archived log from a real VR session
carries `[UnityXRHelper] Unexpected manufacturer name: Unknown`, so
`TryGetLegacyPoseOffsetForNode` returns false and leaves the offset at zero there too.

The game's source would otherwise apply -16.3 degrees of X for a Valve Index. A mod shipping
to other people must therefore read this at runtime and never assume either case — the two
differ by more than the whole correction being applied.

Because it is zero in both, fpfc runs are representative *here*, which is what makes the loop
developable without a headset.

## Status

Complete and in testing. Replays are the source; live capture survives as a dormant fallback
behind a marker file, for if replay data ever stops being available. Builds ship for 1.40.5
and 1.45.0 from one source, with the three members that differ reached through `GameApi`.

## The sign, twice

The story above — a sign-flipped correction reads as a setting that did nothing — happened a
second time, in a different place, and was found much later.

`DistanceUnder` predicts where a cut lands under a candidate setting. It subtracted the
predicted shift where the geometry adds it. Turning the saber by a small local rotation `t`
moves a point a distance `L` along the blade by `t × (0,0,L)`; the cut point moves and the
note does not, so the gap changes by the negative of that. Because the shift is linear in the
turn, subtracting returns the *exact negative* of the right answer: the search still
converged, still reported a gain of two or three tenths of a percent, and recommended moving
the grip as far the wrong way as it should have gone the right way.

Nothing in the suite could catch it. Every case built its cuts with the same functions it
then verified, so the sign cancelled and they passed. The parity test compared against the
Python this was ported from, which has the same convention, so it agreed to the cut. What
found it was rebuilding the counterfactual from scratch — put a saber in the world under one
setting, turn it to another, measure the gap again with nothing but rotated vectors — which
is now two self-test cases. At 0.05° the correct form agrees to seven decimals and the old
one is out by 8e-4.

The lesson worth keeping: **a test that builds its input with the code under test proves
self-consistency and nothing else.** Both sign errors in this project were of that shape.

## Where the accuracy actually goes

Measured over 300 runs and 210,000 cuts from the developing player's own history, using the
mod's cache. Per note, out of 115:

| | lost |
|---|---|
| swing points (70 + 30), already at 99.95% of maximum | 0.04 pp |
| cut distance (the 15-point term) | 5.56 pp |

Essentially the whole shortfall is placement, which is the argument for the mod looking only
at that term. Implied accuracy 94.40%. Splitting the 5.56 pp by what a fixed offset can
reach:

| | |
|---|---|
| best whole-degree rotation | 0.24 pp |
| adding a position offset as well | 0.27 pp |
| per-swing scatter, unreachable by any fixed offset | **5.29 pp** |

**Ninety-five percent of the loss is scatter.** The mod is a rounding correction against it,
and the readme says so rather than implying otherwise.

Position was checked and left out on evidence, not taste: adding it buys 0.03 pp, and to get
that the fit wants to move the controller 5.9 cm one way on the left and the opposite way on
the right. Large moves for nothing is what a badly identified parameter fitting noise looks
like.

## What the scatter is made of

Three candidate structures were tested against the residual left after the best rigid fit.

**Swing direction: no.** Bucketed by the cut normal's angle in the sabre frame, direction
explains 1.5% of the variance on the left and 0.3% on the right. The two dominant clusters --
up-swings and down-swings, 93% of cuts -- already sit near zero mean, so the rigid fit has
centred the bulk properly. The only structure is on the left hand in two minority directions
(4,900 cuts at -81 mm, 866 at -145 mm), about 6% of its cuts.

**Distance along the blade: weakly, on the right.** Spread grows from 107 mm near the hilt to
120 mm at the tip. The left hand does not show it.

**Cadence: yes, strongly.** Comparing each run's fastest third of inter-cut gaps against *its
own* slowest third, so map difficulty is held constant:

| | left | right |
|---|---|---|
| fastest third minus slowest third | +19.3 mm | +24.8 mm |
| worse when swinging fast | 235 of 300 runs | 268 of 300 runs |
| t | +13.0 | +18.1 |

Pooled across runs the gradient is roughly twice that, but only about half survives holding
the map constant -- the rest is difficulty confounding, which is why the within-run pairing is
the number to quote.

Two mechanisms were separated with the replay's own per-cut sabre speed. Both raw speed and
proximity to a swing reversal correlate with worse centring, and they are confounded, since a
dense passage produces both. Holding speed roughly constant in bands, cuts taken soon after a
reversal remain worse by 1.7 cm on the left and 0.9 cm on the right -- present, but smaller
than speed and inconsistent across bands. The reversal detector is crude (a frame-to-frame
velocity sign change, so tracking jitter registers as a false reversal), which will blur the
effect toward zero.

## Why cadence cannot be corrected here

It is variance, not bias, and that is the whole answer.

| | left | right |
|---|---|---|
| best turn on the fast third | +2.07, -5.18 deg | -2.57, +1.89 deg |
| best turn on the slow third | +3.03, -5.09 deg | -1.93, +1.30 deg |
| they differ by | 0.96 deg | 0.88 deg |
| spread after each gets its *own* best turn | 194 vs 172 mm | 171 vs 141 mm |
| gain from a perfect per-cadence offset | +0.020 pp | +0.034 pp |

Fast and slow cuts want the same offset to within a degree, which is under the whole-degree
step the settings screen accepts. Give each group its own optimum and the fast cuts are still
22 and 30 mm wider, with mean residuals near zero on both sides. Swinging faster does not move
where the cuts centre; it scatters them around the same centre.

An offset translates and rotates the blade, which moves the centre of a distribution. It has
no mechanism to narrow one, and a deviation that depends on where you are in a swing cannot be
undone by a number that is the same at every point of every swing. Even an oracle switching
offsets per cut -- unshippable anyway, since it means moving sabres at runtime -- buys 0.03 pp.

So the honest ordering of what is on the table:

| | worth |
|---|---|
| narrowing the fast-cadence spread to match the slow-cadence one | ~1 pp |
| the static offset this mod fits | 0.24 pp |
| cadence-aware offsets on top | 0.03 pp |

The largest item is not a settings change. The mod's useful role there is diagnostic, and the
per-cut gap since the hand's previous cut is carried in the cache so it can be asked again.

## A misreading worth recording

The lever was briefly changed to measure to the recorded cut point rather than to the note
centre, on the grounds that 30% of values were "impossible" -- past a metre, sometimes behind
the hand. Both halves of that were wrong, and the correction is worth keeping written down.

The lever is not a point on the sabre. What rotates is the cut plane, about the grip that lies
in it, and what is measured is how far the *note* sits from that plane: turning by `t` changes
that distance by `dot(t, m x (centre - grip))`. The arm is the note's own position, so a value
past a metre is a note further away than the blade is long, and a negative one is a note behind
the grip along the blade.

The cut point is the worse choice: over 12,000 cuts it sits about 21 cm off the blade axis, and
no constant local offset removes that, so it is a point in the cut plane near the note rather
than on the sabre. The change moved the fit by 0.02 degrees, because the difference between the
two points lies almost along the cut normal while the lever only sees the part along the blade.

Two things the investigation did settle, by measurement rather than argument:

- **The replay records the sabre pose, not the pre-offset controller pose.** Perpendicular
  distance from the cut point to the blade axis is 21 cm under the first reading and 85 cm
  under the second.
- **The frame clock agrees with the note clock.** Sweeping a timing offset from -80 ms to
  +80 ms puts the minimum sharply at zero.

## Design decisions worth not relitigating

- **Write vanilla per-hand settings; never apply a runtime saber transform.** A mod that
  moves sabers at runtime is a leaderboard-flagging risk; writing the values the settings
  screen writes is not. It costs whole-degree granularity, which is free — resolving 1°
  takes 10–22 runs.
- **Suggest, with auto-apply opt-in.** Changing someone's grip without telling them is how a
  mod gets uninstalled.
- **Do not over-filter runs.** Measured: excluding fails, early exits and off-range
  difficulties moves the answer by 1–2° and does not improve the held-out gain. A bad run
  contributes noise, not bias — only successful cuts are used, and a sloppy cut is still an
  honest sample. The filters that matter are a minimum cut count *per hand*, excluding speed
  modifiers and One Saber, which genuinely change the geometry, and dropping chain links,
  which do not sit where their grid position claims.
- **Take the cut distance from the replay, not from the reconstruction.** Only which side of
  the plane the note sits on needs rebuilding, and a side is a sign — robust to the
  centimetre of error the reconstruction carries. Using our own magnitude cost a median of
  10–12 mm a cut against effects a few times that size.
- **Thresholds from the noise, not taste.** ≥20 qualifying runs since the last change,
  ≤1–2° per step.

## Building

Game assemblies come from a version-pinned reference set, never a live install. The default
paths point at the `BSModUpdater` tree on the development machine; override them in a
gitignored `Directory.Build.user.props`:

```xml
<Project>
  <PropertyGroup>
    <BSRefRoot>D:\wherever\refs\</BSRefRoot>
    <BSModdedInstallDir>D:\wherever\1.45.0-modded\</BSModdedInstallDir>
  </PropertyGroup>
</Project>
```

```bash
dotnet build -c Release src/ReplayFit/ReplayFit.csproj
```

The manifest is an embedded resource; BSIPA finds plugin metadata that way and skips the
plugin in silence if it is missing. An incremental build does not re-embed it — use
`-t:Rebuild` after editing `manifest.json`.
