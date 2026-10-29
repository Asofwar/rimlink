# Developing Rimlink (guide for coding agents)

Rimlink is a clean-room persistent multiplayer for RimWorld 1.7: a dedicated .NET server, a game-independent client
core, and a Harmony mod. Nothing here may be copied from other multiplayer mods or from the game's own code.

## Layout and what runs where

| Path | What it is | In `Rimlink.sln` |
|---|---|---|
| `src/Rimlink.Protocol` | protobuf/gRPC contracts (`Protos/*.proto`), `ProtocolVersion` | yes |
| `src/Rimlink.Server` | ASP.NET Core 10 server: gRPC game API, REST admin API, EF Core + SQLite, migrations | yes |
| `src/Rimlink.Client.Core` | client logic with no game references: connection, state, outbox, save transfer, live client | yes |
| `src/Rimlink.Client` | the RimWorld mod: Harmony patches, UI, game components. Built against the game, **not** in the solution | no |
| `src/Rimlink.Tools` | admin CLI | yes |
| `tests/Rimlink.Server.Tests` | xUnit against the real server (`WebApplicationFactory`, fixtures in `Support/`) | yes |
| `tests/Rimlink.Client.Tests` | xUnit for the client core, talking to a real test server | yes |
| `mod/` | mod metadata (`About/About.xml`), defs, `Languages/` (English + Russian keyed strings) | — |
| `docs/` | `player-guide.md`, `server-setup.md`, `manual-checklist.md`, `superpowers/specs`, `superpowers/plans` | — |
| `deploy/` | Dockerfile, docker-compose, `appsettings.example.json` (every config key with its default) | — |
| `scripts/` | `Build-ClientMod.ps1` (compile + assemble + install the mod), `Package-Release.ps1` | — |

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
5. Version: `mod/About/About.xml` `<modVersion>` and `ClientInfo.Version` stay equal; one bump per sub-project.
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
  (`Patch_LifeStageWorker_HumanlikeAdult` skips it while Scribe is loading).

## Commits

Subject line describes the change in the imperative ("Keep the accept loop running when a connection fails"); body only when the
why is not obvious. No trailers of any kind. Commit identity is the repository owner's; check `git var GIT_AUTHOR_IDENT` before
committing. Tests green and the mod building are the bar for every commit; merge and push only when asked.
