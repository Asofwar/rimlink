---
pageId: developers/testing
contentRevision: 28
---

# Testing and acceptance

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Record the actual loaded .NET/ASP.NET runtime, provider and native SQLite identities for executor qualification. SDK selection alone does not select the runtime patch. The 10.0.14 qualification has its own exact QualificationId/ProviderProfile; legacy 10.0.13 witnesses remain separate. Main CI executes the full suite; a tag may reuse only a completed successful main push for the exact commit with successful Build/Test steps, recording its run and attempt instead of claiming a second test execution.

First reproduce the case and observe a failing targeted test, then make the minimal fix and rerun. Server tests use a real WebApplicationFactory; client-core tests talk to a test server. Full suite: dotnet test Rimlink.sln -nologo -v q.

For byte-sensitive migrations, build from fresh Git LF sources on both Windows and Linux. Copying a Windows working tree with rsync preserves its CRLF bytes and does not reproduce a clean CI checkout. Check the exact migration SQL, complete real SQLite catalog and lineage, and the pinned additive recipe. Retain the original profiles and negative outcomes; a passing source copy is evidence only for those bytes.

Open returning is not proof that the peer attached: assert on received events. Measure deadlines with Stopwatch, not DateTime.UtcNow. For timing changes, repeat on Linux/two CPUs where available.

Verify game logic with isolated host/guest instances, separate savedata and a separate server/port. BridgeTools/RimBridge is an available development tool, not a release dependency. Record executed steps/results in the manual checklist; a written scenario alone is not PASS.

For a new test world, an administrator can pass `planetCoverage` to the development companion's `rimlink/start_colony`. The default `0` keeps the native choice. Supported values are `0.3`, `0.5` and `1`. The native `0.05` option requires dev mode and selects the game's 5% world. A nonzero value is accepted only before the first world generation and is refused for an existing world. The result's `steps` record the applied value. Export the entire generated world and record its tile count, seed and manifest identity.

Wiki: test:checks → check:content → check:reference → check:drift -- --base &lt;commit&gt; → build → check:links, followed by real browser/search/mobile navigation.

## Which check answers which question? {#test-matrix}

| Check | Establishes | Does not establish |
|---|---|---|
| Solution build/tests | Core/server contracts, storage, boundary scenarios | Game UI/Scribe/pawns |
| Build-ClientMod | Compilation against installed game | A completed two-player flow |
| Two isolated clients | Execution of specified real steps | Every mod/DLC/GOG combination |
| Wiki validators/build | Structure, links, inventory | Semantic article accuracy |
| Browser acceptance | Search/navigation/layout at tested sizes | All browsers or actual OS zoom |

For a bugfix observe RED on the original case, then GREEN. Wait for received stream events, not Open returning. Use Stopwatch deadlines; repeat Linux/two-CPU checks for timing/race changes where available.

For Discord, distinguish permanent from temporary errors: 403/404 disable only outbound delivery to the affected channel until restart, while 429/5xx retain the batch. Test `#`, `##`, `-#` and new-line headings as user text. FakeDiscordOut can inject the exact failure without real tokens; this does not establish a live Discord connection.

For the own-colony save-mod guard, test a save whose required mods exceed the server manifest, including administrator login. Missing mods must stop downloaded-save loading and local pending recovery before upload or pending cleanup. Restore the full mod set and verify actual map loading separately. Both branches were exercised in the real game: downloaded-save refusal/full-mod loading and a separate local-pending missing-DLC refusal with unchanged files, followed by acceptance of the exact snapshot once and successful loading. A normal exit also saved to the isolated server. Normal exit with a new save on the deployed server remains unverified; these cases do not establish two-player outpost acceptance.

## How is the research capture arbiter tested? {#capture-prototype}

Automatic capture is being developed as a separate dev-only prototype. By itself it does not enable guild objects or their economy on an ordinary server. `tools/Rimlink.BattlePrototypeHost` is a local coordinator with separate SQLite storage and a fixture owner; it is excluded from `Rimlink.sln` and release packages. It needs three owned processes of the same installed game: an arbiter and two Entry controllers, separate savedata, and no `RimlinkSession`. Confirm that the license permits this setup before preparing the stand.

First complete Task 0 of the approved `docs/superpowers/plans/2026-11-08-guild-outpost-capture-prototype.md`: an owned runtime copy, identical mods, a neutral vanilla template, and verified `stand.json`/`evidence/env-verified.json`. Never point the coordinator at the working game installation or server data. On an already prepared stand:

```powershell
dotnet run --project tools/Rimlink.BattlePrototypeHost -- prepare --stand 'D:\Build\RimlinkCaptureStand'
dotnet run --project tools/Rimlink.BattlePrototypeHost -- serve --stand 'D:\Build\RimlinkCaptureStand'
# Separate terminal:
dotnet run --project tools/Rimlink.BattlePrototypeHost -- inspect --stand 'D:\Build\RimlinkCaptureStand'
dotnet test tests/Rimlink.BattlePrototype.Tests/Rimlink.BattlePrototype.Tests.csproj -nologo -v q
```

`prepare` creates a separate battle ID and three secret role bindings; do not publish them. Build the companion through `Build-BridgeTools.ps1` into the owned game only, with its processes closed. `rimlink/battle_bind` checks the process, savedata, builds, and absence of a player session. The arbiter uses `battle_prepare`/`battle_start`; controllers use `battle_view`. `battle_status` reports actual ticks, commands, state, and receipts; `battle_stop` removes only its own hooks. Preparation waits for the loading fade to finish and game control to become available.

For the owned fault stand only: `fault --stand <root>` revokes the executor; `rotate-boundary --stand <root>` changes the restore boundary and fence. `fault --stand <root> --at <point>` arms a one-shot Exit(86) when that point is reached: `before-initial-file-complete` and `after-final-durable` terminate the arbiter; `before-start-commit`, `after-start-commit`, `after-view-commit`, and `after-result-commit` terminate the coordinator. Retain the `.consumed` marker, files, and SQLite evidence; do not clean up an unknown outcome or rerun simulation under the same battle ID. An exact replay of the latest view is accepted after a lost reply; changed content under the same sequence and older sequences are rejected. This is controlled process termination, not a physical power cut.

The arbiter saves and reloads a map with eight equally equipped fighters. It alone simulates combat. Controllers display the complete small state in native debug windows and send their own side's Move/Attack/Draft commands; this is not a full native-map mirror. Rules require 3000 consecutive capture ticks, reset on contest, and a 30000-tick limit; wall-clock time cannot turn a technical stall into a defender victory. AI takes over a disconnected side after 30 seconds. Losing the coordinator pauses ticks, followed by TechnicalAbort after 60 seconds. A result with complete native snapshot/journal evidence is applied once; a cold arbiter resends the saved envelope without simulating again.

Actual ENV/CONTRACT/ENGINE/FEASIBILITY/RESULT/FAULT gates and unfinished checks belong in `docs/verification/2026-11-08-guild-outpost-capture-prototype.md`. Unit tests, three windows, or one receipt do not replace the full suite. Mark mutual elimination prepared through debug actions separately from natural combat. This prototype does not qualify production restore, a large-scale scheduler, modpacks, escrow, physical power loss, or full-disk behavior. Publication, deployment, and Product activation require a separate decision.

## How do I record reproducible acceptance? {#acceptance-record}

Record commit, versions, separate DataDir/port, host/guest savedata and initial state. List actions, expected/observed results, operation IDs, return mail and save/relogin checks. A UI screenshot without server outcome does not prove escrow.

For coop test assigned/unassigned pawns, exit, revocation and rollback. For economy test unknown responses and original replay. Do not operate others' games or clear shared Player.log. Mark unfinished scenarios partial, not PASS.

The current continuation adds focused Core checks for the tick gate, operator settings, full inventory, dedicated executor transport, three orders, final capture and personal-save guard. The combined focused client run passed 214/214; server Upload separately refused both battle XML variants while preserving the previous canonical version. These results do not replace the full final-build suite, Linux/two-CPU qualification or a real three-game run.

Native modules build through the standard Build-ClientMod.ps1 into a separate OutputDir. Before adding hooks, the installed Assembly-CSharp.dll and actual DoSingleTick were inspected: a postfix evaluates terminal state and the prefix blocks the next tick. The native Product executor/two viewers have not been launched; gameplay, home return and resource issuance on this version remain NOT RUN.

Additional automated checks cover immutable runtime assignment before work, same-process transport reconnect, dispatched outcomes and cuts, a separate courier and historical-profile Final after config rotation or revoke. Checkpoint tests cover malformed fields, scope and endpoint changes, a complete cold envelope, lost Final response and incorrect transport replies. At that version restart execution and the full fault and performance matrix had not been run yet.

## Limits and safety {#limits}

Do not start another server against live data or stop someone else's game. Game instances may overwrite the shared Player.log. Never report an unrun check as successful.

## How is Product capture development tested? {#guild-capture-product}

The separate branch checks GuildProduction/GuildBattleSchedule, 13 financial tables, explicit V2→V3 activation, and basic R3 restoration. Eight new catalogs were read on Windows/Linux. After separating the command journal, 113 targeted tests passed on Windows and three times on two Linux CPUs. An earlier R3 snapshot passed 1717 Outposts/Backup/Storage tests; this does not verify subsequent services. A later server family snapshot passed 483 tests three times on two Linux CPUs; it predates the new RPCs and game handler.

Internal services place neutral objects, bind a leader's exact consent, accrue future production, and atomically create typed resource claims/parcels. Joining and leader transfer use common authority. Disbanding with unresolved guild assets is currently refused. Generic mailbox ACK and an older client cannot confirm typed delivery. The new object/claim services are not exposed through a public Product API yet.

The command journal covers replay, conflicts, queue bounds, corruption, sealing, and closed handles. A local-disk measurement of 48000 commands with durable acknowledgements yielded mean 1.05 ms and p99 3.57 ms; it excluded network/auth/contended storage/game loop and does not qualify the transport.

Server tests cover exact canonical receipts, immutable first acceptance, authority crashes, and absence of false wealth freezes. The client journal and materialization decision passed 19 targeted tests; pinned manifest reading passed 3 server tests. The standard mod build succeeds, but the new delivery has not been exercised in the real game. The production executor, full battle mirror, campaigns, and issued-resource restoration remain unfinished. The qualification seal was removed in 0.10.0; the setting is off by default. The prototype does not replace acceptance. Evidence belongs in docs/verification/2026-11-08-guild-capture-product.md. Targeted command: dotnet test tests/Rimlink.Server.Tests/Rimlink.Server.Tests.csproj --filter FullyQualifiedName~GuildCapture.


A later delivery snapshot passed 986 client tests and 173 targeted server tests; its network subset passed 123 cases. Subsequent initial registry/campaign/preparation/artifact checks passed 38 cases. Preparation fixes accumulated stock and pauses production, but transfers no ownership and does not confirm Start. The registry is disabled by default; its dedicated credential handler is wired and its auth/rate/refusal suite passed 278 cases, plus 1 startup refusal case for missing required parameters. Ready/terminal, the real battle, and result integration remain unfinished.

## See also {#related}

- [Building the project](./build.md)
- [Contributing and wiki maintenance](./contributing.md)
- [Administrator troubleshooting](../admins/troubleshooting.md)

Current WIP adds targeted checks: 17 shipping game-free 4×4 rule tests, 31 canonical document tests, 8 command-journal tests including terminal rejection without a tick, 3 profile-archive tests, 8 approval/crash/revoke tests, 10 proposal-admission tests, 11 Ready tests, and 12 Start tests. These runs are not complete qualification of the current Product build. A mock initial fixture does not replace the native runtime. Further changes require applicable consumer checks, the final suite, and a real three-process flow.

Read-only transport separately verifies no A acquisition and closure of its own handles; the Windows rename test releases only its fixture-owned WAL observer and first proves an unlocked baseline. Never stop unrelated processes for that test. Session rotation/membership/fence/profile/initial/stock/deadline changes must close proposal/Ready/Start. Replay after an anchor crash preserves the exact manifest, grant, server time, and head instead of issuing another start.

A later server checkpoint passed 623 GuildCapture/Social/DomainFormat/Lifecycle/Restore tests in 13m05 before Final/Cleanup were added. Final and immediate consumers then passed 37/37: one capture, old-stock obligation, future production and typed claims for both sides, post-Start freeze/revoke, missing/foreign artifacts, authority crashes, and terminal rejection without a tick. Cleanup/result/campaign/auth passed 57/57 in 2m22: fresh proof, expiry with a healthy lease, a persistent physical barrier after registry restart, four anchor checkpoints, and exact replay after cleanup. These runs do not add up to complete qualification of the current build.

The snapshot, simulation and template bytes in these unit fixtures are artificial. Native evidence is recorded separately below. Restricted historical-profile delivery still requires an in-game check.

Additional automated WIP checks: guild-scheduler-preparation-cancel.trx — 22/22 (scheduling, preparation, Ready offer, expiry, and tagged cleanup); battle-operator-state-green.trx — 35/35 (basis builder, checkpoint, and configuration); battle-start-wall-green.trx — 9/9; battle-lost-start-green.trx — 7/7. These are separate test snapshots, not counts to add into a full-suite total. Negative cases cover lost Start/Final responses, incorrect artifact progress/receipts, and old cleanup replay during a new assignment.

Isolated RESTORE tests verified the archive digest, source lineage, interruption recovery and admission of the bound target. An isolated three-process RimWorld run restored both colonies. A new arbiter proved the original process had exited, released the arena after pre-Start cancellation and resumed ordinary polling under the new boundary. The selected home save retained the 13 Steel and one accepted delivery. In-game recovery of an applied result and pending or interrupted deliveries, performance measurement, the full final suite and final review remain pending. Earlier prototype results apply to the prototype only. Capture is off by default in configuration.

The dev-only `Rimlink.GuildAcceptanceHost` requires an explicit stand ID and raw evidence tied to the source. The isolated three-process HOME run verified both mirrors, recovery before and after loading, reconnect and tagged cancellation. A later SLICE verified live orders from both sides, refusal of a cross-side order, zone victory and automatic transfer. Both players received the same result and returned home. The former and new owners received 100 and 1 steel respectively after logging in again. Exact claim replays returned the original receipts. Cleanup stayed pending after the arbiter's running poll was cancelled. Native retesting must verify online delivery and cleanup after the fixes.

On the build with the regular **Open prepared battle** button, both commanders entered an isolated battle with eight fighters. All three processes stayed at tick 580 before Start. Both returned through the regular button, and the home snapshot IDs and hashes matched the canonical server saves. This check covered entry and return. The battle result and delivery were outside that run.

During the following native run, a server restart interrupted an assignment between its commit and delivery to the executor. The battle expired before Start and the physical slot stayed held. Server regression tests cover the original process recovering its exact preparation or cancellation, and refusals for a different runtime or a partial initial upload. The fixed handshake still needs a native retest.

The first real independent process completed profile preflight: 7433 files, Harmony/Core/Rimlink/RimBridgeServer, with DLC disabled. The earlier refusal caused by Core’s empty ModVersion is retained separately; a missing version is now explicitly unknown while game/runtime remain pinned by SHA-256. A large inventory uses a separate compact viewer descriptor. This measures runtime/reference closure; it is not HOME/SLICE PASS or a battle. The process was closed normally and the installed game was unchanged.

## Ports for multiple game bridges {#isolated-gabs-ports}

Standalone RimBridge 2.3 uses a single fixed port; a second process in this mode may fail with socket address already in use. Simultaneous clients use the installed GABS with a private configuration through server stdio --configDir and separate game IDs/savedatafolder. This requires no changes to global configuration or other game processes. Game API results and actual paths establish readiness; transport contextDelivery unknown is not proof of context.
