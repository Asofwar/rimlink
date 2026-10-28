# Rimlink Raids and Visits (3a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A player raids another player's colony (the defender's game fights it as a vanilla raid whenever they next play) and visits a copy of another player's colony with a shop, with neither side required to be online at the same time.

**Architecture:** The server gains a raid state machine (`raids` table, escrowed pawn blobs, timings, cooldowns, protection), visit tickets, a per-player shop list and pending removals; every movement of things or pawns between games stays a mailbox parcel (new kinds). The defender's client spawns the raid as a hostile lord and reports the outcome; the visitor's client loads the host's save as a temporary game. Client-core gets typed clients, raid state and new outbox kinds; the game layer gets `RaidOutgoing`/`RaidIncoming`/`RaidResolver`, `VisitSession` and the shop.

**Tech Stack:** .NET 10, Grpc.AspNetCore, EF Core Sqlite (migrations), xUnit + `WebApplicationFactory` (`ServerFixture`), Lib.Harmony 3.0.0-rc.3, RimWorld 1.7 (`Assembly-CSharp.dll`, `UnityStubs.dll`), ZstdSharp.

**Spec:** `docs/superpowers/specs/2026-10-28-rimlink-raids-visits-design.md` (builds on `2026-10-28-rimlink-economy-design.md` and `2026-10-28-rimlink-core-design.md`).

## Global Constraints

- Protocol only grows: new files `raids.proto`, `visits.proto` (services `Raids`, `Visits`, `Shop`); `ErrorCode` values appended after `SELF_TRADE = 19`; `ServerEvent.body` fields 60–66; `ParcelKind` += `PARCEL_LOOT = 5`, `PARCEL_RAID_RETURN = 6`, `PARCEL_CAPTIVE = 7`; `SaveHeader` += `int64 wealth_total = 6`, `int64 wealth_items = 7`; `Saves` += `rpc DownloadOf (PlayerRef) returns (stream SaveChunk)`. Nothing renumbered.
- Storage: one additive EF Core migration `Raids` (tables `raids`, `raid_outcomes`, `visits`, `shop_items`, `pending_removals`; `players` += `Peaceful`, `PeacefulOffAt`, `VisitsClosed`, `FirstSaveAt`, `WealthTotal`, `WealthItems`).
- Config `Raids:*` (spec §3, defaults): `Enabled` true, `MaxRaiders` 10, `MaxActiveRaidsPerAttacker` 1, `TargetCooldownHours` 8, `PairCooldownHours` 24, `NewbieProtectionDays` 3, `MinColonyWealth` 20000, `LootCapPercent` 10, `LootCapSilver` 5000, `AllowKidnap` false, `WarnTicks` 60000, `TimeoutDays` 3, `ExpireRealDays` 7, `PeacefulOffDelayHours` 24.
- Error codes (spec §5): `RAID_COOLDOWN = 20`, `TARGET_PROTECTED = 21`, `PEACEFUL_MODE = 22`, `RAID_ACTIVE = 23`, `RAID_GONE = 24`, `LOOT_OVER_CAP = 25`, `VISITS_CLOSED = 26`, `ITEM_SOLD = 27`. A frozen/disabled raid system answers `ECONOMY_FROZEN` (own `RaidFreezeState`).
- Loot multipliers: Instant ×0.5, Snapshot ×0.5, cumulative. Loot cap = `min(wealth_items × LootCapPercent / 100, LootCapSilver)` × multipliers, in silver, compared against `Σ market_value_x100 × count / 100` of the loot blobs.
- Every money movement stays a `WalletEntry`; shop sales credit the host minus `Economy:MarketFeePercent` to the bank (player 0); events only after the transaction commits (`WalletService.FlushEvents()`).
- Raid pawn blobs and reports are parcels in size: ≤ `Limits.EconomyMaxParcelBytes` per `Launch`/`Report`/`Buy` message, ≤ `MaxRaiders` pawns. `Launch` and `Buy` go through the existing cooldown interceptor (`Economy:SendCooldownSeconds`).
- Game code touches Verse/UI only on the main thread (`UI.MainThread.Post`); every RPC that moves things or confirms state goes through `TransferOutbox` (new kinds `Launch`, `Land`, `Report`, `Buy`, `AckRemoval`); a Real raid launch runs the `Outgoing.Run` pipeline (take → save → upload → RPC).
- Harmony: no unconditional `return false` prefixes; new patches: `Thing.GetGizmos` postfix (shop gizmo, home map only), `GameDataSaveLoader.SaveGame` prefix extension (block saving in visit mode), `WorldObject.GetTransportPodsFloatMenuOptions` is an override on `PlayerSettlement`.
- TDD per task; `dotnet test Rimlink.sln` green before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no attribution lines. Branch `raids` (holds the spec commit). Mod/client version `0.3.0`.
- Russian + English Keyed strings for every new UI string.
- Entity names `Raid`, `Visit`, `ShopItem` clash with proto types: use `using Raid = Rimlink.Server.Storage.Entities.Raid;` aliases as the economy services do.

## Review Focus

1. The defender's game crashes between `Land` and the report (or the defender reloads their save): the raid must still resolve exactly once from the `GameComponent` state, and a save older than the landing must not redeliver the raid as a second wave (Task 3 test `LandIsIdempotentAndASaveOlderThanLandingDoesNotRequeue`, Task 7 `ExposeData` step).
2. The target resets their colony (or is deleted) while a Real raid is queued or announced: `Announce`/`Land` answer `RAID_GONE`, the attacker's pawns come back as `RAID_RETURN`, nothing is lost or duplicated (Task 3 test `RaidOnAResetTargetReturnsThePawns`).
3. Two visitors buy the same shop item in the same instant, or one buys after the host lowered the count: exactly one sale, one parcel, one removal; the other gets `ITEM_SOLD` and no wallet entry (Task 4 test `TwoBuyersRaceOnOneItemOnlyOnePays`).
4. The defender reports loot exactly at the cap, one silver over it, and with an item whose def is not in the modpack: at-cap passes, over-cap is `LOOT_OVER_CAP` with no state change, unknown def is `DEF_NOT_IN_MODPACK` (Task 3 test `ReportLootAtCapPassesAndOverCapIsRefused`, `ReportWithAnUnknownDefIsRefused`).
5. The host changed their modpack (or the visitor lacks a mod) so the host's save cannot load on the visitor: `VisitSession` must catch the load failure, end the visit on the server and reload the visitor's own save instead of leaving the game on a half-loaded map (Task 8 step on load failure; manual checklist).

---

### Task 1: Protocol, storage, options and player flags

**Files:**
- Create: `src/Rimlink.Protocol/Protos/raids.proto`, `src/Rimlink.Protocol/Protos/visits.proto`
- Modify: `common.proto` (ErrorCode 20–27), `events.proto` (imports + oneof 60–66), `economy.proto` (`ParcelKind` 5–7), `saves.proto` (`wealth_total`, `wealth_items`, `DownloadOf`), `Rimlink.Protocol.csproj`
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs`, `Storage/RimlinkDbContext.cs`, `Hosting/ServerOptions.cs` (`RaidOptions`, section `Raids`), `Program.cs` (Configure)
- Create: `src/Rimlink.Server/Storage/Migrations/<ts>_Raids.cs` (generated), `src/Rimlink.Server/Application/Raids/RaidFreezeState.cs`
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs` (extend), `tests/Rimlink.Server.Tests/Raids/RaidOptionsTests.cs`

**Interfaces:**
- Produces proto (`package rimlink.v1`, `csharp_namespace Rimlink.Protocol.V1`), `raids.proto`:
  ```proto
  enum PawnMode { PAWN_REAL = 0; PAWN_SNAPSHOT = 1; }   enum RaidTiming { TIMING_WARNED = 0; TIMING_INSTANT = 1; }
  enum RaidArrival { ARRIVAL_EDGE = 0; ARRIVAL_PODS = 1; }   enum RaidController { CONTROLLER_AI = 0; CONTROLLER_LIVE = 1; }
  enum RaidState { RAID_QUEUED = 0; RAID_ANNOUNCED = 1; RAID_LANDED = 2; RAID_RESOLVED = 3; RAID_EXPIRED = 4; RAID_CANCELLED = 5; }
  enum PawnOutcome { OUTCOME_DEAD = 0; OUTCOME_CAPTURED = 1; OUTCOME_FLED = 2; OUTCOME_ALIVE = 3; }
  message LaunchRequest { string client_request_id = 1; int64 target_player_id = 2; PawnMode pawn_mode = 3; RaidTiming timing = 4; RaidArrival arrival = 5; PawnList pawns = 6; int64 game_tick = 7; }
  message Raid { int64 id = 1; PlayerRef attacker = 2; PlayerRef target = 3; PawnMode pawn_mode = 4; RaidTiming timing = 5; RaidArrival arrival = 6; RaidController controller = 7; RaidState state = 8; PawnList pawns = 9; int64 loot_cap = 10; google.protobuf.Timestamp created_at = 11; int64 announced_tick = 12; int64 land_at_tick = 13; int64 landed_tick = 14; google.protobuf.Timestamp expires_at = 15; RaidSummary summary = 16; bool allow_kidnap = 17; }
  message RaidSummary { int32 dead = 1; int32 captured = 2; int32 fled = 3; int32 alive = 4; int64 loot_value = 5; int32 captives = 6; }
  message RaidList { repeated Raid raids = 1; }   message RaidRef { int64 raid_id = 1; }   message RaidTick { int64 raid_id = 1; int64 game_tick = 2; }
  message PawnOutcomeMsg { int32 index = 1; PawnOutcome outcome = 2; PawnBlob blob = 3; }
  message RaidReport { int64 raid_id = 1; int64 game_tick = 2; repeated PawnOutcomeMsg outcomes = 3; ItemList loot = 4; PawnList captives = 5; }
  message PeacefulRequest { bool peaceful = 1; }   message VisitsClosedRequest { bool closed = 1; }
  message PlayerFlags { bool peaceful = 1; google.protobuf.Timestamp peaceful_off_at = 2; bool visits_closed = 3; }
  service Raids { rpc Launch (LaunchRequest) returns (Raid); rpc Pending (Empty) returns (RaidList); rpc Get (RaidRef) returns (Raid); rpc Announce (RaidTick) returns (Raid); rpc Land (RaidTick) returns (Empty); rpc Report (RaidReport) returns (Empty); rpc Mine (Empty) returns (RaidList); rpc Flags (Empty) returns (PlayerFlags); rpc SetPeaceful (PeacefulRequest) returns (PlayerFlags); rpc SetVisitsClosed (VisitsClosedRequest) returns (PlayerFlags); }
  ```
  `visits.proto`:
  ```proto
  message VisitRequest { int64 target_player_id = 1; int64 game_tick = 2; }   message VisitTicket { int64 visit_id = 1; PlayerRef host = 2; }   message VisitRef { int64 visit_id = 1; }
  message ShopItem { int64 thing_id = 1; string def_name = 2; string stuff_def = 3; int32 count = 4; int32 quality = 5; int32 hit_points = 6; int64 price = 7; int64 save_tick = 8; string label = 9; }
  message ShopList { PlayerRef host = 1; repeated ShopItem items = 2; }
  message BuyRequest { string client_request_id = 1; int64 host_player_id = 2; int64 thing_id = 3; int32 count = 4; ItemBlob blob = 5; }
  message Removal { int64 id = 1; int64 thing_id = 2; int32 count = 3; }   message RemovalList { repeated Removal removals = 1; }   message RemovalAck { repeated int64 ids = 1; int64 game_tick = 2; }
  service Visits { rpc Begin (VisitRequest) returns (VisitTicket); rpc End (VisitRef) returns (Empty); }
  service Shop { rpc Publish (ShopList) returns (Empty); rpc Of (PlayerRef) returns (ShopList); rpc Buy (BuyRequest) returns (Parcel); rpc PendingRemovals (Empty) returns (RemovalList); rpc AckRemoval (RemovalAck) returns (Empty); }
  ```
  Events (`events.proto`): `RaidQueued raid_queued = 60` (`Raid raid` descriptor, no blobs), `RaidLanded raid_landed = 61` (`int64 raid_id`, `PlayerRef target`), `RaidResolved raid_resolved = 62` (`int64 raid_id`, `RaidSummary summary`), `RaidExpired raid_expired = 63` (`int64 raid_id`), `ItemSold item_sold = 64` (`int64 thing_id`, `int32 count`, `int64 price`, `PlayerRef buyer`), `VisitStarted visit_started = 65` (`PlayerRef visitor`), `PlayerFlagsChanged player_flags_changed = 66` (`PlayerFlags flags`).
- Produces entities: `Raid { Id, AttackerPlayerId, TargetPlayerId, PawnMode (int), Timing, Arrival, Controller, State (RaidStateDb), Pawns byte[], LootCap long, CreatedAt, AnnouncedTick long?, LandAtTick long?, LandedTick long?, ResolvedAt?, ExpiresAt, SummaryJson string?, ClientRequestId string? }` (unique filtered index `AttackerPlayerId, ClientRequestId`); `RaidOutcome { Id, RaidId, Index, Outcome }`; `Visit { Id, VisitorPlayerId, HostPlayerId, StartedAt, EndedAt? }`; `ShopItem { Id, HostPlayerId, ThingId, DefName, StuffDef, Count, Quality, HitPoints, Price, SaveTick, Label, SoldAt? }` (unique `HostPlayerId, ThingId`); `PendingRemoval { Id, HostPlayerId, ThingId, Count, CreatedAt, AckedAt? }`; `Player` += `Peaceful bool`, `PeacefulOffAt DateTime?`, `VisitsClosed bool`, `FirstSaveAt DateTime?`, `WealthTotal long`, `WealthItems long`. `enum RaidStateDb { Queued, Announced, Landed, Resolved, Expired, Cancelled }`.
- Produces `RaidOptions` (all keys above) and `RaidFreezeState { bool Frozen; string? Reason }` singleton.
- `SaveStore.Store` sets `Player.FirstSaveAt` when null and copies `header.WealthTotal/WealthItems` to the player (test in Task 3).

- [ ] **Step 1: Failing tests** — `SchemaTests`: `RaidsMigrationCreatesTheTables` (tables `raids`, `raid_outcomes`, `visits`, `shop_items`, `pending_removals` exist; `players` has `peaceful`, `wealth_items`; unique index on `shop_items(host_player_id, thing_id)`); `RaidOptionsTests.DefaultsMatchTheSpec` (every value from Global Constraints) and `SettingsOverrideDefaults` (`Raids:MaxRaiders` 3).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~RaidOptionsTests"` — Expected: FAIL (compile: missing types).
- [ ] **Step 3: Implement** the proto files, entities, `DbContext` configuration (snake_case names as the existing tables, CHECK `count >= 0` on `shop_items`), options, freeze state; generate the migration with `dotnet ef migrations add Raids --project src/Rimlink.Server`; `SaveStore.Store` fields.
- [ ] **Step 4: Run** the same filter — Expected: PASS; `dotnet test Rimlink.sln` green (existing tests unaffected).
- [ ] **Step 5: Commit** — `git commit -m "Add the raid, visit and shop protocol, storage and options"`.

---

### Task 2: Raid service — launch, pending, announce, land, flags

**Files:**
- Create: `src/Rimlink.Server/Application/Raids/RaidService.cs`, `.../Raids/RaidGate.cs`, `.../Raids/RaidRules.cs`, `src/Rimlink.Server/Grpc/RaidsGrpcService.cs`
- Modify: `Program.cs` (DI + `MapGrpcService<RaidsGrpcService>`), `Grpc/RateLimitInterceptor.cs` (cooldown methods += `/rimlink.v1.Raids/Launch`, `/rimlink.v1.Shop/Buy`), `Grpc/RateLimitPolicy.cs` (same two, `(1, 5 s)`)
- Test: `tests/Rimlink.Server.Tests/Raids/RaidTests.cs`, `tests/Rimlink.Server.Tests/Support/RaidFixtures.cs`

**Interfaces:**
- Consumes: Task 1; `MailboxService.Create(...)`, `ParcelValidator.Validate(items, pawns)`, `WalletService`, `EventBus.SendTo`, `RpcErrors.With`, `SaveStore`, `ModpackDefs`.
- Produces:
  - `RaidGate(IOptions<RaidOptions>, RaidFreezeState)`: `void RequireOpen()` → `ECONOMY_FROZEN` when `!Enabled` or frozen.
  - `RaidRules(IOptions<RaidOptions>, RimlinkDbContext db)`: `Task<ErrorCode?> CheckLaunch(Player attacker, Player target, DateTime now)` → `PEACEFUL_MODE` (attacker or target peaceful, including `PeacefulOffAt > now`), `TARGET_PROTECTED` (`FirstSaveAt == null || now - FirstSaveAt < NewbieProtectionDays` or `WealthTotal < MinColonyWealth`), `RAID_COOLDOWN` (a raid on the target created within `TargetCooldownHours`, or by this pair within `PairCooldownHours`, states any but Cancelled), `RAID_ACTIVE` (attacker has a raid not in Resolved/Expired/Cancelled); `long LootCap(Player target, PawnMode mode, RaidTiming timing)`.
  - `RaidService(db, mailbox, validator, gate, rules, events, options)`:
    - `Task<Raid> Launch(long attackerId, LaunchRequest r)` — serializable tx: gate, `SELF_TRADE` when target == attacker, `RECIPIENT_UNKNOWN`, validator on `(null, r.Pawns)` (count ≤ `MaxRaiders`, bytes, defs), rules, idempotent by `ClientRequestId`; stores `Raid` in `Queued` with `ExpiresAt = now + ExpireRealDays`, `LootCap`; stamps `attacker.LastOutgoingTick = r.GameTick` when `PAWN_REAL`; after commit: `RaidQueued` to the target.
    - `Task<List<Raid>> Pending(long targetId)` (Queued|Announced, for the target), `Task<Raid> Get(long playerId, long raidId)` (attacker or target, else NotFound), `Task<List<Raid>> Mine(long attackerId)`.
    - `Task<Raid> Announce(long targetId, long raidId, long gameTick)` — Queued → Announced, `AnnouncedTick = gameTick`, `LandAtTick = gameTick + (Warned ? WarnTicks : 0)`; idempotent when already Announced/Landed (returns the row); `RAID_GONE` when Cancelled/Expired/missing.
    - `Task Land(long targetId, long raidId, long gameTick)` — Announced → Landed, `LandedTick = gameTick`; idempotent; stamps `target.LastOutgoingTick = gameTick` (a save older than the landing must not recreate the pre-raid colony); after commit `RaidLanded` to the attacker.
    - `Task<PlayerFlags> SetPeaceful(long playerId, bool on)` (off → `PeacefulOffAt = now + PeacefulOffDelayHours`, `Peaceful` stays true until then; `RaidRules` treats `Peaceful || PeacefulOffAt > now` as peaceful; on → `Peaceful = true`, `PeacefulOffAt = null`), `SetVisitsClosed`, `Flags`; `PlayerFlagsChanged` event to the player.
  - `static Protocol.V1.Raid ToProto(Raid r, Player attacker, Player target, bool includeBlobs)`.
  - `RaidFixtures`: `RaidServer()` (= `EconomyServer()` + `Raids:NewbieProtectionDays` 0 + `Raids:MinColonyWealth` 0 so plain tests can raid), `Launch(long target, int pawns = 2, PawnMode mode = Real, RaidTiming timing = Warned, RaidArrival arrival = Edge, long tick = 100, string? requestId = null)`, `Clients(fixture, token)` += `Raids`, `Visits`, `Shop` clients, `MakeTargetRaidable(fixture, playerId, wealthItems = 100000)` (sets `FirstSaveAt = now - 10 d`, wealth columns via a scope).

- [ ] **Step 1: Failing tests** in `RaidTests.cs`:

```csharp
[Fact] public async Task LaunchQueuesARaidTheTargetSeesAndAnEvent()      // nova → bob, 2 pawns Real tick 500: Queued, bob Pending has it without blobs, bob's stream yields RaidQueued, nova.LastOutgoingTick == 500, LootCap == min(100000*10%, 5000) == 5000
[Fact] public async Task SnapshotLaunchDoesNotStampTheAttacker()         // LastOutgoingTick stays 0; LootCap == 2500
[Fact] public async Task InstantSnapshotLootCapIsAQuarter()              // 1250
[Fact] public async Task SameClientRequestIdReturnsTheSameRaid()
[Theory] [InlineData(11)] public async Task TooManyRaidersIsParcelTooBig(int pawns)
[Fact] public async Task LaunchingAtYourselfIsSelfTrade()
[Fact] public async Task SecondActiveRaidIsRaidActive()
[Fact] public async Task TargetRaidedWithinEightHoursIsRaidCooldown()    // nova raids bob; alice raids bob → RAID_COOLDOWN
[Fact] public async Task NewTargetIsTargetProtected()                    // EconomyServer() (spec defaults): bob with FirstSaveAt == now → TARGET_PROTECTED; FirstSaveAt 10 days ago but WealthTotal 100 → TARGET_PROTECTED
[Fact] public async Task PeacefulPlayerCannotRaidOrBeRaided()            // bob SetPeaceful(true) → PEACEFUL_MODE both ways; bob SetPeaceful(false) → still PEACEFUL_MODE (PeacefulOffAt in 24 h); Flags shows peaceful_off_at
[Fact] public async Task AnnounceSetsLandAtTickAndIsIdempotent()         // Warned: land_at == announced + 60000; second Announce same; Instant: land_at == announced
[Fact] public async Task LandStampsTheTargetAndNotifiesTheAttacker()     // bob Land tick 70000 → state Landed, bob.LastOutgoingTick == 70000, nova's stream yields RaidLanded
[Fact] public async Task GetCarriesBlobsForBothSidesOnly()               // nova and bob Get OK with blobs; alice → NotFound
[Fact] public async Task LaunchIsRateLimitedPerPlayer()                  // WithRateLimits: two Launch calls (different targets, second attacker state Resolved via scope) within 5 s → ResourceExhausted
```

- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~RaidTests` — Expected: FAIL (missing services).
- [ ] **Step 3: Implement** per Interfaces; `Pending`/`Mine` strip blobs (`MailboxService.StripBlobs`).
- [ ] **Step 4: Run** the filter — Expected: PASS; `dotnet test Rimlink.sln` green.
- [ ] **Step 5: Commit** — `git commit -m "Add raid launch, announcement, landing and player flags"`.

---

### Task 3: Raid report, expiry, cancellation, admin and metrics

**Files:**
- Create: `src/Rimlink.Server/Application/Raids/RaidReportValidator.cs`, `.../Raids/RaidExpiryService.cs` (BackgroundService, hourly, `RaidService.ExpireDue(now)`), `.../Raids/RaidMetrics.cs`
- Modify: `RaidService.cs` (`Report`, `ExpireDue`, `Cancel`), `Application/Admin/AdminOperations.cs` (`ListRaids`, `CancelRaid`, `SetRaidsFrozen`, `RaidStats`, `SetPeaceful(actor, id, on)` without delay), `AdminCommandRunner.cs` (`raids list|cancel|freeze|unfreeze|stats`, `player peaceful <id> on|off`), `Rest/AdminEndpoints.cs` (`/api/admin/raids/*`), `src/Rimlink.Tools/.../AdminCommand.cs`, `SaveStore.Archive` (raids of the archived player: Queued/Announced/Landed where target → Cancelled with pawn return; attacker's own → Cancelled, pawns dropped since the colony is gone)
- Test: `RaidTests.cs` (extend), `tests/Rimlink.Server.Tests/Raids/RaidAdminTests.cs`

**Interfaces:**
- Consumes: Task 2; `ModpackDefs`, `WalletService`, `AuditLog`, `EconomyMetrics` style.
- Produces:
  - `RaidReportValidator(IOptions<RaidOptions>, ModpackDefs)`: `ErrorCode? Validate(Raid raid, RaidReport report, out long lootValue)` → `LOOT_OVER_CAP` when `lootValue > raid.LootCap`, `DEF_NOT_IN_MODPACK` (defs loaded and a loot/captive def unknown), `InvalidArgument` (outcome index out of range or duplicated, captives > 1, captives with `!AllowKidnap`, `game_tick < LandedTick`, `FLED/ALIVE` outcome of a Real raid without a blob).
  - `RaidService.Report(long targetId, RaidReport r)` — serializable tx: Landed → Resolved (idempotent: a Resolved raid returns OK without changes), validator, `RaidOutcome` rows, `SummaryJson`; parcels to the attacker: `PARCEL_LOOT` with `r.Loot` when non-empty, `PARCEL_RAID_RETURN` with the `FLED/ALIVE` blobs when `PAWN_REAL`, `PARCEL_CAPTIVE` with captives (all `ignoreMailboxLimit: true`); stamps `target.LastOutgoingTick = r.GameTick`; after commit `RaidResolved` to the attacker, announces parcels.
  - `RaidService.ExpireDue(DateTime now)` → Queued/Announced with `ExpiresAt <= now` → Expired, Real pawns returned as `PARCEL_RAID_RETURN`, `RaidExpired` to the attacker; returns count. `Cancel(long raidId, string reason)` same return, state Cancelled (any non-final state; Landed too — the defender's client then gets `RAID_GONE` on report and keeps what is on its map).
  - `RaidMetrics`: `rimlink_raids_total{outcome=launched|landed|resolved|expired|cancelled}`, `rimlink_raids_active` gauge, `rimlink_visits_total`, `rimlink_shop_sales_total`.

- [ ] **Step 1: Failing tests**, `RaidTests.cs`:

```csharp
[Fact] public async Task ReportResolvesTheRaidAndParcelsTheAttacker()    // Real raid, 3 pawns; bob reports: 0 dead, 1 captured, 2 fled (blobs), loot 2 items value 30 silver, tick 80000 → Resolved; nova's mailbox: LOOT parcel (2 items) + RAID_RETURN parcel (2 pawns); bob.LastOutgoingTick == 80000; nova stream RaidResolved summary fled 2 captured 1 loot_value 30
[Fact] public async Task SnapshotReportReturnsNoPawns()                  // only the LOOT parcel
[Fact] public async Task ReportLootAtCapPassesAndOverCapIsRefused()      // LootCap 5000: loot of exactly 5000 OK; a second raid (another target) with 5001 → LOOT_OVER_CAP, raid still Landed, no parcels
[Fact] public async Task ReportWithAnUnknownDefIsRefused()               // ModpackDefs set to {Steel}: loot "Gold" → DEF_NOT_IN_MODPACK
[Fact] public async Task ReportIsIdempotentAndOnlyForTheTarget()         // second Report OK, parcels still 2; nova reporting bob's raid → NotFound
[Fact] public async Task ReportBeforeLandingIsRefused()                  // Announced → FailedPrecondition
[Fact] public async Task LandIsIdempotentAndASaveOlderThanLandingDoesNotRequeue() // Land tick 70000 twice; bob uploads a save tick 60000 → SAVE_STALE; Pending no longer lists the raid
[Fact] public async Task ExpiredRealRaidReturnsThePawns()                // ExpiresAt in the past via scope; ExpireDue → Expired, nova gets RAID_RETURN parcel + RaidExpired event; Snapshot raid → no parcel
[Fact] public async Task RaidOnAResetTargetReturnsThePawns()             // admin ResetPlayer(bob) with a Queued Real raid → Cancelled, nova has RAID_RETURN; bob's Announce afterwards → RAID_GONE
[Fact] public async Task CaptivesNeedAllowKidnap()                       // default: 1 captive → InvalidArgument; WithSetting("Raids:AllowKidnap","true"): 1 captive → CAPTIVE parcel; 2 → InvalidArgument
```

`RaidAdminTests.cs`: `CancelReturnsThePawnsAndAudits`, `FreezeRefusesLaunchWithEconomyFrozen`, `StatsCountStates`, `AdminPeacefulOffHasNoDelay`, `RestEndpointsNeedTheToken`, `ChatCommandsNeedAdmin` (same shape as `EconomyAdminTests`).

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Raid"` — Expected: FAIL.
- [ ] **Step 3: Implement**; register `RaidExpiryService`; add the admin surface in all three places (chat, REST, CLI) mirroring the economy commands.
- [ ] **Step 4: Run** — Expected: PASS; `dotnet test Rimlink.sln` green.
- [ ] **Step 5: Commit** — `git commit -m "Add raid reports, expiry, cancellation and the raid admin surface"`.

---

### Task 4: Visits, foreign save download and the shop

**Files:**
- Create: `src/Rimlink.Server/Application/Visits/VisitService.cs`, `.../Visits/ShopService.cs`, `src/Rimlink.Server/Grpc/VisitsGrpcService.cs`, `.../Grpc/ShopGrpcService.cs`
- Modify: `Grpc/SavesGrpcService.cs` (`DownloadOf`), `Application/Saves/SaveStore.cs` (`Archive` ends the player's visits, clears their shop and removals), `AdminOperations.cs` (`ListShop`, `ListRemovals`), `AdminCommandRunner.cs` (`shop <player>`, `removals <player>`), `Rest/AdminEndpoints.cs`, `Program.cs`
- Test: `tests/Rimlink.Server.Tests/Visits/VisitTests.cs`, `tests/Rimlink.Server.Tests/Visits/ShopTests.cs`

**Interfaces:**
- Consumes: Task 1; `SaveStore.OpenCurrent(playerId)`, `WorldObjects` (the visitor's caravan position is not tracked by the server: `Begin` trusts the client's claim, as caravan transfers do), `WalletService.Apply/FlushEvents`, `MailboxService.Create`.
- Produces:
  - `VisitService(db, saves, events)`: `Task<Visit> Begin(long visitorId, long hostId)` → `SELF_TRADE` when equal, `RECIPIENT_UNKNOWN`, `VISITS_CLOSED` when `host.VisitsClosed`, NotFound when the host has no current save; ends any earlier open visit of the visitor; inserts `Visit`; `VisitStarted` to the host. `Task End(long visitorId, long visitId)` (idempotent). `Task<Visit?> Active(long visitorId, long hostId)`.
  - `SavesGrpcService.DownloadOf(PlayerRef)` → PermissionDenied unless `Active(caller, target)` is open; then streams exactly like `Download`.
  - `ShopService(db, wallet, mailbox, events, options)`: `Task Publish(long hostId, ShopList list)` (replaces unsold rows of the host; a row that is `SoldAt != null` is kept until the host's next publish after its removal is acked, so a re-publish from an older save cannot resurrect it: rows with `SoldAt` are never replaced, rows with a `PendingRemoval` not acked are kept); `Task<ShopList> Of(long hostId)` (unsold rows); `Task<Parcel> Buy(long buyerId, BuyRequest r)` — serializable tx: `Active(buyer, host)` required (FailedPrecondition "not visiting"), idempotent by `ClientRequestId` (`Transfers` row kind `shop`), row exists and unsold and `r.Count ≤ row.Count` else `ITEM_SOLD`, blob descriptor matches (`def_name`, `stuff_def`, `quality`, `count == r.Count`) else InvalidArgument, price = `row.Price × r.Count`, `WalletService.Apply(buyer, -price, "buy")` (→ `WALLET_INSUFFICIENT`), host `+price − fee`, bank `+fee`, `row.Count -= r.Count` (0 → `SoldAt = now`), `PendingRemoval`, parcel `PARCEL_PURCHASE` with the blob to the buyer (`ignoreMailboxLimit: false`); after commit `FlushEvents`, `ItemSold` to the host, parcel announce. `Task<List<PendingRemoval>> Pending(long hostId)`, `Task Ack(long hostId, IEnumerable<long> ids, long gameTick)` (sets `AckedAt`, stamps `host.LastOutgoingTick = gameTick`).

- [ ] **Step 1: Failing tests**, `VisitTests.cs`:

```csharp
[Fact] public async Task BeginNeedsAHostSaveAndOpensATicket()          // bob has no save → NotFound; bob uploads → ticket, bob's stream VisitStarted
[Fact] public async Task DownloadOfWorksOnlyDuringAVisit()              // before Begin → PermissionDenied; after → bytes equal bob's upload; after End → PermissionDenied
[Fact] public async Task ClosedVisitsAreRefused()                       // bob SetVisitsClosed(true) → VISITS_CLOSED
```

`ShopTests.cs`:

```csharp
[Fact] public async Task PublishReplacesTheListAndOfShowsUnsoldOnly()
[Fact] public async Task BuyPaysCreditsAndParcelsWithinAVisit()          // nova visiting bob, wallet 1000, item price 100 count 3, buy 2 → nova 800, bob 200 − fee (MarketFeePercent 10 → 180, bank 20), parcel PURCHASE count 2, row count 1, PendingRemovals(bob) has (thing, 2), bob stream ItemSold, WalletChanged after commit (fence Notice pattern)
[Fact] public async Task BuyWithoutAVisitIsRefused()
[Fact] public async Task TwoBuyersRaceOnOneItemOnlyOnePays()            // count 1, nova and alice visiting; parallel Buy → one Parcel, one ITEM_SOLD, wallets consistent, one removal
[Fact] public async Task BuyWithAMismatchedBlobIsInvalidArgument()
[Fact] public async Task SameClientRequestIdReturnsTheSameParcel()
[Fact] public async Task AckRemovalStampsTheHost()                      // Ack tick 900 → AckedAt set, bob.LastOutgoingTick == 900; a later Publish from a save with save_tick 800 does not bring the sold row back
[Fact] public async Task ResetClearsShopVisitsAndRemovals()
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Visits"` — Expected: FAIL.
- [ ] **Step 3: Implement**; metrics `rimlink_visits_total`, `rimlink_shop_sales_total`.
- [ ] **Step 4: Run** — Expected: PASS; `dotnet test Rimlink.sln` green.
- [ ] **Step 5: Commit** — `git commit -m "Add visits, foreign save download and the shop"`.

---

### Task 5: Client-core — raid, visit and shop clients, state, outbox kinds

**Files:**
- Create: `src/Rimlink.Client.Core/Raids/RaidClient.cs`, `.../Raids/RaidsState.cs` (named so it does not clash with the proto enum `RaidState`), `.../Visits/VisitClient.cs`, `.../Visits/ShopClient.cs`
- Modify: `Net/RimlinkConnection.cs` (`RaidsApi`, `VisitsApi`, `ShopApi`), `State/SharedState.cs` (`Raids`, `Flags`, apply 60–66), `Economy/TransferOutbox.cs` (`OutboxKind` += `Launch`, `Land`, `Report`, `Buy`, `AckRemoval`; `Add(IMessage)` dispatch; `Attempt` switch takes a `ClientBundle` instead of `EconomyClient` — see below), `Economy/EconomyClient.cs` (unchanged API)
- Test: `tests/Rimlink.Client.Tests/Raids/RaidClientTests.cs`, `tests/Rimlink.Client.Tests/Economy/TransferOutboxTests.cs` (extend)

**Interfaces:**
- Produces:
  - `RaidClient(RimlinkConnection)`: `Launch(LaunchRequest, ct) → Raid`, `Pending(ct)`, `Get(id, ct)`, `Announce(id, tick, ct)`, `Land(id, tick, ct)`, `Report(RaidReport, ct)`, `Mine(ct)`, `Flags(ct)`, `SetPeaceful(bool, ct)`, `SetVisitsClosed(bool, ct)`; 30 s deadlines.
  - `VisitClient`: `Begin(targetId, tick, ct) → VisitTicket`, `End(visitId, ct)`, `DownloadOf(playerId, targetPath, ct)` (via `SaveTransfer`-like streaming to a temp file + rename). `ShopClient`: `Publish`, `Of`, `Buy`, `PendingRemovals`, `AckRemoval`.
  - `RaidsState`: `List<Raid> Incoming` (Pending, deduped), `List<Raid> Mine`, `PlayerFlags Flags`, events `RaidQueued`, `RaidLanded`, `RaidResolved`, `RaidExpired`, `ItemSold`, `VisitStarted`, `FlagsChanged`; `Load(RaidList pending, RaidList mine, PlayerFlags)`, `Apply(ServerEvent)`, `MarkLanded(id)`, `Reset()`.
  - `TransferOutbox.Replay(OutboxClients clients, ct)` where `OutboxClients(EconomyClient Economy, RaidClient Raids, ShopClient Shop)` (record in `Economy/TransferOutbox.cs`); the old `Replay(EconomyClient, ct)` stays as an overload building a bundle with null raid/shop clients (entries of those kinds are left pending with a warning).
  - Definitive status rules unchanged; `RAID_GONE` is definitive (the game layer drops the entry: nothing to return for `Land`/`Report`; a refused `Launch` returns the pawns like a refused `Send`).

- [ ] **Step 1: Failing tests**, `RaidClientTests.cs`: `RaidsStateGetsQueuedRaidFromTheEventStream` (target state has 1 incoming, event fired once on replay), `LoadFillsIncomingMineAndFlags`, `OutboxReplaysALaunchOnce` (two replays, one raid), `OutboxReplaysLandAndReport` (entries removed, server state Landed → Resolved), `ARefusedLaunchKeepsItsPawnsForReturn` (target peaceful → entry `Refusal` set, `Decode` yields the `LaunchRequest` with the pawns).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests --filter "FullyQualifiedName~Raid|FullyQualifiedName~TransferOutbox"` — Expected: FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** — Expected: PASS; `dotnet test Rimlink.sln` green.
- [ ] **Step 5: Commit** — `git commit -m "Add raid, visit and shop clients, raid state and outbox kinds"`.

---

### Task 6: Game — raider faction, launching a raid, incoming raids

**Files:**
- Create: `mod/Defs/FactionDefs/Rimlink_Factions.xml` (`RimlinkRaiders`: `hidden`, `permanentEnemy`, `humanlikeFaction`, no settlements, `requiredCountAtGameStart 0`), `src/Rimlink.Client/Game/Raids/RimlinkFactions.cs`, `.../Raids/RaidOutgoing.cs`, `.../Raids/RaidIncoming.cs`, `.../Raids/TransportPodsArrivalAction_Rimlink.cs`, `src/Rimlink.Client/UI/Raids/Dialog_Raid.cs`
- Modify: `Game/PlayerSettlement.cs` (`GetFloatMenuOptions(Caravan)` += Raid/Visit, override `GetTransportPodsFloatMenuOptions(IEnumerable<IThingHolder> pods, CompLaunchable launcher)` += Raid/Visit), `Game/Economy/Outgoing.cs` (`Run` made internal with an overload taking pre-serialised `PawnList` for Snapshot launches), `Game/RimlinkSession.cs` (`Raids` client property), `Game/RimlinkGameComponent.cs` (`RaidIncoming.Tick()`, `ExposeData` of active raids), `UI/LoginFlow.cs` (`LoadEconomy` also loads `RaidsState`), `Languages`
- Test: game build (`scripts/Build-ClientMod.ps1`), manual checklist (Task 9)

**Interfaces:**
- Consumes: Task 5; `ThingBlobs.SerializePawn/DeserializePawn(newIds: true)`, `Outgoing.Run` pipeline, `Incoming.DropCenter`, `TransferOutbox`.
- Produces:
  - `RimlinkFactions.Raiders` → `Faction` (finds `FactionDefOf`-style def `RimlinkRaiders` in `Find.FactionManager`; creates it via `FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(def))` + `Find.FactionManager.Add` when missing; sets goodwill −100 permanent). `RimlinkFactions.VisitHost(string playerName)` (Task 8).
  - `RaidOutgoing.Launch(long targetId, string targetName, IReadOnlyList<Pawn> pawns, PawnMode mode, RaidTiming timing, RaidArrival arrival, Action onDone)`: Real → `Outgoing.Run` with `LaunchRequest` built from the taken pawns (caravan or pods source); Snapshot → serialise copies (`ThingBlobs.SerializePawn`, no removal), outbox entry, `RaidClient.Launch`; success message; definitive refusal → restore (Real) / message (Snapshot). Pods: `TransportPodsArrivalAction_Rimlink(kind: Raid|Visit, target)` — `Arrived(pods, tile)`: pawns from the pods' contents, items in pods dropped back home is not possible → the dialog only lets pawns board (items in pods are refused with a message before launch via `StillValid`).
  - `Dialog_Raid(target, source pawns)`: pawn picker (humanlike + animals), mode/timing/arrival toggles (arrival fixed by source), loot-cap note, confirm.
  - `RaidIncoming.Tick()` (5 s, home map only): `RaidsState.Incoming` → for `Queued`: `Announce(tick)` (outbox not needed: idempotent and cheap; retried next tick) → letter `Rimlink.Raid.Warning` (Warned) with `land_at_tick`; for `Announced` with `TicksGame >= land_at_tick` and not yet landed this session: `Get` → spawn (`Spawn(raid, map)`: pawns `DeserializePawn(newIds: true)`, `SetFaction(RimlinkFactions.Raiders)`, Edge → `RCellFinder.TryFindRandomPawnEntryCell` + `GenSpawn.Spawn`, Pods → `DropPodUtility.DropThingsNear(DropCellFinder.FindRaidDropCenterDistant(map), …)`; lord `LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: raid.AllowKidnap, canTimeoutOrFlee: true, sappers: false, useAvoidGridSmart: true, canSteal: true), map, pawns)` (`Raid.allow_kidnap` carries the server option, Task 1); letter `Rimlink.Raid.Landed`; outbox `Land(id, tick)`; `RimlinkGameComponent.ActiveRaids.Add(new ActiveRaid { RaidId, Lord, LandedTick, PawnIds, Mode })`; `MarkLanded`; `SaveSync.TriggerSave("raid-landed")`.
  - `RimlinkGameComponent.ActiveRaids` (`List<ActiveRaid>`, `IExposable`: raid id, lord reference via `Scribe_References`, landed tick, pawn thing IDs via `Scribe_Collections`, mode, timeout tick).

- [ ] **Step 1: Build check** — add the files, `scripts/Build-ClientMod.ps1 -InstallTo …` — Expected: build succeeds (no tests in the game layer).
- [ ] **Step 2: In-game smoke** (admin → Nova, two accounts, test server): launch a Snapshot Instant Edge raid from a caravan on Nova's settlement; log in as Nova: raiders walk in, letter shows, `raid N landed` in the log, `Land` in the server log; `dotnet test Rimlink.sln` green.
- [ ] **Step 3: Commit** — `git commit -m "Add the raider faction, raid launching and incoming raids"`.

---

### Task 7: Game — raid resolution and report

**Files:**
- Create: `src/Rimlink.Client/Game/Raids/RaidResolver.cs`, `.../Raids/MapComponent_RaidWatch.cs`
- Modify: `RimlinkGameComponent.cs` (`RaidResolver.Tick()`), `Languages`

**Interfaces:**
- Consumes: Task 6 `ActiveRaids`; `ThingBlobs.SerializeItem/SerializePawn`; `Outgoing.Run`-style save+upload before the RPC; `TransferOutbox` kind `Report`.
- Produces `RaidResolver.Tick()` (every 5 s on the raid's map): for each `ActiveRaid`: done when `lord == null || lord.ownedPawns.Count == 0` after counting, or when every tracked pawn is dead / off-map / a prisoner of the player, or `TicksGame >= LandedTick + TimeoutDays × 60000`. Outcomes per tracked pawn id: `Dead` (corpse or missing after death), `Captured` (`pawn.IsPrisonerOfColony`), `Fled` (not on the map, alive: `pawn.Destroyed` with exit), `Alive` (still on the map at timeout → despawn). Loot: things carried (`carryTracker`) and in `inventory` of Fled/Alive pawns at the moment they leave/are despawned — a pawn that exits the map is gone with its load, so `MapComponent_RaidWatch` (`MapComponentTick`, every 60 ticks) snapshots the carried and inventory things of every tracked raid pawn as blobs; the last snapshot of a pawn that exited is its loot; capped by `raid.LootCap` by `MarketValue` (excess dropped at the map edge `CellFinder.RandomEdgeCell`); captives: kidnapped colonists (`PawnsFinder` of player faction missing + `LordJob` kidnap list) at most 1, only when `raid.AllowKidnap`. Then: Real → blobs of Fled/Alive pawns (after despawn) → `SaveSync.TriggerSave("raid-resolved")` → wait upload → `Report` via outbox; `LOOT_OVER_CAP` → loot things spawned back at the edge, report retried next tick with the trimmed list; `RAID_GONE` → entry dropped, message. Message `Rimlink.Raid.Resolved` with the summary.

- [ ] **Step 1: Build check** — Expected: build succeeds.
- [ ] **Step 2: In-game** — the Task 6 raid: Nova's colonists kill one raider, one flees with stolen items (use dev "Kill" on others, let the lord flee): log `raid N resolved: dead 1 fled 1 …`, server `Report 200`, admin's mailbox shows a `LOOT` parcel (and `RAID_RETURN` for a Real raid repeated with mode Real).
- [ ] **Step 3: Commit** — `git commit -m "Resolve raids on the defender's map and report the outcome"`.

---

### Task 8: Game — visits and the shop

**Files:**
- Create: `src/Rimlink.Client/Game/Visits/VisitSession.cs`, `.../Visits/VisitLoader.cs`, `.../Shop/MapComponent_RimlinkShop.cs`, `.../Shop/ShopGizmos.cs`, `.../Shop/ShopSync.cs`, `src/Rimlink.Client/UI/Visits/Dialog_Buy.cs`, `src/Rimlink.Client/Game/Patches/Patch_Thing_GetGizmos.cs`
- Modify: `Patches/Patch_GameDataSaveLoader_SaveGame.cs` (visit mode blocks `SaveGame` and `Autosaver.DoAutosave`), `Game/SaveSync.cs` (`OnSaved` → `ShopSync.Publish()` after a successful upload; no upload in visit mode), `Game/Economy/Incoming.cs` (skip while visiting), `RimlinkGameComponent.cs` (visit flag, "End visit" overlay button, `ShopSync.ApplyRemovals()` on `FinalizeInit`), `UI/Overlay.cs`, `Languages`

**Interfaces:**
- Consumes: Task 5 `VisitClient`, `ShopClient`; Task 6 `RimlinkFactions.VisitHost`.
- Produces:
  - `VisitSession.Begin(long hostId, string hostName, IReadOnlyList<Pawn> guests)` (main thread): `VisitClient.Begin` → snapshot blobs of the guests → `SaveSync.TriggerSave("visit")` + wait upload (the visitor's own game is now safe on disk and on the server) → `DownloadOf` into `Saves/Rimlink/visit-<hostId>.rws` → `VisitSession.Pending = new(visitId, hostId, hostName, blobs)` → `GameDataSaveLoader.LoadGame("Rimlink/visit-<hostId>")`. Any failure before the load: `VisitClient.End`, message, nothing changed.
  - `VisitLoader` (Harmony postfix on `Game.FinalizeInit` when `VisitSession.Pending != null`): re-faction: `Faction host = RimlinkFactions.VisitHost(hostName)` (generated, `FactionDef` `OutlanderCivil`-like, goodwill 100); every `Pawn` and `Thing` with `Faction == Faction.OfPlayer` → `SetFaction(host)`; the player's `Settlement` world object → `SetFaction(host)`; `Find.World.worldObjects` of type `PlayerSettlement` untouched; guests `DeserializePawn(newIds: true)` spawned at `RCellFinder.TryFindRandomPawnEntryCell` as `Faction.OfPlayer`; `VisitSession.Active = true`; `Current.Game.Info.permadeathMode = false`; `Pending = null`. Load failure (exception in the postfix or `Scribe` errors flagged by `Log.Error` count) → `VisitSession.Abort()`: `VisitClient.End`, delete the temp file, `GameDataSaveLoader.LoadGame(SaveSync.SaveFileName)`.
  - Visit mode: `SaveSync.OnSaved` returns early; `Patch_GameDataSaveLoader_SaveGame` prefix returns `false` for any save name while `VisitSession.Active` (allowed exception: none; the prefix logs once); `Incoming.Tick` returns; overlay button `Rimlink.Visit.End` → `VisitSession.End()`: `VisitClient.End` (outbox-less, best effort), delete temp file, `LoadGame(SaveSync.SaveFileName)`.
  - Shop: `MapComponent_RimlinkShop` (`Dictionary<int thingId, long price>`, `ExposeData`); `ShopGizmos` adds `Command_Action` "Rimlink.Shop.Sell" to haulable, spawned, player-faction things on the home map when the session is active (price dialog `Dialog_Slider`-style via `Dialog_Economy`'s number field helper); in visit mode shows a `Command_Action` "Rimlink.Shop.Buy" on things whose `thingIDNumber` is in the host's published list (fetched by `ShopClient.Of(hostId)` at visit start; price label overlay). `ShopSync.Publish()` (after a successful upload: current home map's component → `ShopList`), `ShopSync.ApplyRemovals()` (login/`FinalizeInit` on the home map: `PendingRemovals` → `thing.SplitOff(count).Destroy()`/`Destroy` by `thingIDNumber` → `TriggerSave("shop-removal")` → `AckRemoval` via outbox).
  - `Dialog_Buy(thing, ShopItem, hostId)`: count field, total price, confirm → `var part = thing.SplitOff(count); var blob = ThingBlobs.SerializeItem(part); thing.TryAbsorbStack(part, respectStackLimit: false);` (the copy stays whole for a repeat purchase) → `ShopClient.Buy` via outbox → message; the purchased parcel arrives at home after the visit.

- [ ] **Step 1: Build check** — Expected: build succeeds.
- [ ] **Step 2: In-game** — admin marks 50 wood "for sale 10"; save → server `Shop/Publish`; Nova's caravan on admin's settlement → Visit: admin's colony loads with admin's colonists as "<admin>" faction, Nova's two guests walk in; Buy 20 wood → `Shop/Buy 200`, Nova wallet −200; End visit → Nova's colony back; Nova's mailbox has the `PURCHASE` parcel; admin logs in → 20 wood removed, `AckRemoval 200`.
- [ ] **Step 3: Commit** — `git commit -m "Add visits to other colonies and the shop"`.

---

### Task 9: UI, documentation, checklist, version

**Files:**
- Modify: `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs` (tabs `Raids` (incoming with state and countdown, mine with state/summary), `Shops` (view `ShopClient.Of` for a chosen player), settings row: `Peaceful mode` / `Close visits` checkboxes bound to `RaidClient.SetPeaceful/SetVisitsClosed`), `mod/Languages/*/Keyed/Rimlink.xml`, `mod/About/About.xml` (`0.3.0`), `src/Rimlink.Client.Core/ClientInfo.cs` (`Version = "0.3.0"`), `docs/player-guide.md` (section 6 "Raids and visits", error rows), `docs/server-setup.md` (`Raids:*`, admin commands, metrics), `deploy/appsettings.example.json` (`Raids` block), `README.md`, `docs/manual-checklist.md` (section "Рейды и визиты")
- Test: existing suites; game build; manual checklist run

- [ ] **Step 1: Implement** UI tabs and strings; docs.
- [ ] **Step 2: Manual checklist** (two accounts on the test server): Real/Snapshot × Warned/Instant × Edge/Pods (at least Real+Warned+Edge and Snapshot+Instant+Pods), defender reload during a landed raid (raid resolves after reload), expiry via `Raids:ExpireRealDays` 0 + a restart, peaceful mode refusal, visit + buy + gift, host modpack change → visit aborts cleanly. Tick each line in `docs/manual-checklist.md` with the log evidence.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln` — Expected: green; `scripts/Build-ClientMod.ps1` succeeds.
- [ ] **Step 4: Commit** — `git commit -m "Add the raids and visits UI, documentation and checklist; version 0.3.0"`.
