# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this is

Open Hand is a code mod for Vintage Story 1.22.x that adds a virtual, always-empty
main-hand selection to the hotbar, plus an optional hotkey-toggled empty-offhand
mode. They are **not** inventory slots: they never store, move, or mutate item
stacks — the physical hotbar is untouched, and while the offhand toggle is
active the real item stays parked in its slot. The mod is universal (client +
server) with server-authoritative selection sync.

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

Verifying game internals: decompile with `~/.dotnet/tools/ilspycmd -t <full type
name>` against the install in `Local.props`. `VintagestoryLib.dll` holds the
client internals (`ClientMain`, `GuiManager`, `HudHotbar`, `HotkeyManager` —
note the `.NoObf` namespace); `VintagestoryAPI.dll` holds the public API
surface. Client internals and API behavior are stable across 1.22.x, but
re-verify anything version-sensitive after a game update.

## Layout

- `src/OpenHand/OpenHandModSystem.cs` — mod entry point: applies Harmony patches, registers the `/openhand status` command
- `src/OpenHand/Common/` — shared runtime state (`OpenHandRuntime`), pure decision
  logic (`OpenHandWheelRing`, `OpenHandDoubleTap`), and the client config
- `src/OpenHand/Client/` — hotkey registration, wheel input, HUD icon rendering, in-game settings dialog
- `src/OpenHand/Server/` — server authority and selection broadcast
- `src/OpenHand/Patches/` — the mod's Harmony patches: the vanilla-target
  patches below (main hand and offhand), plus the optional CarryOn HUD patch
- `src/OpenHand/modinfo.json` — the authoritative mod manifest (see Packaging)
- `assets/` — assets shipped in the mod zip (HUD texture, mod icon)
- `assets-src/` — design sources, fully tracked on purpose
- `tests/OpenHand.StateTests/` — CI state tests (required check), one suite file
  per module under a zero-framework console runner (`Program.cs` registers the
  suites; a failing suite records its exception and the rest still run). Links
  individual `Common/` sources via its csproj `<Compile>` list: every new
  `Common/` file must be added there or the test project fails to build (CI
  catches it). Includes `ProtocolTests` (protobuf wire bytes + round trips
  against the NuGet `protobuf-net`) and the `openhand.json` config round trip.
- `tests/OpenHand.RenderingTests/` — local-only, game-DLL-backed tests (outside
  the solution; not run by CI): continuous-background pixel comparisons,
  substituted-slot inventory contracts, server request validation (revision
  gating, carry lock, join/leave, toggle persistence across relogs), the
  patch-target tripwire, CarryOn interop, hotkey binding priority/persistence
  targets, and the conflict scanner. Run:
  `dotnet run --project tests/OpenHand.RenderingTests -c Release` (see Testing).
- `scripts/package.py` — deterministic release zip packaging
- `scripts/setup-branch-protection.sh` — re-applies GitHub branch protection

## Architecture invariants

These are load-bearing design decisions. Do not weaken them without discussion.

- **Never mutate inventories.** The empty main hand is implemented by patching the
  `PlayerInventoryManager.ActiveHotbarSlot` property getter
  (`src/OpenHand/Patches/ActiveHandPatch.cs`), not by adding or editing slots.
  Item stacks must remain untouched in every code path.
- **The substituted slot satisfies vanilla slot contracts.** While selected,
  `ActiveHotbarSlot` returns a shared empty slot that is a real member
  (index 0) of a mod-owned one-slot `DummyInventory` (`Inventory` non-null;
  `GetSlotId` returns 0). Third-party mods dereference `slot.Inventory` every
  tick (Overhaul lib legacy compat crashed on a null inventory there), and
  CarryOn's `LockedItemSlot` constructor searches `slot.Inventory` by
  reference identity and throws when the slot is not a member — the 1.0.1
  build attached the player's hotbar inventory without membership and
  crashed on chest pick-up. Do not re-point the slot at the player's own
  inventories or hand it out unattached. Do not hand out the current index-0
  occupant either: CarryOn's pick-up replaces the occupant with a
  `LockedItemSlot` wrapper and stacks leak through that wrapper into engine
  item-move paths, duplicating items — tried and reverted.
- **The main-hand substitution patches only two vanilla targets**: the
  `ActiveHotbarSlot` getter and `HudHotbar.OnRenderGUI` (plus reading its
  private `hotbarSlotGrid` field) in `src/OpenHand/Patches/HudHotbarPatch.cs`.
  Patches resolve private members via `AccessTools` reflection, and
  `TargetMethod()` deliberately returns `null` (patch silently no-ops, logged)
  instead of throwing when a target is missing — the mod degrades gracefully
  rather than crashing. Keep that behavior.
- **The empty-offhand toggle patches exactly two offhand read paths** (in
  `src/OpenHand/Patches/OffhandSlotPatches.cs`): the
  `PlayerInventoryManager.OffhandHotbarSlot` getter (base class — neither
  `ClientPlayerInventoryManager` nor `ServerPlayerInventoryManager` overrides
  it, so one patch covers both sides) and the `EntityPlayer.LeftHandItemSlot`
  override (it re-fetches `GetHotbarInventory()[11]` per call, so the override
  — never the base `EntityAgent` property — is the target). Verified against
  decompiled 1.22.7; re-verify on game updates. Substituting only one of the
  two paths leaks the "empty" lie into whichever system takes the other, and
  the substituted slot must satisfy the same slot-contract invariant as the
  main hand. While the toggle is active every mod reading the offhand sees it
  empty — that is the feature's purpose and is deliberate; the real item
  stays parked and untouched.
- **Third-party compatibility patches are allowed, but only as a last
  resort.** Try the simpler tools first — public APIs, engine events,
  reflection reads, or the other mod's own configuration — and patch another
  mod's internals only when the interaction cannot be handled any other way.
  Every compatibility patch must stay optional and degrade to a no-op:
  `TargetMethod()` returns null when the mod is absent, renamed internals
  pass original behavior through untouched, and each target is verified
  against decompiled assemblies of the mod's shipping version (include the
  evidence in the PR; re-verify on mod updates). Document every target here.
  Current target: `CarryOnHudPatch` postfixes CarryOn's private
  `GetPositionForAnchor` so carried-item icons clear the indicator cell and
  follow the real hotbar (CarryOn hardcodes a vanilla-centered 850px bar).
  Verified against decompiled CarryOn 1.14.3
  (`CarryOn.Client.HudCarried+HudCarriedRenderer.GetPositionForAnchor`) and
  2.0.0-pre.8 (top-level `CarryOn.Client.Logic.HudCarriedRenderer`, same
  method/field names, same scaled(32)/scaled(16) geometry and hardcoded 850px
  bar in `UpdateCachedPositions`); re-verify on CarryOn updates. Every guarded
  path logs once per session, and repositionings log on first placement and
  again whenever the correction inputs change (a carried-item pickup recomposes
  the hotbar and can move the bar), so a "the icons didn't move" report is
  diagnosable from `client-main.log` without a debugger.
  A second guarded target, `CarryOnRenderOrderPatch`, postfixes
  `HudCarriedRenderer.RenderOrder` to at least 1.01: CarryOn registers at 1.0
  — the same order as `GuiManager`'s Ortho GUI pass — and
  `ClientEventManager.RegisterRenderer` inserts a new renderer BEFORE the
  first entry whose order is not strictly smaller, so carried icons render
  UNDER every dialog. Vanilla bars never expose this (icons sit outside the
  bar), but Open Hand's left extension grows the bar into the icon zone and
  then paints over the icons (reported as "the hotbar is overlapping the
  CarryOn icons"). 1.01 keeps them below the 1.02 crosshair/cursor. Also:
  the anchor correction keeps the last rendered hotbar geometry and applies
  it on frames where the hotbar dialog has not published (world-join
  ordering, HUD transitions), instead of snapping back to CarryOn's
  overlapping defaults; the cache clears on world exit
  (`CarryOnHudPatch.ResetLastGeometry`). Verified against decompiled CarryOn
  2.0.0-pre.8 (`HudCarriedRenderer.RenderOrder => 1.0`, registration in
  `HudCarried` at stage Ortho) and the 1.22.7 client event manager; re-verify
  on updates.
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
broadcast; the client never trusts its own selection in multiplayer. Both the
selection and the empty-offhand toggle persist per player: every server-side
mutation writes through to the player's entity WatchedAttributes (the same
mechanism CarryOn uses for carries) and the join handler restores the saved
state into the runtime before the snapshot replay — so both toggles survive
relogs and server restarts. A persisted offhand state that arrives while the
client's `EmptyOffhandEnabled` switch is off is dropped at once (the drop
re-persists the cleared state).
- **Runtime state is partitioned per API side.** Single-player runs the client
and server in one process, and the client applies toggle requests
optimistically before the server confirms. A shared runtime dictionary let
the client's optimistic write satisfy the server's own stale-revision gate:
the server read the client's just-written revision as its own, rejected every
request as stale, and the persistence write never ran — the 1.0.6 bug that
kept the toggles from surviving a relog (invisible in-session because the
optimistic state drives the visuals; diagnosable only from the
`received a … request at revision N while holding revision N — stale` server
line). `OpenHandRuntime` keys state by (API side, player UID), and the server
join handler re-seeds the server partition from the persisted truth. Keep the
client's optimistic writes on the client side of that partition.
- **The server's join replay arrives before the client's local player exists.**
Decompiled 1.22.7: `HandleRequestJoin` fires `PlayerJoin` between
`LevelInitialize` and `LevelFinalize`, so replay packets hit the client while
the world is still loading and `capi.World.Player` is still null — a client
handler that ignores updates while the player is null silently DROPS the
restored state (the restored selection stayed invisible and the next toggle
bounced off the server's restored revision as stale). The client buffers such
updates and applies them on the first tick after the player exists, and sends
one revision-0 refresh pair at ready — the server's stale-revision branch
answers those with the authoritative state, healing any other missed
broadcast. See `OpenHandClientController.ApplyJoinReplay`.
- **Centering is reversible and on by default.** It adds owned layout offsets, not
  render-only shifts. Gear hover and item-name bounds counter-offset the root.
  A guarded transpiler on the existing `HudHotbar.OnRenderGUI` target prepares
  after vanilla rebuilds and adjusts only the skill renderer's X argument.
  If either hook is unavailable, centering stays off. Preserve foreign bounds
  writes and yield rather than repeatedly overriding another mod's layout.
  Vanilla recomposes the hotbar on every inventory change and rebuilds the
  composer's elements (fresh anchor bound objects, owned root offsets
  surviving): centering re-owns the fresh anchors and re-applies the shift —
  blocking there instead once permanently dropped the shift on the first
  carried-item pickup, visibly jumping the bar inward for the session.
- **Patch registration must be idempotent.** Client and server startup can share
  a process; registering the same Harmony patch twice duplicates draw calls.
- **Digit-key interception rides `capi.Event.KeyDown`, not hotkey registration.**
  Vanilla's `hotbarslot1-10` handlers return `true` and `HotkeyManager` stops at
  the first handler that does, so a mod hotkey bound to the same keys never fires.
  `KeyDown` reaches mod listeners *before* hotkey dispatch (verified against
  1.22.7 `ClientMain.OnKeyDown`); a listener must leave `args.Handled` untouched,
  apply its own capture-inputs dialog filter (the event fires even while chat
  captures input), and yield when a hovered slot would turn the press into an
  inventory swap. Resolve presses against the live `hotbarslot` bindings
  (`capi.Input.HotKeys`) so user rebinds are honored. While Open Hand is
  selected, `ActiveHotbarSlotNumber` still reports the remembered physical slot
  — so pressing that slot's digit is a vanilla same-value no-op (the
  `ClientPlayerInventoryManager.ActiveHotbarSlotNumber` setter returns before
  firing any events, decompiled 1.22.7), and the listener must perform the
  exit itself; the double-tap preference gates only the entry gesture, never
  the exit. See `OpenHandDoubleTap` + `OpenHandClientController.OnKeyDown`
  for the pattern.
- **Indicator click interception rides `capi.Event.MouseDown`, not the GUI.**
  `api.eventapi.TriggerMouseDown` fires before any client system or dialog
  sees the click (verified against 1.22.7 `ClientMain.UpdateMouseButtonState`),
  so `OpenHandClientController.OnMouseDown` can guard a cursor-held stack from
  `HudDropItem`, which drops stacks clicked outside every opened composer's
  root bounds — where the indicator cell is drawn. The handler must keep the
  guard order: skip when already handled, indicator hidden, mouse grabbed,
  capture-inputs dialogs open, outside the rect from
  `HudHotbarPatch.TryGetIndicatorRect`, or inside an open dialog's composer
  bounds. With an empty cursor it toggles Open Hand like the hotkey.
- **Keybind capture, persistence, and mouse dispatch follow vanilla's own
  paths** (all verified against decompiled 1.22.7; see
  `OpenHandSettingsDialog` + `OpenHand.Client.OpenHandHotkeyBinding`).
  Capture: keys ride `capi.Event.KeyDown` (fires before hotkey dispatch);
  mouse buttons ride the dialog's `CaptureRawMouse()` override — while it
  returns true, `ClientMain.OnMouseDownRaw` routes every raw click straight
  to the dialogs (via `GuiManager`) and skips the hotkey manager, which is
  the ONLY way buttons 4-8 reach a dialog (their clicks never fire
  `capi.Event.MouseDown`, because no vanilla hotkey routes them into
  `UpdateMouseButtonState` — the left/middle/right clicks dialogs see are
  re-dispatched by the `primarymouse`/`secondarymouse`/`middlemouse`
  handlers in `SystemHotkeys`). This is exactly how vanilla's own settings
  capture works (`GuiDialog.CaptureRawMouse` doc comment +
  `GuiDialogEscapeMenu` override). The click that starts a capture is
  dispatched before capturing begins, so no ignore-next-click dance is
  needed — the first press the override sees is the intended binding.
  Persistence: `HotKey.CurrentMapping` alone is session-only; remaps must
  also go through `ClientSettings.Inst.SetKeyMapping` (reflection —
  `Vintagestory.Client.NoObf.ClientSettings` lives in VintagestoryLib, which
  mods do not compile against). That is vanilla's own remap path
  (`GuiCompositeSettings.CompletedCapture`), it persists to
  `clientsettings.json`, and `HotkeyManager.RegisterHotKey` reads
  `ClientSettings.KeyMapping` back at registration. A missing reflection
  target degrades to session-only bindings (logged once).
  Dispatch: `HotkeyManager.TriggerHotKey` walks `HotKeys.ValuesOrdered` and
  stops at the first handler returning true, and vanilla registers its
  mouse consumers (plus `pickblock` on middle) before any mod hotkey — so a
  mouse-bound Open Hand hotkey is moved to the FRONT of `capi.Input.HotKeys`
  (that dictionary IS `hotkeyManager.HotKeys`, `InputAPI`; keyboard bindings
  move back to the end). Consequently every Open Hand hotkey handler must
  return **false** whenever it declines — mouse-bound and ungrabbed mouse,
  carrying locks, feature switch off, same-state no-ops — so declined
  clicks fall through to vanilla's consumers unchanged. A handler that
  returns true without acting would eat world clicks.

## Compatibility policy

- Supported game versions: the whole 1.22.x line (dependency floor `1.22.0` in
  `modinfo.json`). The Vintage Story API is stable across 1.22.x revisions.
- Building the game itself requires the .NET 10 SDK (game requirement since 1.22).
- Known conflict: Forever Empty (both mods modify selected-hand behavior; Open Hand
  warns on startup). Mods that cache or alter `ActiveHotbarSlot` may also conflict.
- The empty-offhand toggle (hotkey `openhand.offhand`, Shift+tilde by default)
  substitutes the offhand for as long as it is active: the real item stays
  parked and untouched, but everything — engine and mods alike — resolves the
  offhand to an empty slot, which is the feature's purpose and is
  balance-relevant (e.g. ranged reload checks that require an empty offhand
  succeed while it is active). State is server-validated and broadcast like
  the main-hand selection, persists per player across relogs and server
  restarts (entity WatchedAttributes, restored at join), and the offhand dummy
  is swept every tick like the main-hand slot. The feature is gated by the
  `EmptyOffhandEnabled` switch in the settings menu (persisted in
  `openhand.json`); the switch also drops any live substitution when turned
  off, and all three Open Hand keybinds are rebindable in the same menu —
  keys and all eight mouse buttons, persisted and dispatched like vanilla's
  own controls (see the keybind invariant above). The two HUD visuals are
  toggled independently in the same menu: `ShowIndicator` (main-hand cell,
  also gates wheel entry and centering) and `ShowOffhandIndicator` (the
  ghosted offhand cell + full-slot highlight); hiding either leaves the
  toggles themselves fully functional.
- CarryOn is supported: slot membership since 1.0.2 (no crash on container
  pick-up), and since 1.0.3 `CarryOnHudPatch` repositions its carried-item HUD
  anchors, which are hardcoded to a vanilla-centered 850px bar and otherwise
  collide with the indicator cell. A CarryOn update that renames its HUD
  internals disables only that correction (logged), never the rest of the mod.
  CarryOn 2.x reorganized its internals (renderer moved to the top-level
  `CarryOn.Client.Logic.HudCarriedRenderer`; carry state moved from a static
  extension to the `ICarryManager` instance on the new CarryOnLib library
  mod); Open Hand supports both the 1.14.x and 2.x layouts, preferring 2.x
  when present.
- While carrying, Open Hand locks in BOTH directions, for BOTH features: the
  selection cannot be entered or exited (scrolling is swallowed before vanilla
  slot cycling, digit keys pass through untouched — never set `Handled` in the
  KeyDown listener, it receives every key and swallowing strands movement and
  escape — the same-slot digit press does not exit, slot-change attempts do
  not deselect), and the empty-offhand toggle cannot be activated or dropped
  while the carry lasts (the hands-carry slot IS the offhand): the client
  gates every path (hotkey, wheel, digit double-tap, indicator click falls
  through by returning false) and the server rejects offhand activations —
  dropping the SUBSTITUTION stays allowed because the `EmptyOffhandEnabled`
  switch-off path rides the same request and must always win. The lock is
  symmetric because CarryOn wraps BOTH hand slots in `LockedItemSlot` for the
  whole carry (`CarryStateService.SetCarried` locks `RightHandItemSlot` and
  `LeftHandItemSlot`; `Restore` runs only in `RemoveCarried`, which re-reads
  those same getters — decompiled CarryOn 2.0.0-pre.8): flipping either
  substitution mid-carry strands the wrapper in a real hotbar slot (dead
  until relog), and exiting mid-carry strands the player on a slot that
  cannot place the block. An entry-only lock was tried and reverted — it is
  the bug shape that strands wrappers.
  Carry state is reflection-read on both sides (client input locks, server
  request validation) in `OpenHand.Client.CarryOnInterop`, which
  supports both shipping CarryOn layouts: 2.0.0 (instance
  `ICarryManager.GetCarried(Entity, CarrySlot)` reached through the
  `CarryOnLibSystem` ModSystem of the separate CarryOnLib library mod —
  CarryOn 2.0.0 requires it) and 1.14.x (static `GetCarried` extension on
  `CarryOn.API.Common.CarryableExtensions`). A missing or renamed API simply
  reports not-carrying, never breaks Open Hand's own input handling.
- CarryOn carries persist across relog (entity WatchedAttributes), so joining
  a world while already carrying is normal and the locks above apply from the
  first tick. If the carried stack was saved corrupt (e.g. by an older
  Open Hand/CarryOn interaction), the block places as an unknown "?" block and
  placing can crash in vanilla block behaviors — drop the stuck carry with Q
  and pick the item up fresh. `/openhand status` reports the CarryOn interop
  state and a live hands-carry on both sides, the first fire of each Open Hand
  hotkey logs once with its binding (`handler fired (binding: …)`, described
  without `KeyCombination.ToString()` — that pulls GlKeyNames/OpenTK), and
  carry declines log once per carry episode (`declined while carrying`), so a
  "the hotkey did nothing" report is diagnosable from the logs alone.
  Persistence telemetry: the client logs the first send of each request kind
  with the channel's handshake state (`sending … request (channel connected:
  …)`, failures logged and swallowed instead of crashing the hotkey dispatch),
  the server logs the first request per player with its revision outcome
  (`applied a … request … and persisted it` / `stale, re-sending the current
  state`) and every join's restored state (`joined — restored persisted: …`),
  so a "the toggles did not survive a relog" report is attributable from
  `client-main.log` + `server-main.log` alone: a client send with no server
  line means the packet died in dispatch; a server `applied` line with a join
  `restored persisted: none` means persistence itself broke.
- CarryOn's placement transaction leaves two artifacts in the substituted
  slot, both reclaimed by `OpenHandRuntime.SweepSubstitutedSlot()` every game
  tick (client and server): the placed block's stack stays in the active hand
  slot after a successful place-down (CarryOn clears it on failure but not on
  success, and vanilla `TryPlaceBlock` does not consume it), which otherwise
  duplicates the block on the next interaction; and pick-up replaces the
  mod-owned inventory's index-0 occupant with a `LockedItemSlot` wrapper,
  which otherwise crashes the next pick-up. The sweep is why the substituted
  slot must always be handed out directly (see the slot-contract invariant):
  exposing the wrapper instead leaks stacks into engine item-move paths and
  duplicates items — tried and reverted.

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
  The Release workflow only validates the tag/version match — creating the GitHub
  release and attaching the zip is done manually with `gh release create`.
- The Mod DB page (description, changelog) at https://mods.vintagestory.at/openhand
  is maintained by hand in a browser as HTML — agents cannot log in there. Supply
  paste-ready HTML copy (description sections use `<h3>`/`<ul>`/`<li>` with
  `<code>` for commands) and remind the owner to upload the new zip and switch
  the page's download to it. Comments on the page are publicly viewable and can
  be read by fetching the page.

## Changelog formats

Release notes are produced at release time in two places (see Branching &
releases), both summarizing only that release's changes.

- **GitHub release notes** — attached to the `v<version>` tag by
  `gh release create`. Title `Open Hand <version>`; a one-sentence summary
  naming the release type and what it was validated against (game build,
  CarryOn versions where relevant); then `##` sections grouped by theme
  (`## CarryOn compatibility`, `## Fixes`, `## UX`), most significant first;
  a closing `## Notes` section with support requirements (Vintage Story
  1.22.x on client and server) and the `/openhand status` diagnostics
  pointer. Bullets may name the version a change shipped in when a section
  covers several releases.
- **Mod DB changelog** — paste-ready HTML for the page's changelog section
  (the page is hand-maintained; see Branching & releases). One
  `<h3><version> — <short title></h3>` heading per release, newest first,
  followed by a `<ul>` of `<li>` items describing that release's
  user-visible changes, with commands and paths in `<code>`.

## Local test instance

A private Vintage Story test instance lives outside this repo at `~/code/vs-testing/`
(its `launch-test.sh` documents the layout):

- `game/` — copied game install, launched directly via `~/code/vs-testing/launch-test.sh`
- `xdg/VintagestoryData/Mods/` — the instance's mod folder. `game/Mods/` is vanilla-only
  (`do_not_add_mods_here.txt` says so); mods must go in the XDG data folder.
- `tmp/` — private TMPDIR so the game's URI-scheme named pipe never collides with the
  main Flatpak install

Deploy the current build to the test instance (run from the repo root):

```bash
python3 scripts/package.py
cp artifacts/openhand_<version>.zip ~/code/vs-testing/xdg/VintagestoryData/Mods/
```

Overwriting the existing `openhand_*.zip` in place is fine; the game picks it up on the
next launch. The zip name follows the modinfo version, so an unreleased feature build
overwrites the same `openhand_<version>.zip` as the published release. Never deploy to
the main (Flatpak) install's mod folder from this repo's workflow.

## Testing

Two projects, both zero-framework console runners (a failing suite records its
exception and the remaining suites still run; a non-zero exit gates CI):

- `OpenHand.StateTests` (CI, no game DLLs): pure logic — selection/offhand
  state, wheel ring, double-tap, gap solver, config parsing plus the
  `openhand.json` round trip (System.Text.Json pins the property names),
  HUD/centering geometry, the CarryOn anchor solver, and the protocol wire
  format. The protobuf round trips run against the NuGet `protobuf-net` 3.x
  (Apache-2.0), wire-compatible with the game's own copy, which CI cannot
  ship. `ProtocolTests` pins the exact serialized bytes of the messages, so a
  ProtoMember tag change is a test failure instead of a silent desync.
- `OpenHand.RenderingTests` (local only, outside the solution; needs
  `VintageStoryPath`): everything that requires the game DLLs or real
  internals. Fakes are DispatchProxy recordings for interfaces plus real
  uninitialized `ClientPlayer`/`ServerPlayer` objects seeded with exactly the
  fields the mod reads (decompile evidence lives in `TestFakes.cs`;
  `IServerPlayer` cannot be DispatchProxy-ed — its hierarchy hides a member
  from the proxy builder — so the server fake is a real `ServerPlayer`).
  - `PatchTargetTests` is the game-update tripwire: it resolves every patch
    `TargetMethod()` and the mod's private reflection lookups against the
    installed game and applies the vanilla-target patches for real. Run it
    alongside the decompile evidence whenever a game update lands.
  - `CarryOnInteropTests` probes the known mod folders, extracts the newest
    `CarryOn*.zip`/`CarryOnLib*.zip` into a scratch directory, and loads the
    assemblies; it skips with a notice when CarryOn is absent.
  - `ServerControllerTests` covers the offhand carry lock deterministically by
    injecting the interop's reflection state with a test double, so it never
    needs CarryOn installed.
  - The hotkey persistence test deliberately never invokes
    `ClientSettings.SetKeyMapping` — resolving the reflection target is the
    assertion; invoking it would dirty the real `clientsettings.json` of the
    machine running the tests.

## Validation expectations

- State tests must pass before any merge.
- Run `tests/OpenHand.RenderingTests` locally for any change touching
  client/server internals, patch targets, or the runtime slot contract.
- Changes to player-facing behavior must be validated in-game (single-player at
  minimum; both GUI scales if HUD rendering changed). Note what you checked in the
  PR's Testing section.
- `/openhand status` prints selection state, remembered slot, server revision, and
  patch status — include its output in bug reports and use it to verify patch
  application after upgrading the game version.
