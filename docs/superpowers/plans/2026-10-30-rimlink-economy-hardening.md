# Rimlink Economy Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Server-held wealth counts in threat points, the loot cap and `MinColonyWealth`; nothing leaves a colony under threat; pawns and perishables stored on the server cost silver (debt blocks spending); the admin forbids defNames for sends and lots; parcels land at a buildable delivery spot.

**Architecture:** Three small server services (`ServerWealth` — one query; `EconomyGuard` — the "under threat" and "in debt" predicates; `StorageFee` — a pure calculator plus charge points in the existing market/mailbox flows) and a `ForbiddenDefs` singleton beside `ModpackDefs`; the protocol only grows. The mod reads `server_wealth` and `EconomyRules` into `EconomyState`, patches `Map.PlayerWealthForStoryteller`, checks threats before taking things off the map, and gets one new building def.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + REST, EF Core SQLite, xUnit (`WebApplicationFactory` fixtures in `tests/Rimlink.Server.Tests/Support`), Lib.Harmony 3.0.0-rc.3, RimWorld 1.7.

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-economy-hardening-design.md`

## Global Constraints

- Protocol only grows; new numbers are tentative and are renumbered at rebase if a sibling branch took them first, never after merge: `ErrorCode` `UNDER_THREAT = 32`, `IN_DEBT = 33`, `DEF_FORBIDDEN = 34`; `WalletState.server_wealth = 2`; `PawnBlob.market_value_x100 = 8`; `ItemBlob.perishable = 10`; `TransferRequest.from_caravan = 7`; `WalletChanged.ref_id = 4`; `LotSold.storage_fee = 5`; `LotClosed.storage_fee = 3`; `message EconomyRules { repeated string forbidden_defs = 1; double storage_fee_percent_per_day = 2; double storage_free_days = 3; }`; `rpc Wallet.Rules (Empty) returns (EconomyRules)`. `ProtocolVersion.Current` unchanged.
- `EconomyOptions` new keys and defaults: `ServerWealthPercent = 100` (clamped to 0–1000 at use), `BlockOutflowUnderThreat = true`, `StorageFeePercentPerDay = 0.5` (clamped 0–100), `StorageFreeDays = 3` (clamped ≥ 0). Clamped where used, like `MarketFeePercent` (the class has no validator).
- One EF migration `EconomyHardening` (drop `CK_wallets_balance`, create `forbidden_defs` with `DefName` as key); regenerated at rebase if a sibling migration lands first.
- Wallet kinds: `"storage"` for fees (player debit and bank credit). The bank is player 0 (`WalletService.BankPlayerId`).
- Under threat (server): a raid with `TargetPlayerId == me`, `Controller != (int)RaidController.ControllerLive`, `State` in `Queued`/`Announced`/`Landed`; or a `LiveSession` with `Kind == (int)LiveKind.LiveRaid`, `HostPlayerId == me`, `State` in `Starting`/`Running`/`Waiting`/`Ending`. Live raid rows are excluded from the first rule because their `Raid` row is `Queued` from the proposal on.
- Exact server messages: `UNDER_THREAT` "your colony is under attack: deposits, lots and sends wait until it is over"; `IN_DEBT` "you owe {n} silver in storage fees"; `DEF_FORBIDDEN` "def '{def}' is forbidden for transfers".
- Storage fee: `floor(value × percent/100 × max(0, days − freeDays))`, real fractional days since `CreatedAt`; exempt kinds `LotReturn`, `Withdraw`, `Admin`, `Loot`, `RaidReturn`, `Captive` and admin refunds; payer: seller for lots, recipient for `Gift`/`Purchase`.
- Every user-facing string is keyed in `mod/Languages/English/Keyed/Rimlink.xml` and `mod/Languages/Russian/Keyed/Rimlink.xml`; the spot's label/description in the def (EN) and `mod/Languages/Russian/DefInjected/ThingDef/Rimlink.xml` (RU).
- `src/Rimlink.Client.Core` keeps no `Verse`/`RimWorld` reference; game objects on the main thread only (`MainThread.Post`).
- No version bump (`About.xml` `<modVersion>`, `ClientInfo.Version` untouched).
- Stand s2 only: `D:\Build\rimlink-s2` (game copy, saves, server data), server port 25590, GABS `rimlink-s2-host`/`rimlink-s2-guest`. Never touch `D:\Build\rimlink-gabs`, `D:\Build\rimlink-bt`, `D:\Build\rimlink-s1`, the Steam install's `Mods` folder, or ports 25560/25570/25580; kill only the `rimlink-server.exe` whose path contains `beautiful-haibt`.
- `dotnet test Rimlink.sln -nologo -v q` green and the mod building before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>` (preflight `bash ~/.claude/skills/git-safe-commit/preflight.sh`), imperative subject, no trailers. Branch `asofwar/economy-hardening`; no merge, no push.
- Admin plumbing goes into new methods appended at the end of `AdminOperations`, `AdminEndpoints`, `AdminCommandRunner` and `AdminCommand` (siblings edit the same files); `Dialog_Economy` changes stay in one block per method touched.

## Review Focus

1. A client declaring absurd values (`market_value_x100 = long.MaxValue`, count 200) must not crash any call: sums saturate at `long.MaxValue` instead of overflowing (Task 2 `HugeDeclaredValuesSaturate`).
2. A parcel delivered twice (redelivery after a rollback, or a replayed `Deliver`) pays storage once (Task 6 `RedeliveryDoesNotPayAgain`).
3. An expired lot whose seller has no wallet row (reset player, never deposited) is charged without throwing, leaving a negative wallet (Task 6 `ExpiryChargesASellerWithoutAWallet`).
4. A warned raid cancelled by the admin or expired before landing must stop blocking the target's client, not only the server: the target is told and its `Incoming` list drops the raid (Task 4 `CancelledQueuedRaidTellsTheTarget`, Task 9 `ExpiredEventDropsAnIncomingRaid`).
5. Old 0.5 clients: a pawn blob without `market_value_x100` in a lot falls back to the lot price for wealth and fee; a perishable stack from an old client pays nothing; an old client that receives `UNDER_THREAT` treats it as a final refusal and gets its things back (Task 2 `OldPawnBlobUsesTheLotPrice`, Task 9 `UnderThreatIsDefinitive`).

---

### Task 1: Protocol, options and migration

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/common.proto`, `src/Rimlink.Protocol/Protos/economy.proto`
- Modify: `src/Rimlink.Server/Hosting/ServerOptions.cs` (`EconomyOptions`)
- Create: `src/Rimlink.Server/Storage/Entities/ForbiddenDef.cs` (or append to `Entities.cs`), migration under `src/Rimlink.Server/Storage/Migrations/`
- Modify: `src/Rimlink.Server/Storage/RimlinkDbContext.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/EconomyOptionsTests.cs`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`

**Interfaces:**
- Produces: every proto addition in Global Constraints; `EconomyOptions.ServerWealthPercent` (int), `BlockOutflowUnderThreat` (bool), `StorageFeePercentPerDay` (double), `StorageFreeDays` (double); `public sealed class ForbiddenDef { public string DefName { get; set; } = ""; }`; `DbSet<ForbiddenDef> ForbiddenDefs` mapped to table `forbidden_defs`.

- [ ] **Step 1: Write the failing tests**
  - `EconomyOptionsTests.DefaultsMatchTheSpec`: add `Assert.Equal(100, o.ServerWealthPercent); Assert.True(o.BlockOutflowUnderThreat); Assert.Equal(0.5, o.StorageFeePercentPerDay); Assert.Equal(3, o.StorageFreeDays);`
  - `SchemaTests.WalletsAcceptANegativeBalance`: insert `Wallet { PlayerId = <registered>, Balance = -5 }` and save → no exception.
  - `SchemaTests.ForbiddenDefsTableExists`: add `ForbiddenDef { DefName = "Gun_Revolver" }`, save, read back; a second add of the same name throws `DbUpdateException`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~EconomyOptionsTests|FullyQualifiedName~SchemaTests"` — Expected: build errors / FAIL on the new members.
- [ ] **Step 3: Add the proto fields and messages, the four options, the entity and `DbSet`** (`e.ToTable("forbidden_defs"); e.HasKey(x => x.DefName);`), drop the `HasCheckConstraint` on `wallets`, then `dotnet ef migrations add EconomyHardening --project src/Rimlink.Server`.
- [ ] **Step 4: Run** the same filter, then `dotnet test Rimlink.sln -nologo -v q` — Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the economy hardening protocol fields, options and migration"`

### Task 2: Server wealth

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/ServerWealth.cs`
- Modify: `src/Rimlink.Server/Grpc/EconomyGrpcServices.cs` (`WalletGrpcService.Get`, `Deposit`), `src/Rimlink.Server/Program.cs` (DI, scoped)
- Test: `tests/Rimlink.Server.Tests/Economy/ServerWealthTests.cs`

**Interfaces:**
- Consumes: Task 1 fields and options.
- Produces: `public sealed class ServerWealth(RimlinkDbContext db, IOptions<EconomyOptions> options)` with `public async Task<long> Of(long playerId)` (already × `ServerWealthPercent`/100, 0 when `Enabled == false`); static `public static long ValueOf(ItemList? items)` and `public static long ValueOf(PawnBlob? pawn)` in silver (`market_value_x100 / 100`, saturating at `long.MaxValue`); `public static long ValueOf(Lot lot)` (items, or pawn value, or `Price` when the pawn value is 0). `WalletState.ServerWealth` is filled by `Wallet.Get` and `Wallet.Deposit`.

- [ ] **Step 1: Write the failing tests** (seed rows through `RimlinkDbContext` in a scope; `Item(def, count, marketValueX100: …)` and `Pawn(kind, name)` from `EconomyFixtures`):
  - `CountsWalletLotsAndParcels`: balance 300; open lot of 2 × `Steel` at 150 (x100 = 15000 each → 300); open pawn lot with `PawnBlob.MarketValueX100 = 50000` (500); undelivered `Gift` parcel with 10 × 1000 (100) plus `Silver = 40`; a `Sold` lot and a delivered parcel are ignored → `Of == 300 + 300 + 500 + 140 = 1240`; `Wallet.Get(...).ServerWealth == 1240`.
  - `OldPawnBlobUsesTheLotPrice`: pawn lot, `MarketValueX100 = 0`, `Price = 700` → 700.
  - `NegativeBalanceCountsAsZero`: balance −50, nothing else → 0.
  - `PercentScalesAndDisabledEconomyIsZero`: `Economy:ServerWealthPercent=50` → half; `Economy:Enabled=false` → 0.
  - `RaidEscrowIsNotCounted`: a `Queued` raid of the player carrying pawns → 0.
  - `HugeDeclaredValuesSaturate`: item `MarketValueX100 = long.MaxValue`, count 200 → `Of == long.MaxValue`, no exception.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~ServerWealthTests"` — Expected: FAIL (type missing).
- [ ] **Step 3: Implement `ServerWealth`** (sum in `decimal`, clamp to `long.MaxValue`; parcels `DeliveredAt == null && ToPlayerId == player`, any kind), register it, set `ServerWealth` in the two gRPC methods.
- [ ] **Step 4: Run** the filter, then the whole suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Count wallet, open lots and parcels in transit as server-held wealth"`

### Task 3: Loot cap and MinColonyWealth use server wealth

**Files:**
- Modify: `src/Rimlink.Server/Application/Raids/RaidRules.cs`, `src/Rimlink.Server/Application/Raids/RaidService.cs` (`Launch`, the `LootCap` call)
- Test: `tests/Rimlink.Server.Tests/Raids/RaidTests.cs`

**Interfaces:**
- Consumes: `ServerWealth.Of(long)`.
- Produces: `RaidRules(IOptions<RaidOptions> options, RimlinkDbContext db, ServerWealth wealth)`; `public long LootCap(Player target, long serverWealth, PawnMode mode, RaidTiming timing)` = `min((WealthItems + serverWealth) × LootCapPercent/100, LootCapSilver)`, then ×0.5 for Snapshot and ×0.5 for Instant; `CheckLaunch` compares `target.WealthTotal + await wealth.Of(target.Id)` with `MinColonyWealth`.

- [ ] **Step 1: Write the failing tests**
  - `LootCapIncludesTheTargetsWallet`: target `WealthItems = 10000` (`MakeTargetRaidable(..., wealthItems: 10000)`), wallet 20000, defaults (10 %, cap 5000) → Real/Warned raid `LootCap == 3000`.
  - `PoorColonyWithARichWalletIsRaidable`: `Raids:MinColonyWealth=20000`, `WealthTotal = 5000`, wallet 16000 → launch succeeds; with wallet 14000 → `TARGET_PROTECTED`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RaidTests"` — Expected: the two new tests FAIL.
- [ ] **Step 3: Implement** the constructor dependency, the new `LootCap` parameter and the `CheckLaunch` sum; `RaidService.Launch` passes `await wealth.Of(target.Id)`.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Include server-held wealth in the loot cap and the poor-colony threshold"`

### Task 4: Outflow refused under threat

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/EconomyGuard.cs`
- Modify: `WalletService.Deposit` (new parameter), `MarketService` (ctor, `Post`), `TransferService` (ctor, `Send`), `Grpc/EconomyGrpcServices.cs` (pass the guard to `Deposit`), `Program.cs` (DI); `RaidService.Close` (tell the target)
- Test: `tests/Rimlink.Server.Tests/Economy/UnderThreatTests.cs`, `tests/Rimlink.Server.Tests/Raids/RaidTests.cs`

**Interfaces:**
- Consumes: Task 1 `ErrorCode.UnderThreat`, `TransferRequest.FromCaravan`, `EconomyOptions.BlockOutflowUnderThreat`.
- Produces: `public sealed class EconomyGuard(RimlinkDbContext db, IOptions<EconomyOptions> options)` with `public Task<bool> UnderThreat(long playerId)` (predicate in Global Constraints) and `public async Task RequireNoThreat(long playerId)` (no-op when `BlockOutflowUnderThreat` is false; throws `UNDER_THREAT` with the exact message). `WalletService.Deposit(long playerId, DepositRequest request, TransferService transfers, EconomyGate gate, EconomyGuard guard)`. `Send` checks when `(items or pawns) && !FromCaravan`, or `Silver > 0`. `RaidService.Close` sends `RaidExpired { RaidId }` to the target as well when the raid ends `Expired` or `Cancelled` from `Queued`/`Announced`.

- [ ] **Step 1: Write the failing tests** (`RaidServer()`, `MakeTargetRaidable`, a raid by a second player; set states directly in the db where a flow is long):
  - `DepositPostAndSendAreRefusedWhileARaidIsQueued` — target with a `Queued` raid: `Deposit`, `Post` (items), `Send` (items), `Send` (silver only) each throw `RpcException` whose code is `UNDER_THREAT` and message equals the constant.
  - `AnnouncedAndLandedRaidsBlockToo` — same for `Announced` and `Landed` (theory over the two states).
  - `ALiveRaidBlocksItsHostOnceAccepted` — `LiveSession { Kind = LiveRaid, HostPlayerId = target, State = Running }` blocks; `State = Proposed` (with its `Queued` live raid row) does not.
  - `NothingBlocksTheAttackerOthersOrACaravanHandOver` — attacker and a third player deposit fine; the target's `Send` with items and `FromCaravan = true` and no silver succeeds.
  - `TheBlockEndsWithTheRaid` — after `Resolved`, `Expired`, `Cancelled` the target's deposit succeeds.
  - `TheBlockCanBeSwitchedOff` — `Economy:BlockOutflowUnderThreat=false` → deposit under a `Queued` raid succeeds.
  - `InboundStaysOpenUnderThreat` — under a `Queued` raid: `Wallet.Withdraw`, `Market.Buy`, `Market.Withdraw` (own lot), `Mailbox.Deliver` succeed.
  - `RaidTests.CancelledQueuedRaidTellsTheTarget` — target subscribed to events; admin `Cancel` of a `Queued` raid → target reads `RaidExpired` with that id.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~UnderThreatTests|FullyQualifiedName~CancelledQueuedRaidTellsTheTarget"` — Expected: FAIL.
- [ ] **Step 3: Implement** `EconomyGuard`, the three call sites (after `gate.RequireOpen()`), and the extra `RaidExpired` send in `Close`.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Refuse deposits, lots and sends while the colony is under attack"`

### Task 5: Storage fee calculator

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/StorageFee.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/StorageFeeTests.cs`

**Interfaces:**
- Consumes: `ServerWealth.ValueOf(...)`.
- Produces: `public static class StorageFee` with `public static long For(long valueSilver, DateTime storedAt, DateTime now, double percentPerDay, double freeDays)`; `public static long LotBase(Lot lot)` (pawn lot: pawn value, or `Price` when 0; items lot: value of the stacks with `Perishable`); `public static long ParcelBase(Parcel parcel)` (`Gift`/`Purchase`: pawns' value + perishable stacks' value; every other kind 0).

- [ ] **Step 1: Write the failing tests**
  - `NothingInsideTheFreeDays` — value 1000, 2.9 days, 0.5 %, 3 free → 0.
  - `FractionalDaysAfterTheFreeDays` — value 1000, 5 days → `floor(1000 × 0.005 × 2) = 10`; 4.5 days → 7.
  - `ZeroRateIsFree` — percent 0, 100 days → 0.
  - `LotBaseCountsPawnsAndPerishablesOnly` — items lot: 10 × `RawBerries` (perishable, x100 = 500 each → 50) + 5 × `Steel` (not perishable) → 50; pawn lot with value 0 and `Price = 400` → 400.
  - `ParcelBaseExemptsSystemKinds` — the same contents in `Loot`, `RaidReturn`, `Captive`, `LotReturn`, `Withdraw`, `Admin` → 0; in `Gift` and `Purchase` → the value.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~StorageFeeTests"` — Expected: FAIL.
- [ ] **Step 3: Implement** (`days = (now − storedAt).TotalDays`; `floor` in `decimal`; negative inputs give 0).
- [ ] **Step 4: Run** the filter — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Compute the storage fee for pawns and perishables held on the server"`

### Task 6: Storage fee charged when things leave the server

**Files:**
- Modify: `WalletService.cs` (`Charge`, `ref_id` on events), `MarketService.cs` (`Buy`, `Withdraw`, `ExpireDue`, `Close`), `MailboxService.cs` (`Deliver`), `Application/Admin/AdminOperations.cs` (`RefundLot`, `EconomyStats`, `EconomyStatsResult`), `Application/Admin/AdminCommandRunner.cs` (stats line)
- Test: `tests/Rimlink.Server.Tests/Economy/StorageChargeTests.cs`

**Interfaces:**
- Consumes: `StorageFee.*`, options.
- Produces: `public async Task<WalletEntry> Charge(long playerId, long amount, string kind, long refId)` — debit that may go below zero, same event as `Apply`; every `WalletChanged` now carries `RefId`. `MarketService.Withdraw(long sellerId, long lotId, bool bypassGate = false, bool chargeStorage = true)`; `RefundLot` passes `chargeStorage: false`. `LotSold.StorageFee`, `LotClosed.StorageFee`. `EconomyStatsResult(..., long StorageFeesCollected = 0)` (trailing, sum of bank `"storage"` entries); chat `economy stats` appends `, storage fees {n}`.

- [ ] **Step 1: Write the failing tests** (`EconomyServer().WithSetting("Economy:StorageFreeDays","0")`; age rows by setting `CreatedAt = UtcNow − 2 days` in the db; 0.5 %/day):
  - `SoldPawnLotPaysFromTheProceeds` — pawn lot value 1000, price 300, aged 2 days → fee 10; seller entries `sale +300` then `storage −10` (balance 290), bank `+10` kind `storage`; `LotSold.StorageFee == 10`, `Proceeds == 290` (price − market fee − storage).
  - `FeeAboveTheProceedsBecomesDebt` — pawn lot value 100000, price 50, aged 2 days → fee 1000; seller balance `50 − 1000 = −950`.
  - `WithdrawnAndExpiredLotsChargeTheSeller` — perishable lot aged 2 days withdrawn → seller charged; another expired by `ExpireDue` → charged and `LotClosed.StorageFee` set.
  - `AdminRefundChargesNothing` — `RefundLot` of an aged pawn lot → no `storage` entry.
  - `DeliveredGiftChargesTheRecipient` — gift with one pawn value 2000 aged 2 days → recipient balance `−20`, `WalletChanged { Kind = "storage", Delta = −20, RefId = parcel id }` read from the stream.
  - `RedeliveryDoesNotPayAgain` — `Deliver` twice → one `storage` entry.
  - `ExemptParcelsChargeNothing` — aged `RaidReturn` with a pawn → no entry.
  - `ExpiryChargesASellerWithoutAWallet` — seller has no `wallets` row → after `ExpireDue` the row exists with a negative balance; no exception logged as failure.
  - `StatsReportStorageFees` — after the sale above, `EconomyStats(...).StorageFeesCollected == 10`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~StorageChargeTests"` — Expected: FAIL.
- [ ] **Step 3: Implement** `Charge`, `RefId` on `WalletChanged`, the charge points (sold: the `sale` credit as today, then `Charge(seller, fee, "storage", lotId)`; withdrawn/expired: `Charge` when `chargeStorage`; delivered: `Charge` only on the first delivery of `Gift`/`Purchase`), each with the bank credited by `Apply(BankPlayerId, fee, "storage", refId)`, and the stats field.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Charge the storage fee when a lot closes or a parcel is delivered"`

### Task 7: Debt blocks spending

**Files:**
- Modify: `EconomyGuard.cs` (`RequireNoDebt`), `MarketService.cs` (`Post`, `Buy`), `TransferService.cs` (`Send`)
- Test: `tests/Rimlink.Server.Tests/Economy/StorageChargeTests.cs`

**Interfaces:**
- Produces: `public async Task RequireNoDebt(long playerId)` — throws `IN_DEBT` "you owe {n} silver in storage fees" when the balance is below 0. Called in `Post`, `Buy` (before the debit) and `Send`; not in `Deposit`, `Withdraw` (lot), `Deliver`.

- [ ] **Step 1: Write the failing tests**
  - `DebtRefusesBuyPostAndSend` — balance set to −30 → `Buy` of a 1-silver lot, `Post`, `Send` (items) each `IN_DEBT` with "you owe 30 silver in storage fees".
  - `DepositPaysTheDebtOff` — deposit 50 → balance 20, `Post` succeeds.
  - `DebtDoesNotBlockInbound` — balance −30: `Mailbox.Deliver`, `Market.Withdraw` (own lot) succeed.
- [ ] **Step 2: Run** the filter — Expected: the three FAIL.
- [ ] **Step 3: Implement** `RequireNoDebt` and the three calls.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Refuse purchases, lots and sends while storage fees are owed"`

### Task 8: Forbidden defs for sends and lots

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/ForbiddenDefs.cs` (singleton set), `src/Rimlink.Server/Application/Economy/ForbiddenDefsService.cs`
- Modify: `ParcelValidator.cs`, `MarketService.Post`, `TransferService.Send`, `Grpc/EconomyGrpcServices.cs` (`Rules`), `Program.cs` (DI + startup load next to `ModpackService.LoadDefs(app.Services)`); append to `AdminOperations.cs`, `Rest/AdminEndpoints.cs`, `AdminCommandRunner.cs`, `src/Rimlink.Tools/Commands/AdminCommand.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/ForbiddenDefsTests.cs`

**Interfaces:**
- Produces: `public sealed class ForbiddenDefs { bool Contains(string def); IReadOnlyCollection<string> All { get; } void Replace(IEnumerable<string> defs); }` (ordinal, trimmed, empty names dropped); `ForbiddenDefsService(RimlinkDbContext db, ForbiddenDefs defs)` with `Task<IReadOnlyCollection<string>> Set(IEnumerable<string> defs)` and `public static void Load(IServiceProvider services)`. `ParcelValidator.Validate(ItemList? items, PawnList? pawns, bool checkForbidden = false)`; `CodeFor` maps "is forbidden for transfers" to `ErrorCode.DefForbidden`. `Wallet.Rules` returns `EconomyRules` (sorted defs, the two fee options after clamping). `AdminOperations.ForbiddenDefs(Actor)` (Moderator) and `SetForbiddenDefs(Actor, IEnumerable<string>)` (Admin, audit `"forbidden-defs"`). REST `GET /api/admin/economy/forbidden-defs` → `{ "defs": [...] }`, `PUT` with `{ "defs": [...] }`, same scope as `/economy/freeze`. Chat `forbid list | add <def...> | remove <def...>`. CLI `rimlink-tools admin forbid list | add <def...> | remove <def...>` (the CLI's existing `admin <command>` shape; the spec's `economy forbid` wording maps here).

- [ ] **Step 1: Write the failing tests**
  - `ForbiddenItemStuffAndKindAreRefusedForSendAndPost` — forbid `Gun_Revolver`, `Leather_Thrumbo`, `Thrumbo`: `Send` with each (item def, stuff, pawn kind) and `Post` → `DEF_FORBIDDEN`, message "def 'Gun_Revolver' is forbidden for transfers".
  - `RaidsIgnoreTheList` — forbidden pawn kind in a raid `Launch` → accepted.
  - `OpenLotsStayOpen` — lot posted, then the def forbidden → lot still `Open` and buyable.
  - `AdminSetsListsAndAuditsTheList` — REST `PUT` then `GET` returns the sorted list; an `admin_actions` row `forbidden-defs`; a player token gets 401/403.
  - `ChatCommandEditsTheList` — `forbid add A B`, `forbid remove A`, `forbid list` → "B".
  - `RulesCarryTheListAndTheFees` — `Wallet.Rules` → defs, 0.5, 3.
  - `TheListSurvivesARestart` — set, rebuild the fixture on the same data dir → `Rules` still lists it (follow an existing restart test in `ModpackDefsTests`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ForbiddenDefsTests"` — Expected: FAIL.
- [ ] **Step 3: Implement** the set, service, validator flag (`Send` and `Post` pass `checkForbidden: true`), `Rules`, and the appended admin methods, endpoints, chat case and CLI verb.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Let the admin forbid defs in sends and lots"`

### Task 9: Client core state

**Files:**
- Modify: `src/Rimlink.Client.Core/Economy/EconomyState.cs`, `src/Rimlink.Client.Core/Economy/EconomyClient.cs`, `src/Rimlink.Client.Core/Raids/RaidsState.cs`
- Test: `tests/Rimlink.Client.Tests/Economy/EconomyStateTests.cs` (create), `tests/Rimlink.Client.Tests/Economy/TransferOutboxTests.cs`, `tests/Rimlink.Client.Tests/Raids/RaidsStateTests.cs` (create or extend)

**Interfaces:**
- Produces: `EconomyState.ServerWealth` (long), `EconomyState.Rules` (`EconomyRules`, never null), `EconomyState.WealthStale` (bool), `EconomyState.InDebt => Balance < 0`, `bool IsForbidden(string def)`; `Load(WalletState wallet, ParcelList parcels, LotList myLots, EconomyRules? rules = null)`; `void SetWallet(WalletState wallet)` (balance + wealth, clears `WealthStale`); `Apply` sets `WealthStale` on `ParcelArrived`, `LotSold`, `LotClosed`, `WalletChanged`. `EconomyClient.Rules(CancellationToken ct)`. `RaidsState` drops an `Incoming` raid on `RaidExpired` with its id.

- [ ] **Step 1: Write the failing tests**
  - `LoadKeepsWealthAndRules` — `Load(new WalletState { Balance = 5, ServerWealth = 900 }, …, new EconomyRules { ForbiddenDefs = { "Gun_Revolver" } })` → `ServerWealth == 900`, `IsForbidden("Gun_Revolver")`, `!IsForbidden("Steel")`, `!WealthStale`.
  - `EventsMarkWealthStaleAndSetWalletClearsIt` — each of the four events → `WealthStale`; `SetWallet` → false, values updated.
  - `NegativeBalanceIsDebt` — `WalletChanged { Balance = -1 }` → `InDebt`.
  - `ExpiredEventDropsAnIncomingRaid` — `AddIncoming(raid 7)`, `Apply(RaidExpired { RaidId = 7 })` → `Incoming` empty.
  - `UnderThreatIsDefinitive` — `TransferOutbox.IsDefinitive` of an `RpcException` carrying `UNDER_THREAT` (build it like the existing `ECONOMY_FROZEN` test) → true.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter "FullyQualifiedName~EconomyStateTests|FullyQualifiedName~UnderThreatIsDefinitive|FullyQualifiedName~ExpiredEventDropsAnIncomingRaid"` — Expected: FAIL.
- [ ] **Step 3: Implement** the members and the `RaidExpired` removal.
- [ ] **Step 4: Run** the filter and the suite — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Keep server wealth, economy rules and debt in the client state"`

### Task 10: Mod — blobs, wealth refresh and the storyteller patch

**Files:**
- Modify: `src/Rimlink.Client/Game/Economy/ThingBlobs.cs` (`SerializeItem`, `SerializePawn`), `src/Rimlink.Client/UI/LoginFlow.cs` (`LoadEconomy`), `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (call beside `Raids.RaidIncoming.Tick()`)
- Create: `src/Rimlink.Client/Game/Economy/WealthRefresh.cs`, `src/Rimlink.Client/Game/Patches/Patch_Map_PlayerWealthForStoryteller.cs`

**Interfaces:**
- Consumes: Task 9 state; `EconomyClient.Wallet`, `EconomyClient.Rules`.
- Produces: `ItemBlob.Perishable = t.TryGetComp<CompRottable>() is not null`; `PawnBlob.MarketValueX100 = (long)(p.MarketValue * 100f)`; `WealthRefresh.Tick()` — when `RimlinkSession.Active`, `State.Economy.WealthStale` and ≥ 5 s since the last call, fetch `Wallet` off the main thread and `MainThread.Post(() => State.Economy.SetWallet(w))`; login loads `Rules` with the wallet. `Patch_Map_PlayerWealthForStoryteller` — postfix on the `Map.PlayerWealthForStoryteller` getter, one-line summary; adds `State.Economy.ServerWealth` when `RimlinkSession.Active`, `!VisitSession.InVisit`, not a live guest, `__instance == Find.AnyPlayerHomeMap`, `__instance.TreatAsPlayerHomeForThreatPoints`, `!Find.Storyteller.difficulty.fixedWealthMode`.

- [ ] **Step 1: Build** `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-s2\RimWorld' -InstallTo 'D:\Build\rimlink-s2\RimWorld\Mods'` (games closed) — Expected: build succeeds.
- [ ] **Step 2: Game check (RED → GREEN in one run, write the numbers into the checklist draft):** s2 host, `StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap)` read through a RimBridge Lua call before and after `rimlink/deposit {silver: 2000, anywhere: true}` — Expected: points rise; `rimlink/status` `economy` shows the new balance; during `rimlink/visit` the host copy's points do not include the visitor's wealth.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Feed server-held wealth to the storyteller and declare pawn value and perishables in blobs"`

### Task 11: Mod — outflow block, fee messages, forbidden things

**Files:**
- Modify: `src/Rimlink.Client/Game/Economy/Outgoing.cs` (`RefusedUnderThreat` beside `RefusedInVisit`; `SendFromCaravan` sets `FromCaravan = true`), `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs`, `src/Rimlink.Client/UI/Economy/ThingPicker.cs`, the handler that shows `WalletChanged` / `LotSold` / `LotClosed` messages, `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`

**Interfaces:**
- Consumes: Task 9 state; `RimlinkGameComponent.ActiveRaids`; `GenHostility.AnyHostileActiveThreatToPlayer(Map map, bool countDormantPawnsAsHostile = false, bool canBeFogged = false)`.
- Produces: `static bool Outgoing.UnderThreat(Map? home)` — true when `State.Raids.Incoming` holds a raid whose controller is not live, `ActiveRaids` is non-empty, this player hosts a live raid that is not ended, or `home` has an active hostile threat; `RefusedUnderThreat(bool fromTheMap, long walletSilver)` shows `Rimlink.Economy.UnderThreat` (`RejectInput`) and refuses `Deposit`, `PostLot`, `SendItems` (map things, pawns or wallet silver); `SendFromCaravan` is not refused for things. Keys: `Rimlink.Economy.UnderThreat` (EN "Your colony is under threat: deposits, lots and sends are closed until it is over." / RU "Колонии угрожают: депозит, лоты и отправки закрыты до конца угрозы."), `Rimlink.Economy.StorageFeeParcel` ("Storage fee for parcel {0}: {1} silver." / "За хранение посылки {0} списано {1} серебра."), `Rimlink.Economy.StorageFeeLot` ("Storage fee for lot {0}: {1} silver." / "За хранение лота {0} списано {1} серебра."), `Rimlink.Economy.InDebt` ("You owe {0} silver in storage fees: buying, posting lots and sending are closed until it is paid." / "Долг за хранение {0} серебра: покупки, лоты и отправки закрыты, пока он не погашен."), `Rimlink.Economy.Forbidden` ("Forbidden on this server" / "Запрещено на этом сервере"). The dialog: one threat line and disabled Deposit / Post / Send buttons when `UnderThreat`; balance drawn red with `InDebt` text when negative; `ThingPicker` draws forbidden things greyed with the tooltip and does not pick them.

- [ ] **Step 1: Build** the mod (as Task 10) — Expected: success.
- [ ] **Step 2: Game check on s2 (record into the checklist draft):** guest warned raid on host (`rimlink/raid {mode: real, timing: warned}`) → host deposit/lot/send refused with the client message before the letter and after it (the server side is covered by Task 4); after the raid is resolved deposit works; dev spawn of a hostile on the host map → refused, after the kill → allowed; a caravan "Transfer to …" under a raid passes; with `Economy:StorageFreeDays=0` and an aged gift pawn (age the parcel row with `sqlite3 D:\Build\rimlink-s2\server\rimlink.db "update parcels set CreatedAt=datetime('now','-2 days') where Id=<id>"`) → the storage message and a negative balance shown red; buy/post/send refused with the debt message; deposit pays it; `forbid add Steel` → steel greyed in the picker, `rimlink/send` with steel refused by the server.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Close outflow in the game under threat and show storage fees, debt and forbidden things"`

### Task 12: Mod — delivery spot

**Files:**
- Create: `mod/Defs/ThingDefs/Rimlink_DeliverySpot.xml`, `src/Rimlink.Client/Game/Economy/Building_DeliverySpot.cs`, `mod/Languages/Russian/DefInjected/ThingDef/Rimlink.xml`
- Modify: `src/Rimlink.Client/Game/Economy/Incoming.cs` (`DropCenter`)

**Interfaces:**
- Produces: `ThingDef` `Rimlink_DeliverySpot` (own XML: `thingClass` `Rimlink.Client.Game.Economy.Building_DeliverySpot`, `designationCategory` `Misc`, `WorkToBuild` 0, no `costList`, `useHitPoints` false, `passability` `Standable`, `altitudeLayer` `FloorEmplacement`, graphic `Things/Building/Misc/CaravanHitchingSpot` `Graphic_Single` with a distinct `color`, label "Rimlink delivery spot", description "Parcels from other players land here."; RU label «точка доставки Rimlink», description «Сюда падают посылки других игроков.»). `Building_DeliverySpot.SpawnSetup` destroys every other `Rimlink_DeliverySpot` on the map when not loading (`respawningAfterLoad == false`). `Incoming.DropCenter(Map map)`: spot → first colonist beacon → `DropCellFinder.TradeDropSpot(map)`.

- [ ] **Step 1: Build** the mod — Expected: success, no def errors in `Player.log` at start.
- [ ] **Step 2: Game check on s2:** place the spot (instant, no blueprint), guest `rimlink/send` 10 steel → pods open at the spot; place a second spot → the first disappears; remove it → the next parcel lands at the beacon (or the trade drop spot without one); a refused send's return pod lands at the spot too.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Add a delivery spot where parcels land"`

### Task 13: Documentation and the full game run

**Files:**
- Modify: `docs/player-guide.md`, `docs/server-setup.md`, `deploy/appsettings.example.json`, `README.md`, `docs/manual-checklist.md`

- [ ] **Step 1: Write the docs** — player guide: server wealth in threats and the loot cap, the outflow block, storage fee and debt, forbidden things, the delivery spot; server setup: the four keys with defaults, `forbid` chat command, REST `forbidden-defs`, CLI `admin forbid`; `appsettings.example.json`: the four keys under `Economy`; README status line; checklist: one item per game check of Tasks 10–12 (threat points before/after a deposit; a new raid's loot cap grows with the target's wallet; block under a warned raid before and after the letter, under a landed raid, lifted after the outcome; block under dev-spawned enemies, lifted after their death; caravan hand-over passes; storage fee message, debt blocks, deposit pays; forbidden thing greyed and refused; spot, second spot, beacon fallback, refused return at the spot), each with the date and the run's evidence.
- [ ] **Step 2: Verify** each checklist item against the s2 run of Tasks 10–12 (rerun any item not yet seen) — Expected: every item ticked with evidence or marked not run with the reason.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln -nologo -v q` and the mod build — Expected: PASS.
- [ ] **Step 4: Commit** `git commit -m "Document the economy hardening and record its game checks"`

### Task 14: Final review and hand-off

- [ ] **Step 1:** One review of the whole branch (`code-reviewer` agent on the most capable model, or Codex via `codex-run.ps1 -PromptFile`), with the spec, this plan's Review Focus and the rulings; one fix pass, each fix RED→GREEN; minors listed for the report.
- [ ] **Step 2:** `git fetch origin` and rebase onto `origin/main`; renumber protocol numbers and regenerate the migration if a sibling took them (Global Constraints); suite green, mod builds.
- [ ] **Step 3:** Report the head, the rulings and deferred minors to the parent session; no merge, no push. Then the second spec (paid incidents) starts with brainstorming.
