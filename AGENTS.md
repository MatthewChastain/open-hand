# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this is

Open Hand is a code mod for Vintage Story 1.22.x that adds a virtual, always-empty
main-hand selection to the hotbar. It is **not** an inventory slot: it never stores,
moves, or mutates item stacks, and the physical hotbar and offhand are untouched.
The mod is universal (client + server) with server-authoritative selection sync.

## Commands

Building requires a local Vintage Story installation — the project compiles against
the game's own DLLs (`VintagestoryAPI.dll`, `0Harmony.dll`, `cairo-sharp.dll`,
`protobuf-net.dll`). Proprietary game DLLs must never be committed to this repo.
Point the build at your install with either:

- the `VINTAGE_STORY` environment variable, or
- a gitignored `Local.props` that sets `<VintageStoryPath>`

```bash
dotnet build OpenHand.sln -c Release
dotnet run --project tests/OpenHand.StateTests/OpenHand.StateTests.csproj -c Release
python3 scripts/package.py   # writes artifacts/openhand_<version>.zip
```

CI runs the state tests and modinfo JSON validation on GitHub-hosted runners
**without** the game installed, so the state-test project must stay buildable
without Vintage Story DLLs.

`Directory.Build.props` sets `TreatWarningsAsErrors` — code must compile
warning-free under `net10.0` with nullable enabled.

## Layout

- `src/OpenHand/OpenHandModSystem.cs` — mod entry point: applies Harmony patches, registers the `/openhand status` command
- `src/OpenHand/Common/` — shared runtime state (`OpenHandRuntime`, wheel-ring order)
- `src/OpenHand/Client/` — hotkey registration, wheel input, HUD icon rendering
- `src/OpenHand/Server/` — server authority and selection broadcast
- `src/OpenHand/Patches/` — the only two Harmony patches in the mod
- `src/OpenHand/modinfo.json` — the authoritative mod manifest (see Packaging)
- `assets/` — assets shipped in the mod zip (HUD texture, mod icon)
- `assets-src/` — design sources, fully tracked on purpose
- `tests/OpenHand.StateTests/` — state tests (required check)
- `scripts/package.py` — deterministic release zip packaging
- `scripts/setup-branch-protection.sh` — re-applies GitHub branch protection

## Architecture invariants

These are load-bearing design decisions. Do not weaken them without discussion.

- **Never mutate inventories.** The empty main hand is implemented by patching the
  `PlayerInventoryManager.ActiveHotbarSlot` property getter
  (`src/OpenHand/Patches/ActiveHandPatch.cs`), not by adding or editing slots.
  Item stacks must remain untouched in every code path.
- **Only two patch targets exist**: the `ActiveHotbarSlot` getter and
  `HudHotbar.OnRenderGUI` (plus reading its private `hotbarSlotGrid` field) in
  `src/OpenHand/Patches/HudHotbarPatch.cs`. Patches resolve private members via
  `AccessTools` reflection, and `TargetMethod()` deliberately returns `null`
  (patch silently no-ops, logged) instead of throwing when a target is missing —
  the mod degrades gracefully rather than crashing. Keep that behavior.
- **Patch targets are verified against decompiled 1.22.7 assemblies.** Changes to
  patch targets or game-version assumptions must include decompile evidence in the PR.
- **Same-value slot assignment is a no-op in vanilla.** Setting
  `ActiveHotbarSlotNumber` to its current value fires no events. Any feature that
  changes selection and needs UI updates (e.g. highlight restore on wheel exit) must
  handle the no-change case explicitly — this caused a real bug before.
- **HUD icon positioning is pixel-snapped** to vanilla integer slot coordinates
  (unscaled slot size 48, padding 3) and derived from HudHotbar internals at render
  time so it stays correct across GUI scales and resolutions. Icon textures are
  baked at the scaled size with bilinear filtering; watch RGBA channel order when
  manipulating bitmaps.
- **The server is authoritative.** Selection state is validated server-side and
  broadcast; the client never trusts its own selection in multiplayer.

## Compatibility policy

- Supported game versions: the whole 1.22.x line (dependency floor `1.22.0` in
  `modinfo.json`). The Vintage Story API is stable across 1.22.x revisions.
- Building the game itself requires the .NET 10 SDK (game requirement since 1.22).
- Known conflict: Forever Empty (both mods modify selected-hand behavior; Open Hand
  warns on startup). Mods that cache or alter `ActiveHotbarSlot` may also conflict.

## Packaging

- `src/OpenHand/modinfo.json` is the authoritative manifest. Never package the
  build-output copy — `dotnet build` does not recopy it when only the source file
  changes, which is how stale metadata once shipped inside a release zip.
  `scripts/package.py` reads the source manifest for exactly this reason.
- The release zip ships only the DLL, PDB, modinfo, mod icon, and game assets.
  Never ship `.cs` sources: the game runtime-compiles any `.cs` files found in a
  mod folder without Harmony or full BCL references, which breaks the mod.
- Zip timestamps are fixed for deterministic output.
- `networkVersion` in modinfo.json is the **game's network protocol version** in
game-version format (e.g. `1.22.0`), not the mod's own protocol counter. The Mod DB
rejects other formats ("The NetworkVersion of this mod ... is malformed").

## Branching & releases

- `main` is the stable release branch and the default on GitHub. Changes land only
  via pull request from `develop`, gated on the `state-tests` check. No force
  pushes or deletions on either branch.
- `develop` is the main working branch.
- To release: bump `version` in **both** `src/OpenHand/modinfo.json` and
  `src/OpenHand/OpenHand.csproj`, merge `develop` into `main`, tag `v<version>`,
  build locally with `scripts/package.py`, and attach the zip to the GitHub release.
  The Release workflow fails if the tag does not match the modinfo version.

## Validation expectations

- State tests must pass before any merge.
- Changes to player-facing behavior must be validated in-game (single-player at
  minimum; both GUI scales if HUD rendering changed). Note what you checked in the
  PR's Testing section.
- `/openhand status` prints selection state, remembered slot, server revision, and
  patch status — include its output in bug reports and use it to verify patch
  application after upgrading the game version.
