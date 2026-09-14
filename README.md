# Open Hand

An always-empty hand option for the discerning adventurer.

Adds a virtual, always-empty main-hand selection to Vintage Story. It is not an inventory slot: it cannot be filled, moved, saved, crafted into, or targeted by inventory automation. Install on both the client and the server — grab the latest release from the [releases page](https://github.com/MatthewChastain/open-hand/releases/latest).

The client adds a wheel entry and hotkey, an optional HUD indicator that blends into the hotbar, hotbar centering with automatic fallbacks, optional slot-key double-tap entry, an optional empty-offhand toggle, and an in-game settings menu (Ctrl+tilde). Both the selection and the empty-offhand toggle persist per player across relogs and server restarts.

## Supported versions

Built and tested against Vintage Story **1.22.7**, and the packaged mod declares the 1.22 line as a dependency (minimum 1.22.0). The Harmony patches target internal APIs verified against the decompiled 1.22.7 assemblies; the game API is stable across 1.22.x revisions, so any 1.22 release should work. Newer game versions may not work until the mod is re-verified — each release's notes state the supported game version.

## Controls

- Tilde (rebindable under Settings → Controls → Movement & character controls as **Select Open Hand**) selects Open Hand. Press it again to jump back to the slot you had selected before entering it.
- Ctrl + tilde opens the Open Hand settings menu without changing your hand selection. Rebind it in the same controls category as **Open Hand Settings**. Its clearly separated Main hand, Offhand, Hotbar appearance, and Keybind sections apply and save every change immediately.
- **Enable Open Hand** controls all main-hand entry paths (hotkey, wheel, indicator click, and slot-key double-tap). It is on by default; switching it off hides the main-hand indicator and drops an active selection when doing so is safe, without changing the separate **Show indicator** preference.
- With the **empty offhand** switch enabled, Shift + tilde (rebindable as **Toggle empty offhand**) substitutes an empty offhand: everything — engine and mods alike — reads the offhand as empty while the real item stays parked in its slot, untouched. Press it again to hand the offhand back. The state is server-validated and persists across relogs and server restarts; the switch itself lives in the settings menu and dropping it also drops any live substitution.
- While carrying a block with CarryOn, the offhand toggle locks too: the carried block occupies the hands, so neither hand's substitution can flip until it is placed or dropped.
- With the indicator hidden, scrolling skips Open Hand; use the **Select Open Hand** hotkey to activate it. You can still scroll out of Open Hand afterward. Showing the indicator restores wheel entry immediately.
- With the indicator shown, the wheel ring runs `1` through `0`, then Open Hand, then back to `1`. Scroll down from the `0` slot — or from an occupied skill slot — or scroll up from the `1` slot to enter Open Hand.
- Scroll once more to leave: down selects the `1` slot, up selects the `0` slot, or the skill slot while it holds an item.
- Any number key or hotbar click leaves Open Hand.
- With **slot key double-tap** enabled (off by default), pressing the number key of the already-active slot selects Open Hand — and while Open Hand is selected, that same key returns to the slot even though vanilla sees no slot change.
- Wheel scrolling works normally in dialogs and vanilla backpack mode.
- Clicking the indicator cell toggles Open Hand, just like the hotkey. If you are holding an item stack on the mouse cursor, the click is swallowed instead — the stack is never dropped by clicking the indicator.
- While carrying a block with CarryOn, the selection locks in both directions: scrolling, number keys, and toggling can neither enter nor leave Open Hand — and the empty-offhand toggle can't flip either — until the block is placed or dropped. Place the block first — then everything behaves normally.

While Open Hand is selected the engine resolves the main hand as empty. The ten physical hotbar slots are never touched, and neither is the real offhand item while the empty-offhand toggle is active.

## Chat commands

- `/openhand status` — diagnostics: selection state, remembered slot, server revision, empty-offhand state, CarryOn interop and any live hands-carry, applied/failed patches, indicator placement, centering shift and fallback reason, and any other mods patching the same targets.
- `.openhand indicator on|off|toggle` — the saved indicator visibility (use a period, not a slash).
- `.openhand center on|off|toggle` — the saved hotbar-centering preference.
- `.openhand doubletap on|off|toggle` — the saved slot-key double-tap preference.
- `.openhand mainhand on|off|toggle` — the saved main-hand feature switch; turning it off prevents entry and drops an active selection when safe.
- `.openhand offhand on|off|toggle` — the saved empty-offhand switch; turning it off also drops any live substitution.

All of these settings are also in the settings menu, which applies and saves every change immediately.

## Hotbar centering

Centering is **on by default**. Disable it with the **Center hotbar** switch in the settings menu (Ctrl+tilde) or `.openhand center off`; both write the saved `CenterHotbar` preference in `openhand.json`.

When the indicator is visible and `IconAnchor` is `auto`, compatible hotbars are centered together with the Open Hand extension. Slots and their click targets move together; the skill icon follows the slots, while the temporal gear, its hover target, and item-name text stay screen-centered. Hiding the indicator restores the original hotbar position without clearing the centering preference.

`.openhand status` reports the actual shift and why centering is inactive. Unsupported layouts, unavailable render hooks, overlapping independent HUD cells, or conflicting position changes fall back to uncentered placement. After another mod changes an owned offset, toggle centering off/on to retry. Compatibility with arbitrary custom renderers is not guaranteed; use `.openhand center off` if another mod's graphics do not follow the bar.

For mod authors: renderers attached to the existing bounds tree already inherit the shift. Independently drawn, hotbar-attached graphics can read `OpenHandModSystem.HotbarCenteringOffsetX` during client rendering and add it to otherwise unshifted coordinates. Do not add it to bounds-derived coordinates or to the skill-render coordinates supplied by vanilla: those are already adjusted.

## Configuration

All client settings live in `openhand.json` under the game's `ModConfig` folder and can be edited from the settings menu. The file is created with defaults on first launch.

- `IconAnchor` — where the indicator cell attaches: `auto` (a compatible external panel left of the hotbar), `offhandGap` (the classic but vanilla-reserved position), `left`, or `right` of the row.
- `IconOffsetX` / `IconOffsetY` — final pixel nudges applied after the anchor resolves (settings menu steppers clamp to ±100).
- `ShowIndicator` — whether the HUD panel, hand cell, and selection outline render while the main-hand feature is enabled. When disabled, entry is hotkey-only; wheel exit and server synchronization are unchanged.
- `MainHandEnabled` — enables the main-hand Open Hand feature. On by default; when disabled, its indicator is suppressed, hotkey/wheel/indicator-click/double-tap entry are unavailable, and a live selection is dropped when safe. Disabling it does not change `ShowIndicator`, so an enabled indicator returns immediately when this feature is re-enabled.
- `ShowOffhandIndicator` — whether the empty-offhand feedback (the ghosted offhand cell and its full-slot highlight) renders while the substitution is active. Independent of `ShowIndicator`; hiding either leaves the toggles themselves fully functional.
- `EmptyOffhandEnabled` — enables the empty-offhand toggle hotkey (Shift + tilde by default). Off by default; disabling the switch also drops any live substitution.
- `CenterHotbar` — centering described above, on by default; unsupported layouts remain uncentered.
- `DoubleTapHotbarKey` — off by default. When enabled, pressing the number key of the already-active hotbar slot selects Open Hand; the press is resolved against vanilla's own `hotbarslot` bindings, so rebinds are honored.

## Branching & releases

- `main` is the stable release branch and the default on GitHub. Changes land here only via pull request from `develop`, gated on the CI `state-tests` check.
- `develop` is the main working branch; direct pushes are allowed.
- To release: bump `version` in `src/OpenHand/modinfo.json` (and the csproj), merge `develop` into `main`, tag `v<version>`, then build locally with `scripts/package.py` and attach the zip to the GitHub release. The Release workflow verifies the tag matches the modinfo version.
- Branch protection is enforced: pull requests into `main` with a green `state-tests` check, no force pushes or deletions on either branch. Re-apply or adjust with `bash scripts/setup-branch-protection.sh`.

## Build

Building requires a local Vintage Story 1.22.x installation — the project compiles against the game's own DLLs. Point the build at your install with `VINTAGE_STORY` (or an ignored `Local.props` that sets `VintageStoryPath`).

```bash
dotnet build OpenHand.sln -c Release
dotnet run --project tests/OpenHand.StateTests/OpenHand.StateTests.csproj -c Release
python3 scripts/package.py
```

The release archive is written to `artifacts/openhand_<version>.zip`, where `<version>` is read from `src/OpenHand/modinfo.json`.

## Manual validation

Before relying on the mod in a save, test a fully populated hotbar and offhand in single-player and multiplayer. Confirm that Open Hand shows the empty marker, does not alter any item stack, allows normal empty-hand interactions, and remains clean after returning to the main menu and entering another world in the same game process.

For the settings menu, open it with Ctrl+tilde in a world, toggle all the switches, change the anchor dropdown, nudge and reset the offsets, and confirm the values survive closing and reopening the menu (and the game). With double-tap enabled, also confirm a number press for a different slot behaves exactly like vanilla and that the double-tap does not fire while the inventory holds a hovered slot or while typing in chat.

For persistence, toggle Open Hand and the empty offhand on, leave to the main menu, and rejoin: both should come back. `/openhand status` should report the restored state on both sides, and a relog with the empty-offhand switch off should drop the restored substitution.

For centering, also test center/indicator toggles, slot clicking and hover targets, the occupied skill slot and its highlight, temporal-gear hover, window resizing, GUI-scale changes, texture reloads, and world transitions. Test other hotbar mods separately before claiming compatibility.

The optional local API/render regression suite requires the game installation and is intentionally outside CI: `dotnet run --project tests/OpenHand.RenderingTests/OpenHand.RenderingTests.csproj -c Release`. It covers pixel composition, input registration, actual bounds/hitboxes, and guarded transpiler matching/compilation against the installed game.

## Compatibility

Do not run alongside Forever Empty; both mods modify selected-hand behavior, and Open Hand warns about the conflict on startup. Overhaul lib legacy compat works as of 1.0.0: the substituted hand slot now satisfies vanilla slot contracts (`Inventory` is always populated), which that mod's per-tick hand checks rely on. CarryOn works as of 1.0.2: the substituted hand slot is a real member of a mod-owned inventory, satisfying CarryOn's slot identity check, so picking up containers while Open Hand is selected no longer crashes. As of 1.0.3, Open Hand also repositions CarryOn's carried-item icons: CarryOn hardcodes their positions relative to an assumed vanilla hotbar, which draws them on top of the indicator cell — Open Hand re-derives them from the real hotbar so they clear the cell and follow centering. As of 1.0.4, your selection locks onto Open Hand while you are carrying something with CarryOn — scrolling, number keys, and toggling cannot leave a carried block stranded on a slot that cannot place it. As of 1.0.6, the lock is fully symmetric (no entry, no exit, and the empty-offhand toggle cannot flip either) because CarryOn wraps both hand slots for the whole carry, and both CarryOn 1.14.x and the 2.x reorganization (CarryOnLib) are supported. The carried-item HUD integration and every locked toggle report what they did to the logs, so problems are diagnosable from `client-main.log`/`server-main.log` without a debugger. Open Hand additionally clears CarryOn's temporary placement bookkeeping from its substituted hand slot after every place-down, so a carried chest can no longer be placed twice. If a carried stack was saved corrupt by an older interaction (it places as an unknown "?" block), drop the stuck carry with Q and pick the item up fresh. Mods that cache or alter `ActiveHotbarSlot` directly may still need compatibility work — open an issue with a minimal reproduction and your Vintage Story version.

## Contributing

Bug reports and feature requests go to [GitHub issues](https://github.com/MatthewChastain/open-hand/issues) — use the templates and include your Vintage Story version, Open Hand version, and `/openhand status` output where relevant.

Code changes go through pull requests into `develop`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full workflow: build and test locally, target `develop`, and make sure the `state-tests` check is green.

## License

[MIT](LICENSE). By contributing you agree that your contributions are licensed under it.
