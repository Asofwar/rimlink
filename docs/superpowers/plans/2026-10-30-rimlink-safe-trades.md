# Rimlink Safe Trades Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A player decides what enters their colony and who may reach them: player gifts wait for "accept / refuse", a personal block list and a "no trades" switch, and a two-way trade with counter-offers and double confirmation on server escrow that works while the partner is offline.

**Architecture:** Server first: `parcels.Decision` turns a player gift into an awaiting parcel the client does not drop; `GiftService` answers or auto-refuses it and returns the contents through a `GiftReturn` parcel and the wallet. `SocialService` owns `player_blocks` and the two new player flags; chat, transfers and trades ask it before anything moves. `TradeService` keeps both sides' escrow in the `trades` row, bumps a revision on every change and exchanges by `Trade` parcels once both sides confirmed the same revision. The client core gets `TradesClient`, `SocialClient`, `SocialState` and two outbox kinds; the mod gets a gift letter, mailbox buttons, a Trades tab, Players-window buttons and two settings; BridgeTools gets `rimlink/*` tools for the s4 run.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + minimal APIs, EF Core Sqlite, protobuf, prometheus-net, xUnit, RimWorld 1.7 + Harmony (mod), RimBridgeServer companion (BridgeTools).

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-safe-trades-design.md`

## Global Constraints

- Protocol only grows. Numbers as of `main` 07df8d9: `ErrorCode` `BLOCKED = 32`, `TRADES_CLOSED = 33`, `TRADE_GONE = 34`, `TRADE_CHANGED = 35`, `TRADE_LIMIT = 36`; `ParcelKind` `PARCEL_GIFT_RETURN = 8`, `PARCEL_TRADE = 9`, `PARCEL_TRADE_RETURN = 10`; `Parcel.awaiting_decision = 11`; `PlayerFlags.accept_gifts = 5`, `trades_closed = 6`; `ServerEvent` `gift_refused = 76`, `trade_updated = 77`. A branch landing later with the same numbers renumbers at rebase. `ProtocolVersion.Current` does not change.
- One migration `SafeTrades` (`dotnet ef migrations add SafeTrades --project src/Rimlink.Server`, no `-nologo`); regenerate it at rebase if `main` gained a migration meanwhile.
- No version bump (`mod/About/About.xml`, `ClientInfo.Version` stay).
- Config defaults: `Economy:GiftDecisionDays` 7, `Economy:TradeLifetimeDays` 7, `Economy:MaxOpenTrades` 5, `Social:MaxBlocks` 200; trade note at most 200 characters.
- Wallet entry kinds: `trade` (silver into a trade's escrow), `refund` (silver back from a refused gift, a closed trade or a lowered trade side), `deliver` (existing: silver of a `Trade` parcel on delivery). `GiftReturn` and `TradeReturn` parcels carry items and pawns only, their silver goes to the wallet; a `Trade` parcel carries the other side's silver and is credited by the existing `Mailbox.Deliver`.
- Returns and exchange parcels are created with `ignoreMailboxLimit: true`. Nothing is ever destroyed: a side's contents are at any moment in exactly one place (owner's game, trade escrow, or a parcel).
- Block checks run in both directions (A blocked B or B blocked A) for DMs, gifts and new trades; the error names who blocked: `"you blocked this player"` or `"the player does not accept this from you"`, code `BLOCKED`.
- Every user-facing string is keyed in `mod/Languages/English` and `mod/Languages/Russian`.
- Personal events (gifts, refusals, blocks, trades) never reach `AdminFeed`; the admin trade cancel reaches it only through the existing audit (`admin.action`).
- Before every commit: `dotnet test Rimlink.sln -nologo -v q` green and the mod builds with `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'` (no `-InstallTo`; the shared game is the coordinator's). The mod and companion are installed only into the s4 stand.
- Commits: identity `Asofwar <7397608+Asofwar@users.noreply.github.com>`, imperative subject, no trailers of any kind (git-safe-commit preflight). Never merge or push `main`.

## Review Focus

1. Two `Confirm` presses, or `Confirm` racing the other side's `SetSide`: exactly one exchange, never a confirm of a side the confirmer has not seen. Tests in Task 4: `ConfirmOfAnOldRevisionIsTradeChanged`, `SecondConfirmAfterTheExchangeReturnsTheCompletedTrade`.
2. `SetSide` replayed from the outbox after the partner confirmed (exchange done) or after a cancel: the replay returns the trade and moves nothing, and the client does not put the things back on the map. Tests: Task 4 `SetSideRepeatAfterTheTradeClosedReturnsTheTradeAndMovesNothing`, Task 6 `ReplayedSetSideAfterTheExchangeCompletesTheEntry`.
3. Refusing a gift whose sender was reset: no crash, the contents land in the sender's mailbox for the new colony. Task 3 `RefusingAGiftOfAResetSenderReturnsItToTheirMailbox`.
4. A block while a trade between the two is open: both sides come back, nothing stays in escrow. Task 5 `BlockCancelsOpenTradesAndReturnsBothSides`.
5. The sender's save is older than the tick a gift return was delivered at: the return drops again instead of being lost, and is not doubled. Task 3 `GiftReturnRedeliversWhenTheSendersSaveIsOlder`.

---

### Task 1: Storage, protocol, config

**Files:**
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs` (`ParcelKindDb`, `Parcel`, `Player`; new `ParcelDecisionDb`, `TradeStatusDb`, `Trade`, `PlayerBlock`)
- Modify: `src/Rimlink.Server/Storage/RimlinkDbContext.cs`
- Create: `src/Rimlink.Server/Storage/Migrations/<timestamp>_SafeTrades.cs` (generated)
- Modify: `src/Rimlink.Protocol/Protos/common.proto`, `economy.proto`, `raids.proto`, `events.proto`; Create: `trades.proto`, `social.proto`
- Modify: `src/Rimlink.Server/Hosting/ServerOptions.cs` (`EconomyOptions` + new `SocialOptions`), `src/Rimlink.Server/Program.cs` (bind/validate `Social`)
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`, `tests/Rimlink.Server.Tests/Economy/EconomyOptionsTests.cs`

**Interfaces:**
- Produces (storage): `enum ParcelKindDb { …, Captive = 7, GiftReturn = 8, Trade = 9, TradeReturn = 10 }`; `enum ParcelDecisionDb { None = 0, Awaiting = 1, Accepted = 2, Refused = 3 }`; `Parcel.Decision`; `Player.AcceptGifts`, `Player.TradesClosed` (bool, default false); `enum TradeStatusDb { Open = 0, Completed = 1, Cancelled = 2, Expired = 3 }`; `class Trade { long Id; long InitiatorPlayerId; long PartnerPlayerId; TradeStatusDb Status; int Revision; DateTime CreatedAt; DateTime ExpiresAt; DateTime? ClosedAt; string? CloseReason; string? ClientRequestId; byte[] InitiatorItems, InitiatorPawns; long InitiatorSilver; string InitiatorNote; bool InitiatorConfirmed; byte[] PartnerItems, PartnerPawns; long PartnerSilver; string PartnerNote; bool PartnerConfirmed }` (the entity shares its name with the proto message, as `Parcel` does; alias where both are in scope); `class PlayerBlock { long Id; long BlockerPlayerId; long BlockedPlayerId; DateTime CreatedAt }`. DbSets `Trades`, `PlayerBlocks`; tables `trades`, `player_blocks`; unique index `(BlockerPlayerId, BlockedPlayerId)`, index `BlockedPlayerId`; `trades` indexes `Status`, `InitiatorPlayerId`, `PartnerPlayerId`, unique filtered `ClientRequestId`.
- Produces (protocol): see the proto text below. Options: `EconomyOptions.GiftDecisionDays = 7`, `TradeLifetimeDays = 7`, `MaxOpenTrades = 5` (each ≥ 1); `SocialOptions { int MaxBlocks = 200 }` (≥ 1), section `Social`.

```proto
// trades.proto (package and options as in economy.proto; imports common.proto, economy.proto, google/protobuf/timestamp.proto)
enum TradeStatus { TRADE_OPEN = 0; TRADE_COMPLETED = 1; TRADE_CANCELLED = 2; TRADE_EXPIRED = 3; }
message TradeSide { ItemList items = 1; PawnList pawns = 2; int64 silver = 3; string note = 4; bool confirmed = 5; }
message Trade {
  int64 id = 1; PlayerRef initiator = 2; PlayerRef partner = 3; TradeStatus status = 4; int32 revision = 5;
  TradeSide initiator_side = 6; TradeSide partner_side = 7;
  google.protobuf.Timestamp created_at = 8; google.protobuf.Timestamp expires_at = 9; google.protobuf.Timestamp closed_at = 10;
  string close_reason = 11;
}
message TradeOpenRequest { string client_request_id = 1; int64 partner_id = 2; ItemList items = 3; PawnList pawns = 4; int64 silver = 5; string note = 6; int64 game_tick = 7; }
// silver and note are the side's new totals; return_goods: the escrowed goods come back by TradeReturn and items/pawns replace them,
// otherwise items/pawns are added to what is already offered (a note-only change sends neither and moves no goods).
message TradeSideRequest { string client_request_id = 1; int64 trade_id = 2; ItemList items = 3; PawnList pawns = 4; int64 silver = 5; string note = 6; int64 game_tick = 7; bool return_goods = 8; }
message TradeConfirmRequest { int64 trade_id = 1; int32 revision = 2; }
message TradeRef { int64 trade_id = 1; }
message TradeList { repeated Trade trades = 1; }
message TradeUpdated { Trade trade = 1; }
service Trades {
  rpc Open (TradeOpenRequest) returns (Trade);
  rpc SetSide (TradeSideRequest) returns (Trade);
  rpc Confirm (TradeConfirmRequest) returns (Trade);
  rpc Cancel (TradeRef) returns (Trade);
  rpc List (Empty) returns (TradeList);
}

// social.proto (imports common.proto, raids.proto)
message BlockList { repeated PlayerRef players = 1; }
message FlagRequest { bool on = 1; }
service Social {
  rpc Block (PlayerRef) returns (BlockList);
  rpc Unblock (PlayerRef) returns (BlockList);
  rpc ListBlocks (Empty) returns (BlockList);
  rpc SetAcceptGifts (FlagRequest) returns (PlayerFlags);
  rpc SetTradesClosed (FlagRequest) returns (PlayerFlags);
}

// economy.proto additions
//   ParcelKind: PARCEL_GIFT_RETURN = 8; PARCEL_TRADE = 9; PARCEL_TRADE_RETURN = 10;
//   Parcel: bool awaiting_decision = 11;
message ParcelAnswer { int64 parcel_id = 1; bool accept = 2; }
//   service Mailbox: rpc Answer (ParcelAnswer) returns (Empty);
// Sent to the sender and to the recipient (whose client drops the parcel from its mailbox state).
message GiftRefused { int64 parcel_id = 1; PlayerRef by = 2; string returned = 3; bool expired = 4; }

// raids.proto PlayerFlags: bool accept_gifts = 5; bool trades_closed = 6;
// common.proto ErrorCode: BLOCKED = 32; TRADES_CLOSED = 33; TRADE_GONE = 34; TRADE_CHANGED = 35; TRADE_LIMIT = 36;
// events.proto: import "trades.proto"; oneof body: GiftRefused gift_refused = 76; TradeUpdated trade_updated = 77;
```

- [ ] **Step 1: Write the failing tests.** In `SchemaTests`: `SafeTradesTablesAndColumnsExist` — after migrations, `PRAGMA table_info` shows `trades` (all columns above), `player_blocks` (`BlockerPlayerId`, `BlockedPlayerId`, `CreatedAt`), `parcels.Decision`, `players.AcceptGifts`, `players.TradesClosed`; inserting the same `(BlockerPlayerId, BlockedPlayerId)` twice throws `DbUpdateException`. In `EconomyOptionsTests`: `SafeTradesDefaults` — `new EconomyOptions()` has `GiftDecisionDays == 7`, `TradeLifetimeDays == 7`, `MaxOpenTrades == 5`; `new SocialOptions().MaxBlocks == 200`; a server with `Economy:MaxOpenTrades=0` or `Social:MaxBlocks=0` fails validation at start (same assertion style as the existing invalid-option tests in that file).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~EconomyOptionsTests" -nologo -v q`. Expected: FAIL to compile (`SocialOptions`, `Trades` missing).
- [ ] **Step 3: Implement** entities, context configuration, options (+ validation in the same style as `EconomyOptions`), the protos; generate the migration with the command from Global Constraints.
- [ ] **Step 4: Run** the same filter. Expected: PASS. Then the full suite. Expected: green (nothing uses the new fields yet).
- [ ] **Step 5: Commit** `Add the storage, protocol and options for gifts, blocks and trades`.

### Task 2: Block list, flags, chat filter

**Files:**
- Create: `src/Rimlink.Server/Application/Social/BlockRules.cs`, `src/Rimlink.Server/Application/Social/SocialService.cs`, `src/Rimlink.Server/Grpc/SocialGrpcService.cs`
- Modify: `src/Rimlink.Server/Application/Raids/RaidService.cs` (`FlagsOf`), `src/Rimlink.Server/Application/Events/EventBus.cs`, `src/Rimlink.Server/Application/Chat/ChatService.cs`, `src/Rimlink.Server/Application/Economy/TransferService.cs`, `src/Rimlink.Server/Program.cs` (DI, `MapGrpcService<SocialGrpcService>`)
- Create: `tests/Rimlink.Server.Tests/Support/SocialFixtures.cs`; Test: `tests/Rimlink.Server.Tests/Social/BlockTests.cs`, `tests/Rimlink.Server.Tests/Social/FlagsTests.cs`

**Interfaces:**
- Consumes: Task 1 entities and `Social` proto, `SocialOptions`.
- Produces: `static class BlockRules` with `Task RequireNotBlocked(RimlinkDbContext db, long actorId, long otherId)` (throws `BLOCKED` with the Global Constraints texts; a static helper so `TradeService` can check blocks without depending on `SocialService`, which in Task 5 depends on `TradeService`). `SocialService(RimlinkDbContext db, EventBus events, IOptions<SocialOptions> options)` scoped, with `Task<List<PlayerRef>> Block(long blockerId, long blockedId)`, `Task<List<PlayerRef>> Unblock(long blockerId, long blockedId)`, `Task<List<PlayerRef>> List(long blockerId)`, `Task<HashSet<long>> BlockersOf(long playerId)` (who blocked this player), `Task<HashSet<long>> BlockedBy(long playerId)` (whom this player blocked), `Task<PlayerFlags> SetAcceptGifts(long playerId, bool on)`, `Task<PlayerFlags> SetTradesClosed(long playerId, bool on)` (both send `PlayerFlagsChanged` like `RaidService.SetVisitsClosed`). `EventBus.BroadcastExcept(IReadOnlySet<long> playerIds, ServerEvent ev)`. Fixture: `record SocialClients(Protocol.V1.Social.SocialClient Social, Protocol.V1.Trades.TradesClient Trades, EconomyClients Economy)`; `SocialFixtures.Social(ServerFixture fixture, string token)`; `SocialFixtures.AcceptGifts(ServerFixture fixture, long playerId)` (sets `Player.AcceptGifts` in the DB).
- Block rules: self or a target with role Moderator/Admin → `InvalidArgument`; unknown player → `NotFound`; at `MaxBlocks` → `InvalidArgument` `"block list is full ({max})"`; blocking twice or unblocking a stranger is a no-op returning the list. `FlagsOf` sets `AcceptGifts`, `TradesClosed`.

- [ ] **Step 1: Write the failing tests** (two or three players registered with `RegisterAndLogin`, as in `TransferTests`):
  - `BlockedPlayersDirectMessageIsBlocked`: Nova blocks Bob; Bob's `Chat.Send` to Nova → `RpcException` `FailedPrecondition`, detail code `ErrorCode.Blocked`; Nova's DM to Bob → also `Blocked` (message `"you blocked this player"`); nothing stored in `ChatMessages`.
  - `BlockedPlayersGiftIsRefusedBeforeAnythingMoves`: Bob funded 100; Nova blocks Bob; Bob `Transfer.Send` to Nova with items and silver 50 → `Blocked`; Bob's wallet still 100, no parcel, `Players.LastOutgoingTick` of Bob unchanged.
  - `BlockedPlayersPublicLinesSkipTheBlocker`: Nova blocks Bob; Bob sends a public line; Carol's stream receives it, Nova's stream does not within 1 s (then Carol's second line does reach Nova, proving the stream is live); Nova's `Chat.History` lacks Bob's line and Carol's history has it; a `ChatService.System` line reaches Nova.
  - `ModeratorsAndYourselfCannotBeBlocked`: block self → `InvalidArgument`; block a player with `Role.Moderator` → `InvalidArgument`.
  - `BlockListIsCappedAndIdempotent`: `Social:MaxBlocks=1`; block Bob twice → one row; block Carol → `InvalidArgument`; unblock Bob → empty list.
  - `FlagsTests.AcceptGiftsAndTradesClosedRoundTrip`: `SetAcceptGifts(true)` returns flags with `AcceptGifts`, stream gets `PlayerFlagsChanged` with it; `Raids.Flags` returns both; `SetTradesClosed(true)` likewise; toggling peaceful afterwards keeps both set.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~Social" -nologo -v q`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** `SocialService`, `SocialGrpcService` (player id from `context.GetSession().PlayerId`), `FlagsOf`, `EventBus.BroadcastExcept(set, ev)`; `ChatService.Send` checks `BlockRules.RequireNotBlocked` for a DM after the mute check, and broadcasts a public line with `BroadcastExcept(await social.BlockersOf(from.Id), ev)` when that set is not empty; `History` excludes public rows whose `FromPlayerId` is in `BlockedBy(reader)` inside the query, so the reader still gets up to `Limits.ChatOnLogin` lines; `TransferService.Send` calls `BlockRules.RequireNotBlocked` first, before the validator and the transaction.
- [ ] **Step 4: Run** the filter, then the full suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `Add a personal block list and the gifts and trades flags`.

### Task 3: Gifts wait for a decision

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/GiftService.cs`, `src/Rimlink.Server/Application/Economy/SafeTradesSweep.cs`
- Modify: `src/Rimlink.Server/Application/Economy/MailboxService.cs` (`Create`, `ToProto`, `Deliver`), `EconomyMetrics.cs`, `src/Rimlink.Server/Grpc/EconomyGrpcServices.cs` (`MailboxGrpcService.Answer`), `src/Rimlink.Server/Program.cs`
- Modify: existing tests that deliver a player gift (found by the suite run in Step 4)
- Test: `tests/Rimlink.Server.Tests/Economy/GiftDecisionTests.cs`

**Interfaces:**
- Consumes: Task 2 `SocialFixtures.AcceptGifts`, `Player.AcceptGifts`.
- Produces: `GiftService(RimlinkDbContext db, MailboxService mailbox, WalletService wallet, EventBus events, IOptions<EconomyOptions> options, ILogger<GiftService> log)` scoped: `Task Answer(long playerId, long parcelId, bool accept)`, `Task<int> RefuseOverdue(DateTime now)`. `SafeTradesSweep : BackgroundService` (pattern of `LotExpiryService`: at start, then hourly) calls `GiftService.RefuseOverdue(DateTime.UtcNow)`; Task 4 adds the trade expiry to the same loop. `EconomyMetrics.GiftsRefused` = counter `rimlink_gifts_refused_total`.
- Rules: `MailboxService.Create` gives a `Gift` with a sender `Decision = Awaiting` unless the recipient has `AcceptGifts` (then `Accepted`); every other parcel `None`. `ToProto` sets `AwaitingDecision`. `Pending` still lists awaiting parcels. `Deliver` of an awaiting parcel → bare `RpcException(StatusCode.FailedPrecondition, "the gift awaits a decision")`. `Answer`: parcel not found or not addressed to `playerId` → `NotFound`; `Decision` `None` → `FailedPrecondition` `"this parcel needs no decision"`; already decided → return without change; accept → `Accepted`, then `ParcelArrived` with the flag cleared to the recipient; refuse → in one `Serializable` transaction `Refused`, `DeliveredAt = now`, `DeliveredTick = 0`, a `GiftReturn` parcel from the recipient to the sender with the items and pawns (only if any), silver to the sender's wallet (`refund`, ref = parcel id), a `Transfer` row kind `gift-refuse`; after commit `wallet.FlushEvents()`, `mailbox.Announce(return)`, `GiftRefused {parcel_id, by = recipient, returned = TransferService.Summary(...), expired}` to sender and recipient, metric. `RefuseOverdue` refuses awaiting gifts older than `GiftDecisionDays` the same way with `expired = true`.

- [ ] **Step 1: Write the failing tests:**
  - `GiftAwaitsADecisionAndCannotBeDelivered`: Nova sends Bob steel; Bob's `Mailbox.List` has it with `AwaitingDecision == true`; `Mailbox.Deliver` → `FailedPrecondition`.
  - `AcceptedGiftIsDeliveredNormally`: `Answer(accept)` → stream gets `ParcelArrived` with `AwaitingDecision == false`; `Deliver` succeeds; a second `Answer(refuse)` changes nothing (still delivered, no return parcel).
  - `RefusedGiftComesBackToTheSender`: Nova sends 10 steel, a pawn and 50 silver; Bob refuses; Bob's pending list is empty; Nova's mailbox has one `ParcelGiftReturn` from Bob with the steel and the pawn and silver 0; Nova's wallet is back to the funded amount; Nova's stream gets `GiftRefused` with `By.Name == "Bob"`, `Expired == false`; Bob's stream gets `GiftRefused` with the same parcel id; `rimlink_gifts_refused_total` grew by 1.
  - `SilverOnlyGiftRefusalCreatesNoParcel`: refusal of a silver-only gift → no parcel for Nova, wallet restored.
  - `AcceptGiftsFlagSkipsTheDecision`: `AcceptGifts(fixture, bob)`; the gift arrives with `AwaitingDecision == false` and delivers.
  - `OverdueGiftsAreRefusedBySweep`: gift `CreatedAt` set 8 days back in the DB; `GiftService.RefuseOverdue(DateTime.UtcNow)` returns 1; Nova gets the return and `GiftRefused` with `Expired == true`.
  - `OnlyTheRecipientMayAnswer`: Carol answers Bob's gift → `NotFound`; answering a `Purchase` parcel → `FailedPrecondition`.
  - `RefusingAGiftOfAResetSenderReturnsItToTheirMailbox`: admin `ResetPlayer(nova)` (via `AdminOperations` with an admin `Actor`), then Bob refuses → no exception, Nova's mailbox has the return.
  - `GiftReturnRedeliversWhenTheSendersSaveIsOlder`: after the refusal Nova delivers the return at tick 900; Nova uploads a save with tick 800 (helper used by `AntiDupeTests`); Nova's `Mailbox.List` shows the return again with `Redeliver == true`, and exactly one return parcel exists.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~GiftDecisionTests" -nologo -v q`. Expected: FAIL to compile (`Answer` missing).
- [ ] **Step 3: Implement** the rules above; register `GiftService` and `SafeTradesSweep`.
- [ ] **Step 4: Run** the filter (PASS), then the full suite. Tests that deliver a player gift and are not about gifts now fail on `Deliver`: give each recipient `AcceptGifts(fixture, id)` right after registration (server tests: `SocialFixtures.AcceptGifts`; client tests: the same helper added to `tests/Rimlink.Client.Tests/Support`). Expected: green.
- [ ] **Step 5: Commit** `Let gift recipients accept or refuse a gift`.

### Task 4: Two-way trades

**Files:**
- Create: `src/Rimlink.Server/Application/Trades/TradeService.cs`, `src/Rimlink.Server/Application/Trades/TradeMetrics.cs`, `src/Rimlink.Server/Grpc/TradesGrpcService.cs`
- Modify: `src/Rimlink.Server/Application/Economy/SafeTradesSweep.cs`, `src/Rimlink.Server/Grpc/RateLimitPolicy.cs`, `src/Rimlink.Server/Program.cs`
- Test: `tests/Rimlink.Server.Tests/Trades/TradeTests.cs`, `tests/Rimlink.Server.Tests/Trades/TradeEscrowTests.cs`

**Interfaces:**
- Consumes: Task 1 `Trade` entity and proto; Task 2 `BlockRules.RequireNotBlocked`; `MailboxService.Create/Announce/StripBlobs`, `WalletService.Apply/FlushEvents`, `TransferService.StampOutgoing/Summary`, `ParcelValidator`, `EconomyGate.RequireOpen`.
- Produces: `TradeService` scoped: `Task<Protocol.V1.Trade> Open(long playerId, TradeOpenRequest r)`, `SetSide(long playerId, TradeSideRequest r)`, `Confirm(long playerId, long tradeId, int revision)`, `Cancel(long playerId, long tradeId)`, `Task<TradeList> List(long playerId)`, `Task<int> CancelBetween(long a, long b, string reason)`, `Task<int> CancelAllOf(long playerId, string reason)`, `Task<bool> AdminCancel(long tradeId, string reason)`, `Task<int> ExpireDue(DateTime now)`, `Task<List<Storage.Entities.Trade>> ListFor(long? playerId)` (admin, newest first, at most 200). `TradeMetrics.Outcomes` = counter `rimlink_player_trades_total` with label `outcome` (`opened|completed|cancelled|expired`); the name differs from the spec's because `rimlink_trades_total{kind}` already exists in `EconomyMetrics`. Rate limits `/rimlink.v1.Trades/Open` and `/rimlink.v1.Trades/SetSide` = `(1, TimeSpan.FromSeconds(5))`.
- Rules (each mutating call is one `Serializable` transaction; events and announces after commit; `TradeUpdated` to both parties after every change, blobs stripped):
  - `Open`: idempotency first (a trade with this `ClientRequestId` → return it). Gate open; partner: self → `SelfTrade`, unknown → `RecipientUnknown`, `BlockRules.RequireNotBlocked`, partner `TradesClosed` → `TRADES_CLOSED`; open trades of either party ≥ `MaxOpenTrades` → `TRADE_LIMIT`; note: `ChatService.CleanText` rules but empty allowed, ≤ 200; side size by `ParcelValidator` (same limits as a parcel); silver ≥ 0 and wallet covers it (`WalletInsufficient`). Wallet `-silver` `trade`; `StampOutgoing(playerId, game_tick)` when goods are present; revision 1, initiator confirmed, `ExpiresAt = now + TradeLifetimeDays`; `Transfer` row `trade-open`; metric `opened`.
  - `SetSide`: idempotency first: a `Transfer` row with this `ClientRequestId` → return the current trade whatever its status (it was applied). Then the trade must exist, the caller a party, status open → else `TRADE_GONE`. Gate open. With `return_goods` the escrowed goods go back as one `TradeReturn` parcel (only if any) and the request's goods replace them; without it the request's goods are added (size check on the sum). Silver: wallet delta `old − new` (`trade` when taking, `refund` when giving back). Own side confirmed, the other side unconfirmed, revision + 1; `Transfer` row `trade-side` carrying the `ClientRequestId`; `StampOutgoing` when the request carries goods.
  - `Confirm`: caller not a party or unknown trade → `TRADE_GONE`; completed → return it; cancelled/expired → `TRADE_GONE`; `revision` ≠ current → `TRADE_CHANGED` (the current trade reaches the client by the `TradeUpdated` the change sent, or `List`); own side already confirmed → return unchanged; else confirm, and when both are confirmed: exchange — each party gets the other side as a `Trade` parcel from the other party (items, pawns and silver; no parcel for an empty side), status `Completed`, `ClosedAt`, `Transfer` rows `trade` both ways, metric `completed`.
  - `Cancel`: a party only; closed → return it; open → close as `Cancelled` with reason `"cancelled by {name}"`. Closing (cancel, block, reset, admin, expiry) gives each side its goods back as `TradeReturn` (only if any) and its silver back to the wallet (`refund`), clears the escrow columns, metric `cancelled` or `expired`. Cancel and expiry ignore the economy gate.
  - `List`: open trades of the player plus those closed within the last 7 days, newest first.

- [ ] **Step 1: Write the failing tests** (`TradeTests` for moves, `TradeEscrowTests` for where goods are; Nova and Bob funded 100 each):
  - `OpenEscrowsTheInitiatorsSide`: Nova opens with 10 steel, a pawn, 30 silver, note "for your rifle"; returned trade has revision 1, initiator confirmed, partner not; Nova's wallet 70, `LastOutgoingTick` = request tick; Bob's stream gets `TradeUpdated` with blobs stripped (`Items.Items[0].Data` empty); `rimlink_player_trades_total{outcome="opened"}` +1.
  - `OpenRefusals`: to self → `SelfTrade`; to a player who closed trades → `TradesClosed`; to/from a blocked pair → `Blocked`; sixth open trade → `TradeLimit`; 201-character note → `InvalidArgument`; silver 500 → `WalletInsufficient`; in each case Nova's wallet unchanged and no trade row.
  - `SameOpenRequestIdReturnsTheSameTrade`: two `Open` with one id → one trade, wallet charged once.
  - `SetSideReplacingReturnsThePreviousGoods`: Bob `SetSide` (5 plasteel, silver 20) → revision 2, no parcel; Bob `SetSide` with `return_goods`, 3 gold, silver 10 → Bob's mailbox has one `ParcelTradeReturn` with the plasteel, Bob's wallet 90; Bob's side: gold, silver 10; revision 3; Bob confirmed, Nova not.
  - `SetSideAddingKeepsTheEscrow`: Nova `SetSide` without `return_goods` adding 5 steel and silver 40 → side holds 15 steel, silver 40, wallet 60, no return parcel; a note-only change (no goods, same silver) bumps the revision, unconfirms Bob, moves nothing.
  - `ConfirmOfAnOldRevisionIsTradeChanged`: Bob confirms revision 1 after Nova's `SetSide` made it 2 → `TradeChanged`; trade unchanged.
  - `ExchangeAfterBothConfirm`: Nova opens (steel, pawn, 30 silver); Bob `SetSide` (plasteel, 20 silver); Nova confirms the current revision → `Completed`; Nova's mailbox has a `ParcelTrade` from Bob with plasteel and silver 20; Bob's has one from Nova with steel, the pawn and silver 30; delivering both credits the silver once (`deliver`); both streams get the completed `TradeUpdated`; metric `completed` +1.
  - `SecondConfirmAfterTheExchangeReturnsTheCompletedTrade`: Nova's second `Confirm` → returns the completed trade, still exactly two `Trade` parcels.
  - `CancelReturnsBothSides`: Bob cancels an open trade with goods on both sides → each party gets a `ParcelTradeReturn` with its own goods and its silver back; status `Cancelled`, reason names Bob; a second cancel returns it unchanged; `Confirm` afterwards → `TradeGone`.
  - `ExpiredTradesReturnBothSides`: `ExpiresAt` set in the past; `ExpireDue(now)` = 1; status `Expired`; returns as in cancel.
  - `SetSideRepeatAfterTheTradeClosedReturnsTheTradeAndMovesNothing`: Bob's `SetSide` with id X applied, then the exchange completes; the same `SetSide` X again → returns the completed trade, no new parcel, wallet unchanged; a new `SetSide` id Y → `TradeGone`.
  - `ThirdPlayerCannotTouchTheTrade`: Carol `SetSide`/`Confirm`/`Cancel` → `TradeGone`; Carol's `List` is empty.
  - `ListShowsOpenAndRecentlyClosed`: one open, one closed 2 days ago, one closed 8 days ago (dates set in the DB) → list has two.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~Trades" -nologo -v q`. Expected: FAIL to compile.
- [ ] **Step 3: Implement** `TradeService`, `TradeMetrics`, `TradesGrpcService`, rate limits; `SafeTradesSweep` also calls `TradeService.ExpireDue(DateTime.UtcNow)`.
- [ ] **Step 4: Run** the filter, then the full suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `Add two-way trades on server escrow`.

### Task 5: Trades closed by block, reset and admins; admin tools; config docs

**Files:**
- Modify: `src/Rimlink.Server/Application/Social/SocialService.cs` (`Block` cancels), `src/Rimlink.Server/Application/Admin/AdminOperations.cs`, `AdminDirectory.cs`, `AdminCommandRunner.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs`, `src/Rimlink.Tools/Commands/AdminCommand.cs`
- Modify: `deploy/appsettings.example.json`, `docs/server-setup.md`
- Test: `tests/Rimlink.Server.Tests/Trades/TradeAdminTests.cs`, `tests/Rimlink.Server.Tests/Tools/` (existing CLI test file for admin commands)

**Interfaces:**
- Consumes: Task 4 `TradeService.CancelBetween/CancelAllOf/AdminCancel/ListFor`, Task 2 `SocialService.BlockedBy`.
- Produces: `record TradeView(long Id, long InitiatorId, string InitiatorName, long PartnerId, string PartnerName, string Status, int Revision, string InitiatorSide, string PartnerSide, DateTime CreatedAt, DateTime ExpiresAt, DateTime? ClosedAt, string? CloseReason)` (sides as `TransferService.Summary` text); `AdminOperations.ListTrades(Actor actor, long? playerId)` (Moderator), `AdminOperations.CancelTrade(Actor actor, long tradeId, string reason)` → `bool` (Admin, audit `trade-cancel`); `PlayerCard` gains a last positional member `List<string>? Blocks = null` filled by `AdminDirectory.Card` (names the player blocks). REST `GET /api/admin/trades?playerId=` (`ApiScope.Moderate`), `POST /api/admin/trades/{id:long}/cancel` body `{reason?}` (`ApiScope.Admin`, 200 or 404). CLI `rimlink-tools admin trades [playerId]`, `rimlink-tools admin trade cancel <id> [reason]`. Chat `/trades [player]` (moderator), `/trade cancel <id> [reason]` (admin).
- `SocialService.Block` calls `TradeService.CancelBetween(blocker, blocked, "blocked")` after storing the block; `AdminOperations.ResetPlayer` calls `TradeService.CancelAllOf(playerId, "colony reset")` next to `raids.CancelAllOf`.

- [ ] **Step 1: Write the failing tests:**
  - `BlockCancelsOpenTradesAndReturnsBothSides`: open trade with goods and silver on both sides, plus a gift from Bob awaiting Nova's decision; Nova blocks Bob → status `Cancelled`, reason `blocked`, both `ParcelTradeReturn` parcels and both wallets restored; the escrow columns of the row are empty; Bob's gift still awaits and Nova can refuse it.
  - `ResetCancelsTheirTrades`: `ResetPlayer(bob)` cancels Bob's open trades with returns to both.
  - `AdminListsAndCancelsTrades`: REST `GET /api/admin/trades?playerId={nova}` with a moderate token lists the trade with both names; `POST …/cancel` with a moderate token → 403, with an admin token → 200, status `Cancelled`, an `admin_actions` row `trade-cancel`, and the feed probe sees `admin.action` but no other trade event; unknown id → 404.
  - `CardShowsBlocks`: `GET /api/admin/players/{nova}` has `blocks: ["Bob"]`.
  - `ChatCommandsListAndCancelTrades`: an admin's `/trades Nova` answer contains the trade id; `/trade cancel <id>` answers success and cancels.
  - CLI test (style of the existing Tools tests): `admin trades` and `admin trade cancel <id>` call the two routes.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~TradeAdminTests|FullyQualifiedName~Tools" -nologo -v q`. Expected: FAIL.
- [ ] **Step 3: Implement**; document the four config keys with their defaults in `deploy/appsettings.example.json` and `docs/server-setup.md` (config table and the admin API/CLI/chat command lists), plus the two new metrics in the metrics list.
- [ ] **Step 4: Run** the filter, then the full suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `Cancel trades on block, reset and by admins; add the trade admin tools`.

### Task 6: Client core

**Files:**
- Create: `src/Rimlink.Client.Core/Social/TradesClient.cs`, `SocialClient.cs`, `SocialState.cs`
- Modify: `src/Rimlink.Client.Core/Net/RimlinkConnection.cs` (`TradesApi`, `SocialApi`), `src/Rimlink.Client.Core/Economy/EconomyClient.cs` (`Answer`), `EconomyState.cs`, `TransferOutbox.cs`, `src/Rimlink.Client.Core/State/SharedState.cs`
- Test: `tests/Rimlink.Client.Tests/Social/SocialStateTests.cs`, `tests/Rimlink.Client.Tests/Social/TradeOutboxTests.cs`, `tests/Rimlink.Client.Tests/Economy/` (gift state)

**Interfaces:**
- Consumes: protos from Task 1; real server behaviour from Tasks 2–4 (client tests run against a test server).
- Produces (namespace `Rimlink.Client.Social`; calls with the 30 s deadline like `RaidClient`): `TradesClient(RimlinkConnection)`: `Task<Trade> Open(TradeOpenRequest, CancellationToken)`, `Task<Trade> SetSide(TradeSideRequest, CancellationToken)`, `Task<Trade> Confirm(long tradeId, int revision, CancellationToken)`, `Task<Trade> Cancel(long tradeId, CancellationToken)`, `Task<TradeList> List(CancellationToken)`. `SocialClient(RimlinkConnection)`: `Task<BlockList> Block(long playerId, CancellationToken)`, `Unblock(long playerId, …)`, `ListBlocks(…)`, `Task<PlayerFlags> SetAcceptGifts(bool, …)`, `SetTradesClosed(bool, …)`. `EconomyClient.Answer(long parcelId, bool accept, CancellationToken)`. `SocialState`: `IReadOnlyList<Trade> Trades`, `IReadOnlyCollection<PlayerRef> Blocks`, `bool IsBlocked(long playerId)`, `void Load(TradeList trades, BlockList blocks)`, `void SetBlocks(BlockList)`, `void Upsert(Trade)` (replace by id, newest first), `void Apply(ServerEvent)` (`TradeUpdated` → `Upsert` + event `TradeUpdated`; `GiftRefused` → event `GiftRefused`), `void Reset()`; events `Action<Trade>? TradeUpdated`, `Action<GiftRefused>? GiftRefused`. `SharedState.Social`, forwarded in `Apply` and `Reset`. `EconomyState`: `ParcelArrived` with an id already pending replaces that parcel; `GiftRefused` → `MarkDelivered(parcel_id)`. Outbox: `OutboxKind` + `TradeOpen`, `TradeSetSide` (appended); `OutboxClients` + `Social.TradesClient? Trades = null` (last member); `Add`, `Decode`, `CanReplay` (needs `Trades`), `NothingToReturn` (items and pawns both empty), `Attempt` handle both.

- [ ] **Step 1: Write the failing tests:**
  - `SocialStateTests.TradeUpdatedReplacesByIdAndRaises`: `Load` with trade 1 rev 1; `Apply(TradeUpdated rev 2)` → one trade with rev 2, event raised once; another id → two trades, newest first.
  - `SocialStateTests.BlocksAnswerIsBlocked`: `SetBlocks([Bob])` → `IsBlocked(bob) == true`; `Reset` clears trades and blocks.
  - `EconomyStateTests.AcceptedGiftReplacesTheAwaitingOne` and `RefusedGiftLeavesThePending`: `ParcelArrived` awaiting, then the same id not awaiting → one pending, flag cleared; `GiftRefused` with its id → pending empty.
  - `TradeOutboxTests.OpenAnsweredLateIsReplayedOnce` (test server, two players): add a `TradeOpenRequest` to the outbox without calling, `Replay` with `OutboxClients(economy, null, null, null, trades)` → `Completed == 1`, server has one trade; replaying the same request again (re-added) → still one trade.
  - `TradeOutboxTests.ReplayedSetSideAfterTheExchangeCompletesTheEntry`: Bob's `SetSide` X sent directly (answer "lost": entry kept in the outbox), Nova confirms → completed; `Replay` → the entry counts as `Completed`, not `Refused`, and is removed.
  - `TradeOutboxTests.SetSideOnACancelledTradeIsRefused`: a new `SetSide` for a cancelled trade in the outbox → `Refused == 1`, entry keeps its refusal reason (the game returns its goods).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo -v q`. Expected: FAIL to compile.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the client tests, then the full suite. Expected: PASS, green.
- [ ] **Step 5: Commit** `Add trades, blocks and gift decisions to the client core`.

### Task 7: Game UI

No automated tests: the game code is verified by the s4 run in Task 8. Before writing the letter, decompile `ChoiceLetter` and `LetterDef` (`ilspycmd -t RimWorld.ChoiceLetter <game>\RimWorldWin64_Data\Managed\Assembly-CSharp.dll` from PowerShell).

**Files:**
- Create: `src/Rimlink.Client/Game/Economy/GiftLetters.cs` (`ChoiceLetter_RimlinkGift : ChoiceLetter`, saved `parcelId`, choices Accept/Refuse/Postpone; the refused-gift letter), `mod/Defs/LetterDefs/Rimlink_Letters.xml` (`Rimlink_GiftOffer` with `letterClass` = that type), `src/Rimlink.Client/UI/Economy/TradesTab.cs` (list + card; "new trade" picks a partner among the owners of settlements in `SharedState.Objects` and the online players, minus me and blocked players, so an offline partner can be chosen), `src/Rimlink.Client/UI/Economy/Dialog_TradeSide.cs` (picker from beacons via `ThingPicker`, silver field, note, "return what is already offered" checkbox)
- Modify: `src/Rimlink.Client/Game/Economy/Incoming.cs` (skip `AwaitingDecision`), `Outgoing.cs` (`OpenTrade`, `SetTradeSide` through `TakeFromMap`/`Run`; `Contents` handles `TradeOpenRequest`/`TradeSideRequest`), `src/Rimlink.Client/UI/Economy/Dialog_Economy.cs` (`Tab.Trades` → `TradesTab.Draw`; mailbox rows of awaiting gifts get Accept/Refuse; checkboxes "Accept gifts at once", "Not accepting trades" next to peaceful/visits), `src/Rimlink.Client/UI/Dialog_Players.cs` (Trade, Block/Unblock, blocked marked; blocked players who are offline listed below the online ones so they can be unblocked), `src/Rimlink.Client/UI/Dialog_Chat.cs` (no DM to a blocked player), the Send tab of `Dialog_Economy` (no gift to a blocked player), `src/Rimlink.Client/UI/LoginFlow.cs` (load `SocialState` with `List` and `ListBlocks`; `OutboxClients` gets `TradesClient`), `src/Rimlink.Client/Game/RimlinkSession.cs` (`Trades`, `Social` clients), `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (on `ParcelArrived` awaiting → gift letter; on `GiftRefused` for my gift → letter; on `TradeUpdated` → message)
- Modify: `mod/Languages/English/Keyed/*.xml`, `mod/Languages/Russian/Keyed/*.xml`

**Interfaces:**
- Consumes: Task 6 clients and state; `Outgoing.TakeFromMap/Run/Restore/ReturnRefused`, `ThingPicker`.
- Produces: `Outgoing.OpenTrade(Map map, long partnerId, string partnerName, IReadOnlyList<Pick> picks, IReadOnlyList<Pawn> pawns, long silver, string note)`, `Outgoing.SetTradeSide(Map map, Trade trade, IReadOnlyList<Pick> picks, IReadOnlyList<Pawn> pawns, long silver, string note, bool returnGoods)` (a call without goods skips the save and the tick like a wallet-only send); refusals return the goods through the existing outbox refusal path. Keys under `Rimlink.Gifts.*`, `Rimlink.Trades.*`, `Rimlink.Social.*`.

- [ ] **Step 1:** Write `GiftLetters`, the def, mailbox buttons and `Incoming` skip; build the mod. Expected: `Build-ClientMod.ps1` succeeds, no warnings from the new files.
- [ ] **Step 2:** Players window, chat guard, settings checkboxes, login loading; build. Expected: success.
- [ ] **Step 3:** `TradesTab`, `Dialog_TradeSide`, `Outgoing.OpenTrade/SetTradeSide`, `Contents` cases; build. Expected: success.
- [ ] **Step 4:** Keyed strings in both languages; run the full suite (client core unchanged) and build. Expected: green, success.
- [ ] **Step 5: Commit** `Add gift letters, the trades tab and block buttons to the game`.

### Task 8: BridgeTools, s4 stand, two-player run, documentation

**Files:**
- Create: `tools/Rimlink.BridgeTools/SocialTools.cs`
- Modify: `tools/Rimlink.BridgeTools/StatusTools.cs` (status gets `gifts` awaiting, `trades`, `blocks`, `flags.acceptGifts/tradesClosed`), `AGENTS.md` (tool table rows), `docs/player-guide.md`, `docs/manual-checklist.md`, `README.md` (status line)

**Interfaces:**
- Consumes: Task 7 `Outgoing.OpenTrade/SetTradeSide`, Task 6 clients.
- Produces tools (shape of `EconomyTools`, results `{success, error?, ...}`): `rimlink/gift_answer {parcelId, accept}`, `rimlink/block {player, unblock=false}`, `rimlink/trades_flags {acceptGifts?, tradesClosed?}`, `rimlink/trade_open {player, items, pawns, silver, note, anywhere}`, `rimlink/trade_side {tradeId, items, pawns, silver, note, returnGoods, anywhere}`, `rimlink/trade_confirm {tradeId}` (confirms the revision in `SocialState`), `rimlink/trade_cancel {tradeId}`.

- [ ] **Step 1:** Write the tools and status fields; build with `.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld' -NoInstall`. Expected: success.
- [ ] **Step 2: Stand s4** (recipe of the `rimlink-bt` stand): robocopy the Steam RimWorld folder to `D:\Build\rimlink-s4\RimWorld` (keeps `steam_appid.txt`), save folders `D:\Build\rimlink-s4\{host,guest}` with `Config` copied from `D:\Build\rimlink-gabs\*`, GABS entries `rimlink-s4-host` and `rimlink-s4-guest` in `~/.gabs/config.json` (back it up first; edit with a `jq -f` filter file from PowerShell), server `Rimlink__Server__Port=25610`, `Rimlink__Server__DataDir=D:\Build\rimlink-s4\server`, `Rimlink__Auth__AllowPassword=true`, `Rimlink__Auth__AdminLogins__0=s4-admin`, `Rimlink__Admin__ApiToken=s4test`. Deploy with the games stopped: `Build-ClientMod.ps1 -RimWorldDir D:\Build\rimlink-s4\RimWorld -InstallTo D:\Build\rimlink-s4\RimWorld\Mods` and `Build-BridgeTools.ps1 -RimWorldDir D:\Build\rimlink-s4\RimWorld`. Expected: both games start, `rimlink/status` shows `clientAssembly` under `D:\Build\rimlink-s4\RimWorld\Mods\Rimlink`.
- [ ] **Step 3: Run** host `s4-admin` and guest `s4-guest`, each `rimlink/connect` + `rimlink/start_colony`: (a) guest sends steel → host gets the letter, `gift_answer accept` → pod lands; (b) guest sends steel + a pawn + 50 silver → host refuses → guest gets the refusal letter, the return pod with steel and pawn, wallet back; (c) host blocks guest → guest's DM, gift and `trade_open` are refused with nothing leaving the guest's map, guest's public line absent from the host's `rimlink/chat`; unblock; (d) guest opens a trade with steel, host `trade_side` with a pawn and silver, guest `trade_side` adding steel, both confirm → each receives the other's side, the pawn joins the guest's colony; (e) a second trade with goods on both sides cancelled by the host → both get their returns. Expected: every step as described, verified through `rimlink/status`, `[Rimlink]` log lines and the server DB (`sqlite3 D:\Build\rimlink-s4\server\rimlink.db`).
- [ ] **Step 4:** Write the results into `docs/manual-checklist.md` (new section «Подарки, блок-лист, сделки» with the five flows), the player guide sections for gifts, blocks and trades, the AGENTS.md tool rows; run the full suite and build the mod. Expected: green, success.
- [ ] **Step 5: Commit** `Add the trade and block bridge tools, run them on two players, document the flows`.
