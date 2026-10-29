# Rimlink Discord Bot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Discord bot, deployed next to the Rimlink server, that posts server events into Discord channels, bridges the public chat both ways, runs admin commands for Discord roles and answers public queries, talking to the server only through `/api/admin/*`.

**Architecture:** The server gains an admin event feed (`AdminFeed`: in-memory ring buffer, SSE at `GET /api/admin/events`, resume by `Last-Event-ID`), explicit `Publish` calls at every event source, `POST /chat` with a new `EXTERNAL` message kind, a forward cursor on the admin panel's `GET /chat`, and lot summaries. Player search, the player card, `GET /status`, the request actor with `X-Rimlink-Actor` and `admin_actions.ActorName` come from the admin panel plan and are used as given. A new worker `src/Rimlink.Discord` (Discord.Net) reads the feed, routes events to channels through a batching queue, bridges chat, and serves slash commands through plain handler services that are tested against the real in-process server; the Discord.Net modules are a thin shell.

**Tech Stack:** .NET 10 (SDK 10.0.402), ASP.NET Core minimal APIs (`TypedResults.ServerSentEvents`), EF Core Sqlite, `System.Net.ServerSentEvents` (in-box `SseParser`), Discord.Net 3.20.2, Microsoft.Extensions.Hosting/Http 10.0.14, Microsoft.Extensions.TimeProvider.Testing 10.11.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-discord-bot-design.md`

## Global Constraints

- Protocol only grows: `chat.proto` gets `EXTERNAL = 2` in `MessageKind` and `string external_author = 7` in `ChatMessageEvent`; `ProtocolVersion.Current` does not change.
- The feed never carries direct messages, wallets, parcels, `raid.queued`, or `/`-command answers.
- `src/Rimlink.Discord` references neither `Rimlink.Server` nor `Rimlink.Protocol`; its tests may reference `Rimlink.Server.Tests` (for `ServerFixture`) like `Rimlink.Client.Tests` does.
- Feed event JSON uses `JsonSerializerDefaults.Web` (camelCase); a player inside event data is `{ "id": long, "name": string }`.
- Event id: `"<epoch>:<seq>"`, `epoch` = Unix seconds at `AdminFeed` construction, `seq` from 1; seq 1 is always `server.started`.
- `Admin:FeedBufferSize` default 1000, valid 100–100000; `Admin:FeedPingSeconds` default 15, valid 1–300 (validated with `ValidateOnStart`).
- Routes owned by the admin panel spec (`docs/superpowers/specs/2026-10-29-rimlink-admin-panel-design.md` §3–4, plan `docs/superpowers/plans/2026-10-29-rimlink-admin-panel.md` Tasks 1, 2, 4, 6 on branch `asofwar/gracious-jepsen-5f80d9`) are used, never redefined: `ctx.Actor()` / `AdminAuth.ActorHeader` (`X-Rimlink-Actor`, sanitised, actor name `api:<label>`), `admin_actions.ActorName`, `GET /players?q=&limit=&offset=` → `{total, items}`, `GET /players/{id}`, `GET /status` (with `certificateFingerprint`), `GET /chat?playerId=&before=&limit=` (`AdminDirectory.Chat`, `ChatLine`), `ServerVersion.Current`. The bot's label is `discord:<username> (<userId>)`.
- Chat from Discord: author 1–32 characters without control characters; text follows the game chat rules (`Trim`, non-empty, ≤ `Limits.ChatMaxLength` = 500, no control characters except `\n`); never parsed as a `/` command.
- Bot texts exist in `en` and `ru` (`RimlinkBot:Discord:Language`, default `en`); command names English; every message the bot sends uses `AllowedMentions.None`.
- Bot config root section `RimlinkBot` (env `RimlinkBot__Discord__Token`, …); REST timeout 10 s; feed reconnect backoff 1, 2, 4, 8, 16, 30 s; feed idle timeout 45 s; outbound flush every 1 s, ≤ 2000 characters per message, ≤ 500 queued lines per channel; "server down" after 60 s without the feed; confirmation buttons live 60 s.
- Mod: `Dialog_Chat` renders `External` lines with the keyed prefix `Rimlink.Chat.External` (`[Discord] {0}` in English and Russian); `mod/About/About.xml` `<modVersion>` and `ClientInfo.Version` go from `0.4.0` to `0.5.0` together (or the next minor if `main` already moved on at merge time).
- Package versions go into `Directory.Packages.props` only.
- The shared game setup is reserved by session `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`: build the mod without `-InstallTo`; in-game checks are sent to that session.
- Auth: the bot uses its own scoped token from `Admin:Tokens` (`{name, token, scopes}`, scopes `read`, `chat`, `moderate`, `admin`; mechanism in the panel's amended Task 2), never `Admin:ApiToken`; audit actor `api:<tokenName>:<X-Rimlink-Actor>`, the token named `discord-bot` in tests and docs. This plan's routes require: `GET /events` → `read`, `POST /chat` → `chat`, through the panel's scope requirement API.
- `dotnet test Rimlink.sln -nologo -v q` green and the mod building before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no trailers. Branch `asofwar/kind-shamir-62e491` (spec and plan on `main` 3754d44; Task 1 brings in the admin panel routes).

## Review Focus

1. An event published while a subscriber connects is neither lost nor delivered twice (the backlog snapshot and the subscription are taken under the publish lock) — Task 3 `ConcurrentPublishDuringSubscribeLosesNothing`.
2. The server restarts while the bot runs: the bot's last id belongs to the old epoch, so it gets one `gap` and the new buffer from `server.started`, no old event again, and the channel shows at most one "down" and one "back" line — Task 6 `ForeignIdYieldsGap`, Task 7 `ServerStartedReplacesUpLine`.
3. Chat loops and pings: a line the bot posted comes back as `external` and is not re-posted; a game line containing `@everyone`, `@here` or `<@123>` does not ping anyone — Task 8 `ExternalLinesAreNotEchoed`, Task 7 `MentionsAreNeutralised`.
4. A caller without the level is refused before any HTTP request leaves the bot — Task 9 `RefusedCommandsMakeNoRequest`.
5. Confirmation buttons: pressed by someone else (refused, stays pending), pressed twice (runs once), pressed after 60 s (expired) — Task 9 `ConfirmationRunsOnceForItsOwner`, `ConfirmationExpires`.

---

### Task 1: Bring in the admin panel routes and pin the contract the bot reads

**Files:**
- Merge: the admin panel's Tasks 1, 2, 4 and 6 (`main` once they are merged there, else `git merge` of `refs/heads/asofwar/gracious-jepsen-5f80d9` up to the commit of its Task 6 — only with the user's yes, recorded as a ledger ruling)
- Test: `tests/Rimlink.Server.Tests/Admin/BotContractTests.cs`

**Interfaces:**
- Consumes: the panel routes listed in Global Constraints.
- Produces: nothing new on the server; the test pins every field the bot deserializes, so a later panel change that breaks the bot fails here:
  - `GET /players?q=` → `total`, `items[].id`, `.name`, `.factionName`, `.online`, `.peaceful`, `.role`;
  - `GET /players/{id}` → `name`, `factionName`, `online`, `peaceful`, `settlements[].name`;
  - `GET /status` → `version`, `playersOnline`, `maxPlayers`, `worldExists`, `economy.frozen`, `economy.reason`, `raids.frozen`, `raids.reason`, `certificateFingerprint`;
  - `GET /actions` → `actorName`; `GET /chat` → `id`, `fromName`, `kind`, `text`.

- [ ] **Step 1: Check** `git log --oneline main` and the panel branch: which of its Tasks 1, 2, 4, 6 are merged. Missing ones → ask the user whether to merge the panel branch here; stop until answered.
- [ ] **Step 2: Write the failing test** `BotContractTests.FieldsTheBotReadsExist` (players `Nova` online with a settlement as in `World/WorldTests.cs`, `Bob` offline; one REST kick with the scoped token `discord-bot` and `X-Rimlink-Actor: discord:Mod (1)`; one public chat line) asserting each field above, `actorName == "api:discord-bot:discord:Mod (1)"`, and that `GET /players?q=no` contains `Nova`.
- [ ] **Step 3: Run** `dotnet test tests/Rimlink.Server.Tests --filter BotContractTests -nologo -v q` — Expected: FAIL before the merge (404 / missing fields), PASS after it.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Pin the admin API fields the Discord bot reads"`

### Task 2: Lot summaries

**Files:**
- Create: `src/Rimlink.Server/Application/Economy/LotSummary.cs`
- Modify: `src/Rimlink.Server/Application/Admin/AdminOperations.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs`
- Test: `tests/Rimlink.Server.Tests/Economy/LotSummaryTests.cs`, `tests/Rimlink.Server.Tests/Economy/EconomyAdminTests.cs`

**Interfaces:**
- Produces:
  - `public static class LotSummary { public static string Of(ItemList? items, PawnBlob? pawn); }` — items in order as `label ×count` (`label` falls back to `def_name`; `×count` omitted when count is 1) joined by `", "`, or the pawn's `label` (else `name`); longer than 200 characters → first 199 + `…`.
  - `public sealed record LotView(long Id, long SellerPlayerId, string SellerName, long Price, DateTime CreatedAt, DateTime ExpiresAt, string Status, string Summary)`; `AdminOperations.ListLotViews(Actor actor, long? playerId) : Task<List<LotView>>` (Moderator, like `ListLots`).
  - `GET /api/admin/economy/lots` returns `LotView` (old fields kept, `sellerName` and `summary` added).

- [ ] **Step 1: Write the failing tests**:
  - `LotSummaryTests`: `ItemsAreLabelledWithCounts` (`Steel`×50 + `Component`×3 → `"Steel ×50, Component ×3"`), `SingleItemHasNoCount`, `EmptyLabelFallsBackToDefName`, `PawnLotUsesThePawnLabel`, `LongSummaryIsCut` (30 items → length 200, ends with `…`).
  - `EconomyAdminTests.LotsCarrySellerNameAndSummary` (a lot posted as in `Economy/MarketTests.cs`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "LotSummaryTests|EconomyAdminTests" -nologo -v q` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** as in Interfaces.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Show what a lot holds and who sells it in the admin lot list"`

### Task 3: Admin feed and the SSE endpoint

**Files:**
- Create: `src/Rimlink.Server/Application/Feed/AdminFeed.cs`, `src/Rimlink.Server/Application/Feed/FeedEvent.cs`, `src/Rimlink.Server/Application/Feed/FeedData.cs`, `src/Rimlink.Server/Rest/AdminFeedEndpoint.cs`
- Modify: `src/Rimlink.Server/Hosting/ServerOptions.cs` (`AdminOptions`), `src/Rimlink.Server/Program.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs` (map `/events` in the token group)
- Test: `tests/Rimlink.Server.Tests/Feed/AdminFeedTests.cs`, `tests/Rimlink.Server.Tests/Feed/AdminFeedRestTests.cs`, `tests/Rimlink.Server.Tests/Support/FeedProbe.cs`

**Interfaces:**
- Produces:
  - `public sealed record FeedEvent(string Id, long Seq, DateTime At, string Type, string DataJson)`.
  - `FeedData.cs`: `public static class FeedTypes` with the string constants `ServerStarted = "server.started"`, `PlayerJoined`, `PlayerLeft`, `ColonyFounded`, `ColonyRemoved`, `Notice`, `ServerShutdown`, `EconomyFrozen`, `RaidsFrozen`, `RaidLanded`, `RaidResolved`, `VisitStarted`, `LiveStarted`, `LiveEnded`, `LotPosted`, `LotSold`, `ShopSold`, `AdminAction` (`"admin.action"`), `ChatMessage` (`"chat.message"`), and `IReadOnlySet<string> All`; data records `FeedPlayer(long Id, string Name)`, `ServerStartedData(string Version)`, `PlayerData(FeedPlayer Player, string Faction)`, `ColonyData(FeedPlayer Player, string Colony, int? Tile)`, `NoticeData(string Text)`, `ShutdownData(int InSeconds, string Reason)`, `FreezeData(bool Frozen, string? Reason)`, `RaidLandedData(long RaidId, FeedPlayer Attacker, FeedPlayer Target)`, `RaidResolvedData(long RaidId, FeedPlayer Attacker, FeedPlayer Target, int Dead, int Captured, int Fled, int Alive, long LootValue, int Captives)`, `VisitData(FeedPlayer Visitor, FeedPlayer Host)`, `LiveData(long SessionId, string Kind, FeedPlayer Host, FeedPlayer Guest, string? Reason)`, `LotData(long LotId, FeedPlayer Seller, FeedPlayer? Buyer, long Price, string Summary)`, `ShopSoldData(FeedPlayer Host, FeedPlayer Buyer, string Item, int Count, long Price)`, `AdminActionData(string Action, string Actor, FeedPlayer? Target, string Details)`, `ChatData(long Id, string Kind, FeedPlayer? From, string? Author, string Text)`.
  - `public sealed class AdminFeed` (singleton): ctor `(IOptions<AdminOptions> options, TimeProvider time)` publishes `server.started` with `ServerVersion.Current` (panel Task 1); `long Epoch`; `string LastId`; `int SubscriberCount`; `FeedEvent Publish(string type, object data)` (never throws, never blocks: logs and returns on a serialization failure); `FeedSubscription Subscribe(string? after)`.
  - `public sealed class FeedSubscription : IDisposable { bool Gap; IReadOnlyList<FeedEvent> Backlog; ChannelReader<FeedEvent> Live; }` — `after` null → no backlog, no gap; malformed, foreign epoch, or `seq` older than the oldest buffered − 1 → `Gap = true`, whole buffer; else the buffered events with `Seq > seq`. Backlog snapshot and registration happen under the same lock as `Publish`. A subscriber whose bounded channel (capacity `FeedBufferSize`) is full is completed and removed.
  - `AdminOptions.FeedBufferSize` (1000), `AdminOptions.FeedPingSeconds` (15), bound with `AddOptions<AdminOptions>().Bind(...).Validate(...).ValidateOnStart()`; gauge `rimlink_admin_feed_subscribers`.
  - `GET /api/admin/events`: point from `Last-Event-ID`, else `?after=`; returns `TypedResults.ServerSentEvents` of `SseItem<string>`: first `("{\"from\":\"<after>\"}", "gap")` when `Gap`, then each backlog event as `new SseItem<string>(e.DataJson, e.Type) { EventId = e.Id }`, then one `new SseItem<string>("", "ping")` (no id) at once — so the response headers and the client's "connected" state do not wait for the first live event — then live events, and a `ping` after every `FeedPingSeconds` without an event; the enumeration ends when the channel completes or the request aborts. Ruling: ping is an `event: ping` item, not a `: ping` comment — `SseItem` cannot write comments; the bot ignores both. Ruling: `Admin:FeedPingSeconds` is a key the spec does not list; it exists so tests need not wait 15 s and admins behind an aggressive proxy can shorten it (documented in Task 11).
  - Test helper `FeedProbe`: `fixture.Probe()` subscribes at the current `LastId`; `Task<FeedEvent> Next(string type, TimeSpan? timeout = null)` (default 10 s, skips other types); `Task<List<FeedEvent>> Drain(TimeSpan quiet)`; `T Data<T>(FeedEvent e)` (Web defaults).

- [ ] **Step 1: Write the failing tests**:
  - `AdminFeedTests` (construct `AdminFeed` directly with `Options.Create(new AdminOptions { FeedBufferSize = n })`, bypassing validation for small buffers): `FirstEventIsServerStarted` (`Subscribe($"{Epoch}:0").Backlog[0]` is `server.started`, `Seq` 1), `NoAfterMeansOnlyNewEvents`, `AfterResumesWithoutLossOrDuplicates` (publish 5; after seq 3 → backlog 4..6; publish → live 7), `ForeignEpochIsAGap`, `MalformedAfterIsAGap`, `EvictedSeqIsAGap` (buffer 3, seqs 1..10: after `E:2` → gap, backlog 8..10; after `E:7` → no gap, backlog 8..10), `SlowSubscriberIsCompleted` (buffer 3, never read, publish 4 → `Live.Completion` completes, `SubscriberCount` 0), `ConcurrentPublishDuringSubscribeLosesNothing` (buffer 5000; a task publishes 2000 events; meanwhile subscribe at the current `LastId` and read backlog + live up to the last seq: the seqs are contiguous with no repeats).
  - `AdminFeedRestTests`: `EventsNeedTheToken` (401), `EventsNeedReadScope` (a token with only `chat` → 403), `StreamDeliversEventsWithIds` (open with `HttpCompletionOption.ResponseHeadersRead`, parse with `SseParser.Create(stream)`, publish `"test"` through `fixture.Services.GetRequiredService<AdminFeed>()` → item type `test`, `EventId` matches `^\d+:\d+$`), `LastEventIdResumes` (two events, reconnect with `Last-Event-ID` of the first → the second only), `GapComesFirstForAForeignId` (`?after=1:1`), `PingArrives` (`Admin:FeedPingSeconds=1` → a `ping` item within 5 s), `BufferSizeOutOfRangeStopsTheServer` (`Admin:FeedBufferSize=10` → creating the client throws `OptionsValidationException`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "AdminFeed" -nologo -v q` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** `FeedEvent`, `FeedData`, `AdminFeed`, the endpoint, options and registration (`AddSingleton<AdminFeed>`, `AddSingleton(TimeProvider.System)` if not registered, resolve `AdminFeed` right after `Build()` as with `PresenceNotifier` so `server.started` is seq 1), and `FeedProbe`.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Add the admin event feed with a resumable SSE endpoint"`

### Task 4: Publish events at their sources

**Files:**
- Modify: `src/Rimlink.Server/Application/Sessions/PresenceNotifier.cs`, `Application/World/WorldService.cs` (`Settle`, `Abandon`), `Application/Admin/AdminOperations.cs` (`Broadcast`, `Shutdown`, `SetEconomyFrozen`, `SetRaidsFrozen`, `RemoveObjectsOf`, `Audit`), `Application/Raids/RaidService.cs` (`Land`, `Report`), `Application/Visits/VisitService.cs` (`Begin`), `Application/Live/LiveSessionService.cs` (`Start`, `Close`), `Application/Economy/MarketService.cs` (`Post`, `Buy`), `Application/Visits/ShopService.cs` (`Buy`) — all under `src/Rimlink.Server/`
- Test: `tests/Rimlink.Server.Tests/Feed/FeedSourcesTests.cs`

**Interfaces:**
- Consumes: `AdminFeed.Publish`, `FeedTypes`, the data records (Task 3); `LotSummary.Of` (Task 2); the actor names of the panel's Task 2.
- Produces: one `Publish` per source, after the transaction commits, with these values:
  - presence: `PlayerData(player, faction)`; `colony.founded`: `ColonyData(owner, name, tile)` for `Settlement` objects; `colony.removed`: `ColonyData(owner, name, null)` from `Abandon` and `RemoveObjectsOf`.
  - `raid.landed` from `Land`; `raid.resolved` from `Report` with the `RaidSummary` fields; `visit.started` from `Begin`; `live.started` from `Start`; `live.ended` exactly once per started session: from `MarkEnding` (the moment both games learn the end), and from `Close` only when the session was not `Ending` before (`announced == false`). Sessions that never started (`Fallback` from `Proposed`, a guest cancelling a proposal) publish nothing: they had no `live.started`. `Kind` `"raid"`/`"visit"`, `Reason` from `LiveEndReason`: `normal`, `guest-lost`, `host-lost`, `host-aborted`, `guest-cancelled`, `admin`.
  - `lot.posted` from `Post` (`Buyer` null) and `lot.sold` from `Buy`, `Summary` = `LotSummary.Of`; `shop.sold` from `ShopService.Buy` with the shop item's label, the bought count and price.
  - `admin.action` from `Audit`: `Actor` = `actor.Name` (the value `Audit` writes to `ActorName`: `api`, `api:discord:<…>`, `panel`, or the player's name), `Target` = the player of `targetAccountId` if any, `Details` as audited.
  - Nothing for `Launch` (`raid.queued`), transfers, parcels, wallets.

- [ ] **Step 1: Write the failing tests** in `FeedSourcesTests`, each with `fixture.Probe()` and the flows of the existing tests named in brackets: `JoinAndLeaveArePublished` (login; REST kick → `player.left`), `SettleAndAbandonArePublished` [`World/WorldTests.cs`], `BroadcastShutdownAndFreezesArePublished` (REST broadcast, `shutdown` with 600 s as in `Admin/AdminOperationsTests.cs`, economy and raids freeze/unfreeze), `RaidLandedAndResolvedArePublished` [`Raids/RaidReportTests.cs`: names of attacker and target, summary counts], `RaidQueuedIsNotPublished` (after `Launch`, `Drain(500 ms)` has no `raid.*`), `VisitAndLiveArePublished` [`Visits/VisitTests.cs`, `Live/LiveEndTests.cs`: one `live.started`, then exactly one `live.ended` with reason `normal` across `MarkEnding` and `Close`; an admin `live/{id}/end` → one `live.ended` with reason `admin`; a declined proposal → no `live.*`], `LotPostedAndSoldArePublished` [`Economy/MarketTests.cs`: seller, buyer, price, summary], `ShopSaleIsPublished` [`Visits/ShopTests.cs`], `AdminActionCarriesTheActorName` (REST kick with `X-Rimlink-Actor: discord:Mod (1)` → `actor` `api:discord:Mod (1)`; without the header → `api`; an admin player's chat `/kick` → the player's name), `PrivateEventsAreNotPublished` (a gift transfer and a parcel delivery → `Drain(500 ms)` is empty; every type seen across this class is in `FeedTypes.All`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FeedSourcesTests -nologo -v q` — Expected: FAIL (no events).
- [ ] **Step 3: Implement** the publish calls (inject `AdminFeed`; load names already in scope or with one `AsNoTracking` query).
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Publish server, raid, market and audit events to the admin feed"`

### Task 5: Chat over REST, Discord lines in the game

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/chat.proto`, `src/Rimlink.Server/Storage/Entities/Entities.cs` (`MessageKind`, `ChatMessage`), `src/Rimlink.Server/Application/Chat/ChatService.cs`, `src/Rimlink.Server/Application/Admin/AdminDirectory.cs` (panel's `ChatLine`, `Chat`), `src/Rimlink.Server/Rest/AdminEndpoints.cs`, `src/Rimlink.Client/UI/Dialog_Chat.cs`, `src/Rimlink.Tools/Commands/AdminCommand.cs`, `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`, `mod/About/About.xml`, `src/Rimlink.Client.Core/ClientInfo.cs`
- Create: migration `ChatExternalAuthor` (`dotnet ef migrations add ChatExternalAuthor --project src/Rimlink.Server`)
- Test: `tests/Rimlink.Server.Tests/Chat/ChatRestTests.cs`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`

**Interfaces:**
- Consumes: `AdminFeed`, `ChatData`, `FeedPlayer` (Task 3); the panel's `AdminDirectory.Chat`, `ChatLine` and `GET /chat` (Task 1).
- Produces:
  - `MessageKind { Player = 0, System = 1, External = 2 }`; `ChatMessage.ExternalAuthor : string?` (column `chat_messages.ExternalAuthor`, TEXT NULL).
  - `ChatService.External(string author, string text) : Task<long>` (stores, broadcasts as `EXTERNAL` with `external_author`, returns the id); the panel's `ChatLine` gains `string? Author = null` (last positional parameter) and `Kind` value `external`; `AdminDirectory.Chat(long? playerId, long? before, int limit, long? after = null)` — with `after`: lines with `Id > after`, ascending; `before` and `after` together → `InvalidArgument`; a shared `static string CleanText(string text)` used by `Send` and `External`; `Persist(ChatMessage, FeedPlayer? from)` publishes `chat.message` for public lines (`Kind` `player`/`system`/`external`). `ToEvent` fills `ExternalAuthor`; `History` returns it.
  - REST: the panel's `GET /api/admin/chat` takes `after=` and returns `author`; `POST /api/admin/chat {author, text}` → `{id}`; validation failures are `InvalidArgument` → 400 through the panel's filter.
  - `Dialog_Chat.Format`: `External` → `[time] <b>{"Rimlink.Chat.External".Translate(Escape(author))}</b>: {body}`.
  - CLI mirror (`AdminCommand.cs`): the panel's `chat` gains an `--after <id>` option; new `say <author> <text…>` → `POST chat`; both in the `command` description.

- [ ] **Step 1: Write the failing tests**:
  - `ChatRestTests`: `PostedLineReachesSessionsAsExternal` (a subscribed player gets `Kind == External`, `ExternalAuthor == "Mod"`, `From == null`), `PostedLineIsInHistoryWithAuthor` (gRPC `History`), `PostValidatesAuthorAndText` (author `""`, 33 characters, `"a\u0001"`; text `""`, `"   "`, 501 characters, `"a\u0007"` → 400 each), `PostChatNeedsChatScope` (a token with only `read` → 403), `SlashLineIsNotACommand` (`/shutdown 1` → stored as text, `GET /actions` unchanged), `AfterCursorReadsForward` (three public lines and a DM: `?after=<first id>` → the second and third in ascending order, no DM; `author` set on an external line; `?after=1&before=9` → 400), `ChatLinesArePublishedToTheFeed` (player line → `kind` `player` with `from`; posted line → `external` with `author`; DM → nothing).
  - `SchemaTests.ChatMessagesHaveExternalAuthor` (`PRAGMA table_info(chat_messages)` lists `ExternalAuthor`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "ChatRestTests|SchemaTests" -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** the proto fields, entity, migration, service methods, endpoints, chat feed publishing, the mod rendering, the keyed string in both languages and the version bump.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green. Run `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'` (PowerShell, no `-InstallTo`) — Expected: build succeeds.
- [ ] **Step 5: Commit** → `git commit -m "Accept chat lines from outside the game and show them as Discord lines"`

### Task 6: Bot project, configuration, REST client and feed reader

**Files:**
- Create: `src/Rimlink.Discord/Rimlink.Discord.csproj` (`Microsoft.NET.Sdk.Worker`, `AssemblyName` `rimlink-discord`), `src/Rimlink.Discord/appsettings.json`, `Options/BotOptions.cs`, `Api/Dtos.cs`, `Api/RimlinkApi.cs`, `Api/RimlinkApiException.cs`, `Api/ServerHandler.cs`, `Api/FeedReader.cs` (all under `src/Rimlink.Discord/`); `tests/Rimlink.Discord.Tests/Rimlink.Discord.Tests.csproj`, `tests/Rimlink.Discord.Tests/Support/BotFixture.cs`
- Modify: `Directory.Packages.props` (`Discord.Net` 3.20.2, `Microsoft.Extensions.Hosting` 10.0.14, `Microsoft.Extensions.Http` 10.0.14, `Microsoft.Extensions.TimeProvider.Testing` 10.11.0), `Rimlink.sln` (`dotnet sln add … --solution-folder src|tests`)
- Test: `tests/Rimlink.Discord.Tests/Api/BotOptionsTests.cs`, `Api/RimlinkApiTests.cs`, `Api/ServerHandlerTests.cs`, `Api/FeedReaderTests.cs`

**Interfaces:**
- Produces:
  - `BotOptions` (`Section = "RimlinkBot"`) with `ServerSection Server { Url = "https://rimlink:25560", ApiToken = "", CertificateSha256, Insecure }` and `DiscordSection Discord { Token = "", GuildId, Language = "en", ulong[] ModeratorRoleIds, ulong[] AdminRoleIds, ChannelsSection Channels { ulong Server, Raids, Market, Moderation, Chat } }`; `static List<string> Validate(BotOptions o)` — errors for empty `ApiToken`, empty `Token`, `Url` not absolute http(s), `Language` not `en`/`ru`, `CertificateSha256` set but not 64 hex characters.
  - `Dtos.cs`: `PlayerRef(long Id, string Name)`, `FeedEvent(string? Id, string Type, JsonElement Data)`, `OnlinePlayer`, `PlayerInfo` (list item), `PlayerCard`, `ServerStatusInfo` (only the fields `BotContractTests` pins), `LotInfo`, `BanInfo`, `ActionInfo`, `RaidInfo`, `RaidStatsInfo`, `LiveInfo`, `EconomyStatsInfo`, `SaveInfo`, `ChatPosted(long Id)` — properties named as the server's JSON (Web defaults).
  - `RimlinkApiException(int Status, string Detail)`; status 0 = no answer (timeout, connection error).
  - `public sealed class RimlinkApi(HttpClient http)`, every method takes `CancellationToken ct = default`; reads: `Online()`, `Status()` (panel shape), `Players(string q, int limit = 25)` (`items` of the panel's list), `Player(long id)` (the panel's card), `Lots(long? sellerId)`, `Bans()`, `Actions(int limit)`, `Raids(long? playerId)`, `RaidStats()`, `Live(long? playerId)`, `EconomyStats()`, `Saves(long playerId)`; writes take `string actor` first (sent as `X-Rimlink-Actor`): `Kick(actor, playerId, reason)`, `Mute(actor, playerId, minutes)`, `Unmute(actor, playerId)`, `Broadcast(actor, text)`, `Ban(actor, playerId, double? days, reason)`, `Pardon(actor, banId)`, `Wallet(actor, playerId, delta, reason)`, `RefundLot(actor, id)`, `RefundParcel(actor, id)`, `FreezeEconomy(actor, bool frozen, reason)`, `CancelRaid(actor, id, reason, bool returnPawns)`, `FreezeRaids(actor, bool frozen, reason)`, `EndLive(actor, id, reason)`, `Peaceful(actor, playerId, bool on)`, `Shutdown(actor, seconds, reason)`, `Rollback(actor, playerId, version)`, `Reset(actor, playerId)`, `RecreateWorld(actor)`; `PostChat(string author, string text) : Task<ChatPosted>`. Non-success → `RimlinkApiException(status, problem detail or reason phrase)`.
  - `ServerHandler`: `static bool Accepts(ServerSection s, X509Certificate2? cert, SslPolicyErrors errors)` (no errors → true; `Insecure` → true; pin equals lowercase hex SHA-256 of `RawData`, case-insensitive → true; else false) and `static SocketsHttpHandler Create(ServerSection s)`.
  - `public enum FeedSignalKind { Connected, Disconnected, Gap, Event }`, `public sealed record FeedSignal(FeedSignalKind Kind, FeedEvent? Event = null)`.
  - `public sealed class FeedReader(Func<HttpClient> client, FeedReaderOptions options, ILogger<FeedReader> log)`: `string? LastId { get; set; }`, `IAsyncEnumerable<FeedSignal> Run(CancellationToken ct)` — GET `events` with `Last-Event-ID` = `LastId`, `Connected` after the response headers, `SseParser.Create(stream).EnumerateAsync`, `ping` ignored, `gap` → `Gap`, other items → `LastId = EventId` and `Event`; on any failure or end of stream → `Disconnected`, wait the next backoff step, reconnect; the step resets after a successful connect; no item for `IdleTimeout` → reconnect. `FeedReaderOptions { TimeSpan[] Backoff (1, 2, 4, 8, 16, 30 s); TimeSpan IdleTimeout (45 s) }`.
  - `BotFixture` (tests): `ServerFixture` with the scoped token `discord-bot` (all four scopes) through the panel's `Admin:Tokens` keys, plus `Token(string name, params string[] scopes)` for narrower tokens; `RimlinkApi Api(string? token = null, DelegatingHandler? spy = null)` over `fixture.Server.CreateHandler()` with base address `…/api/admin/`; `Func<HttpClient> FeedClient()`.

- [ ] **Step 1: Write the failing tests**:
  - `BotOptionsTests`: `DefaultsNeedTheTwoTokens` (two errors), `ValidOptionsHaveNoErrors`, `BadLanguageUrlAndPinAreReported`.
  - `ServerHandlerTests`: `TrustedChainIsAccepted`, `PinMatchesAnyCase`, `WrongPinIsRefused`, `InsecureAcceptsAnything` (certificate from `CertificateRequest` + `CreateSelfSigned`).
  - `RimlinkApiTests`: `SendsTokenAndActor` (kick a logged-in player as `discord:Mod (1)` → `Actions(1)[0].ActorName == "api:discord-bot:discord:Mod (1)"`), `ErrorsCarryStatusAndDetail` (kick player 999 → status 404, detail mentions `999`), `WrongTokenIs401`, `ReadsDeserialize` (`Status`, `Players`, `Player`, `Online`, `Lots` against seeded data), `PostChatReturnsTheId`.
  - `FeedReaderTests`: `ReadsEventsWithIds` (publish through `AdminFeed`, read `Connected` then the event; `LastId` = its id), `ResumesWithoutLossOrDuplicates` (read 3, cancel, publish 2, run again with the same reader → exactly those 2), `ForeignIdYieldsGap` (`LastId = "1:5"` → `Gap` then `server.started`), `RetriesWhileServerIsDown` (a client factory whose handler throws twice, backoff 10 ms → `Disconnected`, `Disconnected`, `Connected`), `PingsAreNotEvents` (`Admin:FeedPingSeconds=1`, 3 s → no `Event` of type `ping`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests -nologo -v q` — Expected: FAIL (project compiles only after Step 3; a build error counts as the red state).
- [ ] **Step 3: Implement** the project, options, DTOs, client, handler and reader as in Interfaces.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Add the Discord bot project with the REST client and the feed reader"`

### Task 7: Strings, formatting, outbound queue and the feed router

**Files:**
- Create: `src/Rimlink.Discord/Strings/Strings.cs`, `Strings/Strings.en.json`, `Strings/Strings.ru.json` (embedded resources), `Discord/IDiscordOut.cs`, `Feed/FeedGroups.cs`, `Feed/ChannelMap.cs`, `Feed/Formatter.cs`, `Feed/OutboundQueue.cs`, `Feed/FeedRouter.cs` (all under `src/Rimlink.Discord/`)
- Test: `tests/Rimlink.Discord.Tests/Feed/StringsTests.cs`, `Feed/FormatterTests.cs`, `Feed/OutboundQueueTests.cs`, `Feed/FeedRouterTests.cs`, `tests/Rimlink.Discord.Tests/Support/FakeDiscordOut.cs`

**Interfaces:**
- Consumes: `FeedSignal`, `FeedEvent` (Task 6).
- Produces:
  - `public interface IDiscordOut { Task Send(ulong channelId, string text, CancellationToken ct); Task React(ulong channelId, ulong messageId, string emoji, CancellationToken ct); }`; `FakeDiscordOut` records `(channel, text)` and reactions.
  - `public sealed class Strings(string language)`: `string T(string key, params object[] args)` (missing key → the key itself); `IReadOnlyCollection<string> Keys`. Keys: `feed.<type>` for every feed type except `chat.message`, plus `feed.gap`, `feed.server.down`, `feed.server.up`, `chat.attachment`, and the `cmd.*` keys added in Task 9.
  - `public enum FeedGroup { Server, Raids, Market, Moderation, Chat }`; `FeedGroups.Of(string type) : FeedGroup?` (the spec §2 table; unknown → null).
  - `public sealed record ChannelMap(ulong Server, ulong Raids, ulong Market, ulong Moderation, ulong Chat) { public ulong For(FeedGroup g); }` (0 = off).
  - `public sealed class Formatter(Strings s)`: `string? Format(FeedEvent e)` (null for `chat.message` and unknown types); `public static string Escape(string text)` — `Discord.Format.Sanitize` plus a zero-width space after every `@` and `<`, so `@everyone`, `@here`, `<@123>` render as text.
  - `public sealed class OutboundQueue(IDiscordOut discord, ILogger<OutboundQueue> log, int maxLines = 500)`: `void Enqueue(ulong channelId, string line)` (0 ignored; over `maxLines` drops the oldest and counts it), `int Dropped`, `Task FlushAsync(CancellationToken ct)` (one message per channel per call, lines joined by `\n` up to 2000 characters, the rest waits; one line over 2000 → first 1999 + `…`).
  - `public sealed class FeedRouter(ChannelMap channels, Formatter formatter, OutboundQueue queue, Strings s, TimeProvider time, Action<FeedEvent> chat)`: `void Handle(FeedSignal signal)`, `void Tick()`. Rules: `Event` `chat.message` → `chat(e)`; other events → formatted line to its group's channel; `Gap` → `feed.gap` to Moderation; `Disconnected` starts the outage clock if not running; `Tick` with the outage ≥ 60 s and not yet announced → `feed.server.down` to Server once; `Connected` stops the clock, and after an announced outage the first following `Event` decides: `server.started` → only its own line, anything else → `feed.server.up` before it.

- [ ] **Step 1: Write the failing tests**:
  - `StringsTests`: `BothLanguagesHaveTheSameKeys`, `EveryFeedTypeHasAText` (`feed.<type>` for each type of the spec table except `chat.message`), `ArgumentsAreFormatted`.
  - `FormatterTests`: one test per group checking the names and numbers appear (`player.joined` → contains `Nova`; `raid.resolved` → attacker, target, dead count; `lot.sold` → seller, buyer, price, summary; `admin.action` → action, actor, details), `NamesAreEscaped` (`**x**` comes out sanitized), `MentionsAreNeutralised` (`@everyone`, `@here`, `<@123>` in a player name or notice text never appear verbatim), `ChatAndUnknownTypesAreNotFormatted`.
  - `OutboundQueueTests`: `LinesAreJoinedUpTo2000Chars` (30 lines of 100 → first flush one message ≤ 2000, second flush the rest), `LongLineIsTruncated`, `OverflowDropsOldest` (`maxLines` 3, enqueue 5 → flushed lines 3..5, `Dropped` 2), `ChannelsAreSeparate`, `ZeroChannelIsIgnored`.
  - `FeedRouterTests` (`FakeTimeProvider`, `FakeDiscordOut`, flush after each step): `EventsGoToTheirGroupChannel`, `DisabledGroupIsSilent`, `ChatEventsGoToTheChatHandler`, `GapGoesToModeration`, `ShortOutageSaysNothing` (disconnect, 30 s, connect, event → no down/up line), `DownLineAfterSixtySecondsOnce` (59 s → none; 61 s → one; 3 min → still one), `UpLineAfterOutage` (then `Connected` + `notice` → `feed.server.up` line then the notice), `ServerStartedReplacesUpLine` (`Connected`, `Gap`, `server.started` → one `feed.server.started` line, no `feed.server.up`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests --filter "StringsTests|FormatterTests|OutboundQueueTests|FeedRouterTests" -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces; texts in both languages, names in bold.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Route feed events to Discord channels with batching and outage lines"`

### Task 8: Chat bridge

**Files:**
- Create: `src/Rimlink.Discord/Chat/ChatBridge.cs`
- Test: `tests/Rimlink.Discord.Tests/Chat/ChatBridgeTests.cs`

**Interfaces:**
- Consumes: `RimlinkApi.PostChat` (Task 6); `ChannelMap`, `OutboundQueue`, `Formatter.Escape`, `Strings`, `IDiscordOut` (Task 7).
- Produces:
  - `public sealed record DiscordChatMessage(ulong ChannelId, ulong MessageId, bool AuthorIsBot, string AuthorName, string CleanContent, int Attachments)`.
  - `public sealed class ChatBridge(ChannelMap channels, OutboundQueue queue, RimlinkApi api, IDiscordOut discord, Strings s, ILogger<ChatBridge> log)`: `const int MaxLength = 500` (mirrors the server's `Limits.ChatMaxLength`); `void FromGame(FeedEvent e)` — `kind` `player` → `**{Escape(from.name)}**: {Escape(text)}` to the Chat channel, `system` and `external` skipped; `Task<bool> FromDiscord(DiscordChatMessage m, CancellationToken ct)` — other channel, bot author or Chat channel 0 → false; text = `CleanContent.Trim()` plus ` ` + `chat.attachment` when `Attachments > 0`; empty → false; longer than `MaxLength` → first 499 + `…`; author = `AuthorName` cut to 32; `RimlinkApiException` → `React(channel, message, "❌")`, log, false; posted → true.
  - `FeedRouter`'s `chat` delegate is `ChatBridge.FromGame` (wired in Task 10).

- [ ] **Step 1: Write the failing tests** (against `BotFixture`): `PlayerLineGoesToChatChannel`, `ExternalLinesAreNotEchoed` (post through the bridge, take the resulting `chat.message` from the feed, `FromGame` it → nothing queued), `SystemLinesAreSkipped`, `GameTextIsEscaped` (`@everyone **x**` in game text → neither verbatim), `DiscordMessageIsPosted` (gRPC `History` has the line with `ExternalAuthor`), `BotsAndOtherChannelsAreIgnored` (no request: spy handler count 0), `LongMessageIsCutToTheServerLimit` (600 characters → accepted, stored text 500 characters ending `…`), `AttachmentIsMarked`, `EmptyMessageIsNotSent`, `RefusalReacts` (bridge over a wrong token → ❌ reaction on that message, returns false).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests --filter ChatBridgeTests -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** `ChatBridge`.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Bridge the public game chat and a Discord channel both ways"`

### Task 9: Permissions, command handlers and confirmations

**Files:**
- Create: `src/Rimlink.Discord/Commands/Caller.cs`, `Commands/Permissions.cs`, `Commands/Reply.cs`, `Commands/PlayerResolver.cs`, `Commands/Confirmations.cs`, `Commands/CommandHandlers.cs`; `cmd.*` keys in both `Strings.*.json`
- Test: `tests/Rimlink.Discord.Tests/Commands/PermissionsTests.cs`, `Commands/PlayerResolverTests.cs`, `Commands/ConfirmationsTests.cs`, `Commands/CommandHandlersTests.cs`

**Interfaces:**
- Consumes: `RimlinkApi`, `RimlinkApiException`, DTOs (Task 6); `Strings`, `Formatter.Escape` (Task 7).
- Produces:
  - `public enum Level { Everyone, Moderator, Admin }`; `public sealed record Caller(ulong UserId, string UserName, Level Level) { public string Actor => $"discord:{UserName} ({UserId})"; }`.
  - `public sealed class Permissions(DiscordSection options) { public Level LevelOf(IEnumerable<ulong> roleIds); }` — any admin role → Admin, else any moderator role → Moderator, else Everyone.
  - `public sealed record Reply(string Text, bool Ephemeral, Confirmation? Confirm = null)`; `public sealed record Confirmation(string Id, string Description)`.
  - `public sealed class PlayerResolver(RimlinkApi api)`: `Task<(PlayerInfo? Player, string? Error)> Resolve(string name, CancellationToken ct)` — `Players(name, 25)` (the panel's substring search), then: exact name (case-insensitive) → it; else exactly one name with that prefix → it; several → error `cmd.player.ambiguous` listing up to 5 names; none → `cmd.player.unknown`.
  - `public sealed class Confirmations(TimeProvider time)`: `Confirmation Create(Caller caller, string description, Func<Task<string>> action)`; `Task<Reply> Press(string id, ulong userId, bool confirm)` — unknown or older than 60 s → `cmd.confirm.expired`; other user → `cmd.confirm.notyours` (entry kept); owner → the entry is removed atomically (`ConcurrentDictionary.TryRemove`; a losing concurrent press gets `cmd.confirm.expired`), then `confirm` runs the action and replies with its text, cancel replies `cmd.confirm.cancelled`; all replies ephemeral.
  - `public sealed class CommandHandlers(RimlinkApi api, PlayerResolver players, Confirmations confirmations, Strings s)` — one method per command, each `Task<Reply> X(Caller caller, …args, CancellationToken ct = default)`; the level check is the first statement and refuses with ephemeral `cmd.denied` before any request; `RimlinkApiException` status 0 → `cmd.noanswer`, 403 → `cmd.forbidden` (the token lacks the scope), 404 → `cmd.notfound`, other → the detail; public queries reply non-ephemeral, the rest ephemeral.

  | Method | Level | Call | Reply |
  |---|---|---|---|
  | `Online` | Everyone | `Online` | names and since (the server's online list has no faction), or `cmd.online.none` |
  | `Status` | Everyone | `Status` | online/max, version, freezes |
  | `Market(string? seller)` | Everyone | resolve + `Lots` | ≤ 20 lots: id, seller, price, summary |
  | `Player(string name)` | Everyone | resolve + `Player(id)` (card) | faction, online, settlements, peaceful |
  | `Kick(player, reason?)` | Moderator | `Kick` | server text |
  | `Mute(player, int minutes)` / `Unmute(player)` | Moderator | `Mute` / `Unmute` | `cmd.done` |
  | `Broadcast(text)` | Moderator | `Broadcast` | `cmd.done` |
  | `Bans` / `Actions(int limit = 20)` | Moderator | `Bans` / `Actions` | list |
  | `RaidsList(player?)` / `RaidStats` / `LiveList(player?)` / `EconomyStats` | Moderator | as named | list or numbers |
  | `Ban(player, double? days, reason?)` / `Pardon(long banId)` | Admin | `Ban` / `Pardon` | ban id / `cmd.done` |
  | `Wallet(player, long delta, reason?)` | Admin | `Wallet` | new balance |
  | `RefundLot(long id)` / `RefundParcel(long id)` | Admin | as named | `cmd.done` |
  | `EconomyFreeze(bool frozen, reason?)` / `RaidsFreeze(bool frozen, reason?)` | Admin | as named | `cmd.done` |
  | `RaidCancel(long id, bool returnPawns, reason?)` / `LiveEnd(long id, reason?)` | Admin | as named | `cmd.done` |
  | `Peaceful(player, bool on)` / `Saves(player)` | Admin | as named | `cmd.done` / versions |
  | `Shutdown(int seconds, reason?)`, `Rollback(player, int version)`, `Reset(player)`, `RecreateWorld()` | Admin | none yet: `Confirmations.Create` with the call as the action | ephemeral description + `Confirm` |

- [ ] **Step 1: Write the failing tests**:
  - `PermissionsTests`: `AdminRoleImpliesModerator`, `ModeratorRole`, `NoRoleIsEveryone`.
  - `PlayerResolverTests` (`BotFixture`, players `Nova`, `Novak`, `Bob`): `ExactWinsOverPrefix` (`nova` → Nova), `SinglePrefixResolves` (`bo` → Bob), `AmbiguousListsCandidates` (`no` with only `Novak`, `Novella` → both names), `UnknownIsAnError`.
  - `ConfirmationsTests` (`FakeTimeProvider`): `ConfirmationRunsOnceForItsOwner` (other user → `notyours`, action not run; owner → runs once; second press → `expired`; two concurrent owner presses → action ran once), `ConfirmationExpires` (61 s → `expired`, action not run), `CancelDoesNotRun`.
  - `CommandHandlersTests` (`BotFixture`): `PublicCommandsWorkForEveryone` (`Online`, `Status`, `Market`, `Player` return non-ephemeral text with the seeded names), `RefusedCommandsMakeNoRequest` (Everyone → `Kick`, Moderator → `Ban`, Moderator → `Reset`: `cmd.denied`, spy handler counted 0 requests), `KickRecordsTheDiscordActor` (`Actions(1)[0].ActorName == "api:discord-bot:discord:Mod (7)"`), `MissingScopeBecomesItsOwnText` (handlers over a token with `read`, `chat`, `moderate`: Admin caller → `Ban` → `cmd.forbidden`), `IrreversibleCommandsWaitForConfirmation` (`Reset` → `Confirm` set, `Actions` has no `reset`; `Confirmations.Press` by the owner → `reset` recorded), `NotFoundBecomesItsOwnText` (`Pardon(9999)` → `cmd.notfound`), `NoAnswerBecomesItsOwnText` (a handler that throws `HttpRequestException` → `cmd.noanswer`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests --filter "PermissionsTests|PlayerResolverTests|ConfirmationsTests|CommandHandlersTests" -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces and the table; `cmd.*` texts in both languages.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** → `git commit -m "Add Discord command handlers with role levels and confirmations"`

### Task 10: Discord.Net shell and host wiring

**Files:**
- Create: `src/Rimlink.Discord/Program.cs`, `BotHost.cs`, `Discord/DiscordOut.cs`, `Discord/DiscordHost.cs`, `Discord/FeedWorker.cs`, `Discord/Modules/PublicModule.cs`, `Discord/Modules/ModerationModule.cs`, `Discord/Modules/AdminModule.cs`, `Discord/Modules/ConfirmModule.cs`, `Discord/Modules/PlayerAutocomplete.cs`, `Discord/Modules/ModuleBase.cs`, `Strings/commands.en-US.json`, `Strings/commands.ru.json`
- Test: `tests/Rimlink.Discord.Tests/HostTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 6–9.
- Produces:
  - `public static class BotHost { public static IHost Create(string[] args, IConfiguration? overrides = null); }` — reads `appsettings.json` + environment; registers `BotOptions`, the `api` `HttpClient` (base `<Url>/api/admin/`, bearer token, timeout 10 s, `ServerHandler.Create`), the `feed` client (infinite timeout), `RimlinkApi`, `FeedReader` (`Func<HttpClient>` from the `feed` client), `FeedReaderOptions`, `Strings` (configured language), `Formatter`, `OutboundQueue`, `ChannelMap` (from options; `DiscordHost` zeroes channels it cannot use), `ChatBridge`, `FeedRouter` (with `ChatBridge.FromGame`), `Permissions`, `PlayerResolver`, `Confirmations`, `CommandHandlers`, `TimeProvider.System`, `DiscordSocketClient` (intents `Guilds | GuildMessages | MessageContent`), `InteractionService` (localization from `commands.<locale>.json` via Discord.Net's JSON localization manager), `IDiscordOut` → `DiscordOut`, hosted `DiscordHost` and `FeedWorker`. `Program.Main`: `BotOptions.Validate` errors → logged, exit code 1; else run.
  - `DiscordOut`: `SendMessageAsync(text, allowedMentions: AllowedMentions.None)`, `AddReactionAsync`.
  - `DiscordHost` (BackgroundService): log in, wait for Ready, check the guild (missing → error log, no commands), each configured channel (exists, text, bot may view and send — else warning and that group off), each configured role (missing → warning), `RegisterCommandsToGuildAsync(GuildId)`, route `InteractionCreated` to the interaction service, `MessageReceived` from the Chat channel to `ChatBridge.FromDiscord`.
  - `FeedWorker` (BackgroundService): `await foreach` over `FeedReader.Run` into `FeedRouter.Handle`; a 1 s `PeriodicTimer` loop calls `FeedRouter.Tick()` and `OutboundQueue.FlushAsync`.
  - Modules: `/online`, `/status`, `/market [seller]`, `/player <name>` (public); `/kick`, `/mute`, `/unmute`, `/broadcast`, `/bans`, `/actions`, `raids list|stats`, `live list`, `economy stats` (Moderation, `[DefaultMemberPermissions(GuildPermission.ManageGuild)]`); the admin table rows of Task 9 as `/ban`, `/pardon`, `/wallet`, `refund lot|parcel`, `economy freeze|unfreeze`, `raids cancel|freeze|unfreeze`, `live end`, `/peaceful`, `/saves`, `/shutdown`, `/rollback`, `/reset`, `/recreateworld` (Admin, same attribute). `ModuleBase` builds `Caller` from the guild user's role ids through `Permissions` and sends the `Reply` (ephemeral flag; `Confirm` → buttons with custom ids `confirm:<id>` and `cancel:<id>`). `ConfirmModule` handles both ids through `Confirmations.Press`. `player`/`seller` options use `PlayerAutocomplete` (`Players(prefix, 25)`).

- [ ] **Step 1: Write the failing tests** in `HostTests`: `HostResolvesEveryService` (`BotHost.Create` with in-memory valid options, resolve `CommandHandlers`, `FeedRouter`, `ChatBridge`, `DiscordHost`, `FeedWorker` without starting the host), `ModulesBuild` (`new InteractionService(new DiscordRestClient())` → `AddModulesAsync(typeof(BotHost).Assembly, services)` succeeds and lists every command name of the Modules list above; where Discord.Net offers a public way to build the application-command properties of a module, call it too, since name rules are otherwise checked only by the first real `RegisterCommandsToGuildAsync`), `InvalidConfigurationExitsWithErrors` (`BotOptions.Validate` errors surface from `Program`'s check method with exit code 1).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Discord.Tests --filter HostTests -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** the host, modules, `DiscordOut`, `DiscordHost`, `FeedWorker` and the command localization files (descriptions in English and Russian).
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green; `dotnet run --project src/Rimlink.Discord` with no configuration exits with code 1 and names both missing tokens.
- [ ] **Step 5: Commit** → `git commit -m "Wire the bot to Discord: slash commands, chat messages and the feed worker"`

### Task 11: Deployment, documentation and the manual run

**Files:**
- Create: `deploy/Dockerfile.discord`, `deploy/discord.appsettings.example.json`
- Modify: `deploy/docker-compose.yml`, `deploy/appsettings.example.json` (`Admin:FeedBufferSize`, `Admin:FeedPingSeconds`), `docs/server-setup.md` (REST additions in §6, new section «Discord bot»), `docs/player-guide.md` (chat lines from Discord), `docs/manual-checklist.md` (section «Discord bot»), `README.md` (status)

**Interfaces:**
- Consumes: the bot's options (Task 6), the server's new keys (Tasks 3, 5).
- Produces:
  - `Dockerfile.discord`: two stages like `deploy/Dockerfile` (restore `src/Rimlink.Discord`, publish `-c Release -r linux-x64 --self-contained false`), runtime `mcr.microsoft.com/dotnet/runtime:10.0`, `USER app`, `ENTRYPOINT ["dotnet", "rimlink-discord.dll"]`, no healthcheck, no curl.
  - compose service `rimlink-discord`: `image: ghcr.io/asofwar/rimlink-discord:latest`, commented `build: { context: .., dockerfile: deploy/Dockerfile.discord }`, `depends_on: [rimlink]`, `restart: unless-stopped`, env `RimlinkBot__Server__Url: https://rimlink:25560`, `RimlinkBot__Server__ApiToken: "${RIMLINK_BOT_TOKEN:?set RIMLINK_BOT_TOKEN in .env}"` (and on the `rimlink` service the `Admin:Tokens` entry `discord-bot` with that token and scopes `read`, `chat`, `moderate`, `admin`, as env keys of the panel's token config), `RimlinkBot__Server__CertificateSha256: "${RIMLINK_CERT_SHA256:-}"`, `RimlinkBot__Discord__Token: "${DISCORD_TOKEN:?set DISCORD_TOKEN in .env}"`, `RimlinkBot__Discord__GuildId`, channel and role ids from `.env`.
  - `server-setup.md` «Discord bot»: create the application and bot in the Discord Developer Portal; enable the Message Content intent; invite with scopes `bot` and `applications.commands` and permissions View Channels, Send Messages, Add Reactions, Read Message History; copy the guild, channel and role ids (Developer Mode); create the bot's scoped token (`discord-bot`, scopes `read`, `chat`, `moderate`, `admin`; without `admin` the admin commands answer `cmd.forbidden`; without `chat` the bridge is one-way); take `certificateFingerprint` from `rimlink-tools admin status`, `GET /api/admin/status` or the public page into `RIMLINK_CERT_SHA256`; `Insecure` only inside the compose network; the event groups and what each posts; commands and levels; that the bot acts as admin on the server, so Discord roles are the only gate.
  - `manual-checklist.md` «Discord bot»: the spec §8 manual list as checkboxes.

- [ ] **Step 1: Write** the files and doc sections.
- [ ] **Step 2: Verify** `docker build -f deploy/Dockerfile.discord -t rimlink-discord:test .` from the repository root — Expected: image builds. `docker compose -f deploy/docker-compose.yml config` with a `.env` holding dummy values — Expected: valid, both services listed.
- [ ] **Step 3: Verify** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 4: Commit** → `git commit -m "Ship the Discord bot as a container and document its setup"`
- [ ] **Step 5: Manual run** (needs the owner): a test Discord server and a bot token from the owner, a local server (`AGENTS.md` command plus the `discord-bot` scoped token in `Admin:Tokens`), the bot via `dotnet run --project src/Rimlink.Discord` with env config; game steps go to session `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`. Walk the checklist section; record results in `docs/manual-checklist.md` and commit them `git commit -m "Record the Discord bot manual run"`.
