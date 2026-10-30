# Rimlink World Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Abandoned colonies go to a restorable archive under configurable conditions, a returning player gets one
"while you were away" letter, and other players' settlements on the world map show an online light, a wealth tier bar and
richer inspect text.

**Architecture:** A new `archived_colonies` table plus save files moved into `<DataDir>/archive/colony-*` with a manifest;
`ColonyArchive` archives and restores, `ColonyHygiene` sweeps on a `TimeProvider` schedule, login restores automatically.
The summary is computed by a new `Away` gRPC service from existing tables since a baseline fixed at login. The map gets
new `WorldObject` fields and a `WorldObjectUpdated` event; the mod draws the light and bar in a Harmony postfix.

**Tech Stack:** .NET 10, gRPC + protobuf, EF Core SQLite (one migration), xUnit + `WebApplicationFactory`, the Harmony
mod (Unity IMGUI), vanilla-JS panel, the Discord bot's feed formatter.

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-world-hygiene-design.md`

## Global Constraints

- Protocol only grows: new field numbers, new messages, new `.proto` files; never renumber or reuse (AGENTS.md). `world.proto`
  `WorldObject` 7–9, `events.proto` oneof 32, `auth.proto` `LoginReply` 11, `WorldInfo` 5 (renumbered at the rebase onto economy-part1: 10 and 4 went to integrity).
- No version bump in this sub-project (coordinator's parallel rule; overrides AGENTS.md convention 5).
- One EF migration `WorldHygiene`. If another branch lands a migration first, regenerate this one at rebase; if a proto
  number collides, the later branch renumbers at rebase.
- Settings section `Hygiene`: `ArchiveAfterDays` 14, `ArchiveMaxWealth` 100000, `ArchiveMaxColonyDays` 60,
  `SweepHours` 6 (1–168), `WealthTiers` `[50000, 150000, 400000, 1000000]` (exactly four, strictly increasing, positive).
  `0` disables a condition; `ArchiveAfterDays = 0` disables the sweep.
- A game day is 60000 ticks; colony age in game days = current save `GameTick / 60000`.
- Hosted services take time from `TimeProvider` and expose a public `SweepDue(DateTime now)` that tests call directly.
- Every user-facing string is keyed in both languages: `mod/Languages/{English,Russian}`, panel `wwwroot/i18n/{en,ru}.json`,
  Discord `Strings.{en,ru}.json` (parity tests exist for all three).
- Game objects only on the main thread (`MainThread.Post`); Harmony patches in `Game/Patches`, one class per method, with a
  one-line summary.
- Panel text reaches the DOM only through `dom.js` `h()`/`append()`/`replace()`.
- Test command for every commit: `dotnet test Rimlink.sln -nologo -v q` green; the mod builds with
  `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'`.
- Commits: identity `Asofwar <7397608+Asofwar@users.noreply.github.com>` (`git var GIT_AUTHOR_IDENT`), imperative subject,
  no trailers. No merge, no push.

## Review Focus

1. A crash in the middle of archiving or restoring (files copied, DB not committed, or the reverse) — nothing is lost: files
   are copied and verified first, the DB commits, sources are deleted last; a rerun or the other path still finds the files.
   Test in Task 3: `ArchiveLeavesSavesUsableWhenTheCommitFails`.
2. The player logs in while the sweep archives them — one of the two wins cleanly; the login never sees half an archive.
   Test in Task 5: `LoginAfterArchiveRestoresWhenTheSweepWonTheRace`.
3. Mention matching with short names, names containing regex characters, Cyrillic names and names inside other words —
   whole-word, case-insensitive, literal. Test in Task 7: `MentionMatcherCases`.
4. A summary after a very long absence and rows exactly at `since` — lists cap at 10 (mentions 5) with `*_more`, the
   boundary row is excluded. Test in Task 7: `ListsCapAndCountTheRest`, `RowsAtSinceAreExcluded`.
5. `WorldObjectUpdated` is sent only when the tier or the age in days changed, never to the owner. Test in Task 8:
   `UploadWithSameTierAndDaySendsNoUpdate`.

---

### Task 1: Schema, options and config view

**Files:**
- Create: `src/Rimlink.Server/Hosting/HygieneOptions.cs`, `tests/Rimlink.Server.Tests/Hosting/HygieneOptionsTests.cs`
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs`, `src/Rimlink.Server/Storage/RimlinkDbContext.cs`,
  `src/Rimlink.Server/Program.cs`, `src/Rimlink.Server/Application/Admin/ServerStatus.cs` (`Config()`),
  `deploy/appsettings.example.json`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`
- Create: migration `src/Rimlink.Server/Storage/Migrations/<ts>_WorldHygiene.cs`

**Interfaces:**
- Produces: `HygieneOptions { const string Section = "Hygiene"; int ArchiveAfterDays = 14; long ArchiveMaxWealth = 100000; int ArchiveMaxColonyDays = 60; int SweepHours = 6; long[] WealthTiers = [50000,150000,400000,1000000]; }`
  validated with `.Validate(...)`/`.ValidateOnStart()` like `AdminOptions`.
- Produces: entity `ArchivedColony { long Id; long PlayerId; Player Player; int Tile; string Name; DateTime SettledAt; DateTime ArchivedAt; string Reason; string ArchivedBy; long WealthTotal; int? ColonyGameDays; DateTime? LastActiveAt; string Dir; ArchiveState State; DateTime? RestoredAt; string? RestoredBy; }`,
  `enum ArchiveState { Archived = 0, Restored = 1 }`, `DbSet<ArchivedColony> ArchivedColonies` (table `archived_colonies`,
  index `(PlayerId, State)`), `Account.LastSessionEndAt : DateTime?`, index on `admin_actions (TargetAccountId, At)`.

- [ ] **Step 1: Write failing tests**
  - `HygieneOptionsTests.DefaultsMatchTheSpec`: bound defaults are 14 / 100000 / 60 / 6 / `[50000,150000,400000,1000000]`.
  - `HygieneOptionsTests.BadValuesStopTheServer` (theory): `Hygiene:SweepHours=0`, `=169`, `Hygiene:ArchiveAfterDays=-1`,
    `Hygiene:WealthTiers:1=10` (not increasing) each make `new ServerFixture().WithSetting(k, v).CreateClient()` throw
    `OptionsValidationException` naming `Hygiene`.
  - `SchemaTests.ArchivedColoniesTableExists` (columns from the entity, index `(PlayerId, State)`),
    `SchemaTests.AccountsHaveLastSessionEnd`, `SchemaTests.AdminActionsAreIndexedByTarget`.
  - `ConfigTests` (existing file for `/config`, or add to it): `GET /api/admin/config` has section `Hygiene` with the five keys.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~HygieneOptionsTests|FullyQualifiedName~SchemaTests|FullyQualifiedName~Config" -nologo -v q`
  Expected: FAIL (types and table missing).
- [ ] **Step 3: Implement** the options class, entity, `DbSet`, indexes, binding in `Program.cs`, `Config()` section,
  `appsettings.example.json` block; then `dotnet ef migrations add WorldHygiene --project src/Rimlink.Server`.
- [ ] **Step 4: Run** the same filter. Expected: PASS. Then the whole suite: green.
- [ ] **Step 5: Commit** `git commit -m "Add the hygiene settings, the colony archive table and the session end column"`

### Task 2: Session end, away baseline, wealth 0 and reset

**Files:**
- Create: `src/Rimlink.Server/Application/Sessions/SessionEndRecorder.cs`, `tests/Rimlink.Server.Tests/Auth/SessionBookkeepingTests.cs`
- Modify: `src/Rimlink.Server/Application/Sessions/Session.cs`, `src/Rimlink.Server/Application/Auth/AuthService.cs`
  (`Complete`), `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`Store`), `src/Rimlink.Server/Application/Admin/AdminOperations.cs`
  (`ResetPlayer`), `src/Rimlink.Server/Program.cs`

**Interfaces:**
- Produces: `Session.AwaySince : DateTime?` (set in `Complete` before `LastSeenAt` is overwritten:
  `LastSessionEndAt` if later than the previous `LastSeenAt`, else the previous `LastSeenAt`; `null` on the first login).
- Produces: `SessionEndRecorder` singleton subscribed to `SessionRegistry.SessionEnded`, writes
  `Account.LastSessionEndAt = time.GetUtcNow()` in its own scope (registered and resolved like `PresenceNotifier`).
- Produces: `static DateTime? ActivityOf(Account a) => max(a.LastSeenAt, a.LastSessionEndAt)` in
  `Application/Sessions/Activity.cs` (used by Tasks 4 and 8).

- [ ] **Step 1: Write failing tests** (`SessionBookkeepingTests`)
  - `LogoutWritesTheSessionEnd`: login, `Auth.Logout`, `LastSessionEndAt` is set and ≥ `LastSeenAt`.
  - `AwaySinceIsTheLastSessionEnd`: login, logout, advance the `ManualTimeProvider`-independent DB value by setting
    `LastSessionEndAt` explicitly, login again → `session.AwaySince == LastSessionEndAt` (read through `SessionRegistry.ByPlayer`).
  - `AwaySinceFallsBackToThePreviousLoginAfterACrash`: `LastSessionEndAt = null`, previous `LastSeenAt = T` → `AwaySince == T`.
  - `FirstLoginHasNoAwaySince`.
  - `HeaderWithZeroWealthKeepsTheKnownWealth`: upload with `WealthTotal = 120000`, then with `0` → `Player.WealthTotal == 120000`.
  - `ResetClearsColonyAgeAndWealth`: after `POST /api/admin/players/{id}/reset`, `FirstSaveAt`, `WealthTotal`, `WealthItems` are null/0.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~SessionBookkeepingTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** the recorder, `Activity.ActivityOf`, the two session properties, the baseline in `Complete`,
  the zero guard in `SaveStore.Store` (keep `WealthTotal`/`WealthItems` when the header's `wealth_total == 0`), the reset clears.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Record session ends, fix the away baseline at login, keep known wealth and clear it on reset"`

### Task 3: Archive and restore

**Files:**
- Create: `src/Rimlink.Server/Application/World/ColonyArchive.cs`, `src/Rimlink.Server/Application/World/HygieneMetrics.cs`,
  `tests/Rimlink.Server.Tests/World/ColonyArchiveTests.cs`
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (archive/restore helpers), `src/Rimlink.Server/Application/Feed/FeedData.cs`,
  `src/Rimlink.Server/Program.cs`, `tests/Rimlink.Server.Tests/Feed/FeedSourcesTests.cs`

**Interfaces:**
- Consumes: Task 1 entity; `RaidService.CancelAllOf`, `VisitService.EndAllOf`, the live-session end used by kick,
  `AdminOperations` audit (make the audit writer reachable: `internal Task Audit(...)` or a small `AdminAudit` service).
- Produces: `ColonyArchive` (scoped):
  - `Task<ArchiveOutcome> Archive(long playerId, string reason, Actor actor)` — `reason` is `"auto"` or `"admin"`.
  - `Task<RestoreOutcome> Restore(long archiveId, string restoredBy)`.
  - `abstract record ArchiveOutcome { Archived(long ArchiveId); NoColony; }`,
    `abstract record RestoreOutcome { Restored(WorldObject Settlement); NotFound; AlreadyRestored; PlayerHasColony; TileTaken; Damaged; }`.
- Produces: `SaveStore.MoveToArchive(long playerId, string dir) : Task<ArchiveManifest>` (copy, verify sha, write
  `manifest.json`; does NOT touch `LastOutgoingTick`/`DeliveredTick`) and `SaveStore.RestoreFromArchive(long playerId, string dir) : Task<bool>`
  (false when a file is missing or its sha differs; nothing changed then). Source files are deleted by the caller after commit.
- Produces: feed types `colony.archived` (`ColonyArchivedData(long PlayerId, string PlayerName, string Colony, int Tile, string Reason, string Actor)`)
  and `colony.restored` (`ColonyRestoredData(long PlayerId, string PlayerName, string Colony, int Tile, string Actor)`);
  metrics `rimlink_colonies_archived_total{reason}`, `rimlink_colonies_restored_total{by}`.

- [ ] **Step 1: Write failing tests** (`ColonyArchiveTests`, real server, a settled player with two uploaded versions)
  - `ArchiveMovesSavesAndFreesTheTile`: outcome `Archived`; `save_versions` of the player empty; `archive/colony-<id>-*/` holds
    both `.rws.zst` and `manifest.json`; `World.ListObjects` lacks the settlement; another player receives `WorldObjectRemoved`;
    `archived_colonies` row has tile, name, `SettledAt`, wealth, `State = Archived`.
  - `ArchiveCancelsRaidsAndReturnsPawns`: a queued Real raid by the player → `Cancelled`, the escrowed pawns parcel back to the
    attacker (use `RaidFixtures`).
  - `ArchiveLeavesWalletLotsParcelsAndTicks`: balance, open lot, pending parcel, `LastOutgoingTick`, `DeliveredTick` unchanged.
  - `ArchiveWithoutColonyIsRefused` → `NoColony`.
  - `ArchivingAnOnlinePlayerEndsTheSession`: the player's event stream gets a `Notice`, then ends; the archive completes.
  - `ArchiveIsRefusedWhileFilesAreMissing`: a save file deleted from disk → the archive throws or refuses without deleting
    `save_versions` or the settlement (the sweep relies on this in Task 4).
  - `RestoreBringsBackTheSameSaves`: after restore, `Saves.Download` returns bytes identical to the last upload, version numbers
    and current flag as before, the settlement is back on the same tile with the same name, row `State = Restored`,
    `RestoredBy` set, the archive directory is gone.
  - `RestoreRefusals` (theory): unknown id → `NotFound`; twice → `AlreadyRestored`; tile settled by someone else → `TileTaken`;
    the player settled again → `PlayerHasColony`; a manifest sha changed → `Damaged` and nothing moved.
  - `ArchiveLeavesSavesUsableWhenTheCommitFails` (Review Focus 1): make the DB commit throw (replace `ColonyArchive`'s
    commit step via a test hook or a failing `IDbContextFactory` decorator); the player's save still downloads and
    `save_versions` is intact; the copied files may remain in the archive dir.
  - `FeedSourcesTests`: the two types are in `FeedTypes.All` and are published by archive and restore.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~ColonyArchiveTests|FullyQualifiedName~FeedSourcesTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** in this order inside `Archive`: end an online session with a Notice (as `ResetPlayer`), cancel raids,
  end visits and live sessions, `MoveToArchive` (copies + manifest), one DB transaction (delete `save_versions` and the
  `world_objects` row, insert `archived_colonies`, audit), broadcast `WorldObjectRemoved`, publish feed, metric, delete the
  source files. `Restore` mirrors it: checks, `RestoreFromArchive` copies back and verifies, one transaction (insert versions,
  settlement with `CreatedAt = SettledAt`, row state), broadcast `WorldObjectAdded`, feed, metric, delete the archive dir.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Archive a colony with its saves and restore it on the same tile"`

### Task 4: The sweep

**Files:**
- Create: `src/Rimlink.Server/Application/World/ColonyHygiene.cs` (scoped: candidates + `SweepDue`),
  `src/Rimlink.Server/Application/World/ColonyHygieneService.cs` (`BackgroundService`), `tests/Rimlink.Server.Tests/World/ColonyHygieneTests.cs`
- Modify: `src/Rimlink.Server/Program.cs`

**Interfaces:**
- Consumes: `ColonyArchive.Archive(playerId, "auto", Actor.System("system:hygiene"))` (add `Actor.System(string name)` if no such
  factory exists: account 0, name given), `Activity.ActivityOf`, `HygieneOptions`.
- Produces: `ColonyHygiene.SweepDue(DateTime now) : Task<int>` (number archived), `ColonyHygiene.IsCandidate(...)` private.
  The service runs `SweepDue(time.GetUtcNow().UtcDateTime)` one minute after start, then every `SweepHours`, one pass at a time.

- [ ] **Step 1: Write failing tests** (`ColonyHygieneTests`; players settled and uploaded through gRPC, then their
  `LastSeenAt`/`LastSessionEndAt`/`WealthTotal`/save tick set in the DB; `SweepDue(now)` called on a scope)
  - `ArchivesAnAbandonedPoorYoungColony`: 15 days idle, wealth 50000, tick 30 days → 1 archived.
  - `EachConditionProtects` (theory): 13 days idle; wealth 100000; tick 60 days (3 600 000) → 0 archived.
  - `DisabledConditionIsIgnored` (theory): `ArchiveMaxWealth=0` with wealth 900000 → archived; `ArchiveMaxColonyDays=0` with tick
    200 days → archived.
  - `SweepOffWhenArchiveAfterDaysIsZero` → 0 archived.
  - `OnlinePlayerIsSkipped`: logged in now with stale DB activity → 0.
  - `ColonyWithoutSaveIsArchived`: settled, never uploaded, 15 days idle → archived.
  - `SaveWithUnknownWealthIsSkipped`: has a save, `WealthTotal = 0` → 0.
  - `ActivityIsTheLaterOfLoginAndSessionEnd`: `LastSeenAt` 20 days ago, `LastSessionEndAt` 2 days ago → 0.
  - `OneFailureDoesNotStopTheSweep`: two candidates, the first one's save file deleted from disk before the sweep → the
    second is archived, the method returns 1.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~ColonyHygieneTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** candidate selection (one query: players with a live settlement, their account, current save tick),
  the age and wealth rules from the spec §2.3, per-candidate try/catch with a log line and metric label `failed`, the service.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Sweep abandoned colonies into the archive on a schedule"`

### Task 5: Restore at login

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/auth.proto`, `src/Rimlink.Server/Grpc/AuthGrpcService.cs` (`ToReply`),
  `src/Rimlink.Server/Application/World/ColonyArchive.cs`
- Create: `tests/Rimlink.Server.Tests/World/LoginRestoreTests.cs`

**Interfaces:**
- Produces (proto): `message ColonyRestore { bool restored = 1; string name = 2; int32 tile = 3; google.protobuf.Timestamp archived_at = 4; string reason = 5; }`,
  `LoginReply.colony_restore = 11`.
- Produces: `ColonyArchive.RestoreOnLogin(long playerId) : Task<ColonyRestore?>` — null when there is nothing archived or the
  player has a live colony; otherwise tries `Restore(latest Archived, "player")`; on `TileTaken`/`Damaged` zeroes
  `LastOutgoingTick` and delivered parcels' `DeliveredTick` (as `SaveStore.Archive` does) and returns `restored = false` with
  `reason = "tile_taken" | "damaged"`. `ToReply` calls it before `HasSettlement`/`HasSave`, stores it in the new
  `Session.ColonyRestore : Protocol.V1.ColonyRestore?` (read by Task 7) and sets `reply.ColonyRestore`.

- [ ] **Step 1: Write failing tests** (`LoginRestoreTests`)
  - `LoginRestoresAnArchivedColony`: archive (Task 3), login → `ColonyRestore.Restored`, `HasSave`, `HasSettlement` true, name and
    tile match, row `Restored`, `RestoredBy == "player"`.
  - `LoginWithTheTileTakenStartsFresh`: archive, another player settles the tile, login → `Restored == false`,
    `Reason == "tile_taken"`, `HasSave == false`, `HasSettlement == false`, `LastOutgoingTick == 0`, row stays `Archived`.
  - `LoginWithoutArchiveHasNoColonyRestore`.
  - `LoginAfterArchiveRestoresWhenTheSweepWonTheRace` (Review Focus 2): run `SweepDue` and `Login` concurrently 20 times on
    fresh players; each ends either live with saves or archived-then-restored by that login, never with neither a settlement
    nor an `Archived` row.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~LoginRestoreTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** the proto, `RestoreOnLogin`, the `ToReply` change. Serialise archive and restore per player
  (a per-player `SemaphoreSlim` in a singleton `ColonyLocks`) so the race test holds.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Restore an archived colony when its player logs in"`

### Task 6: Admin surfaces (REST, text command, CLI)

**Files:**
- Modify: `src/Rimlink.Server/Application/Admin/AdminOperations.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs`,
  `src/Rimlink.Server/Application/Admin/AdminCommandRunner.cs`, `src/Rimlink.Tools/Commands/AdminCommand.cs`
- Create: `tests/Rimlink.Server.Tests/Admin/ArchiveAdminTests.cs`; modify `tests/Rimlink.Server.Tests/Tools/AdminCommandTests.cs`

**Interfaces:**
- Produces: `AdminOperations.ListArchived(Actor, string state) : Task<List<ArchivedRow>>` (moderator),
  `ArchiveColony(Actor, long playerId) : Task<ArchiveOutcome>` (admin; audit `archive`), `RestoreColony(Actor, long archiveId) : Task<RestoreOutcome>`
  (admin; audit `restore`). `ArchivedRow(long Id, long PlayerId, string? PlayerName, string Colony, int Tile, DateTime SettledAt, DateTime ArchivedAt, string Reason, string ArchivedBy, long WealthTotal, int? ColonyGameDays, DateTime? LastActiveAt, string State, DateTime? RestoredAt, string? RestoredBy)`.
- REST: `GET /archive?state=archived|restored|all` (`moderate`, default `archived`, newest first, ≤500);
  `POST /players/{id}/archive` (`admin`) → `{id}`; `POST /archive/{id}/restore` (`admin`) → 200. Outcomes map to 404/409 with
  bodies `{"error": "no colony" | "unknown archive" | "already restored" | "player has a colony" | "tile taken" | "archive damaged"}`
  (use the existing REST error shape if one exists).
- Text command (admin role): `/archive list`, `/archive <player name|#id>`, `/archive restore <id>`; added to `Help`.
- CLI: `admin archived [state]`, `admin archive <playerId>`, `admin restore-colony <id>` (also add the missing `live` to the help text).

- [ ] **Step 1: Write failing tests**
  - `ArchiveAdminTests.ListShowsArchivedColonies` (fields above, `state=all` includes restored).
  - `ArchiveAdminTests.ArchiveAndRestoreThroughRest` (audit rows `archive`, `restore` with actor `api`).
  - `ArchiveAdminTests.RefusalsMapToStatusCodes` (theory over the outcomes).
  - `ArchiveAdminTests.ScopesAreEnforced`: a `read` token → 403 on list; a `moderate` token → 200 on list, 403 on archive/restore.
  - `ArchiveAdminTests.TextCommandArchivesAndRestores` (through `AdminCommandRunner`, admin vs moderator).
  - `AdminCommandTests`: the three CLI verbs call the right routes and print rows.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~ArchiveAdminTests|FullyQualifiedName~AdminCommandTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** the three operations, routes, command cases and CLI verbs.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "List, archive and restore colonies from REST, chat commands and the CLI"`

### Task 7: The away summary service

**Files:**
- Create: `src/Rimlink.Protocol/Protos/away.proto`, `src/Rimlink.Server/Application/Away/AwaySummaryService.cs`,
  `src/Rimlink.Server/Application/Away/MentionMatcher.cs`, `src/Rimlink.Server/Grpc/AwayGrpcService.cs`,
  `tests/Rimlink.Server.Tests/Away/AwaySummaryTests.cs`, `tests/Rimlink.Server.Tests/Away/MentionMatcherTests.cs`
- Modify: `src/Rimlink.Server/Program.cs` (service + `MapGrpcService<AwayGrpcService>`)

**Interfaces:**
- Consumes: `Session.AwaySince`, `Session.ColonyRestore` (Tasks 2, 5).
- Produces (proto): `away.proto` exactly as spec §3.2 (`service Away { rpc Summary (Empty) returns (AwaySummary); }`, messages
  `AwaySummary` 1–18, `AwayRaid` with `RaidSummary summary = 5`, `AwayVisit`, `AwayParcel`, `AwaySale`, `AwayLine`,
  `AwayAdminAction`), importing `common.proto`, `raids.proto`, `auth.proto`.
- Produces: `AwaySummaryService.Build(Session session) : Task<AwaySummary>`; `static bool MentionMatcher.Mentions(string text, string name)`
  (whole word: the characters before and after the match are not `char.IsLetterOrDigit`; ordinal ignore-case; literal, no regex
  from the name; empty name never matches).

- [ ] **Step 1: Write failing tests**
  - `MentionMatcherCases` (Review Focus 3, theory): `("hi Nova", "Nova")` true; `("hi @nova!", "Nova")` true; `("Novak here", "Nova")`
    false; `("supernova", "Nova")` false; `("Привет, Мира", "мира")` true; `("Мирал", "Мира")` false; `("a.b+c said", "a.b+c")` true;
    `("Al", "Al")` true; `("Alpha", "Al")` false; `("", "Nova")` false; `("Nova", "")` false.
  - `AwaySummaryTests` (two players; Nova logs in, logs out, the baseline is set; events are seeded through the real services or
    rows; Nova logs in again and calls `Away.Summary`):
    - `RaidsOnMeListQueuedCancelledAndExpired`, `MyRaidsCarryTheOutcome` (`summary.dead`/`loot_value` from `SummaryJson`),
    - `VisitsParcelsAndLotSalesAreListed` (buyer, price, `LotSummary` text),
    - `ShopSalesAreCountedWithSilver` (`shop_items_sold`, `shop_silver`; lot sales excluded from the shop silver),
    - `MentionsIncludeDirectMessagesAndWholeWordNames` (own lines excluded, `direct` flag),
    - `AdminActionsOnMeAreListed` (wallet change with actor `api`),
    - `ColonyRestoreIsIncluded` (after a login restore),
    - `ListsCapAndCountTheRest` (Review Focus 4: 13 parcels → 10 + `parcels_more = 3`; 7 mentions → 5 + 2),
    - `RowsAtSinceAreExcluded` (a parcel with `CreatedAt == since` is not listed),
    - `FirstLoginGivesAnEmptySummary`, `SummaryNeedsASession` (no token → `UNAUTHENTICATED`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~Away" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** the proto, matcher, one query per section bounded by `> since` and the player id (chat: rows after
  `since`, filtered in memory by the matcher; admin actions through the new index), the shop rule from spec §3.3, caps and counters.
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Summarise what happened while the player was away"`

### Task 8: World object tier, age, last seen and live updates

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/world.proto`, `src/Rimlink.Protocol/Protos/events.proto`,
  `src/Rimlink.Server/Application/World/WorldService.cs` (`ToProto` + queries that feed it), `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`Store`)
- Create: `src/Rimlink.Server/Application/World/WealthTiers.cs`, `tests/Rimlink.Server.Tests/World/WorldObjectLiveTests.cs`

**Interfaces:**
- Produces (proto): `WorldObject.wealth_tier = 7`, `owner_last_seen = 8` (`Timestamp`, UTC midnight of `ActivityOf`),
  `optional int32 colony_game_days = 9`; `message WorldObjectUpdated { WorldObject object = 1; }`;
  `ServerEvent.world_object_updated = 32`.
- Produces: `static int WealthTiers.Of(long wealth, bool known, long[] bounds)` (0 unknown; 1 below `bounds[0]`, …, 5 from `bounds[3]`).
- `ToProto` needs the owner's account (activity), wealth and the current save tick: load them in `ListObjects`/`Settle`/restore
  (one query each, no N+1).

- [ ] **Step 1: Write failing tests** (`WorldObjectLiveTests`)
  - `TierBoundaries` (theory over `WealthTiers.Of`): 0 unknown → 0; 49999 → 1; 50000 → 2; 149999 → 2; 150000 → 3; 400000 → 4;
    999999 → 4; 1000000 → 5.
  - `ListObjectsCarriesTierAgeAndLastSeen`: wealth 200000, tick 2 700 000 → tier 3, `colony_game_days == 45`, `owner_last_seen`
    is midnight UTC of the login day.
  - `NoSaveMeansNoAge`: `HasColonyGameDays == false`, tier 0.
  - `UploadThatChangesTheTierBroadcastsAnUpdate`: Mira listens, Nova uploads with wealth 600000 → Mira gets `WorldObjectUpdated`
    with tier 4; Nova's own stream does not.
  - `UploadWithSameTierAndDaySendsNoUpdate` (Review Focus 5).
  - `ExactWealthIsNeverSent`: no field of `WorldObject` equals the exact wealth (guard: the proto has no wealth number field).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~WorldObjectLiveTests" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement** the proto fields, `WealthTiers`, `ToProto`, the before/after comparison in `SaveStore.Store` and the
  broadcast (`EventBus.BroadcastExcept(owner)`).
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Send wealth tier, colony age and last seen with settlements and update them live"`

### Task 9: Client core

**Files:**
- Modify: `src/Rimlink.Client.Core/State/SharedState.cs`, `src/Rimlink.Client.Core/Net/RimlinkConnection.cs`
- Modify/Create: `tests/Rimlink.Client.Tests/State/SharedStateTests.cs`

**Interfaces:**
- Produces: `WorldObjectsIndex.Update(WorldObject o) : bool` (replace by id; false if unknown), `SharedState.ObjectUpdated : event Action<WorldObject>`
  raised for `ServerEvent.WorldObjectUpdated`; `RimlinkConnection.AwayApi : Protocol.V1.Away.AwayClient`.

- [ ] **Step 1: Write failing tests**: `SharedStateTests.WorldObjectUpdatedReplacesTheEntryAndRaisesTheEvent`,
  `SharedStateTests.UpdateOfAnUnknownObjectIsIgnored`; `RimlinkConnectionTests.AwaySummaryRoundTrips` (against the test server,
  first login → empty summary).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests --filter "FullyQualifiedName~SharedStateTests|FullyQualifiedName~AwaySummaryRoundTrips" -nologo -v q`
  Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Apply settlement updates and expose the away summary in the client core"`

### Task 10: Discord feed events

**Files:**
- Modify: `src/Rimlink.Discord/Feed/FeedTypes.cs` (consts + `Server` group), `src/Rimlink.Discord/Feed/Formatter.cs`,
  `src/Rimlink.Discord/Strings/Strings.en.json`, `Strings.ru.json`, `tests/Rimlink.Discord.Tests/Feed/FormatterTests.cs`
- Modify: `docs/superpowers/specs/2026-10-29-rimlink-discord-bot-design.md` §2 (two new types)

**Interfaces:**
- Consumes: Task 3 DTOs (`colony.archived`: playerId, playerName, colony, tile, reason, actor; `colony.restored`: playerId,
  playerName, colony, tile, actor), camelCase JSON.
- Produces: `feed.colony.archived.auto` = "Colony **{colony}** of {player} went to the archive (inactive)",
  `feed.colony.archived.admin` = "… archived by {actor}", `feed.colony.restored` = "Colony **{colony}** of {player} is back ({actor})";
  Russian equivalents per spec §5. Player and colony names pass through `Formatter.Escape`.

- [ ] **Step 1: Write failing tests**: `FormatterTests` samples for both types (auto and admin reasons; a name with `@everyone`
  is escaped); `StringsTests` parity passes only with both languages.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests -nologo -v q`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Discord tests, then the whole suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `git commit -m "Post archived and restored colonies to the Discord server channel"`

### Task 11: Panel

**Files:**
- Create: `src/Rimlink.Server/wwwroot/admin/views/archive.js`
- Modify: `src/Rimlink.Server/wwwroot/admin/app.js` (route `#/archive`, menu after Players), `admin/views/player.js`
  (fact + danger-zone button), `wwwroot/i18n/en.json`, `ru.json`, `wwwroot/style.css` (only if needed),
  `tests/Rimlink.Server.Tests/Panel/FrontendGuardTests.cs` (only if a new rule is needed)

**Interfaces:**
- Consumes: Task 6 REST.
- Produces: screen `#/archive`: state filter (archived / restored / all), table (player via `playerLink(id, name)`, colony, tile,
  archived at, reason + by, wealth, age in game days, state, restored at/by), "Restore" with a confirmation naming the colony;
  a refusal shows its reason in a banner. Card: fact "In archive: {colony} since {date}" when the player has an `Archived` row
  (from `GET /archive?state=archived` filtered by player id, or a new `archived` field on the card — pick the card field if
  `AdminDirectory.Card` change is one line; ledger the choice); "Archive colony" in the danger zone with typed-name confirmation.

- [ ] **Step 1: Run `impeccable`** (`critique` the new screen against the existing panel: Operate mode, same tokens) — no
  new visual language.
- [ ] **Step 2: Write failing test**: `FrontendGuardTests` key parity and "used keys exist" fail until the new keys exist in both
  languages (run them after adding the view, before the keys).
- [ ] **Step 3: Implement** view, route, card changes, i18n keys.
- [ ] **Step 4: Verify**: `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~FrontendGuardTests" -nologo -v q` PASS;
  `node --input-type=module --check` on each changed module; headless Chrome screenshots at 375 and 1280 (en, ru) of
  `#/archive` and the card with an archived colony on the s5 server: no horizontal page overflow; Restore round trip works.
- [ ] **Step 5: Commit** `git commit -m "Show archived colonies in the panel with restore and archive actions"`

### Task 12: The mod

**Files:**
- Modify: `src/Rimlink.Client/UI/LoginFlow.cs` (colony restore window), `src/Rimlink.Client/Game/RimlinkSession.cs`
  (a `SummaryPending` flag set by a login load), `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (request the summary on the first
  frame of a home load), `src/Rimlink.Client/Game/PlayerSettlement.cs` (fields, inspect), `src/Rimlink.Client/Game/ForeignSettlements.cs`
  (copy and update the new fields), `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`
- Create: `src/Rimlink.Client/Game/Away/AwayLetter.cs`,
  `src/Rimlink.Client/Game/Patches/Patch_ExpandableWorldObjectsUtility_ExpandableWorldObjectsOnGUI.cs`

**Interfaces:**
- Consumes: Tasks 5, 7, 8, 9.
- `LoginFlow`: when `Login.ColonyRestore` is present with `Restored == false`, show `Dialog_MessageBox` with
  `Rimlink.Archive.TileTaken` ({0} colony name) or `Rimlink.Archive.Damaged`, then continue to the new-colony page as today.
- `AwayLetter.Post(AwaySummary s)`: nothing when every list is empty, counters are 0 and `colony_restore` is unset; otherwise
  `Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent)` with `Rimlink.Away.Title` ({0} = duration from `since`)
  and one paragraph per non-empty section (`Rimlink.Away.RaidsOnMe`, `.MyRaids`, `.Visits`, `.Parcels`, `.LotSales`, `.Shop`,
  `.Mentions`, `.Admin`, `.Restored`, `.More` for "and {0} more").
- The summary is requested once per login, only for the home colony (`!VisitSession.InVisit`), in the background; the result is
  posted with `MainThread.Post`; a failure is one log line.
- `PlayerSettlement`: `OwnerPlayerId`, `FactionName`, `WealthTier`, `OwnerLastSeen` (`DateTime?`), `ColonyGameDays` (`int?`);
  `Online => RimlinkSession.State.Players.Online.Any(p => p.PlayerId == OwnerPlayerId)`; inspect lines per spec §4.5
  (`Rimlink.Settlement.Inspect` gains the faction, plus `.Online`, `.SeenToday`, `.SeenYesterday`, `.SeenDaysAgo`, `.Age`, `.Wealth`).
- The patch (postfix, "draws the online light and the wealth bar on other players' expanded settlement icons"): for each
  `PlayerSettlement` with `!HiddenByRules(wo)` and `TransitionPct(wo) > 0`, rect = `ExpandedIconScreenRect(wo)`, alpha = the
  icon's; a dot of `rect.width * 0.3` at the top-right (green `#4CC24C` online, grey `#8C8C8C` offline, 1 px dark outline), and
  below the rect five segments of `rect.width / 5 - 1` × 3 px, filled up to `WealthTier` (light `#E6E6E6`, empty `#3A3A3A`);
  nothing for tier 0. Only `BaseContent.WhiteTex` + `GUI.color`.
- `ForeignSettlements`: subscribe `State.ObjectUpdated` → update the spawned settlement's fields; on `PlayerLeft` set the owner's
  `OwnerLastSeen` to today.

- [ ] **Step 1: Decompile and confirm** `ExpandableWorldObjectsUtility.ExpandableWorldObjectsOnGUI`, `ExpandedIconScreenRect`,
  `TransitionPct`, `HiddenByRules` signatures in the 1.7 assembly (`ilspycmd -t RimWorld.Planet.ExpandableWorldObjectsUtility`)
  and the `LetterStack.ReceiveLetter` overload; ledger anything that differs.
- [ ] **Step 2: Implement** the pieces above and the keyed strings in both languages.
- [ ] **Step 3: Build** `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'`.
  Expected: build succeeds, `Rimlink.Client.dll` assembled. Whole suite green.
- [ ] **Step 4: Commit** `git commit -m "Show the away letter, the archive window and live settlement markers in game"`

### Task 13: Documentation

**Files:**
- Modify: `docs/server-setup.md` (section `Hygiene`, archive/restore REST + CLI + command, metrics, bot events),
  `docs/player-guide.md` (letter, map markers, long absence), `README.md` (status), `docs/manual-checklist.md`
  (new section "Уборка мира и сводка" with the s5 steps from spec §7, unchecked)

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Verify** every config key in `deploy/appsettings.example.json` appears in `server-setup.md` (grep each key).
- [ ] **Step 3: Commit** `git commit -m "Document the colony archive, the away letter and the map markers"`

### Task 14: Stand s5 and the in-game run

**Files:**
- Outside the repo: `D:\Build\rimlink-s5\run-server.sh` (port 25620, DataDir `D:\Build\rimlink-s5\server`, `AllowPassword`,
  `AdminLogins__0=s5-admin`, `Admin__ApiToken=s5test`, raid cooldowns 0), `~/.gabs/config.json` entries `rimlink-s5-host` /
  `rimlink-s5-guest` (backup first; edit with a `jq -f` filter file from PowerShell), saves `D:\Build\rimlink-s5\{host,guest}`.
- Modify: `docs/manual-checklist.md` (tick the lines with results).

- [ ] **Step 1: Deploy** with both s5 games stopped: `Build-ClientMod.ps1 -RimWorldDir D:\Build\rimlink-s5\RimWorld -InstallTo D:\Build\rimlink-s5\RimWorld\Mods`,
  `Build-BridgeTools.ps1 -RimWorldDir D:\Build\rimlink-s5\RimWorld`.
- [ ] **Step 2: Run** the checklist section: host `s5-admin` and guest `s5-guest` colonies; archive the offline guest through the
  panel → host sees the tile freed (`rimlink/status` objects) and the bot feed line (`GET /api/admin/events`); guest logs in →
  colony back, letter mentions the restore; tile-taken path (host's second account settles the tile) → window, new colony;
  summary letter after a raid, a parcel, a lot sale, a mention and a wallet change; light switches on guest login/logout;
  bar and inspect on ru and en (screenshots cropped from the window).
- [ ] **Step 3: Record** results in the checklist and commit `git commit -m "Record the in-game run of the world hygiene features"`.

---

Final: one review of the whole branch (`code-reviewer` on the most capable model or Codex read-only via `codex-run.ps1 -PromptFile`),
one fix pass with RED→GREEN tests, rebase onto `origin/main`, full suite, mod build, report the head to the coordinator.
