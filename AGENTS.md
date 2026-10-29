# Developing Rimlink (guide for coding agents)

Rimlink is a clean-room persistent multiplayer for RimWorld 1.7: a dedicated .NET server, a game-independent client
core, and a Harmony mod. Nothing here may be copied from other multiplayer mods or from the game's own code.

## Layout and what runs where

| Path | What it is | In `Rimlink.sln` |
|---|---|---|
| `src/Rimlink.Protocol` | protobuf/gRPC contracts (`Protos/*.proto`), `ProtocolVersion` | yes |
| `src/Rimlink.Server` | ASP.NET Core 10 server: gRPC game API, REST admin API (scoped tokens), EF Core + SQLite, migrations; `wwwroot/` holds the web admin panel (`/admin/`) and the public page (`/`), plain ES modules without a build step | yes |
| `src/Rimlink.Client.Core` | client logic with no game references: connection, state, outbox, save transfer, live client | yes |
| `src/Rimlink.Client` | the RimWorld mod: Harmony patches, UI, game components. Built against the game, **not** in the solution | no |
| `src/Rimlink.Tools` | admin CLI | yes |
| `tools/Rimlink.BridgeTools` | dev-only RimBridgeServer companion: `rimlink/*` tools that drive the game from GABS. Built against the game, never shipped | no |
| `tests/Rimlink.Server.Tests` | xUnit against the real server (`WebApplicationFactory`, fixtures in `Support/`) | yes |
| `tests/Rimlink.Client.Tests` | xUnit for the client core, talking to a real test server | yes |
| `tests/Rimlink.BridgeTools.Tests` | xUnit for the game-free parts of `tools/Rimlink.BridgeTools` (compiled in by link) | yes |
| `mod/` | mod metadata (`About/About.xml`), defs, `Languages/` (English + Russian keyed strings) | — |
| `docs/` | `player-guide.md`, `server-setup.md`, `manual-checklist.md`, `superpowers/specs`, `superpowers/plans` | — |
| `deploy/` | Dockerfile, docker-compose, `appsettings.example.json` (every config key with its default) | — |
| `scripts/` | `Build-ClientMod.ps1` (compile + assemble + install the mod), `Build-BridgeTools.ps1` (dev companion), `Package-Release.ps1` | — |

Toolchain: .NET SDK pinned in `global.json`, C# latest, nullable on, central package versions in
`Directory.Packages.props`. Harmony is `Lib.Harmony 3.0.0-rc.3` (the game's Harmony mod 3.x at runtime).

## Build, test, install

```bash
dotnet build Rimlink.sln
dotnet test Rimlink.sln -nologo -v q        # must be green before every commit
```

The mod is built and installed only through the script (PowerShell, game closed — the game locks the DLLs):

```powershell
.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld' -InstallTo 'D:\SteamLibrary\steamapps\common\RimWorld\Mods'
```

`-BundleHarmony` is for local runs without the Harmony mod only; release builds never bundle it.
`.\scripts\Package-Release.ps1` produces `dist/` zips. Game log: `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`
(all instances write to the same file; mod lines are prefixed `[Rimlink]`).

Local server for manual tests (no Steam key: the in-game login falls back to the password dialog, expected):

```bash
Rimlink__Server__DataDir=./data Rimlink__Auth__AllowPassword=true Rimlink__Auth__AdminLogins__0=admin Rimlink__Admin__ApiToken=localtest dotnet run --project src/Rimlink.Server -c Release
```

Any config key is an environment variable `Rimlink__Section__Key` (`Rimlink__Raids__TargetCooldownHours=0` to skip cooldowns, etc.).
A second game instance for two-player checks: `RimWorld.exe -savedatafolder=<folder>` with its own `Config/` (mod list, windowed mode)
and the same server pinned. Steam must be running, or the Workshop Harmony mod is invisible and the mod fails with
`Could not load type HarmonyLib.HarmonyPatch`.

## Driving the game from an agent (GABS + Rimlink.BridgeTools)

The live game is driven through GABS (`games_start rimworld-host` / `rimworld-guest`, each with its own `-savedatafolder`) and the
RimBridgeServer mod. `tools/Rimlink.BridgeTools` adds `rimlink/*` tools on top, so login, a new colony and every two-player flow
run without pixel clicks. It is a dev tool: not in the solution, not in `Package-Release.ps1`, no RimBridgeServer reference in `src/`.

Build and deploy with the games closed (companions load when the bridge starts; restart the game after a deploy):

```powershell
.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'
```

It deploys exactly one file, `<RimWorld>\BridgeTools\Rimlink\Rimlink.BridgeTools.dll`, and refuses a folder holding anything else:
RimBridgeServer resolves a companion's dependencies from its own folder first, and a `Rimlink.Client.dll` there would load a second
Rimlink.Client whose session is always empty. `rimlink/status` reports `clientAssembly` (must be under `Mods\Rimlink`). Build the mod
and the companion from the same tree. Tools missing or failing: `rimbridge/get_bridge_status` lists companion load/register errors.

Calls: `games_call_tool {gameId, tool, arguments}`. The host stops an extension tool after 60 s: for `rimlink/connect` and
`rimlink/start_colony` add `"_rimBridgeTimeoutMs": 330000` to `arguments` and give the GABS call `timeout: 340` (the bridge warns
that the argument is unknown, but its host reads it before binding; both flows take 5–15 s on a local server). Lists are
comma-separated strings: `pawns: "Thing_Human12,Thing_Human13"` (ids from `rimworld/list_colonists`, or names), `items: "Silver:100,Steel:50"`.
Every result is `{success, error?, ...}`; the action tools return as soon as the flow starts, follow them with `rimlink/status` and the
`[Rimlink]` lines of `rimbridge/list_logs`.

| Tool | What it does |
|---|---|
| `rimlink/status` | program state, top window, session (connection, player, world, players, settlements, economy, raids, social: flags, blocks, awaiting gifts, trades; live session, visit, outbox) |
| `rimlink/connect {login, password, register=auto\|never, host, port, playerName, serverPassword, trustChangedPin, timeoutSec}` | from the main menu: adds the server, logs in (registers once on refusal with `auto`), trusts a new certificate, returns `Loading`/`NewColony`/`ModpackDiff`/`Failed` and waits for the map on `Loading` |
| `rimlink/start_colony {scenario, storyteller, difficulty, seed, tile, timeoutSec}` | walks the new-colony pages with defaults (Crashlanded, Cassandra/Rough, classic ideology, the server's planet, a free random site) until the server registered the settlement |
| `rimlink/menu_options`, `rimlink/click_menu_option {label}` | the main-menu options (Rimlink's "Multiplayer" included), which `rimworld/get_ui_layout` cannot see |
| `rimlink/page_state`, `rimlink/page_action {next\|back\|random_site}` | the top `Page` and its buttons, including the starting-site page's bottom buttons the layout snapshot misses |
| `rimlink/raid {player, pawns, mode=snapshot\|real, timing=instant\|warned}`, `rimlink/visit {player, pawns}`, `rimlink/visit_end` | raid or visit from the current map, no caravan; leave the visited copy (the overlay button) |
| `rimlink/live_propose {player, pawns, kind=raid\|visit}`, `rimlink/live_answer {accept}`, `rimlink/live_speed {0..3}`, `rimlink/live_end` | live sessions: propose (guest), answer the open invite (host), speed or pause through the live tempo (0 = pause, the guest's comes out of its budget; `status.live.pausedBy`/`guestPausesUsed`), end (guest) or abort (host) without the confirmation dialog |
| `rimlink/send {player, items, pawns, silver, anywhere}`, `rimlink/post_lot {price, items \| pawn}`, `rimlink/deposit {silver, anywhere}`, `rimlink/market {seller}`, `rimlink/buy {lotId}` | the economy window; items come from under the beacons unless `anywhere` (deposit `anywhere` needs dev mode) |
| `rimlink/chat {count}`, `rimlink/chat_send {text, to}` | chat history as received, send public or to one player (`[[tile:N]]` in the text is a place card) |
| `rimlink/gift_answer {parcelId, accept}`, `rimlink/block {player, unblock}`, `rimlink/trades_flags {acceptGifts, tradesClosed}` | answer a gift waiting for a decision (`status.session.social.awaitingGifts`); block or unblock a player; set the two flags (`"true"`/`"false"`, omit to keep) |
| `rimlink/trade_open {player, items, pawns, silver, note, anywhere}`, `rimlink/trade_side {tradeId, items, pawns, silver, note, returnGoods, anywhere}`, `rimlink/trade_confirm {tradeId}`, `rimlink/trade_cancel {tradeId}` | two-way trades as the trades tab does them (`trade_side` adds goods unless `returnGoods`; `silver` -1 and no `note` keep them; confirm uses the revision in `status.session.social.trades`) |
| `rimlink/ping {to, tile, x, z}`, `rimlink/letter_pause {mode=never\|major\|any}` | ping a map cell of the current map or a world tile (`x` -1) for a player (empty `to` = the live/visit counterpart), as the ping key does; `status.lastPing` shows the last one received. Set this player's letter-pause threshold as the settings do (`status.live.letterPause`, `guestLetterPause` on the host, `letterLabel` on the guest) |
| `rimlink/caravan_form {pawns, items}`, `rimlink/caravan_move {caravan, tile, instant}`, `rimlink/caravan_options {caravan, tile}`, `rimlink/caravan_order {caravan, label}` | a caravan of our pawns (items in its inventory, no forming walk), a dev teleport (or a real route) to a tile, the real right-click options there ("Transfer to …", "Raid …", "Visit …", "Live raid …", "Enter Colony") and choosing one; on its own tile the arrival and the window it opens follow at once. The windows (`Dialog_Economy`, `Dialog_Raid`) are ordinary: drive them with `get_ui_layout`/`click_ui_target`, confirmations with `press_accept` |
| `rimlink/pods_launch {tile, label, pawns, items, instant}` | pawns and/or items into a transport pod group launched at a tile with that tile's pod option by label ("Raid … (pods)", "Live raid … (pods …)"); a wrong label lists the options, and a wrong label or an option the game refuses on click (peaceful mode, host offline) puts everything back. Dev shortcut: no launcher or fuel; `instant` lands at the next world-object tick (step ~120 ticks) |
| `rimlink/net_drop {seconds}` | dev only: this client loses its server connection for N s (streams break, calls fail, the machine's network is untouched), then reconnects as after a real outage (`PeerLost` → `PeerBack` in a live session). Attempts go on every 60 s with no end; from 2 minutes the game offers the menu (`rimlink/status` shows `reconnectingForSec`, `outageIsLong`, and `failureReason` after a refusal) |
| `rimlink/live_resync`, `rimlink/work_priority {pawn, work, priority}`, `rimlink/schedule {pawn, hour, assignment}`, `rimlink/capture {pawn}` | live details: the guest asks for a fresh snapshot (as after dropped deltas); set or read (priority -1, no assignment) a Work/Schedule cell as the tabs do, which the mirror turns into orders; dev capture of a downed pawn by the colony without a prisoner room |
| `rimlink/map_pawns {faction}`, `rimlink/select {things}`, `rimlink/faction_ideo {faction}` | debug: every pawn on the map with faction, kind, ideo and previous ideos; select any things by load id (for dev actions on the selection); give a faction a newly generated primary ideo (`RimlinkRaiders` creates the raider faction), since a classic world shares one ideo among all factions |

Typical two-player run: host `rimlink/connect {login:"admin", ...}` (an `AdminLogins` login creates the world) → `rimlink/start_colony`;
guest `rimlink/connect` → `rimlink/start_colony` on the locked world; then `chat_send`, `send`, `raid`, `live_propose` → host
`live_answer {accept:true}` → `live_end`; `rimworld/go_to_main_menu` saves and uploads as the menu button does. Bridge pitfalls:
`rimworld/set_text_field` must use `instant` (`typed` times out on mod dialogs); a Crashlanded colony's pawns arrive in drop pods,
so `rimworld/list_colonists` is empty for the first seconds (`rimworld/play_for` a few seconds first).

Runs that must not touch another agent's games use a separate stand: a copy of the game folder (about 1 GB) with the branch's mod
and companion deployed into it, its own `-savedatafolder`s, its own server port (`Rimlink__Server__Port`), and two extra entries in
`~/.gabs/config.json`. `-logFile` is ignored: every instance still writes into the one `Player.log`, and a new instance truncates
it. Each GABS game keeps its own stdout in `~/.gabs/<gameId>/launch.log` (overwritten only when that game starts again): copy both
before a risky call. A game that dies with no trace there, no `.NET Runtime` event and GABS answering `Game '<id>' not found` for a
call that never ran was ended from outside (see the `caravan_order` investigation in `docs/manual-checklist.md`).

## How work is organised (the workflow and the skills behind it)

The repository was built with the `superpowers` skill set (brainstorming → spec → plan → execution → review → finish). An agent that
has those skills invokes them by name; an agent without them follows the same steps by hand. The artefacts are the contract, not the tool.

| Stage | Skill | What it produces here |
|---|---|---|
| 1. Design | `superpowers:brainstorming` | questions one at a time, 2–3 approaches, a sectioned design approved piece by piece, then the spec `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md`, committed. Every feature here is "architectural": no code before the spec is approved. |
| 2. Plan | `superpowers:writing-plans` | `docs/superpowers/plans/YYYY-MM-DD-<topic>.md`: header (goal, architecture, spec path, **Global Constraints**, **Review Focus**), then tasks with exact files, interfaces, RED→GREEN steps, `Expected:` lines and commit steps. The user picks the execution method at hand-off. |
| 3. Execute | `superpowers:executing-plans` (inline, used so far) or `superpowers:subagent-driven-development` | a git-ignored workspace `.superpowers/sdd/<plan>/` with the ledger `progress.md` (`Task N: complete (commits a..b, tests: …)`, `Task N: Ruling: … — why — cost if wrong`), one todo per task, no check-ins between tasks. Scripts: `sdd-workspace`, `task-start PLAN N`, `task-done PLAN N BASE -- <test cmd>`, `review-package PLAN MERGE_BASE HEAD`. |
| per task | `superpowers:test-driven-development` | failing test watched first, minimal code, whole suite green (`dotnet test Rimlink.sln -nologo -v q`). Game code has no automated tests: the test is the two-player run in the real game, written into `docs/manual-checklist.md`. |
| on a bug | `superpowers:systematic-debugging` | reproduce → isolate → hypothesis → minimal experiment; decompile the game type (`ilspycmd`) before guessing its behaviour; read the save XML or the server SQLite (`sqlite3 <DataDir>/rimlink.db`) as evidence. |
| before "done" | `superpowers:verification-before-completion` | the claim is backed by a command and its output in the session, never by "should pass". |
| 4. Review | `superpowers:requesting-code-review` → `code-reviewer` agent on the most capable model, from the review package, with the plan's Review Focus and the ledger rulings | ONE review of the whole branch, ONE fix pass (each fix RED→GREEN), minors deferred to the ledger. No per-task reviews, no multi-lens fan-out. |
| 5. Finish | `superpowers:finishing-a-development-branch` | rulings and deferred minors reported to the user; merge into `main` (fast-forward) and push only after the user says so; delete the plan's workspace; update the project memory. |
| commits | `git-safe-commit` | identity and attribution check before every commit and push. |

Smaller items: `using-git-worktrees` for isolated work, `dispatching-parallel-agents` only for independent directions (never two
agents in the same file), `receiving-code-review` when a review comes back. The plan's own text states the test command and the
commit message for each task: use them verbatim.

Conventions that follow from this:

1. Read the spec of the area before touching code; the spec is the authority, the plan is its argument, a deviation is a written ruling.
2. One branch per sub-project (`economy`, `raids`, `live`, …) from `main`; fast-forward merge after the final review.
3. Game code (`src/Rimlink.Client`) is verified in the real game on two instances (host + guest); dev-mode debug actions under the
   `Rimlink` category drive every flow that needs a second player.
4. Documentation moves with behaviour: `docs/player-guide.md` (players), `docs/server-setup.md` (admins, config, metrics),
   `deploy/appsettings.example.json` (every key), `README.md` status, `docs/manual-checklist.md`.
5. Version: `mod/About/About.xml` `<modVersion>`, `ClientInfo.Version` and `Directory.Build.props` `<Version>` (the
   server reports it in `/status`) stay equal; one bump per sub-project.
   `ProtocolVersion.Current` changes only on an incompatible protocol change (there has been none).

## Rules that are not obvious from the code

**Protocol only grows.** Add fields with new numbers, new messages, new `.proto` files, new `ErrorCode` values in `common.proto`,
new `ServerEvent` bodies in the `events.proto` oneof. Never renumber or reuse. `events.proto` imports the feature protos, so a feature
proto cannot import `events.proto` (define a local message instead of reusing `Notice`).

**Storage.** Schema changes are EF Core migrations: `dotnet ef migrations add <Name> --project src/Rimlink.Server` (no `-nologo`,
the ef tool rejects it), plus a schema test in `tests/Rimlink.Server.Tests/Storage`. Migrations are applied at server start. Never
commit a data directory or `*.db`.

**Server.** Options classes under `Hosting/` (`*Options`, validated, documented in `appsettings.example.json`). Services under
`Application/<Area>`; gRPC endpoints under `Grpc/`; the REST admin API and the CLI mirror every admin operation. Player-supplied names,
paths and sizes are validated at the boundary (see the save and parcel size limits). Rate limits and size limits refuse with a typed
`ErrorCode`, never with a crash. Escrow rules: anything a player sends away (items, pawns, silver) is stored server-side and returned by
mailbox parcel; nothing is duplicated and nothing is lost on a drop.

**Client core.** No `Verse`/`RimWorld` references. All network calls are async and cancellable; unanswered mutating calls go
through the outbox with a `client_request_id` and are replayed on reconnect. State for the UI lives in `SharedState`.

**Game mod.** Game objects are touched on the main thread only: background results are posted with `MainThread.Post`. Harmony patches
live in `Game/Patches`, one class per patched method, with a one-line summary of why. Every user-facing string is a keyed string in
`mod/Languages/English` and `mod/Languages/Russian`. Dev-mode debug actions (`Rimlink` category) exist for every flow that needs
a second player, use them in manual runs. Pawns and things cross games as serialised blobs (`ThingBlobs`): regenerate ids with
`newIds: true` when the object enters a game that may already hold its id, and `ResetMapIndex` before spawning a blob that was
serialised while spawned.

**RimWorld 1.7 facts learned the hard way** (the decompiled game is the reference: `ilspycmd -t <Type> <game>\Assembly-CSharp.dll`
from PowerShell):
- `Thing` and `Pawn` implement `IEquatable` by `thingIDNumber`: a deserialised copy of a pawn equals the live pawn in every
  `HashSet`/`Dictionary`. Never `Destroy` such a copy (it is passed to `WorldPawns` and the live pawn turns into "a world pawn");
  drop the reference instead.
- A pawn of a non-player faction without a lord leaves the map within seconds; guest pawns need an idle lord.
- `PawnTextureAtlas` is global and keyed by pawn: free it (`GlobalTextureAtlasManager.FreeAllRuntimeAtlases`, `PortraitsCache.Clear`)
  before loading another game in the same process.
- `GameDataSaveLoader.LoadGame` pauses the game and runs `FinalizeInit` before the first frame: faction changes made during
  load do not reach the colonist bar; do them on the first `Playing` frame.
- Multiple player factions / DLC differences between two players surface as unresolved references (`Ideo`, policies) when a
  blob is loaded; `ThingBlobs.DeserializePawn` repoints them to the receiver's defaults.
- Vanilla never loads a pawn during `ProgramState.Playing`; a blob does. Its first life-stage computation then calls
  `Notify_LifeStageStarted` with no previous stage, which the adult worker dereferences for colonists
  (`Patch_LifeStageWorker_HumanlikeAdult` skips it while Scribe is loading). The same assumption sits in `Need_Food.MaxLevel`
  (stats read before the hediffs are attached; a chemical dependency throws and the need is dropped): `Patch_Need_Food_MaxLevel`.
  When a load-time crash names a vanilla `ProgramState.Playing` branch, that is the pattern.
- Faction references inside a blob (royal titles, favor, heirs) resolve against this world's factions by load id
  (`ThingBlobs.RegisterWorldFactions`), like the thing's own faction; a title still without a faction (older saves) is given
  this world's granting faction or dropped (`Patch_Pawn_RoyaltyTracker_ExposeData`).
- A blob loads in a Scribe pass of its own: references to anything outside it (other pawns in relations and memories, ideos,
  policies) resolve to null and are dropped. `Pawn.Discard` (the `PawnExit` path) runs `ClearAllRelations` on both sides and removes
  every pawn's memories of the discarded one, so the colony's half of a relation is gone too. A pawn of the colony leaving for good
  therefore goes through `PawnExit.Leave`, which records its relations in `RelationLedger`, and every homecoming calls
  `RelationLedger.Rejoin` (pawns that arrive together in one call). `Thing` has no `operator ==`: relation code compares references.
  Static UI caches mix the two: `SocialCardUtility` adds rows by reference and drops them by id, so a pawn back under an old id needs
  `SocialCardUtility.ClearCaches()` (done in `Rejoin`) or the discarded original's row stays as "Missing".
- 1.7 runs on the Waylith engine (CoreCLR, GLFW), not Unity; `UnityEngine` is a stub layer (`UnityStubs.dll`). The menu's Quit to OS
  ends in `Application.Quit()`, which asks `Application.wantsToQuit`; the window's close button and Alt+F4 only set the GLFW
  window's close flag and the main loop exits without asking anything (`QuitGuard` hooks `GlfwWindow.OnClose` by reflection).
  Engine code: `ilspycmd -p` on `Waylith.dll` / `UnityStubs.dll` / `Brutal.Glfw.dll` in the game folder.
- `WealthWatcher` recounts at most every 5000 ticks: a value read right after things arrive is stale unless `ForceRecount()` runs
  first (done before a save writes its wealth).

## Commits

Subject line describes the change in the imperative ("Keep the accept loop running when a connection fails"); body only when the
why is not obvious. No trailers of any kind. Commit identity is the repository owner's; check `git var GIT_AUTHOR_IDENT` before
committing. Tests green and the mod building are the bar for every commit; merge and push only when asked.
