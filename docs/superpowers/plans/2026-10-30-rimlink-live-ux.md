# Rimlink Live and Visit UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Location pings (live map, visit copy, world map), tile and settlement cards in chat, a pause on threat letters with a threshold per player, visible speed votes on the time buttons, refusals in the player's language, and the run-3 fixes (host speed after a start abort, stale invite).

**Architecture:** Pings are one server-relayed call (`Chat/Ping` → `ServerEvent.ping`) for every context. Cards are `[[tile:N]]` tokens parsed in client-core. The tempo rules move from the mod's `LiveTempo` into a Verse-free `TempoRules` in client-core (unit-tested), and the new fields ride the existing `Hello`/`Order`/`Delta` frames. Refusals carry a key and arguments in `ErrorDetail`, and the mod translates them by key, falling back to the server text.

**Tech Stack:** .NET 10, gRPC/protobuf, ASP.NET Core server, xUnit, Lib.Harmony 3.0.0-rc.3, RimWorld 1.7 (`Assembly-CSharp.dll`).

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-live-ux-design.md`

## Global Constraints

- Protocol only grows: the field numbers below are the spec's (§6). No version bump, `ProtocolVersion.Current` unchanged, no EF migration.
- `src/Rimlink.Client.Core` keeps no `Verse`/`RimWorld` reference. Game objects are touched on the main thread only (`MainThread.Post` from background tasks).
- Harmony patches live in `src/Rimlink.Client/Game/Patches`, one class per patched method, with a one-line summary of why.
- Every user-facing string is a keyed string in `mod/Languages/English/Keyed/Rimlink.xml` and `mod/Languages/Russian/Keyed/Rimlink.xml`.
- Values pinned by the spec:
  - ping key: `Rimlink_Ping`, default `G`;
  - marker: 8 s of real time;
  - rate limit: 12 pings per minute per account (`RateLimitPolicy`);
  - tile range `[0, 10 000 000)`, cell range `[0, 1000)` on both axes;
  - at most 3 cards per message;
  - letter pause modes `NEVER/MAJOR/ANY`, default `MAJOR`;
  - `PausedBy`: 0 none, 1 host, 2 guest, 3 letter;
  - time-button marks «Х»/«Г» (EN «H»/«G»).
- Pings are not stored, not in chat history, not in the admin/Discord feed.
- The English text of every server refusal stays as it is today: old clients, logs and REST show it.
- `dotnet test Rimlink.sln -nologo -v q` green and the mod building (`scripts/Build-ClientMod.ps1`) before every commit. Commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no trailers. Branch `asofwar/live-ux` from `origin/main 07df8d9`.
- In-game checks run on the s1 stand only:
  - game copy `D:\Build\rimlink-s1\RimWorld`, GABS `rimlink-s1-host`/`rimlink-s1-guest`, server `bash D:/Build/rimlink-s1/run-server.sh` on 25580;
  - deploy with both games stopped;
  - the guest plays in Russian.

## Review Focus

1. Typing a `g` in the chat input or any other text field must not send a ping: the ping key is ignored while a text control has keyboard focus (`GUIUtility.keyboardControl != 0`) or a window absorbs input. Task 9 step 5, scenario P4.
2. A ping that arrives while the receiver is in the main menu or loading a game is dropped without an error; a ping for a tile outside the receiver's world shows only the message. Task 8 test `PingIsKeptWithoutAGame`, Task 9 step 4.
3. Several threat letters at once give one letter pause, and one "play" by either side lifts it. A guest speed order during a letter pause lifts it and costs no pause from the budget; a guest pause order during it is a no-op. Task 11 tests.
4. A card token with a number outside `int`, a negative number or a tile outside the receiver's world shows as plain text or «тайл N» without throwing. Task 6 tests, Task 10 step 2.
5. A refusal from an older server (no key), or with a key the player's language lacks, shows the server's English text, and too few arguments never throw. Task 4 test `ReadWithoutKeyGivesTheMessage`, Task 5 step 1.

---

### Task 1: Protocol additions

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/common.proto`, `chat.proto`, `events.proto`, `live.proto`
- Test: `tests/Rimlink.Server.Tests/Live/LiveRelayTests.cs`

**Interfaces:**
- Produces (C# names from protoc):
  - `ErrorDetail.Key`, `ErrorDetail.Args` (repeated string);
  - `PingRequest { ToPlayerId, Tile, Cell (MapCell) }`, `PingEvent { From, Tile, Cell }`, `MapCell { X, Z }`;
  - `ServerEvent.Ping` (76), `rpc Chat.Ping(PingRequest) returns Empty`;
  - `enum LetterPause { LetterPauseNever = 0, LetterPauseMajor = 1, LetterPauseAny = 2 }`;
  - `Hello.LetterPause` (3), `Order.LetterPause` (14, in `oneof o`);
  - `GuestMessage.Label` (4), `GuestMessage.LetterDef` (5);
  - `Delta.HostWish` (15), `Delta.GuestWish` (16).

- [ ] **Step 1: Add the fields and messages exactly as spec §6 lists them.** Each new field gets a one-line comment. `MapCell` lives in `chat.proto`, because `live.proto` must not be imported there.
- [ ] **Step 2: Write `RelayCarriesTheTempoFieldsUnchanged`.** The host's `Delta` with `HostWish = 2, GuestWish = 1, PausedBy = 3` and `Messages = { new GuestMessage { Label = "Raid", LetterDef = "ThreatBig", Letter = true } }` reaches the guest with the same values. The guest's `Hello { LetterPause = LetterPauseAny }` reaches the host as `PeerHello.LetterPause == LetterPauseAny`. An `Order { LetterPause = LetterPauseMajor }` reaches the host unchanged. Use the existing relay test helpers in this file.
- [ ] **Step 3: Run it.** `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter RelayCarriesTheTempoFieldsUnchanged`. Expected: PASS. The relay is opaque, so this test only pins the contract. Then run the full suite (`dotnet test Rimlink.sln -nologo -v q`): it stays green.
- [ ] **Step 4: Commit** with message `Grow the protocol for pings, letter pauses, speed votes and refusal keys`.

### Task 2: Server ping

**Files:**
- Modify: `src/Rimlink.Server/Application/Chat/ChatService.cs`, `src/Rimlink.Server/Grpc/ChatGrpcService.cs`, `src/Rimlink.Server/Grpc/RateLimitPolicy.cs`
- Test: `tests/Rimlink.Server.Tests/Chat/PingTests.cs`

**Interfaces:**
- Consumes: Task 1 messages.
- Produces:
  - `Task ChatService.Ping(Session sender, long toPlayerId, int tile, MapCell? cell)`;
  - `ChatGrpcService.Ping(PingRequest, ServerCallContext)` returns `Empty`;
  - `RateLimitPolicy` row `["/rimlink.v1.Chat/Ping"] = (12, TimeSpan.FromMinutes(1))`.

- [ ] **Step 1: Write the tests** (fixture as in `ChatTests`: `new ServerFixture().WithSetting("Auth:AllowPassword", "true")`, `RegisterAndLogin`, `Bearer`, an events subscription read with a 10 s timeout):
  - `PingReachesOnlyTheRecipient`: alice pings bob with tile 85538 and cell (12, 34). Bob receives one `PingEvent` with `From.PlayerId == alice`, `Tile == 85538` and `Cell.X/Z == 12/34`. Carol receives no `Ping` within 1 s.
  - `PingWithoutCellIsATilePing`: `Cell == null` arrives as `Cell == null`.
  - `PingIsNotKeptInHistory`: after a ping, `History` is empty for both.
  - `PingRefusals`, one test per case:
    - muted sender → `ErrorDetail().Code == ErrorCode.Muted`;
    - offline recipient → `ErrorCode.RecipientUnknown`;
    - ping to oneself → `RecipientUnknown`;
    - tile −1 or 10 000 000 → `StatusCode.InvalidArgument`;
    - cell x 1000 → `InvalidArgument`.
  - `PingsAreRateLimited`: with `fixture.WithRateLimits()`, 12 pings succeed and the 13th throws `StatusCode.ResourceExhausted`.
- [ ] **Step 2: Run them.** `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter PingTests`. Expected: FAIL, `Ping` is unimplemented.
- [ ] **Step 3: Implement `ChatService.Ping`.** Muted check as in `Send` (lines 42–44). Recipient: online (`sessions.ByPlayer(toPlayerId)` is not null) and not the sender, else `RpcErrors.Failed(ErrorCode.RecipientUnknown, "that player is not online")`. Range checks throw `InvalidArgument`. Delivery via `events.SendTo(toPlayerId, new ServerEvent { Ping = new PingEvent { From = <sender PlayerRef as in ToEvent>, Tile = tile, Cell = cell } })`. Add `SessionRegistry` to the constructor. Wire the gRPC method and the rate-limit row.
- [ ] **Step 4: Run the tests and the full suite.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Relay a location ping to one online player`.

### Task 3: Server refusal keys — mechanism and live, visit, chat sites

**Files:**
- Create: `src/Rimlink.Server/Grpc/RefusalKeys.cs`
- Modify:
  - `src/Rimlink.Server/Grpc/RpcErrors.cs`, `RateLimitInterceptor.cs`;
  - every `RpcErrors.Failed` / `InvalidArgument` site under `Application/Live`, `Application/Visits`, `Application/Chat` (including Task 2's);
  - `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`.
- Test: `tests/Rimlink.Server.Tests/Hosting/RefusalKeysTests.cs`

**Interfaces:**
- Produces:
  - `public static class RefusalKeys` with `const string` per refusal. The value is the keyed-string suffix, e.g. `public const string LiveOwnSession = "Live.OwnSession";`.
  - `RpcErrors.Failed(ErrorCode code, string key, string message, params object[] args)` and `RpcErrors.With(StatusCode status, ErrorCode code, string key, string message, params object[] args)`. Both set `ErrorDetail.Key = key` and `Args` = each arg formatted with `CultureInfo.InvariantCulture`, dates as `"u"`. The old overloads stay for unconverted sites.
  - A keyed string `Rimlink.Refusal.<key>` per constant, `{0}`… in argument order. The English text equals today's server message.

- [ ] **Step 1: Write the tests:**
  - `EveryRefusalKeyHasEnglishAndRussianText`: reflect every `const string` of `RefusalKeys` and load both keyed XMLs (`RepoPaths.Root`, `XDocument.Load`). Each `Rimlink.Refusal.<value>` exists in both files, and the `{n}` placeholder count is the same in both.
  - `OwnLiveSessionRefusalHasItsKey`: a second `Propose` while one is open → `ErrorDetail().Key == "Live.OwnSession"`, `Code == LiveActive`, and `Message` is unchanged («you already have a live session»).
  - `MutedRefusalCarriesTheDate`: `Key == "Chat.Muted"`, `Args.Count == 1`.
  - `RateLimitRefusalHasItsKey`: `Key == "RateLimited"`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement the mechanism and convert the sites.** Covers `Application/Live`, `Application/Visits` and `Application/Chat`, plus the rate-limit interceptor. The interceptor uses `RpcErrors.With(StatusCode.ResourceExhausted, ErrorCode.Unspecified, RefusalKeys.RateLimited, "too many requests, slow down")`. `InvalidArgument` throws become `RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, key, message, args)`. Add the EN and RU keyed strings.
- [ ] **Step 4: Run the tests and the full suite.** Expected: PASS. Existing tests that assert only on `Code` or `Message` still pass.
- [ ] **Step 5: Commit** with message `Give live, visit and chat refusals a key and arguments`.

### Task 4: Server refusal keys — remaining sites

Independent mechanical piece: hand it to Codex (`codex-delegate`, `codex-run.ps1 -PromptFile`) with this task's text, in its own worktree from this branch after Task 3.

**Files:**
- Modify: every remaining `RpcErrors.Failed` and player-facing `InvalidArgument`/`NotFound`/`PermissionDenied` site under `src/Rimlink.Server/Application` (Economy, Raids, World, Saves, Auth, Shop, Admin) and `src/Rimlink.Server/Grpc`, `RefusalKeys.cs`, both keyed XMLs.
- Test: `tests/Rimlink.Server.Tests/Hosting/RefusalKeysTests.cs`

- [ ] **Step 1: Write `EveryTypedRefusalHasAKey`.** It scans the server sources (`RepoPaths.Root`/`src/Rimlink.Server`). Every `RpcErrors.Failed(` or `RpcErrors.With(` call passes a `RefusalKeys.` argument, and no `new RpcException(new Status(StatusCode.InvalidArgument` or `StatusCode.NotFound` remains outside `RpcErrors.cs`. Admin-only REST/CLI messages are exempt by an explicit list in the test.
- [ ] **Step 2: Run it.** Expected: FAIL, listing the unconverted sites.
- [ ] **Step 3: Convert the sites** exactly as in Task 3, adding EN and RU texts.
- [ ] **Step 4: Run the tests and the full suite.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Give the remaining server refusals a key and arguments`.

### Task 5: Client-core refusal detail and outbox

**Files:**
- Create: `src/Rimlink.Client.Core/Net/RpcErrorDetail.cs`
- Modify: `src/Rimlink.Client.Core/Economy/EconomyClient.cs`, `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`
- Test: `tests/Rimlink.Client.Tests/Net/RpcErrorDetailTests.cs`, `tests/Rimlink.Client.Tests/Economy/TransferOutboxTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record Refusal(ErrorCode? Code, string Message, string? Key, IReadOnlyList<string> Args)`.
  - `public static Refusal RpcErrorDetail.Read(RpcException ex)`. `Message` is the detail's message, or `$"{ex.StatusCode}: {ex.Status.Detail}"` without a trailer, as `MessageOf` does today.
  - `OutboxEntry.RefusalKey` (`string?`) and `OutboxEntry.RefusalArgs` (`List<string>?`), saved in `outbox.json`.
  - `bool TransferOutbox.Refuse(string id, Refusal refusal)`. The old `Refuse(string, string)` stays and stores no key.
  - `EconomyClient.MessageOf` returns `RpcErrorDetail.Read(ex).Message`.

- [ ] **Step 1: Write the tests:**
  - `ReadGivesKeyAndArgs`: a trailer with key `Live.OwnSession` and args `["Hosta"]` reads back.
  - `ReadWithoutKeyGivesTheMessage`: an old-server trailer gives `Key == null`.
  - `ReadWithoutTrailerGivesStatus`: no trailer gives `"FailedPrecondition: x"`.
  - `RefusalKeyAndArgsSurviveSaveAndLoad`: `Refuse(id, refusal)`, then a new `TransferOutbox` on the same path, gives the same `Refusal`, `RefusalKey` and `RefusalArgs`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The replay path (`Attempt`, line 239) calls `Refuse(id, RpcErrorDetail.Read(ex))`.
- [ ] **Step 4: Run the tests and the full suite.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Keep a refusal's key and arguments in the client and the outbox`.

### Task 6: Mod refusal texts

**Files:**
- Create: `src/Rimlink.Client/UI/RefusalText.cs`
- Modify: every place in `src/Rimlink.Client` that shows a refusal:
  - `Dialog_Chat.cs:78`, `LoginFlow.cs:139/145/218/224`, `Dialog_Economy.cs` (5 sites), `ShopDialogs.cs:118/124`;
  - `Incoming.cs:115`, `Outgoing.cs:210/233/358/371`;
  - `RaidIncoming.cs:64/99/187`, `RaidOutgoing.cs:134/140`, `RaidResolver.cs:276/299`;
  - `VisitSession.cs:70/142`, `SaveSync.cs:99`, `RimlinkShop.cs:168`;
  - `Patch_WorldObjectsHolder_Add.cs:40`, `Patch_WorldGenerator_GenerateWorld.cs:40/41`.

**Interfaces:**
- Consumes: Task 5 `Refusal`, `RpcErrorDetail.Read`, `OutboxEntry.RefusalKey/RefusalArgs`.
- Produces:
  - `public static string RefusalText.Of(RpcException ex)`;
  - `public static string RefusalText.Of(Refusal r)`: `("Rimlink.Refusal." + r.Key).Translate(args)` when `r.Key` is set and `("Rimlink.Refusal." + r.Key).CanTranslate()`, else `r.Message`;
  - `public static string RefusalText.Of(OutboxEntry e)`.

- [ ] **Step 1: Implement `RefusalText`.** Arguments go in as `NamedArgument`s. A key with fewer args than placeholders must not throw: fall back to `r.Message` when `Translate` throws a `FormatException`.
- [ ] **Step 2: Switch every listed site** to `RefusalText.Of(...)`.
- [ ] **Step 3: Build the mod** with `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-s1\RimWorld'`. Expected: `Ошибок: 0`. Then run the full suite.
- [ ] **Step 4: Commit** with message `Show server refusals in the player's language`.

### Task 7: Client-core chat cards

**Files:**
- Create: `src/Rimlink.Client.Core/Chat/ChatCards.cs`
- Test: `tests/Rimlink.Client.Tests/Chat/ChatCardsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record CardSpan(int Start, int Length, int Tile)`;
  - `public static IReadOnlyList<CardSpan> ChatCards.Parse(string text)`: at most 3 spans, in order;
  - `public static string ChatCards.Token(int tile)` → `"[[tile:" + tile + "]]"`.

- [ ] **Step 1: Write the tests:**
  - `ParsesOneToken`: `"go [[tile:85538]] now"` gives one span with Start 3, Length 14, Tile 85538.
  - `ParsesAtMostThree`: four tokens give three spans.
  - `MalformedTokensAreText`, each giving 0 spans:
    - `"[[tile:]]"`, `"[[tile:-5]]"`, `"[[tile:99999999999]]"` (outside int);
    - `"[[tile:12"`, `"[tile:12]"`, `"[[Tile:12]]"`.
  - `TokenRoundTrips`: `Parse(Token(7))[0].Tile == 7`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** with a compiled regex `\[\[tile:(\d{1,9})\]\]` and `int.TryParse`.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Parse tile cards in chat text`.

### Task 8: Client-core ping event

**Files:**
- Modify: `src/Rimlink.Client.Core/State/SharedState.cs`
- Test: `tests/Rimlink.Client.Tests/State/SharedStateTests.cs`

**Interfaces:**
- Produces:
  - `public event Action<PingEvent>? Pinged` on `SharedState`;
  - `public PingEvent? LastPing { get; private set; }`, set in `Apply` for `ServerEvent.BodyOneofCase.Ping`.

- [ ] **Step 1: Write `PingIsKeptWithoutAGame`.** `new SharedState().Apply(new ServerEvent { Ping = ... })` raises `Pinged` once and sets `LastPing`. `Chat.Messages` stays empty.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement** the `case` in `SharedState.Apply`.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Raise received pings from the shared state`.

### Task 9: Mod pings

**Files:**
- Create:
  - `mod/Defs/KeyBindingDefs/Rimlink_KeyBindings.xml` (`Rimlink_Ping`, default `G`, category `MainTabs` or a new `Rimlink` category);
  - `src/Rimlink.Client/Game/Pings/PingSender.cs` (target resolution, gesture);
  - `src/Rimlink.Client/Game/Pings/PingMarkers.cs` (markers, receive);
  - `src/Rimlink.Client/Game/Patches/Patch_MapInterface_MapInterfaceUpdate_Pings.cs`;
  - `src/Rimlink.Client/Game/Patches/Patch_WorldInterface_WorldInterfaceUpdate_Pings.cs`.
- Modify:
  - `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (OnGUI key check, subscribe `State.Pinged`);
  - both keyed XMLs;
  - `tools/Rimlink.BridgeTools/PlayTools.cs` (`rimlink/ping`), `StatusTools.cs` (`lastPing`);
  - `AGENTS.md` (tools table).

**Interfaces:**
- Consumes: Task 1 (`Chat.PingAsync`), Task 8 (`Pinged`), Task 6 (`RefusalText.Of`).
- Produces:
  - `PingSender.Counterpart()` → `PlayerRef?`. In order: the live peer (`MirrorMode.Session.Host` as guest, `LiveHost.Session.Guest` as host), the visit host (`VisitSession.HostId/HostName` while `InVisit`), else null.
  - `PingSender.Send(long toPlayerId, int tile, IntVec3? cell)`;
  - `PingMarkers.Show(int tile, IntVec3? cell, string fromName, bool sound)`;
  - BridgeTools `rimlink/ping {to, tile, x, z}`. `to` is a player name or empty for the counterpart; `x = -1` means a tile ping.

- [ ] **Step 1: Write the gesture.** Key down on the map gives the cell under the mouse (`UI.MouseCell()`) and `map.Tile`. On the world map it gives `GenWorld.MouseTile()` with no cell. If `Counterpart()` exists and the view is the live mirror, the host's live map or the visit copy, ping the counterpart. Otherwise open a `FloatMenu` of online players (`State.Players`, without self), with the last `VisitStarted` visitor or the counterpart first.
- [ ] **Step 2: Implement `Show`.** One marker per ping, kept in a list with `DateTime` expiry `now + 8 s`. Map: a pulsing ring (`GenDraw.DrawCircleOutline`, radius from a sine over real time) while `Find.CurrentMap.Tile == tile`. World: a ring on the tile center (`Find.WorldGrid.GetTileCenter`) via `GenDraw` in the world camera layer. Message: `"Rimlink.Ping.Received".Translate(fromName)`, `LookTargets` on the cell (`GlobalTargetInfo(cell, map)`) or on the tile (`GlobalTargetInfo(tile)`), `MessageTypeDefOf.NeutralEvent`. Sound: `SoundDefOf.TinyBell` when `sound`.
- [ ] **Step 3: Implement the send side.** `MainThread.Post` the result; a failure shows `Messages.Message(RefusalText.Of(ex), MessageTypeDefOf.RejectInput)`. The sender shows its own marker with `sound: false`.
- [ ] **Step 4: Implement the receive side.** `Pinged` posts to the main thread and calls `Show(...)` only when `Current.ProgramState == ProgramState.Playing`. Otherwise the ping is dropped (Review Focus 2).
- [ ] **Step 5: Add the guard.** No ping while `GUIUtility.keyboardControl != 0` or `Find.WindowStack.WindowsPreventCameraMotion` (Review Focus 1).
- [ ] **Step 6: Build.** Run `Build-ClientMod.ps1` and `Build-BridgeTools.ps1` without `-InstallTo`, then the full suite. Expected: green, `Ошибок: 0`.
- [ ] **Step 7: Commit** with message `Ping a cell or a tile for the other player`.

### Task 10: Mod chat cards

**Files:**
- Modify: `src/Rimlink.Client/UI/Dialog_Chat.cs`, both keyed XMLs

**Interfaces:**
- Consumes: Task 7 `ChatCards.Parse`/`Token`.
- Produces: the chat window draws per-message rows, and messages with cards get link buttons.

- [ ] **Step 1: Add the "Место" button** (`Rimlink.Chat.Place`) between the target button and the input. It appends `ChatCards.Token(tile)` to `input`. The tile comes from `Find.WorldSelector.SelectedTile` or the first selected world object's `Tile`; with nothing selected and a map shown, from `Find.CurrentMap.Tile`.
- [ ] **Step 2: Add the card labels.** `CardLabel(int tile)`:
  - «Поселение {name} ({owner})» from `Find.WorldObjects.SettlementAt(tile)` (`Rimlink.Chat.CardSettlement`);
  - else «тайл {n}, {biome}» when `tile < Find.WorldGrid.TilesCount` (`Rimlink.Chat.CardTile`);
  - else «тайл {n}» (`Rimlink.Chat.CardTileBare`).

  In `Format`, each span's token is replaced by `<color=#8cc8ff>label</color>` (Review Focus 4).
- [ ] **Step 3: Draw per message.** Draw the list per message instead of one joined label: `Text.CalcHeight` per row. A message with spans gets a row of buttons (one per span, 120×22) under its text. A click runs `CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(tile))`, or selects `SettlementAt(tile)` when present. Keep the auto-scroll on new messages.
- [ ] **Step 4: Build.** Build the mod, then run the full suite.
- [ ] **Step 5: Commit** with message `Insert and follow tile cards in chat`.

### Task 11: Client-core tempo rules

**Files:**
- Create: `src/Rimlink.Client.Core/Live/TempoRules.cs`
- Test: `tests/Rimlink.Client.Tests/Live/TempoRulesTests.cs`

**Interfaces:**
- Produces: `public sealed class TempoRules`. Speeds are ints 0..3 (0 = paused).
  - Configuration: `int GuestPauses` (default 10), `TimeSpan GuestPauseLength` (default 60 s).
  - State: `int HostWish`, `int GuestWish`, `bool GuestPaused`, `int GuestPausesUsed`, `DateTime GuestPauseUntil`, `bool WaitingPause`, `bool LetterPause`, `string? LetterLabel`.
  - Thresholds: `LetterPause HostThreshold`, `LetterPause GuestThreshold`.
  - Derived: `int Effective`, `int PausedBy` (0 none, 1 host or waiting, 2 guest, 3 letter).
  - Methods:
    - `void Begin()`;
    - `void ToggleHostPause()`, `void SetHostWish(int speed)`. Both lift a letter pause; a host pause toggle while letter-paused only lifts it;
    - `string? SetGuestPause(bool on, DateTime now)`, `string? SetGuestSpeed(int speed, DateTime now)`;
    - `bool OnThreatLetter(bool major, string label)`: true when it paused;
    - `void Tick(DateTime now)`.
  - The current `LiveTempo` semantics are kept for everything not about letters.

- [ ] **Step 1: Write the tests:**
  - `EffectiveIsTheSlowerWish`: host 3, guest 1 → 1. Any pause → 0.
  - `StricterThresholdDecides`: host NEVER, guest MAJOR. A major letter pauses (`PausedBy == 3`, `LetterLabel == "Raid"`); a small one does not. With host ANY, a small one pauses.
  - `ManyLettersOnePause`: three letters, then one `ToggleHostPause` → `LetterPause == false`, `Effective == HostWish`.
  - `GuestSpeedLiftsALetterPauseFree`: letter-paused, `SetGuestSpeed(2)` → no letter pause and `GuestPausesUsed` unchanged.
  - `GuestPauseDuringLetterPauseIsANoOp`: `SetGuestPause(true)` returns null, `GuestPausesUsed` unchanged, `PausedBy == 3`.
  - `NoLetterPauseBeforeTheGuestArrives`: while `WaitingPause`, `OnThreatLetter` returns false.
  - `BudgetAndExpiry`: the 11th guest pause is refused («pause budget spent (10 pauses)»). A pause ends after `GuestPauseLength` in `Tick`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** with message `Move the live tempo rules into client-core with the letter pause`.

### Task 12: Mod letter pause, settings and run-3 fixes

**Files:**
- Modify:
  - `src/Rimlink.Client/Game/Live/LiveTempo.cs` (wraps one `TempoRules`; `TimeSpeed` ↔ int);
  - `src/Rimlink.Client/RimlinkMod.cs` (`RimlinkSettings.LetterPause`, default `LetterPauseMajor`, saved; radio buttons in `DoSettingsWindowContents`);
  - `src/Rimlink.Client.Core/Live/LiveClient.cs` (`public LetterPause HelloLetterPause { get; set; }` put into the `Hello` at line 209);
  - `src/Rimlink.Client/Game/Live/LiveGuest.cs` (sets `HelloLetterPause` before `Open`; `SendLetterPause(LetterPause)` when the setting changes mid-session);
  - `src/Rimlink.Client/Game/Live/LiveHost.cs` (`PeerHello` reads `LetterPause` into `GuestThreshold`; `End` restores the pre-session speed when the guest never said Hello);
  - `src/Rimlink.Client/Game/Live/OrderExecutor.cs` (`Order.LetterPause` → `GuestThreshold`);
  - `src/Rimlink.Client/Game/Live/DeltaCollector.cs` (threat letters → `Messages`);
  - `src/Rimlink.Client/Game/Live/MirrorApplier.cs` (`GuestMessage` with `Letter` → `Find.LetterStack.ReceiveLetter` in the mirror);
  - `src/Rimlink.Client/UI/Live/Dialog_LiveInvite.cs` (timeout → `ClearInvite()` for the same id);
  - `src/Rimlink.Client/UI/Live/LiveHud.cs` (`PausedBy == 3` → `Rimlink.Live.StateLetterPaused` with the label);
  - both keyed XMLs;
  - `tools/Rimlink.BridgeTools/LiveTools.cs` (`rimlink/letter_pause {mode}`), `StatusTools.cs` (`live.letterPause`, `live.pausedBy`).
- Create: `src/Rimlink.Client/Game/Patches/Patch_LetterStack_ReceiveLetter_Live.cs`
- Test: `tests/Rimlink.Client.Tests/Live/LiveClientTests.cs`

**Interfaces:**
- Consumes: Task 11 `TempoRules`, Task 1 fields.
- Produces:
  - `LiveTempo.Rules` (`TempoRules`);
  - `LiveTempo.SpeedBeforeSession` (`TimeSpeed`), recorded in `Begin`;
  - `LiveTempo.Reset(bool restoreSpeed)`.

- [ ] **Step 1: Write `GuestHelloCarriesTheLetterPause`** in `LiveClientTests`. A guest `LiveClient` with `HelloLetterPause = LetterPauseAny` opens; the host's stream receives `PeerHello.LetterPause == LetterPauseAny`. Use the existing two-client helpers.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement the client-core part** (`HelloLetterPause`), run the test (PASS), then do the mod parts:
  - **patch:** the postfix on `LetterStack.ReceiveLetter(Letter, string, int, bool)` runs only on the host while `LiveHost.Active && LiveHost.GuestHere`. It calls `LiveTempo.OnThreatLetter(let.def == LetterDefOf.ThreatBig, let.Label)` for `ThreatBig`/`ThreatSmall` and queues a `GuestMessage { Label, Text, LetterDef, Look, Letter = true }` for the next delta;
  - **speed restore** (proposal: host stays paused after a start abort);
  - **invite clear** (proposal: stale invite after a timeout).
- [ ] **Step 4: Build.** Build the mod and the companion, then run the full suite. Expected: green.
- [ ] **Step 5: Commit** with message `Pause a live session on threat letters and fix the start-abort speed and the stale invite`.

### Task 13: Speed votes

**Files:**
- Modify:
  - `src/Rimlink.Client.Core/Live/DeltaModel.cs` (`MirrorMap.HostWish`, `MirrorMap.GuestWish`);
  - `src/Rimlink.Client.Core/Live/DeltaCodec.cs` (`Header` and `Apply` copy them);
  - `src/Rimlink.Client/Game/Live/DeltaCollector.cs` (fill from `LiveTempo.Rules`);
  - `src/Rimlink.Client/Game/Live/MirrorMode.cs` (`HostWish`, `GuestWish` from the delta);
  - both keyed XMLs;
  - `tools/Rimlink.BridgeTools/StatusTools.cs` (`live.hostWish`, `live.guestWish`).
- Create: `src/Rimlink.Client/Game/Patches/Patch_TimeControls_DoTimeControlsGUI_Live.cs`
- Test: `tests/Rimlink.Client.Tests/Live/DeltaCodecTests.cs`

**Interfaces:**
- Consumes: Task 11, Task 12.
- Produces: `MirrorMode.HostWish`, `MirrorMode.GuestWish` (int 0..3).

- [ ] **Step 1: Write `WishesTravelInTheHeader`.** `Diff(prev, next)` with `next.HostWish = 3, GuestWish = 1` gives frames whose `HostWish/GuestWish` are 3/1, and `Apply` writes them back into a `MirrorMap`.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement the core part.** Then the postfix on `TimeControls.DoTimeControlsGUI(Rect timerRect)`, active only during a live session. Button rects are computed as vanilla does: `TimeControls.TimeButSize`, buttons left to right over `TimeSpeed` values, skipping `Ultrafast`. It draws `Rimlink.Live.MarkHost`/`MarkGuest` (Tiny font) under the button of each wish (index 0 = paused), plus a `TooltipHandler.TipRegion` of `timerRect` with `Rimlink.Live.SpeedVotes` (host, guest, effective).
- [ ] **Step 4: Build.** Build the mod and the companion, then run the full suite.
- [ ] **Step 5: Commit** with message `Show both players' speed wishes on the time buttons`.

### Task 14: Docs and in-game run on s1

**Files:**
- Modify:
  - `docs/player-guide.md`, `docs/server-setup.md`;
  - `docs/superpowers/specs/2026-10-29-rimlink-live-design.md` (ruling rows: letter pause, speed votes);
  - `docs/manual-checklist.md` (new section «Пинги, карточки, пауза по письму (ветка `asofwar/live-ux`)»);
  - `AGENTS.md` (BridgeTools rows).

- [ ] **Step 1: Deploy to s1.** Stop both games. Run `Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-s1\RimWorld' -InstallTo 'D:\Build\rimlink-s1\RimWorld\Mods'` and `Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-s1\RimWorld'`. Start the server, both games, and `rimlink/connect` for each.
- [ ] **Step 2: Run the scenarios.** Write each result, with the log lines that prove it, into the checklist section:
  - P1: ping in a live visit, both ways — receiver message, marker, `lastPing` in `rimlink/status`;
  - P2: ping from a visit copy to the online host;
  - P3: world ping through the player list;
  - P4: typing `g` in chat sends nothing;
  - C1: card «Место» from a selected settlement and from the current map; a click goes to the tile on both clients;
  - L1: dev raid (`Actions\Incidents\Raid` or `Execute incident`) on the host in a live session with the guest at ANY and the host at NEVER → pause, `pausedBy 3`, the letter in the guest's stack, the guest lifts it, budget unchanged;
  - L2: the guest's threshold changed mid-session takes effect;
  - V1: time-button marks at host 3 / guest 1, as a screenshot on both;
  - R1: mod-mismatch abort at the start → the host's speed is back;
  - R2: invite timeout → `rimlink/status` shows no invite;
  - R3: second `Propose` → «Передача отклонена: у вас уже идёт живая сессия…» in Russian.
- [ ] **Step 3: Update the player and admin docs** and the spec rulings.
- [ ] **Step 4: Commit** with message `Record the live UX run on s1 and document pings, cards and the letter pause`.
