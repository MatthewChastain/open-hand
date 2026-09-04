# Open Hand

An always-empty hand option for the discerning adventurer.

Adds a virtual, always-empty main-hand selection to Vintage Story 1.22.7. It is not an inventory slot: it cannot be filled, moved, saved, crafted into, or targeted by inventory automation. Current release: [v0.2.0](https://github.com/MatthewChastain/open-hand/releases/latest) — install on both the client and the server.

## Controls

- Tilde (rebindable under Settings → Controls → Movement & character controls as **Select Open Hand**) selects Open Hand. Press it again to jump back to the slot you had selected before entering it.
- The wheel ring runs `1` through `0`, then Open Hand, then back to `1`. Scroll down from the `0` slot — or from an occupied skill slot — or scroll up from the `1` slot to enter Open Hand.
- Scroll once more to leave: down selects the `1` slot, up selects the `0` slot, or the skill slot while it holds an item.
- Any number key or hotbar click leaves Open Hand.
- Wheel scrolling works normally in dialogs and vanilla backpack mode.
- `/openhand status` prints diagnostics: selection state, remembered slot, server revision, patch status.

While Open Hand is selected the engine resolves the main hand as empty. The ten physical hotbar slots and the offhand are never touched.

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

Do not run alongside Forever Empty; both mods modify selected-hand behavior, and Open Hand warns about the conflict on startup. Mods that cache or alter `ActiveHotbarSlot` directly may need compatibility work — open an issue with a minimal reproduction and your Vintage Story version.
