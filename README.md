# ControllerAutoAdjust

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

**Milestone 2 — live cut capture.** Next, in order: live cut capture → accumulate with the active offsets stamped → grid search →
a settings panel that suggests rather than silently applies.

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
  honest sample. The filters that matter are a minimum cut count and excluding speed
  modifiers and One Saber, which genuinely change the geometry.
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
dotnet build -c Release src/ControllerAutoAdjust/ControllerAutoAdjust.csproj
```

The manifest is an embedded resource; BSIPA finds plugin metadata that way and skips the
plugin in silence if it is missing. An incremental build does not re-embed it — use
`-t:Rebuild` after editing `manifest.json`.
