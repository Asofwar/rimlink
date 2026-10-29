# Rimlink Paid Incidents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A player buys an incident (raid, insects, mechanoids, weather, or a kind event) against another player's colony with wallet silver; it fires in the target's game when they play, priced by the target's wealth, with raid-like protections, a queue of two, advance warning for big ones and refunds when it cannot run.

**Architecture:** A server `IncidentService` over a new `incidents` table and `incidents.proto` (catalog, quote, order, pending, mine, announce, report), sharing the raid target rules through a `RaidRules.CheckTarget` extracted from `CheckLaunch`; money moves buyer → bank (refunds bank → buyer). The client core keeps `IncidentsState`; the mod's `IncidentIncoming` announces and fires vanilla incidents with `IncidentParms` scaled from the target's own threat points; a new "Events" tab orders them.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + REST, EF Core SQLite, xUnit, Lib.Harmony 3.0.0-rc.3, RimWorld 1.7.

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-paid-incidents-design.md`

## Global Constraints

- Protocol only grows; numbers tentative (renumbered at rebase, never after merge): new `incidents.proto` (package as the other protos), `ServerEvent.incident_queued = 80`, `ServerEvent.incident_resolved = 81`, `ErrorCode.INCIDENT_QUEUE_FULL = 35`. `ProtocolVersion.Current` unchanged.
- `IncidentState`: `INCIDENT_QUEUED = 0`, `INCIDENT_ANNOUNCED = 1`, `INCIDENT_FIRED = 2`, `INCIDENT_FAILED = 3`, `INCIDENT_EXPIRED = 4`, `INCIDENT_CANCELLED = 5`; `IncidentKind`: `INCIDENT_HOSTILE = 0`, `INCIDENT_WEATHER = 1`, `INCIDENT_KIND = 2`.
- `IncidentOptions` (section `Incidents`): `Enabled` true, `MaxQueuedPerTarget` 2, `BasePrice` 100, `MinPrice` 50, `WarnTicks` 30000, `WarnFromLevel` 3, `KindCooldownHours` 1, `ExpireRealDays` 7, `Catalog` = the spec §2 table verbatim (13 entries, ids `pirates`, `outlanders`, `tribals`, `mechanoids`, `infestation`, `toxic-fallout`, `cold-snap`, `heat-wave`, `flashstorm`, `solar-flare`, `trader`, `resource-pod`, `wanderer`).
- Price: `W = target.WealthTotal + ServerWealth.Of(target)`; hostile `round(BasePrice × (W/100000)^(2/3) × L^(3/2) × mult × (pods ? podsMult : 1))` with level 1–4 → `L` 0.5 / 1 / 1.5 / 2; weather and kind `round(BasePrice × (W/100000)^(2/3) × mult)`; floor `MinPrice`; saturate at `long.MaxValue`.
- Rules: hostile and weather obey `RaidGate.RequireOpen`, peaceful mode of buyer and target, newbie protection, `MinColonyWealth` (with server wealth) and the target cooldown shared with player raids (`Raids:TargetCooldownHours`; refunded orders do not count); kind events obey only `KindCooldownHours` per buyer→target pair; every kind obeys `MaxQueuedPerTarget` (unfinished = Queued or Announced) and `IN_DEBT`. Replay by `ClientRequestId` is checked before every rule.
- Money: order `Apply(buyer, −price, "incident", id)` + `Apply(bank, +price, "incident", id)`; refund on Failed / Expired / Cancelled `Apply(bank, −price, "incident-refund", id)` + `Apply(buyer, +price, "incident-refund", id)` in one transaction.
- Fire tick: `Announce` sets `FireTick = gameTick + (kind == Hostile && level >= WarnFromLevel ? WarnTicks : 0)`.
- Hostile orders in Queued/Announced count as "under threat" (`EconomyGuard.UnderThreat`, client `Outgoing.UnderThreat`); weather and kind do not.
- Keyed strings EN + RU; game objects on the main thread; Client.Core without Verse; no version bump; stand s2 only (`D:\Build\rimlink-s2`, port 25590, GABS `rimlink-s2-host`/`-guest`), kill only the `beautiful-haibt` server.
- `dotnet test Rimlink.sln -nologo -v q` green and the mod building before every commit; commits by Asofwar, imperative subject, no trailers; no merge, no push. Admin plumbing appended at the end of `AdminOperations`, `AdminEndpoints`, `AdminCommandRunner`, `AdminCommand`.

## Review Focus

1. The target's game fires an incident, then reloads before `Report` reached the server: the incident must not fire twice (Task 8 `DueSkipsFiredIds`, Task 9 game check).
2. A target reset (colony abandoned) or banned while orders wait: the orders are cancelled with refunds, not left to expire silently (Task 5 `ResetCancelsAndRefunds`).
3. Saturated server wealth (`W = long.MaxValue`) or a huge `BasePrice` must give a saturated price, not an exception (Task 2 `PriceSaturates`).
4. A replayed `Order` after the buyer's balance dropped, the queue filled or a rule changed returns the original order and charges nothing (Task 4 `ReplayReturnsTheOriginalWithoutCharging`).
5. A catalog entry edited or removed in the config while orders wait: the order keeps the incident def, faction and pods it was bought with (Task 4 `OrderKeepsItsDefsWhenTheCatalogChanges`).

---

### Task 1: Protocol, options, storage

**Files:**
- Create: `src/Rimlink.Protocol/Protos/incidents.proto`; `src/Rimlink.Server/Hosting/IncidentOptions.cs` (or append to `ServerOptions.cs`); migration `PaidIncidents`
- Modify: `src/Rimlink.Protocol/Protos/events.proto`, `common.proto`, `src/Rimlink.Protocol/Rimlink.Protocol.csproj` (if protos are listed), `src/Rimlink.Server/Storage/Entities/Entities.cs`, `RimlinkDbContext.cs`, `Program.cs` (`Configure<IncidentOptions>`)
- Test: `tests/Rimlink.Server.Tests/Incidents/IncidentOptionsTests.cs`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`

**Interfaces:**
- Produces: messages `IncidentCatalog {repeated CatalogEntry entries}`, `CatalogEntry {string id; string label; IncidentKind kind; string incident_def; string faction_def; bool pods_allowed}`, `QuoteRequest {int64 target_player_id; string entry_id; int32 level; bool pods}`, `IncidentQuote {int64 price; bool warned}`, `OrderIncidentRequest {string client_request_id; int64 target_player_id; string entry_id; int32 level; bool pods}`, `Incident {int64 id; PlayerRef buyer; PlayerRef target; string entry_id; string incident_def; string faction_def; IncidentKind kind; int32 level; bool pods; int64 price; IncidentState state; int64 fire_tick; google.protobuf.Timestamp created_at; string reason}`, `IncidentList {repeated Incident incidents}`, `AnnounceIncident {int64 incident_id; int64 game_tick}`, `IncidentReport {int64 incident_id; bool fired; string reason}`, `IncidentQueued {Incident incident}`, `IncidentResolved {Incident incident; int64 refund}`; `service Incidents` with the seven RPCs of spec §4. `IncidentOptions` with `IncidentCatalogEntry {Id, IncidentDef, FactionDef?, Kind (string "Hostile"|"Weather"|"Kind"), PriceMultiplier (double), PodsMultiplier (double?)}`. Entity `IncidentOrder {Id, BuyerPlayerId, TargetPlayerId, EntryId, IncidentDef, FactionDef, Kind (int), Level, Pods, Price, State (IncidentStateDb), FireTick?, CreatedAt, ResolvedAt?, ExpiresAt, Reason?, ClientRequestId?}`, `enum IncidentStateDb { Queued, Announced, Fired, Failed, Expired, Cancelled }`, table `incidents`, index `(TargetPlayerId, State)`, unique filtered index on `ClientRequestId`.

- [ ] **Step 1: Failing tests** — `IncidentOptionsTests.DefaultsMatchTheSpec` (every scalar default; `Catalog.Count == 13`; `pirates` = RaidEnemy/Pirate/Hostile/1/1.4; `mechanoids` multiplier 5 pods 1.5; `infestation` 3 no pods; `toxic-fallout` Weather 2; `wanderer` Kind 2); `SchemaTests.IncidentsTableExists` (insert and read an `IncidentOrder`; duplicate `ClientRequestId` throws).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~IncidentOptionsTests|FullyQualifiedName~SchemaTests"` — Expected: build errors.
- [ ] **Step 3: Implement** the protos, options (catalog defaults bound when the section has no `Catalog`), entity, mapping; `dotnet ef migrations add PaidIncidents --project src/Rimlink.Server`.
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the paid incidents protocol, options and table"`

### Task 2: Price

**Files:** Create `src/Rimlink.Server/Application/Incidents/IncidentPrice.cs`; Test `tests/Rimlink.Server.Tests/Incidents/IncidentPriceTests.cs`

**Interfaces:**
- Produces: `public static class IncidentPrice { public static double LevelFactor(int level); public static long For(IncidentCatalogEntry entry, int level, bool pods, decimal wealth, IncidentOptions o); }` — `LevelFactor` 1→0.5, 2→1, 3→1.5, 4→2 (throws `ArgumentOutOfRangeException` otherwise).

- [ ] **Step 1: Failing tests** — `HostilePriceFollowsWealthAndLevel`: wealth 100000, pirates, level 2 → 100; level 4 → `round(100 × 2^1.5) = 283`; wealth 800000 level 2 → `round(100 × 4) = 400`; `PodsAndTypeMultiply`: mechanoids level 2 pods → `round(100 × 5 × 1.5) = 750`; `WeatherAndKindIgnoreLevel`: toxic-fallout wealth 100000 → 200, any level; `MinPriceFloors`: wealth 0 → 50; `PriceSaturates`: wealth `long.MaxValue`, `BasePrice` 1e12 → `long.MaxValue`, no exception.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~IncidentPriceTests"` — Expected: FAIL (type missing).
- [ ] **Step 3: Implement** in `double` math with clamping to `[MinPrice, long.MaxValue]`; NaN/∞ → `long.MaxValue`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Price paid incidents by the target's wealth, level and type"`

### Task 3: Shared target rules

**Files:** Modify `src/Rimlink.Server/Application/Raids/RaidRules.cs`; Test `tests/Rimlink.Server.Tests/Raids/RaidTests.cs`

**Interfaces:**
- Produces: `public async Task<(ErrorCode Code, string Message)?> CheckTarget(Player attacker, Player target, DateTime now)` — peaceful (both), newbie, `MinColonyWealth` with server wealth, target cooldown counting non-cancelled raids **and** hostile/weather `incidents` not in Failed/Expired/Cancelled created within `TargetCooldownHours`. `CheckLaunch` = `CheckTarget` + active-raids-per-attacker + pair cooldown (unchanged messages).

- [ ] **Step 1: Failing test** — `AHostileIncidentStartsTheTargetCooldownForRaids`: seed an `IncidentOrder` (Hostile, Queued, created now) on Bob by Cara; Nova's raid on Bob → `RAID_COOLDOWN`; with the order `Cancelled` → raid accepted.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RaidTests"` — Expected: the new test FAILS.
- [ ] **Step 3: Implement** the extraction and the incidents query.
- [ ] **Step 4: Run** filter and suite — Expected: PASS (existing raid tests unchanged).
- [ ] **Step 5: Commit** `git commit -m "Share the raid target rules and cooldown with hostile incidents"`

### Task 4: Ordering

**Files:** Create `src/Rimlink.Server/Application/Incidents/IncidentService.cs`, `src/Rimlink.Server/Grpc/IncidentsGrpcService.cs`; Modify `Program.cs` (DI, `MapGrpcService`); Test `tests/Rimlink.Server.Tests/Incidents/IncidentOrderTests.cs`, `tests/Rimlink.Server.Tests/Support/IncidentFixtures.cs`

**Interfaces:**
- Consumes: `IncidentPrice`, `RaidRules.CheckTarget`, `EconomyGuard.RequireNoDebt`, `WalletService.Apply`, `ServerWealth.Of`, `RaidGate`.
- Produces: `IncidentService(RimlinkDbContext db, WalletService wallet, RaidRules rules, RaidGate gate, EconomyGuard guard, ServerWealth wealth, EventBus events, IOptions<IncidentOptions> options, ILogger<IncidentService> log)` with `Task<IncidentCatalog> Catalog()`, `Task<IncidentQuote> Quote(long buyerId, QuoteRequest r)`, `Task<IncidentOrder> Order(long buyerId, OrderIncidentRequest r)`, `Task<List<IncidentOrder>> Pending(long targetId)` (Queued/Announced), `Task<List<IncidentOrder>> Mine(long buyerId)` (last 50), `Task<Incident> ToProto(IncidentOrder o)`. `Order` errors: `Incidents:Enabled` false → `ECONOMY_FROZEN` "paid incidents are disabled on this server"; self → `SELF_TRADE`; unknown target → `RECIPIENT_UNKNOWN`; unknown entry, bad level or pods not allowed → `UNSPECIFIED`; queue → `INCIDENT_QUEUE_FULL` "{target} already has {n} events waiting"; kind cooldown → `RAID_COOLDOWN` "you sent {target} an event less than {h} hours ago". Event `IncidentQueued` to the target after commit. `IncidentFixtures.IncidentServer()` = `RaidServer()` + `Incidents:KindCooldownHours=0`, `Incidents(fixture, token)` client helper.

- [ ] **Step 1: Failing tests** (buyer Nova funded 10000, target Bob raidable with `MakeTargetRaidable`):
  - `CatalogListsTheDefaults` (13 entries, `pirates.PodsAllowed`, `tribals` not).
  - `QuoteMatchesThePrice` (Bob `WealthTotal` 100000, wallet 0 → pirates level 2 → 100, `Warned` false; level 3 → `Warned` true).
  - `OrderChargesTheBuyerAndQueuesForTheTarget` (balance 10000 − 100, bank +100, Bob's `Pending` has it with `INCIDENT_QUEUED`, Bob's stream reads `IncidentQueued`).
  - `ReplayReturnsTheOriginalWithoutCharging` (same `ClientRequestId`, buyer balance set to 0 and queue filled in between → same id, balance unchanged).
  - `HostileRulesApply` (theory: target peaceful, buyer peaceful, target newbie, target poor, target raided by a player within cooldown, `/raids freeze`) → the raid codes.
  - `KindEventsSkipRaidRules` (`trader` on a peaceful newbie target → accepted); `KindCooldownPerPair` (`Incidents:KindCooldownHours=1`: second `trader` Nova→Bob refused `RAID_COOLDOWN`, Cara→Bob accepted).
  - `QueueHoldsTwo` (third order on Bob from anyone → `INCIDENT_QUEUE_FULL`).
  - `DebtRefusesOrders` (`IN_DEBT`), `UnknownEntryLevelAndPodsAreRefused`, `DisabledIncidentsRefuse`.
  - `OrderKeepsItsDefsWhenTheCatalogChanges` (order pirates, then rebuild the fixture on the same data dir with a catalog lacking `pirates` → Bob's `Pending` still shows `RaidEnemy`/`Pirate`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~IncidentOrderTests"` — Expected: FAIL.
- [ ] **Step 3: Implement** the service (replay → rules → price from `IncidentPrice` with `W` → order row + money in one serializable transaction → events) and the gRPC service (session player from `context.GetSession()`).
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Let players order paid incidents against other colonies"`

### Task 5: Announce, report, expiry, cancel

**Files:** Modify `IncidentService.cs`, `IncidentsGrpcService.cs`; Create `src/Rimlink.Server/Application/Incidents/IncidentExpiryService.cs`; Modify `Program.cs` (hosted service), the reset/ban path that calls `RaidService.CancelAllOf`; Test `tests/Rimlink.Server.Tests/Incidents/IncidentLifecycleTests.cs`

**Interfaces:**
- Produces: `Task<IncidentOrder> Announce(long targetId, AnnounceIncident r)` (Queued → Announced with `FireTick`; Announced → returned unchanged; any other state → `RAID_GONE` "that event is no longer waiting"); `Task Report(long targetId, IncidentReport r)` (Announced → Fired or Failed + refund; Fired/Failed again → no-op); `Task<int> ExpireDue(DateTime now)` (Queued/Announced with `ExpiresAt <= now` → Expired + refund; `ExpiresAt = CreatedAt + ExpireRealDays`, re-set at Announce); `Task<bool> Cancel(long id, string reason)`; `Task CancelAllOf(long playerId, string reason)` (orders by or at the player). Every refund sends `IncidentResolved {incident, refund}` to the buyer.

- [ ] **Step 1: Failing tests** — `AnnounceSetsTheFireTick` (level 2 → `FireTick == gameTick`; level 3 → `gameTick + 30000`; weather level ignored → `gameTick`); `ReportFiredFinishes` (state Fired, no refund, buyer reads `IncidentResolved` with refund 0); `ReportFailedRefunds` (buyer back to 10000, bank 0, `incident-refund` entries); `ReportTwiceIsANoOp`; `OnlyTheTargetAnnouncesAndReports` (buyer's Announce → NotFound/refused); `ExpiryRefunds` (`ExpiresAt` in the past → `ExpireDue` → Expired, refund); `AdminCancelRefunds`; `ResetCancelsAndRefunds` (reset Bob through the existing admin reset → his pending orders Cancelled, Nova refunded).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~IncidentLifecycleTests"` — Expected: FAIL.
- [ ] **Step 3: Implement**; the hosted service runs `ExpireDue` at start and hourly (as `RaidExpiryService`).
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Fire, fail, expire and cancel paid incidents with refunds"`

### Task 6: Hostile orders close the target's outflow

**Files:** Modify `src/Rimlink.Server/Application/Economy/EconomyGuard.cs`; Test `tests/Rimlink.Server.Tests/Economy/UnderThreatTests.cs`

- [ ] **Step 1: Failing tests** — `AHostileIncidentWaitingBlocksOutflow` (Queued pirates on Bob → Bob's deposit `UNDER_THREAT`; Announced too; Fired → allowed); `WeatherAndKindDoNotBlock` (Queued `toxic-fallout` and `trader` → deposit allowed).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~UnderThreatTests"` — Expected: the two FAIL / pass as stated (the second passes already: pins behaviour).
- [ ] **Step 3: Implement** the third predicate: `incidents` with `TargetPlayerId == me`, `Kind == Hostile`, `State` in Queued/Announced.
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Treat a hostile incident waiting for a colony as a threat"`

### Task 7: Admin

**Files:** Append to `AdminOperations.cs`, `Rest/AdminEndpoints.cs`, `AdminCommandRunner.cs`, `src/Rimlink.Tools/Commands/AdminCommand.cs`; Test `tests/Rimlink.Server.Tests/Incidents/IncidentAdminTests.cs`

**Interfaces:**
- Produces: `Task<List<IncidentOrder>> ListIncidents(Actor actor, long? playerId)` (Moderator), `Task<bool> CancelIncident(Actor actor, long id, string reason)` (Admin, audit `"incident-cancel"`); REST `GET /api/admin/incidents?playerId=` (Read) → `[{id, buyerName, targetName, entryId, level, pods, price, state, createdAt, reason}]`, `POST /api/admin/incidents/{id}/cancel {reason}` (Admin); chat `incidents list [player] | cancel <id> [reason]`; CLI `admin incidents list [playerId] | cancel <id> [reason]`.

- [ ] **Step 1: Failing tests** — `RestListsAndCancels` (list shows the order with names; cancel → Cancelled, refund, `admin_actions` row; player token refused); `ChatListsAndCancels`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~IncidentAdminTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Let admins list and cancel paid incidents"`

### Task 8: Client core

**Files:** Create `src/Rimlink.Client.Core/Incidents/IncidentsState.cs`, `src/Rimlink.Client.Core/Incidents/IncidentClient.cs`; Modify `src/Rimlink.Client.Core/Net/RimlinkConnection.cs` (`IncidentsApi`), `src/Rimlink.Client.Core/State/SharedState.cs` (`Incidents`, dispatch of the two events); Test `tests/Rimlink.Client.Tests/Incidents/IncidentsStateTests.cs`

**Interfaces:**
- Produces: `IncidentsState { IReadOnlyList<Incident> Incoming; IReadOnlyList<Incident> Mine; IncidentCatalog Catalog; void Load(IncidentList incoming, IncidentList mine, IncidentCatalog catalog); void Update(Incident i); IEnumerable<Incident> Due(long ticksGame, ISet<long> fired); bool AnyHostileWaiting; event Action<Incident>? Queued; event Action<IncidentResolved>? Resolved; void Apply(ServerEvent ev); void Reset(); }` — `Due` = Announced incoming with `FireTick <= ticksGame` and id not in `fired`; `IncidentQueued` adds to `Incoming` (replay-safe); `IncidentResolved` removes the id from `Incoming` and updates `Mine`. `IncidentClient(RimlinkConnection c)` with `Catalog`, `Quote`, `Order`, `Pending`, `Mine`, `Announce(long id, long tick)`, `Report(long id, bool fired, string reason)`. `public static class IncidentLevels { public static double Factor(int level); }` in `src/Rimlink.Client.Core/Incidents/IncidentLevels.cs` (1→0.5, 2→1, 3→1.5, 4→2, else 1) — the share of the target's threat points the mod fires.

- [ ] **Step 1: Failing tests** — `LevelFactors` (1→0.5 … 4→2, 0→1), `LoadAndEventsKeepTheQueue`, `DueSkipsFiredIds` (Announced id 5 at fire tick 100, `Due(100, {5})` empty, `Due(100, {})` has 5, `Due(99, {})` empty), `ResolvedRemovesFromIncoming`, `AnyHostileWaitingIgnoresWeatherAndKind`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter "FullyQualifiedName~IncidentsStateTests"` — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter and suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Keep paid incidents in the client state"`

### Task 9: Mod — firing incidents

**Files:** Create `src/Rimlink.Client/Game/Incidents/IncidentIncoming.cs`; Modify `RimlinkGameComponent.cs` (`List<long> FiredIncidents`, Scribe `rimlinkFiredIncidents`, call `Incidents.IncidentIncoming.Tick()` beside `RaidIncoming.Tick()`), `UI/LoginFlow.cs` (`LoadEconomy` loads incoming, mine, catalog), `Game/Economy/Outgoing.cs` (`UnderThreat` adds `State.Incidents.AnyHostileWaiting`), keyed EN/RU

**Interfaces:**
- Consumes: `IncidentsState`, `IncidentClient`.
- Produces: `IncidentIncoming.Tick()` — same guards as `RaidIncoming.Tick`, plus not a live guest; Queued → `Announce`, then `IncidentsState.Update`; future `FireTick` → letter `Rimlink.Incident.Warning` (`{0}` buyer, `{1}` label, `{2}` level); `Due` → `Fire(Incident)`: `IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(...)` (missing → report failed "def missing"); `parms = StorytellerUtility.DefaultParmsNow(def.category, target)` with `target` the map (or `Find.World` when `def.TargetAllowed(Find.World)` and not the map), `forced = true`; hostile: `parms.points = StorytellerUtility.DefaultThreatPointsNow(map) × IncidentLevels.Factor(level)`, raids: `faction` from `FactionDef` (`Find.FactionManager.FirstFactionOfDef`, else leave null for the worker to choose), `raidArrivalMode` `PawnsArrivalModeDefOf.EdgeWalkIn` / `EdgeDrop`; add id to `FiredIncidents`, `def.Worker.TryExecute(parms)`; message `Rimlink.Incident.OrderedBy` (hostile/weather) or `Rimlink.Incident.GiftFrom` (kind); `Report` via a background task (`fired` true/false with the exception message), removed from `FiredIncidents` once the server answered. Keys: `Rimlink.Incident.Warning` ("{0} ordered {1} (level {2}) against your colony: it comes in half a day." / "{0} заказал(а) против вашей колонии: {1} (уровень {2}). Оно придёт через полдня."), `Rimlink.Incident.OrderedBy` ("Ordered by {0}." / "Заказчик: {0}."), `Rimlink.Incident.GiftFrom` ("A gift from {0}." / "Подарок от {0}.").

- [ ] **Step 1: Build** the mod into s2 (`Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-s2\RimWorld' -InstallTo 'D:\Build\rimlink-s2\RimWorld\Mods'`) — Expected: success.
- [ ] **Step 2: Game check** (after Task 11 provides `rimlink/incident_order`): Nova orders `pirates` level 1 on Host-s2 → raid arrives at the host's next tick with the "Ordered by Nova-s2" message; `toxic-fallout` → condition active; `trader` → caravan arrives; level 3 → warning letter, nothing until `FireTick`, host's deposit refused meanwhile; reload the host between firing and `Report` (save, go to menu, reconnect) → no second firing; `infestation` on a map with no overhead mountain → `Failed`, buyer refunded.
- [ ] **Step 3: Run** the suite — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Fire paid incidents in the target's game"`

### Task 10: Mod — the Events tab

**Files:** Modify `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs` (`Tab.Events`, `DrawEvents`), keyed EN/RU

**Interfaces:**
- Produces: tab "Events"/«События»: recipient picker (as Send), catalog list (label, kind), level 1–4 radio for hostile, "by drop pods" checkbox where `pods_allowed`, price line from `Quote` (refreshed when a choice changes, `Rimlink.Incident.Price` "Price: {0} silver{1}" with " — announced half a day ahead" when `warned`), "Order" button (closed when `InDebt`, with the debt note), my orders list (label, target, state, reason). Keys: `Rimlink.Economy.Tab.Events`, `Rimlink.Incident.Price`, `Rimlink.Incident.Warned`, `Rimlink.Incident.Order`, `Rimlink.Incident.Ordered`, `Rimlink.Incident.MyOrders`, `Rimlink.Incident.State.*` (six states), `Rimlink.Incident.Level`, `Rimlink.Incident.Pods`.

- [ ] **Step 1: Build** the mod — Expected: success.
- [ ] **Step 2: Game check**: open the window on the guest, order `pirates` level 2 on Host-s2 through the tab (`get_ui_layout`/`click_ui_target`), the price matches `Quote`, the order appears under My orders and in Host's queue.
- [ ] **Step 3: Run** the suite — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Add the Events tab for ordering paid incidents"`

### Task 11: BridgeTools

**Files:** Create `tools/Rimlink.BridgeTools/IncidentTools.cs`; Modify `tools/Rimlink.BridgeTools/StatusTools.cs` (status gains `incidents: {incoming, mine}`)

**Interfaces:**
- Produces: `rimlink/incident_order {player, entry, level=2, pods=false}` → `{success, id, price, error?}` through `IncidentClient.Order`; `rimlink/incidents` → incoming and mine with state, fire tick, reason.

- [ ] **Step 1: Build** `.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-s2\RimWorld'` — Expected: deployed one file.
- [ ] **Step 2: Game check**: `rimlink/incident_order` and `rimlink/incidents` answer on s2.
- [ ] **Step 3: Commit** `git commit -m "Order and list paid incidents from the bridge"`

### Task 12: Documentation and the game run

**Files:** `docs/player-guide.md`, `docs/server-setup.md`, `deploy/appsettings.example.json` (`Incidents` section with the catalog), `README.md`, `docs/manual-checklist.md`

- [ ] **Step 1: Write** player guide (ordering, price, levels, warning, refunds, protections), server setup (options, catalog format, admin commands), example config, README status, checklist items for every game check of Tasks 9–11 with the run's evidence (or "not run" with the reason).
- [ ] **Step 2: Verify** the checklist against the s2 run.
- [ ] **Step 3: Run** suite and mod build — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Document paid incidents and record their game checks"`

### Task 13: Final review and hand-off

- [ ] **Step 1:** One review of the part-2 range (from the commit after 6e9dea9 to HEAD) by a `code-reviewer` on the most capable model, with this plan's Review Focus and the ledger rulings; one fix pass, each fix RED→GREEN; minors listed.
- [ ] **Step 2:** Rebase onto the head the parent session names (integrity, then main), renumbering both parts' protocol numbers and regenerating both migrations if needed; suite green, mod builds.
- [ ] **Step 3:** Report the head, rulings and deferred minors to the parent; no merge, no push.
