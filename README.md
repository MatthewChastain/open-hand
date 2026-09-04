# Open Hand

Open Hand adds a virtual, always-empty main-hand selection to Vintage Story 1.22.7. It is not an inventory slot: it cannot be filled, moved, saved, crafted into, or targeted by inventory automation.

## Status

This is an early development build. Install it on both the client and server. Do not run it alongside Forever Empty; both mods modify selected-hand behavior.

## Controls

- Press `` ` `` (rebindable under Character Controls as **Select Open Hand**) to select Open Hand. Press it again to jump straight back to the slot you had selected before entering Open Hand, making the hotkey a quick toggle.
- The mouse wheel ring is `1..0`, then Open Hand, then back to `1`. Scroll down from the `0` key slot (or from an occupied skill slot), or scroll up from the `1` key slot, to enter Open Hand.
- Scroll again to leave it: scrolling down selects the `1` key slot; scrolling up selects the `0` key slot, or the skill slot while it is occupied.
- Pressing any number key or selecting any physical hotbar slot leaves Open Hand.
- Wheel scrolling keeps working normally in dialogs and vanilla backpack mode (raw key held).
- Run `/openhand status` in chat for diagnostics: selection, remembered slot, server revision, and patch status.

Open Hand substitutes an empty `DummySlot` only when the engine resolves the active hand. The ten physical hotbar slots and offhand stay intact.

## Branching & releases

- `main` is the stable release branch and the default on GitHub. Changes land here only via pull request from `develop`, gated on the CI `state-tests` check.
- `develop` is the main working branch; direct pushes are allowed.
- To release: bump `version` in `src/OpenHand/modinfo.json` (and the csproj), merge `develop` into `main`, then tag `v<version>` and push the tag. The Release workflow builds and attaches the zip to the GitHub release automatically.
- After switching the repository to public, run `bash scripts/setup-branch-protection.sh` once to enforce the branch rules (GitHub requires the repo to be public, or Pro, for branch protection).

## Build

The project targets the exact Vintage Story 1.22.7 API. Set `VINTAGE_STORY` or create an ignored `Local.props` that sets `VintageStoryPath` to the game installation directory.

```bash
dotnet build OpenHand.sln -c Release
dotnet run --project tests/OpenHand.StateTests/OpenHand.StateTests.csproj -c Release
python3 scripts/package.py
```

The release archive is written to `artifacts/openhand_<version>.zip`, where `<version>` is read from `src/OpenHand/modinfo.json`.

## Manual validation

Before relying on the mod in a save, test a fully populated hotbar and offhand in single-player and multiplayer. Confirm that Open Hand shows the empty marker, does not alter any item stack, allows normal empty-hand interactions, and remains clean after returning to the main menu and entering another world in the same game process.

## Compatibility

Open Hand detects and warns about Forever Empty. Remove Forever Empty before using Open Hand. Mods that directly cache or alter `ActiveHotbarSlot` may need compatibility work; please report a minimal reproduction with Vintage Story and mod versions.
