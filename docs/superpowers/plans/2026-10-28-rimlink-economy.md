# Rimlink Economy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Players on one Rimlink server send items, silver and pawns to each other, trade on an escrow market board and deliver by caravan to another player's settlement, with offline recipients served by a server-side mailbox.

**Architecture:** Server (ASP.NET Core 10 + gRPC + SQLite) gains a wallet, mailbox, market and transfer ledger as new tables and one new gRPC file; it never interprets item contents beyond a descriptor. Client-core gets a typed `EconomyClient` and economy state fed by the existing event stream. The game layer serialises things and pawns with vanilla `Scribe` into opaque blobs, exposes the UI through the comms console, drops incoming parcels with drop pods and adds a caravan action on `PlayerSettlement`. Anti-duplication: every outgoing transfer is stamped with the tick of a save made right before it; the server refuses older saves.

**Tech Stack:** .NET 10, Grpc.AspNetCore, EF Core Sqlite (migrations), xUnit + `WebApplicationFactory` (`ServerFixture`), Lib.Harmony 3.0.0-rc.3, RimWorld 1.7 (`Assembly-CSharp.dll`, `UnityStubs.dll`), ZstdSharp.

**Spec:** `docs/superpowers/specs/2026-10-28-rimlink-economy-design.md` (and the core spec it builds on, `docs/superpowers/specs/2026-10-28-rimlink-core-design.md`).

## Global Constraints

- Protocol only grows: new file `src/Rimlink.Protocol/Protos/economy.proto`, new `ErrorCode` values appended after `MUTED = 11`, new `ServerEvent.body` fields numbered 50–53. No existing field renumbered.
- Storage: one additive EF Core migration `Economy` (new tables + `Players.LastOutgoingTick`); `dotnet ef migrations add` via the existing `DesignTimeDbContextFactory`.
- Limits (spec §3, config `Economy:*`, defaults): `MaxParcelBytes` 4 194 304, `MaxItemsPerParcel` 200, `MaxPawnsPerParcel` 10, `MaxLotsPerPlayer` 20, `MaxPendingParcels` 50, `LotLifetimeDays` 30, `MarketFeePercent` 0, `SendCooldownSeconds` 5, `Enabled` true. Mirrored in `Rimlink.Protocol.Limits` as `EconomyMaxParcelBytes`, `EconomyMaxItemsPerParcel`, `EconomyMaxPawnsPerParcel` for the client's pre-check.
- Error codes (spec §3): `WALLET_INSUFFICIENT = 12`, `LOT_GONE = 13`, `PARCEL_TOO_BIG = 14`, `DEF_NOT_IN_MODPACK = 15`, `SAVE_STALE = 16`, `RECIPIENT_UNKNOWN = 17`, `ECONOMY_FROZEN = 18`, `SELF_TRADE = 19`.
- Every money movement is a `WalletEntry`; `Wallet.Balance` is a cache that must equal the sum of entries (tested). Bank/fee account is `player_id = 0` (no `Player` row; `Wallet` row created on demand).
- Harmony: no unconditional `return false` in a prefix; new patches only on `Building_CommsConsole.GetCommTargets` (postfix), `WorldObject.GetFloatMenuOptions(Caravan)` is an override on `PlayerSettlement` (no patch needed).
- Game code touches Verse/UI only on the main thread: network continuations go through `UI.MainThread.Post`/`Ask`.
- TDD per task; `dotnet test Rimlink.sln` green before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no attribution lines. Branch `economy` (already holds the spec commit).
- Russian + English Keyed strings for every new UI string (`mod/Languages/{English,Russian}/Keyed/Rimlink.xml`).

## Review Focus

1. A player sends items, then loads an older local save (or the pending-upload path sends an older file): the upload must be refused with `SAVE_STALE` and the client must offer the server copy, never silently restore the sent items (Task 4 test `UploadOlderThanLastOutgoingTickIsSaveStale`, Task 9 client handling).
2. Two buyers hit `Market.Buy` on the same lot in the same instant: exactly one pays and one parcel exists; the other gets `LOT_GONE` and no `WalletEntry` (Task 3 test `TwoBuyersRaceOnlyOnePays`).
3. The receiver spawns a parcel, then crashes before any save: at the next login the parcel must spawn again exactly once (Task 2 tests `DeliverIsIdempotent`, `ListMarksParcelsDeliveredAfterTheClientsSaveForRedelivery`; Task 9 client).
4. A parcel or lot whose owner was reset/deleted or never returns: expiry and refunds must not throw and must not lose items (Task 3 test `ExpiredLotOfResetPlayerIsReturnedToTheirMailbox`, Task 5 test `RefundParcelReturnsItToSender`).
5. A blob the receiving game cannot load (def missing because the modpack-defs check is off): the game must catch the Scribe failure, leave the parcel undelivered and tell the player instead of crashing or losing it (Task 9 step on load failure; manual checklist item).

---

### Task 1: Protocol, storage and options

**Files:**
- Create: `src/Rimlink.Protocol/Protos/economy.proto`
- Modify: `src/Rimlink.Protocol/Protos/common.proto` (ErrorCode), `src/Rimlink.Protocol/Protos/events.proto` (import + oneof 50–53), `src/Rimlink.Protocol/Limits.cs`, `src/Rimlink.Protocol/Rimlink.Protocol.csproj` (Protobuf item if protos are listed explicitly)
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs`, `src/Rimlink.Server/Storage/RimlinkDbContext.cs`, `src/Rimlink.Server/Hosting/ServerOptions.cs` (add `EconomyOptions`), `src/Rimlink.Server/Program.cs` (Configure `EconomyOptions`)
- Create: `src/Rimlink.Server/Storage/Migrations/<ts>_Economy.cs` (generated)
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs` (extend), `tests/Rimlink.Server.Tests/Economy/EconomyOptionsTests.cs`

**Interfaces:**
- Produces proto (package `rimlink.v1`, `csharp_namespace Rimlink.Protocol.V1`):
  ```proto
  message SkillBrief { string skill_def = 1; int32 level = 2; }
  message ItemBlob { string def_name = 1; string stuff_def = 2; int32 count = 3; int32 quality = 4; int32 hit_points = 5; int32 max_hit_points = 6; int64 market_value_x100 = 7; string label = 8; bytes scribe_xml_zstd = 9; }
  message PawnBlob { string kind_def = 1; string name = 2; bool humanlike = 3; int32 age_years = 4; string label = 5; repeated SkillBrief skills = 6; bytes scribe_xml_zstd = 7; }
  message ItemList { repeated ItemBlob items = 1; }   message PawnList { repeated PawnBlob pawns = 1; }
  enum ParcelKind { PARCEL_GIFT = 0; PARCEL_PURCHASE = 1; PARCEL_LOT_RETURN = 2; PARCEL_WITHDRAW = 3; PARCEL_ADMIN = 4; }
  message Parcel { int64 id = 1; PlayerRef from = 2; ParcelKind kind = 3; ItemList items = 4; PawnList pawns = 5; int64 silver = 6; google.protobuf.Timestamp created_at = 7; bool redeliver = 8; int64 delivered_tick = 9; int32 size_bytes = 10; }
  message ParcelList { repeated Parcel parcels = 1; }
  enum LotStatus { LOT_OPEN = 0; LOT_SOLD = 1; LOT_WITHDRAWN = 2; LOT_EXPIRED = 3; }
  message Lot { int64 id = 1; PlayerRef seller = 2; ItemList items = 3; PawnBlob pawn = 4; int64 price = 5; google.protobuf.Timestamp created_at = 6; google.protobuf.Timestamp expires_at = 7; LotStatus status = 8; }
  message LotList { repeated Lot lots = 1; int32 total = 2; }
  message WalletState { int64 balance = 1; }
  message WalletEntryMsg { int64 id = 1; int64 delta = 2; string kind = 3; int64 ref_id = 4; google.protobuf.Timestamp at = 5; }
  message WalletHistory { repeated WalletEntryMsg entries = 1; }
  message DepositRequest { string client_request_id = 1; int64 silver = 2; int64 game_tick = 3; }
  message WithdrawRequest { string client_request_id = 1; int64 silver = 2; }
  message DeliverRequest { int64 parcel_id = 1; int64 game_tick = 2; }
  message MarketQuery { int64 seller_player_id = 1; string text = 2; int32 page = 3; }
  message PostRequest { string client_request_id = 1; ItemList items = 2; PawnBlob pawn = 3; int64 price = 4; int64 game_tick = 5; }
  message LotRef { int64 lot_id = 1; }
  message SendRequest { string client_request_id = 1; int64 to_player_id = 2; ItemList items = 3; PawnList pawns = 4; int64 silver = 5; int64 game_tick = 6; }
  service Wallet { rpc Get (Empty) returns (WalletState); rpc Deposit (DepositRequest) returns (WalletState); rpc Withdraw (WithdrawRequest) returns (Parcel); rpc History (Empty) returns (WalletHistory); }
  service Mailbox { rpc List (Empty) returns (ParcelList); rpc Deliver (DeliverRequest) returns (Empty); }
  service Market { rpc List (MarketQuery) returns (LotList); rpc Post (PostRequest) returns (Lot); rpc Withdraw (LotRef) returns (Parcel); rpc Buy (LotRef) returns (Parcel); }
  service Transfer { rpc Send (SendRequest) returns (Parcel); }
  message ParcelArrived { Parcel parcel = 1; }
  message LotSold { int64 lot_id = 1; PlayerRef buyer = 2; int64 price = 3; int64 proceeds = 4; }
  message LotClosed { int64 lot_id = 1; string reason = 2; }
  message WalletChanged { int64 balance = 1; int64 delta = 2; string kind = 3; }
  ```
  `events.proto`: `ParcelArrived parcel_arrived = 50; LotSold lot_sold = 51; LotClosed lot_closed = 52; WalletChanged wallet_changed = 53;`
- Produces entities (`Rimlink.Server.Storage.Entities`): `Wallet { long PlayerId (PK); long Balance; DateTime UpdatedAt }`, `WalletEntry { long Id; long PlayerId; long Delta; string Kind; long RefId; DateTime At }`, `Parcel { long Id; long ToPlayerId; long? FromPlayerId; ParcelKindDb Kind; byte[] Items; byte[] Pawns; long Silver; int SizeBytes; DateTime CreatedAt; DateTime? DeliveredAt; long? DeliveredTick; string? ClientRequestId }`, `Lot { long Id; long SellerPlayerId; byte[] Items; byte[]? Pawn; long Price; DateTime CreatedAt; DateTime ExpiresAt; LotStatusDb Status; long? BuyerPlayerId; DateTime? ClosedAt; string? ClientRequestId }`, `Transfer { long Id; long FromPlayerId; long? ToPlayerId; string Kind; long GameTick; string Summary; DateTime At }`, `Player.LastOutgoingTick (long, default 0)`. Enums `ParcelKindDb { Gift, Purchase, LotReturn, Withdraw, Admin }`, `LotStatusDb { Open, Sold, Withdrawn, Expired }`.
- Produces `EconomyOptions` (`Section = "Economy"`) with the Global Constraints defaults, plus `Rimlink.Protocol.Limits.EconomyMaxParcelBytes/EconomyMaxItemsPerParcel/EconomyMaxPawnsPerParcel`.
- Also `ModpackDef { string DefName (PK) }` (table `ModpackDefs`, filled in Task 5).
- Indexes: `Parcel(ToPlayerId, DeliveredAt)`, `Lot(Status, ExpiresAt)`, unique `Parcel(ClientRequestId)` and `Lot(ClientRequestId)` where not null, `WalletEntry(PlayerId)`. CHECK `Wallet.Balance >= 0`.

- [ ] **Step 1: Write the failing schema test** in `SchemaTests.cs`:

```csharp
[Fact]
public async Task EconomyTablesExist()
{
    using var fixture = new ServerFixture();
    using var scope = fixture.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
    var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
    Assert.Superset(new HashSet<string> { "Wallets", "WalletEntries", "Parcels", "Lots", "Transfers", "ModpackDefs" }, tables.ToHashSet());
    Assert.Equal(0, (await db.Players.Select(p => p.LastOutgoingTick).FirstOrDefaultAsync()));
}
```

- [ ] **Step 2: Run it** — `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~EconomyTablesExist` — Expected: FAIL (compile error: `LastOutgoingTick`, tables missing).
- [ ] **Step 3: Write `economy.proto`, extend `common.proto`/`events.proto`/`Limits.cs`** as in Interfaces. `dotnet build src/Rimlink.Protocol` — Expected: 0 errors.
- [ ] **Step 4: Add entities, `DbSet`s (`Wallets`, `WalletEntries`, `Parcels`, `Lots`, `Transfers`, `ModpackDefs`), indexes/CHECK in `OnModelCreating`, `EconomyOptions`, `Configure<EconomyOptions>`**; generate the migration: `dotnet ef migrations add Economy --project src/Rimlink.Server --output-dir Storage/Migrations` — Expected: a migration that only creates the six tables and adds one column.
- [ ] **Step 5: Options test** `EconomyOptionsTests.DefaultsMatchTheSpec` asserts every default from Global Constraints via `fixture.Services.GetRequiredService<IOptions<EconomyOptions>>().Value`.
- [ ] **Step 6: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~EconomyTablesExist|FullyQualifiedName~EconomyOptionsTests"` — Expected: PASS; full `dotnet test Rimlink.sln` still green.
- [ ] **Step 7: Commit** — `git commit -m "Add economy protocol, storage schema and options"`.

---

### Task 2: Wallet, transfers and mailbox

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/WalletService.cs`, `.../Economy/MailboxService.cs`, `.../Economy/TransferService.cs`, `.../Economy/ParcelValidator.cs`, `.../Economy/EconomyGate.cs`, `src/Rimlink.Server/Grpc/WalletGrpcService.cs`, `.../Grpc/MailboxGrpcService.cs`, `.../Grpc/TransferGrpcService.cs`
- Modify: `src/Rimlink.Server/Program.cs` (DI + `MapGrpcService`), `src/Rimlink.Server/Grpc/RateLimitInterceptor.cs` (per-session cooldown for `/rimlink.v1.Transfer/Send`, `/rimlink.v1.Market/Post`: `SendCooldownSeconds`)
- Test: `tests/Rimlink.Server.Tests/Economy/TransferTests.cs`, `tests/Rimlink.Server.Tests/Economy/MailboxTests.cs`, `tests/Rimlink.Server.Tests/Support/EconomyFixtures.cs`

**Interfaces:**
- Consumes: Task 1 entities/proto; `SessionRegistry`, `EventBus.SendTo(long playerId, ServerEvent)`, `RpcErrors.With/Failed`, `context.GetSession()`.
- Produces:
  - `WalletService(RimlinkDbContext db, EventBus events)`: `Task<long> Balance(long playerId)`; `Task<WalletEntry> Apply(long playerId, long delta, string kind, long refId)` — throws `RpcErrors.With(FailedPrecondition, WALLET_INSUFFICIENT, ...)` when the result would be negative; updates `Wallet.Balance`, writes the entry and sends `WalletChanged` to the player's session if any. Caller owns the transaction.
  - `ParcelValidator(IOptions<EconomyOptions>, ModpackDefs defs)`: `string? Validate(ItemList items, PawnList pawns)` → null or a message; checks item/pawn counts, `count ≥ 1`, total `scribe_xml_zstd` bytes ≤ `MaxParcelBytes`, def names in `defs` (Task 5 fills `ModpackDefs`; until then `ModpackDefs.IsEmpty` → skip). `ErrorCode CodeFor(string message)` maps to `PARCEL_TOO_BIG` / `DEF_NOT_IN_MODPACK`.
  - `EconomyGate(IOptions<EconomyOptions>, FreezeState freeze)`: `void RequireOpen()` throws `ECONOMY_FROZEN` when `!Enabled` or frozen. `FreezeState` singleton `{ bool Frozen; string? Reason }`.
  - `MailboxService(db, events, options)`: `Task<Parcel> Create(long toPlayerId, long? fromPlayerId, ParcelKindDb kind, ItemList items, PawnList pawns, long silver, string? clientRequestId)` (throws `RECIPIENT_UNKNOWN` for a missing player; when the recipient already has `MaxPendingParcels` undelivered parcels throws `RpcErrors.Failed(ErrorCode.Unspecified, "recipient's mailbox is full")`; sends `ParcelArrived`); `Task<List<Parcel>> Pending(long playerId, long currentSaveTick)` (parcels with `DeliveredAt == null` OR `DeliveredTick > currentSaveTick`, the latter flagged `redeliver`); `Task Deliver(long playerId, long parcelId, long gameTick)` (idempotent; credits the parcel's silver to the recipient on the first call unless `Kind == Withdraw`; a parcel of another player → `NotFound`); `static Protocol.V1.Parcel ToProto(Parcel, PlayerRef? from)`.
  - `TransferService(db, wallet, mailbox, validator, gate, options)`: `Task<Parcel> Send(long fromPlayerId, SendRequest request)` — one `BEGIN IMMEDIATE` transaction: gate, `SELF_TRADE` when `to == from`, validator, silver ≤ balance (via `WalletService.Apply(-silver, "send", parcelId)` after parcel insert), `Player.LastOutgoingTick = max(current, request.game_tick)`, `Transfer` row, recipient credited `silver` through `Apply(+silver, "receive", parcelId)` only on `Deliver` (silver "in transit" lives on the parcel). Same `client_request_id` → return the existing parcel without changes.
  - gRPC services map 1:1 to the proto. `Mailbox.List(Empty)` returns every undelivered parcel plus every parcel whose `DeliveredTick` is greater than the tick of the player's current save (`SaveVersions` row with `IsCurrent`), the latter flagged `redeliver = true` (the client saved before the delivery, so the pod must drop again).

- [ ] **Step 1: Fixtures** — `EconomyFixtures.Items(params (string def, int count)[])` builds an `ItemList` with 32-byte fake blobs; `Pawn(string kind, string name)`; `RegisterTwo(fixture)` → `(nova, bob)` logins; `WalletOf(fixture, token)` via `Wallet.Get`.
- [ ] **Step 2: Failing tests** in `TransferTests.cs`:

```csharp
[Fact] public async Task SendCreatesAParcelTheRecipientSeesAndAnEvent()
// nova sends 10 steel to bob with game_tick 500 → reply.Id > 0; bob Mailbox.List has 1 parcel kind GIFT from nova;
// bob's event stream (subscribed before the send) yields ParcelArrived with that id; nova's Player.LastOutgoingTick == 500 (query db).
[Fact] public async Task SendingToYourselfIsSelfTrade()        // ErrorDetail().Code == SELF_TRADE
[Fact] public async Task SendingToAnUnknownPlayerIsRecipientUnknown()
[Fact] public async Task SendingMoreSilverThanTheWalletHoldsIsWalletInsufficient() // fresh wallet, silver = 1
[Fact] public async Task SameClientRequestIdReturnsTheSameParcel() // two Sends, same id → same parcel id, bob has exactly 1 parcel
[Theory] [InlineData(201, 0)] [InlineData(0, 11)] public async Task OversizedParcelIsParcelTooBig(int items, int pawns)
[Fact] public async Task ParcelOverMaxBytesIsParcelTooBig() // one item with a 5 MB blob
[Fact] public async Task RecipientWithFiftyPendingParcelsRefusesMore() // 50 sends OK, 51st → FailedPrecondition, ErrorDetail().Message contains "mailbox is full"
[Fact] public async Task SendIsRateLimitedPerPlayer() // fixture.WithRateLimits(); two sends within 5 s → second ResourceExhausted
```

and in `MailboxTests.cs`:

```csharp
[Fact] public async Task DeliverIsIdempotent()            // Deliver twice → both OK; parcel gone from List; DeliveredTick == given tick
[Fact] public async Task DeliverCreditsSilverToTheRecipient() // nova sends 100 silver (nova funded via admin Apply in test through a scoped WalletService) → bob balance 0 until Deliver → 100 after
[Fact] public async Task ListMarksParcelsDeliveredAfterTheClientsSaveForRedelivery()
// bob delivers parcel at tick 900; bob uploads a save with tick 800 (SaveFixtures) → List shows the parcel with redeliver == true and delivered_tick 900
[Fact] public async Task DeliveringSomeoneElsesParcelIsNotFound()
```

- [ ] **Step 3: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~Economy` — Expected: FAIL (services missing).
- [ ] **Step 4: Implement** the services and gRPC classes per Interfaces; register in `Program.cs` (`AddScoped` for the four services, `AddSingleton<FreezeState>`, `AddSingleton<ModpackDefs>` as an empty placeholder class with `IsEmpty => true` until Task 5); rate-limit partition for `/rimlink.v1.Transfer/Send` and `/rimlink.v1.Market/Post` keyed by session with `SendCooldownSeconds` fixed window of 1 permit.
- [ ] **Step 5: Run** the same filter — Expected: PASS; `dotnet test Rimlink.sln` green.
- [ ] **Step 6: Commit** — `git commit -m "Add wallet, transfers and mailbox"`.

---

### Task 3: Market

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/MarketService.cs`, `.../Economy/LotExpiryService.cs` (BackgroundService, hourly, calls `MarketService.ExpireDue(DateTime now)`), `src/Rimlink.Server/Grpc/MarketGrpcService.cs`
- Modify: `src/Rimlink.Server/Program.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/MarketTests.cs`

**Interfaces:**
- Consumes: Task 2 `WalletService.Apply`, `MailboxService.Create`, `ParcelValidator`, `EconomyGate`, `TransferService` tick stamping (extract `Task StampOutgoing(long playerId, long gameTick)` on `TransferService` and reuse it).
- Produces `MarketService(db, wallet, mailbox, validator, gate, options, events)`:
  - `Task<Lot> Post(long sellerId, PostRequest r)` — gate, validator (items xor pawn), `MaxLotsPerPlayer` open lots, price ≥ 1, `StampOutgoing`, idempotent by `client_request_id`.
  - `Task<LotList> List(MarketQuery q)` — open lots, newest first, 50 per page, `text` matches `label`/`def_name`/seller name (case-insensitive `LIKE`).
  - `Task<Parcel> Withdraw(long sellerId, long lotId)` — only open + own; lot `Withdrawn`; parcel `LotReturn` to seller.
  - `Task<Parcel> Buy(long buyerId, long lotId)` — `BEGIN IMMEDIATE`; `SELF_TRADE` for own lot; lot must be `Open` else `LOT_GONE`; fee = `price * MarketFeePercent / 100` (integer, rounded down); buyer `Apply(-price, "purchase", lotId)`; seller `Apply(+(price - fee), "sale", lotId)`; bank `Apply(+fee, "fee", lotId)` when fee > 0; lot `Sold` + `BuyerPlayerId`; parcel `Purchase` to buyer with the lot's items/pawn; `Transfer` rows for both; `LotSold` to the seller.
  - `Task<int> ExpireDue(DateTime now)` — every open lot with `ExpiresAt <= now` → `Expired`, parcel `LotReturn` to seller, `LotClosed{reason:"expired"}`; returns the count. Must not throw when the seller row is gone (parcel still created for the id).

- [ ] **Step 1: Failing tests** in `MarketTests.cs` (helpers: `Fund(fixture, playerId, amount)` writes a `WalletEntry` + balance through a scoped `WalletService`):

```csharp
[Fact] public async Task BuyMovesSilverClosesTheLotAndMailsTheItems()
// nova posts 5 components at 300 (tick 100); bob funded 1000 buys → bob balance 700, nova 300, lot status SOLD, bob has a PURCHASE parcel with the items, nova gets LotSold event
[Fact] public async Task TwoBuyersRaceOnlyOnePays()
// bob and eve both funded 1000, Task.WhenAll(buy, buy) → exactly one succeeds, the other LOT_GONE; sum of balances == 1700; exactly one Purchase parcel
[Fact] public async Task FeeGoesToTheBankAccount()   // Economy:MarketFeePercent=10, price 100 → seller +90, wallet 0 == 10
[Fact] public async Task BuyingYourOwnLotIsSelfTrade()
[Fact] public async Task WithdrawReturnsTheItemsAsAParcel() // status WITHDRAWN, LOT_RETURN parcel to seller, List no longer shows it
[Fact] public async Task PostingBeyondMaxLotsIsRefused()   // Economy:MaxLotsPerPlayer=2 → third Post FailedPrecondition
[Fact] public async Task ExpiredLotIsReturnedAndAnnounced() // Economy:LotLifetimeDays=0 then ExpireDue(now + 1 day) == 1; LotClosed event; LOT_RETURN parcel
[Fact] public async Task ExpiredLotOfResetPlayerIsReturnedToTheirMailbox() // after AdminOperations.ResetPlayer the lot still expires without throwing
[Fact] public async Task BalanceEqualsTheSumOfEntriesAfterARandomSeries()
// seeded Random(7): 200 operations among post/buy/withdraw/send/deposit over 3 players; at the end for each wallet Balance == Sum(WalletEntry.Delta)
[Fact] public async Task ListSearchesByLabelAndPages() // 60 lots, page 2 has 10; text "comp" matches
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~MarketTests` — Expected: FAIL.
- [ ] **Step 3: Implement** `MarketService`, `MarketGrpcService`, `LotExpiryService` (`PeriodicTimer` 1 h; scoped service per tick), DI.
- [ ] **Step 4: Run** — Expected: PASS; whole suite green.
- [ ] **Step 5: Commit** — `git commit -m "Add the market board with escrow and lot expiry"`.

---

### Task 4: Anti-duplication, deposit/withdraw, freeze, rollback

**Files:**
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (Upload: `SAVE_STALE`; Rollback: reset `LastOutgoingTick`), `src/Rimlink.Server/Application/Economy/WalletService.cs` (Deposit/Withdraw), `src/Rimlink.Server/Application/Admin/AdminOperations.cs` (`RollbackSave` logs "returns given-away items" when below the tick), `src/Rimlink.Server/Grpc/WalletGrpcService.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/AntiDupeTests.cs`, `tests/Rimlink.Server.Tests/Economy/WalletTests.cs`

**Interfaces:**
- Consumes: Task 2/3 services; `SaveFixtures.MakeSave(seed, faction, tick)`, `Upload(...)`.
- Produces: `SaveStore.Upload` throws `RpcErrors.With(FailedPrecondition, SAVE_STALE, $"save tick {tick} is older than your last transfer at tick {last}")` before storing; `WalletService.Deposit(long playerId, DepositRequest)` (silver ≥ 1, stamps outgoing tick, idempotent) and `Withdraw(long playerId, WithdrawRequest)` → `Apply(-silver, "withdraw", parcelId)` + parcel `Withdraw` with `Silver = amount` (delivery credits nothing: the parcel's silver becomes physical in the game — `Parcel.Kind == Withdraw` is excluded from the credit in `MailboxService.Deliver`).

- [ ] **Step 1: Failing tests**:

```csharp
// AntiDupeTests
[Fact] public async Task UploadOlderThanLastOutgoingTickIsSaveStale() // send at tick 500 → upload tick 400 → SAVE_STALE and nothing stored; upload tick 500 → OK
[Fact] public async Task RollbackBelowTheOutgoingTickResetsItAndLogs() // versions tick 300 and 600, send at 600, rollback to v1 → LastOutgoingTick == 300, AdminAction.Details contains "given"
[Fact] public async Task FrozenEconomyRefusesTradesButAllowsDelivery() // FreezeState.Frozen = true → Send ECONOMY_FROZEN; existing parcel Deliver OK
[Fact] public async Task DisabledEconomyRefusesEverything()         // Economy:Enabled=false → Wallet.Get ECONOMY_FROZEN
// WalletTests
[Fact] public async Task DepositCreditsAndStampsTheTick()  // Deposit 50 tick 700 → balance 50, LastOutgoingTick 700; same client_request_id again → still 50
[Fact] public async Task WithdrawDebitsAndMailsPhysicalSilver() // funded 200, Withdraw 150 → balance 50, parcel WITHDRAW silver 150; Deliver does not credit (balance stays 50)
[Fact] public async Task WithdrawMoreThanBalanceIsWalletInsufficient()
[Fact] public async Task HistoryListsEntriesNewestFirst()
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AntiDupeTests|FullyQualifiedName~WalletTests"` — Expected: FAIL.
- [ ] **Step 3: Implement**; the stale check runs right after `SaveValidator` (before any row is written).
- [ ] **Step 4: Run** — PASS; suite green (the existing save tests still pass: no transfers → tick 0).
- [ ] **Step 5: Commit** — `git commit -m "Refuse stale saves after transfers, add deposit, withdraw and freeze"`.

---

### Task 5: Modpack defs, admin surface, metrics, CLI

**Files:**
- Create: `src/Rimlink.Server/Application/Modpack/ModpackDefs.cs` (replaces the Task 2 placeholder: `HashSet<string>` loaded from a new table `ModpackDefs(DefName PK)` at startup and on `Set`; `bool Contains(string)`, `bool IsEmpty`), `src/Rimlink.Tools/Commands/ModpackDefsCommand.cs`, `src/Rimlink.Tools/DefScanner.cs`
- Modify: `src/Rimlink.Protocol/Protos/modpack.proto` (`rpc SetDefs (DefList) returns (Empty); message DefList { repeated string def_names = 1; }`), `src/Rimlink.Server/Grpc/ModpackGrpcService.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs` (`/api/admin/economy/*`, `PUT /api/admin/modpack/defs`), `src/Rimlink.Server/Application/Admin/AdminOperations.cs` + `AdminCommandRunner.cs` (`wallet`, `parcels`, `lots`, `refund`, `economy freeze|unfreeze|stats`), `src/Rimlink.Tools/Commands/AdminCommand.cs`, `src/Rimlink.Tools/Commands/ModpackCommand.cs` (register `defs` subcommand), `src/Rimlink.Server/Hosting/Metrics.cs` (or where prometheus-net counters live)
- Storage: the `ModpackDefs` table is part of Task 1's `Economy` migration (see Task 1 Interfaces); no new migration here.
- Test: `tests/Rimlink.Server.Tests/Economy/ModpackDefsTests.cs`, `tests/Rimlink.Server.Tests/Economy/EconomyAdminTests.cs`, `tests/Rimlink.Server.Tests/Tools/DefScannerTests.cs`

**Interfaces:**
- Produces: `DefScanner.Scan(IEnumerable<string> modDirs) → SortedSet<string>` — reads every `Defs/**/*.xml`, collects `<defName>` of elements whose name is `ThingDef` or `PawnKindDef` (and any element with `Class="ThingDef"`/abstract bases skipped: `Abstract="True"` ignored); `AdminOperations.AdjustWallet(Actor, long playerId, long delta, string reason)`, `ListParcels(Actor, long playerId)`, `ListLots(Actor, long? playerId)`, `RefundLot(Actor, long lotId)` (open → withdraw to seller), `RefundParcel(Actor, long parcelId)` (undelivered → new parcel back to `FromPlayerId`; silver returned to the sender's wallet), `SetEconomyFrozen(Actor, bool, string reason)`, `EconomyStats(Actor) → (long silverInWallets, int openLots, int pendingParcels, int tradesToday)`. REST: `POST economy/wallet/{id}` `{delta, reason}`, `GET economy/parcels/{id}`, `GET economy/lots`, `POST economy/refund/lot/{id}`, `POST economy/refund/parcel/{id}`, `POST economy/freeze` `{frozen, reason}`, `GET economy/stats`, `PUT modpack/defs` `{defNames: []}`. Metrics: `rimlink_trades_total{kind}`, `rimlink_silver_moved_total`, `rimlink_parcels_pending` (gauge refreshed by `LotExpiryService` tick), `rimlink_economy_errors_total{code}` (incremented in the gRPC services' catch of `RpcException` with an economy code).
- Chat/CLI usages: `/wallet <player> [+n|-n] [reason]`, `/parcels <player>`, `/lots [player]`, `/refund lot <id>` / `/refund parcel <id>`, `/economy freeze [reason]|unfreeze|stats`; `rimlink-tools modpack defs --mods-dir <dirs...> --server URL --token T [--insecure] [--dry-run]`.

- [ ] **Step 1: Failing tests**:

```csharp
// DefScannerTests: temp dir with Mods/A/Defs/Things.xml (<ThingDef><defName>Steel</defName>, an Abstract="True" base) and Mods/B/1.7/Defs/Pawns.xml (<PawnKindDef><defName>Colonist</defName>)
[Fact] public void ScansThingAndPawnKindDefsSkippingAbstractOnes() // == {"Colonist","Steel"}
// ModpackDefsTests
[Fact] public async Task SendWithAnUnknownDefIsRefusedOnceDefsAreSet() // SetDefs(["Steel"]) as admin → send "Plasteel" → DEF_NOT_IN_MODPACK; send "Steel" OK; before SetDefs anything passes
[Fact] public async Task SetDefsRequiresAdmin()
// EconomyAdminTests (REST with Admin:ApiToken)
[Fact] public async Task WalletAdjustIsAppliedAndLogged()  // POST economy/wallet/{id} {delta:500} → balance 500, AdminAction row, WalletEntry kind "admin"
[Fact] public async Task RefundParcelReturnsItToSender()   // nova sends to bob (silver 20) → refund → nova has a parcel ADMIN with the items and balance back +20; bob's parcel gone
[Fact] public async Task RefundLotWithdrawsIt()
[Fact] public async Task FreezeViaChatCommandBlocksTrades() // admin chat "/economy freeze maintenance" → bob Send ECONOMY_FROZEN with "maintenance" in Detail
[Fact] public async Task StatsReportCounts()
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~DefScannerTests|FullyQualifiedName~ModpackDefsTests|FullyQualifiedName~EconomyAdminTests"` — FAIL.
- [ ] **Step 3: Implement** (DefScanner with `XmlReader`, `DtdProcessing.Prohibit`; `ModpackDefs` singleton reloaded after `SetDefs`; admin ops + runner cases + REST + CLI + metrics).
- [ ] **Step 4: Run** — PASS; suite green; `dotnet run --project src/Rimlink.Tools -- modpack defs --mods-dir D:\SteamLibrary\steamapps\common\RimWorld\Data --dry-run` prints a count > 1000.
- [ ] **Step 5: Commit** — `git commit -m "Add modpack def list, economy admin operations, metrics and CLI"`.

---

### Task 6: Client-core economy client and state

**Files:**
- Create: `src/Rimlink.Client.Core/Economy/EconomyClient.cs`, `src/Rimlink.Client.Core/Economy/EconomyState.cs`
- Modify: `src/Rimlink.Client.Core/Net/RimlinkConnection.cs` (typed clients `Wallet`, `Mailbox`, `Market`, `Transfer`), `src/Rimlink.Client.Core/State/SharedState.cs` (`Economy` property + `Apply` cases 50–53 + events)
- Test: `tests/Rimlink.Client.Tests/Economy/EconomyClientTests.cs`, `tests/Rimlink.Client.Tests/State/SharedStateTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1–4 proto/services; `RimlinkConnection` call-credentials pattern; `EventInbox`.
- Produces:
  - `EconomyState`: `long Balance`; `List<Parcel> Pending` (keyed by id, newest last); `List<Lot> MyLots`; events `ParcelArrived(Parcel)`, `LotSold(LotSold)`, `LotClosed(LotClosed)`, `WalletChanged(WalletChanged)`; `void Load(WalletState, ParcelList, LotList)`; `void MarkDelivered(long parcelId)`; `void Reset()`.
  - `SharedState.Economy` (`EconomyState`), `Apply` dispatches the four events (dedupe `ParcelArrived` by parcel id).
  - `EconomyClient(RimlinkConnection c)`: `Task<WalletState> Wallet(ct)`, `Task<ParcelList> Pending(ct)`, `Task Deliver(long parcelId, long gameTick, ct)`, `Task<LotList> Lots(MarketQuery, ct)`, `Task<Lot> Post(PostRequest, ct)`, `Task<Parcel> WithdrawLot(long, ct)`, `Task<Parcel> Buy(long, ct)`, `Task<Parcel> Send(SendRequest, ct)`, `Task<WalletState> Deposit(DepositRequest, ct)`, `Task<Parcel> WithdrawSilver(WithdrawRequest, ct)`, `Task<WalletHistory> History(ct)`; `static ErrorCode? CodeOf(RpcException)` (moved from `LoginFlow.ErrorCodeOf`, which now calls it); `static string NewRequestId() => Guid.NewGuid().ToString("N")`.

- [ ] **Step 1: Failing tests** (`EconomyClientTests` uses `RimlinkConnectionTests`'s `Connect`/`PasswordCredentials` helpers — move them to `tests/Rimlink.Client.Tests/Support/ClientFixtures.cs`):

```csharp
[Fact] public async Task RecipientStateGetsTheParcelFromTheEventStream() // bob connected; nova (raw gRPC) sends → bob.State.Economy.Pending has 1 parcel within 10 s; ParcelArrived event raised once even if the event is replayed
[Fact] public async Task WalletChangedUpdatesBalance()                   // admin REST adjust +500 → bob state Balance == 500
[Fact] public async Task SellerGetsLotSold()                             // bob posts, nova buys → bob LotSold event with price
[Fact] public void DuplicateParcelArrivedIsStoredOnce()                  // SharedStateTests
[Fact] public void CodeOfReadsTheErrorTrailer()
```

- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests --filter FullyQualifiedName~Economy` — FAIL.
- [ ] **Step 3: Implement**. `LoginFlow` snapshot (Task 8) will call `Load` after login; here only the core.
- [ ] **Step 4: Run** — PASS; suite green.
- [ ] **Step 5: Commit** — `git commit -m "Add client-core economy client and state"`.

---

### Task 7: Thing and pawn blobs (game layer)

**Files:**
- Create: `src/Rimlink.Client/Game/Economy/ThingBlobs.cs`, `src/Rimlink.Client/Game/Economy/BlobDebugActions.cs`
- Test: none possible in the solution (needs the game); verified by the debug action in game and by the Task 9 round trip.

**Interfaces:**
- Produces `ThingBlobs` (main thread only):
  - `static ItemBlob SerializeItem(Thing t)`; `static PawnBlob SerializePawn(Pawn p)`; `static Thing DeserializeItem(ItemBlob b)`; `static Pawn DeserializePawn(PawnBlob b)`; `static int SizeOf(ItemList, PawnList)`.
  - Approach: `Scribe.saver.InitSaving(tempPath, "rimlink")` → `Scribe_Deep.Look(ref thing, "thing")` → `FinalizeSaving()`; read the file, zstd-compress (`ZstdSharp.Compressor` level 3), delete the file. Load: write to a temp file, `Scribe.loader.InitLoading(tempPath)` → `Scribe_Deep.Look(ref thing, "thing")` → `FinalizeLoading()` (resolves cross-refs inside the blob; references to things outside the blob are dropped by design). Pawn before serialising: `PawnComponentsUtility` untouched; after deserialising: `pawn.SetFaction(Faction.OfPlayer)`; for humanlike `PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn)`; `Find.WorldPawns` not involved (the pawn was despawned, not passed to world).
  - Descriptor: `def_name = t.def.defName`, `stuff_def = t.Stuff?.defName ?? ""`, `count = t.stackCount`, `quality = t.TryGetQuality(out q) ? (int)q : -1`, `hit_points`/`max_hit_points` when `def.useHitPoints`, `market_value_x100 = (long)(t.MarketValue * 100)`, `label = t.LabelCapNoCount`; pawn: `kind_def`, `name = p.Name?.ToStringFull ?? p.LabelShort`, `humanlike = p.RaceProps.Humanlike`, `age_years = p.ageTracker.AgeBiologicalYears`, `skills` = humanlike `p.skills.skills.Select(s => (s.def.defName, s.Level))`.
- `BlobDebugActions`: `[DebugAction("Rimlink", "Blob round-trip selected thing")]` — serialises the first selected `Thing`/`Pawn`, despawns it, deserialises and respawns at the same cell; logs sizes and `Equals` of label/count/hp. (Debug actions appear in Dev mode only.)

- [ ] **Step 1: Implement** `ThingBlobs` + the debug action; `.\scripts\Build-ClientMod.ps1 -InstallTo <Mods>` — Expected: 0 errors.
- [ ] **Step 2: In game (dev mode, any colony):** run the debug action on a steel stack, a quality weapon and a colonist — Expected: log lines `[Rimlink] blob round-trip ok: <label> <bytes> B` for all three, pawn keeps name, skills, health and apparel.
- [ ] **Step 3: Commit** — `git commit -m "Add Scribe-based item and pawn blobs"`.

---

### Task 8: Comms console UI, outgoing flow, wallet

**Files:**
- Create: `src/Rimlink.Client/Game/Economy/RimlinkCommTarget.cs` (`ICommunicable`), `src/Rimlink.Client/Game/Economy/Outgoing.cs`, `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs`, `src/Rimlink.Client/UI/Economy/MarketTab.cs`, `.../MyLotsTab.cs`, `.../SendTab.cs`, `.../WalletTab.cs`, `.../MailboxTab.cs`, `src/Rimlink.Client/UI/Economy/ThingPicker.cs`, `src/Rimlink.Client/Game/Patches/Patch_CommsConsole_GetCommTargets.cs`
- Modify: `src/Rimlink.Client/UI/LoginFlow.cs` (after `LoadSnapshot`: `State.Economy.Load(wallet, pending, myLots)`), `src/Rimlink.Client/Game/RimlinkSession.cs` (`Economy` = `EconomyClient`), `mod/Languages/*/Keyed/Rimlink.xml` (keys `Rimlink.Economy.*`)
- Test: none in the solution (UI); in-game checklist below.

**Interfaces:**
- Consumes: Task 6 `EconomyClient`/`EconomyState`, Task 7 `ThingBlobs`, `SaveSync.TriggerSave`/`WaitForUpload`, `MainThread.Post/Ask`.
- Produces:
  - `Patch_CommsConsole_GetCommTargets` postfix on `Building_CommsConsole.GetCommTargets(Pawn)`: when `RimlinkSession.Active`, yields one `RimlinkCommTarget` (label "Rimlink", `TryOpenComms` → `Find.WindowStack.Add(new Dialog_Economy(console.Map))`). `GetFaction()` returns null; `CommFloatMenuOption` mirrors vanilla.
  - `Outgoing.SendItems(Map map, long toPlayerId, List<Thing> things, List<Pawn> pawns, long silver)` and `Outgoing.PostLot(Map, List<Thing> | Pawn, long price)` and `Outgoing.Deposit(Map, int silver)`: (1) pre-check limits with `Limits.Economy*`; (2) take things off the map (`SplitOff`/`DeSpawn`, pawns `DeSpawn`) and serialise; (3) `SaveSync.TriggerSave("transfer")` then `SaveSync.WaitForUpload` inside a `LongEventHandler.QueueLongEvent(..., "Rimlink.Saving", true, ...)`; (4) RPC with `game_tick = Find.TickManager.TicksGame` of that save and a new `client_request_id`; (5) on failure: deserialise and respawn at the original cells, `Messages.Message` with the code's Keyed text. All UI calls via `MainThread.Post`.
  - `ThingPicker`: list of `TradeUtility.AllLaunchableThingsForTrade(map)` (items under beacons) with count sliders; pawn list from `map.mapPawns.FreeColonistsAndPrisoners` + `map.mapPawns.SpawnedColonyAnimals`.
  - `Dialog_Economy(Map map)` tabs: Market (lots, Buy), My lots (Post from picker, Withdraw), Send (recipient picker over the players the client knows: owners of `State.Objects.All` ∪ `State.Players.Online`, i.e. anyone with a settlement or online; others are unreachable by design), Wallet (balance, history, Deposit from beacon silver, Withdraw n), Mailbox (pending, "Accept now" → Task 9).
  - Humanlike pawn send/post shows `Dialog_MessageBox.CreateConfirmation("Rimlink.Economy.ConfirmPawn", …)`.

- [ ] **Step 1: Implement** + strings (EN/RU); build/install.
- [ ] **Step 2: In game** (local server, two accounts): with a comms console and a beacon, open "Rimlink" from the console: Send 10 steel to nova → server log `Stored save v<n>` then `Transfer`; the steel is gone from the map; nova's `Mailbox.List` (via a second login) has the parcel. Deposit 50 silver → wallet 50. Post a lot, see it in Market from the other account, Buy it. Kill the server before a Send → steel reappears on the map and a message shows.
- [ ] **Step 3: Commit** — `git commit -m "Add the comms console economy UI and outgoing transfers"`.

---

### Task 9: Incoming parcels, delivery and stale saves

**Files:**
- Create: `src/Rimlink.Client/Game/Economy/Incoming.cs`
- Modify: `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (on `FinalizeInit` and on `ParcelArrived`: `Incoming.DeliverPending()` when the current map is the player's settlement map), `src/Rimlink.Client/Game/SaveSync.cs` (upload failure with `SAVE_STALE` → `LastError` + flag NOT set + `StaleSave` event), `src/Rimlink.Client/UI/LoginFlow.cs` (`UploadLocal`: on `SAVE_STALE` keep the file as `*.stale-<date>.rws`, clear the flag, continue with the server copy), `src/Rimlink.Client/UI/Economy/MailboxTab.cs` ("Accept now")
- Test: `tests/Rimlink.Client.Tests/Net/LoginPlanTests.cs` unchanged; in-game checklist.

**Interfaces:**
- Consumes: Task 6/7/8.
- Produces `Incoming.DeliverPending()` (main thread): for each `State.Economy.Pending` (skip ones with `redeliver == false && already spawned this session`): deserialise items/pawns (`ThingBlobs`); on any exception → log, `Messages.Message("Rimlink.Economy.ParcelFailed")`, leave the parcel pending, continue with the next; otherwise `DropPodUtility.DropThingsNear(center, map, things, openDelay: 110, forbid: false)` with `center` = a trade beacon's position if any else `DropCellFinder.TradeDropSpot(map)`; pawns inside the same pod with `SetFaction(OfPlayer)`; then `Economy.Deliver(id, Find.TickManager.TicksGame)` in a background task and on success `State.Economy.MarkDelivered(id)` + `SaveSync.TriggerSave("delivery")` (posted to main thread). Silver of non-`Withdraw` parcels is credited server-side; `Withdraw` parcels spawn `ThingDefOf.Silver` stacks of `parcel.Silver`.
- `SaveSync`: an upload refused with `SAVE_STALE` is terminal for that file: the pending flag is not set; `StaleSave` is raised and `RimlinkGameComponent` shows `Dialog_MessageBox` "Rimlink.Save.Stale" with two buttons: "Back to menu" (`AbortToMenu`: no save, no flag; the player logs in again and gets the server copy) and "Keep playing" (warning text: nothing from this session will reach the server).

- [ ] **Step 1: Implement** + strings; build/install.
- [ ] **Step 2: In game:** log in as nova → the steel from Task 8 drops in a pod; server `Deliver`; `Saves/…` v+1. Kill the game right after the pod lands (before any save), relaunch, log in → the pod drops again (redeliver) and `Deliver` is called with the new tick. Send from admin to nova, then on admin load the local save from before the send via "Load" → session ends (Task 6 core guard) → log in → `SAVE_STALE` dialog with the pending file kept as `*.stale-*.rws`. Set `Economy:…` nothing else.
- [ ] **Step 3: Commit** — `git commit -m "Deliver parcels by drop pod and handle stale saves"`.

---

### Task 10: Caravan delivery to another player's settlement

**Files:**
- Modify: `src/Rimlink.Client/Game/PlayerSettlement.cs` (`GetFloatMenuOptions(Caravan)` → "Rimlink.Economy.CaravanTransfer" → `CaravanArrivalAction_RimlinkTransfer`; `GetCaravanGizmos` when a caravan is on the tile: "Transfer" command), create `src/Rimlink.Client/Game/Economy/CaravanArrivalAction_RimlinkTransfer.cs`, `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs` (constructor `Dialog_Economy(Caravan caravan)` — Send tab only, source = `CaravanInventoryUtility.AllInventoryItems(caravan)` + `caravan.PawnsListForReading`, recipient fixed to the settlement owner), `src/Rimlink.Client/Game/Economy/Outgoing.cs` (`SendFromCaravan(Caravan, long toPlayerId, List<Thing>, List<Pawn>)`: things removed via `CaravanInventoryUtility.GiveThing`/`Thing.Destroy` after serialising, pawns `caravan.RemovePawn` then serialise; if the caravan is left empty, `caravan.Destroy()`)
- Test: in-game checklist.

- [ ] **Step 1: Implement** (`CaravanArrivalAction` subclass with `Arrived(Caravan)` opening the dialog; `StillValid` requires the settlement to still exist); build/install.
- [ ] **Step 2: In game:** form a caravan with 20 steel and one animal, right-click nova's settlement → "Transfer to Nova…", arrive, send both → server `Transfer` rows; caravan stays on the tile; nova receives on login.
- [ ] **Step 3: Commit** — `git commit -m "Add caravan transfers to other players' settlements"`.

---

### Task 11: Documentation, checklist, packaging

**Files:**
- Modify: `docs/player-guide.md` (section "Trading and gifts": console, beacons, wallet, market, caravan, what `SAVE_STALE` means), `docs/server-setup.md` (`Economy:*`, `rimlink-tools modpack defs`, admin economy commands, metrics), `docs/manual-checklist.md` (section "Экономика": the in-game steps of Tasks 7–10 plus Review Focus 5: send an item whose def is not in the receiver's game with defs check off → parcel stays pending with a message), `README.md` (status line), `deploy/appsettings.example.json` (`Economy` block)
- Test: full `dotnet test Rimlink.sln`; `.\scripts\Package-Release.ps1`.

- [ ] **Step 1:** Write the docs; bump `ClientInfo.Version` to `0.2.0` and `mod/About/About.xml` `modVersion`; keep `ProtocolVersion.Current` at 1 (additive protocol).
- [ ] **Step 2:** Run the manual checklist "Экономика" end to end on the local server with two accounts; tick every line or record why not.
- [ ] **Step 3: Commit** — `git commit -m "Document the economy and bump the version to 0.2.0"`.
