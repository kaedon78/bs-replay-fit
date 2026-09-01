# Releasing

Tag and branch naming, shared across the Beat Saber mods in this folder.

## Tags

```
v<MAJOR>.<MINOR>.<PATCH>-bs<GAME VERSION>
```

Examples: `v1.0.0-bs1.45.0`, `v0.3.0-bs1.40.5`.

Both halves are load-bearing. A Beat Saber mod is built against one game version and will not load
against another, so a tag naming only the mod version does not identify a downloadable artifact —
and the same mod version is often released for several game versions. **One tag per (mod version,
game version) pair.**

Rules:

* `v` prefix, then a plain semantic version. No `V`, no bare `1.0.0`.
* `-bs` then the game version exactly as the game reports it: `1.45.0`, `1.40.5`. Lower case, no
  underscore, no `v`.
* **Annotated**, never lightweight (`git tag -a`). The message is the release notes, so the tag
  stands on its own if the GitHub release is ever lost.
* Never reused once others could have pulled it. Re-cutting is only reasonable while a release is
  private and minutes old.

## Branches

Two shapes, chosen by whether the mod needs different code per game version:

* **One default branch** when a single build serves every supported version, with the game version
  carried by the tag alone. `bs-faster-covers` and `bs-replay-fit` work this way.
* **A `BS_<game version>` branch per version** when the code diverges — `BS_1.44.3`, `BS_1.45.0`.
  The release targets that branch. `JDFixer` and this repo work this way.

`BS_1.45.0` keeps the game's own dotted form and matches the existing branches; note it is
upper-case with an underscore, where the tag suffix is lower-case `bs1.45.0`. That asymmetry is
inherited rather than designed, and is kept because both forms are already in use.

## Forks

A fork keeps upstream's tag numbering, so its releases will not match the scheme above — JDFixer's
`v8.5.1` follows the original author's versioning. Carry the game version in the branch there.
Do not renumber a fork to fit this convention.

## Cutting a release

1. Bump the version in `manifest.json` **and** the `<Version>` in the csproj, and commit.
2. Build Release and confirm the embedded manifest: id, version and gameVersion should all match.
3. Push the branch, tag it annotated, push the tag.
4. Create the release against the tag, attaching the built `.dll`.
5. Download the published asset and hash it against the local build. Replacing an asset is exactly
   the step that can silently leave the old file in place.

## README structure

The same skeleton in each mod, so a reader who knows one knows the others:

1. **Title**, one-line description, supported game version
2. *(mod-specific explanation, where the mod needs one)*
3. **Requirements** -- game version, BSIPA, any mod dependencies
4. **Installing** -- where the DLL goes
5. **Settings**, and any mod-specific usage sections
6. **Known limits** -- *optional.* Only what the mod's own logic cannot do. Omit the section
   entirely when there is nothing of that kind; limitations of the game itself are not the mod's
   known limits
7. **Building** -- always states that no game binary is committed and why
8. **Releasing** -- links this file
9. **License**

Three rules that are easy to drift back into:

* **State the supported game version once.** If a badge carries it, do not repeat it in prose; if
  there is no badge, it is a line in Requirements.
* **Requirements lists what is needed, not what is not.** "No FinalIK, no asset bundles" tells a
  reader nothing they can act on.
* **Known limits are limits that are true**, verified in this mod. Not things merely untested, and
  not problems borrowed from another project.

American spelling for the heading, matching the LICENSE filename. All three carry the same MIT
LICENSE, byte for byte.

## Applied here

One `master` branch, with the game version carried entirely by the tag. Two mod versions have each
been released for both supported game versions, which is the case the tag scheme exists for: without
the `-bs` suffix, `v0.3.0` alone would not say which artifact you were downloading.

| version | game | tag |
|---|---|---|
| 0.2.0 | 1.40.5 | `v0.2.0-bs1.40.5` on `master` |
| 0.2.0 | 1.45.0 | `v0.2.0-bs1.45.0` on `master` |
| 0.3.0 | 1.40.5 | `v0.3.0-bs1.40.5` on `master` |
| 0.3.0 | 1.45.0 | `v0.3.0-bs1.45.0` on `master` |
