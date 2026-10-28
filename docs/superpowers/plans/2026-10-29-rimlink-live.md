# Rimlink Live Raids and Visits (3b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When both players are online, a guest controls their own pawns on the host's live map (raid or visit) through a mirrored view and relayed vanilla orders; the host's game is the only simulation.

**Architecture:** The server adds live sessions (invite, fallback to 3a, timeouts) and a frame relay between two bidirectional gRPC streams. Client-core adds `LiveClient`, a Verse-free delta codec and reconnection. The host game layer owns guest pawns, collects deltas every 6 ticks, executes orders and resolves the outcome through the 3a `RaidResolver`; the guest game layer loads the host's save through the 3a `VisitLoader` and runs it in `MirrorMode` (no local simulation of mirrored entities, orders instead of actions).

**Tech Stack:** .NET 10, Grpc.AspNetCore (bidirectional streaming), EF Core Sqlite, xUnit + `WebApplicationFactory`, Lib.Harmony 3.0.0-rc.3, RimWorld 1.7, ZstdSharp.

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-live-design.md` (on top of `2026-10-28-rimlink-raids-visits-design.md`, whose plan `2026-10-29-rimlink-raids-visits.md` must be complete first).

## Global Constraints

- Depends on 3a being merged: `RaidRules`, `RaidService.Launch/Report`, `RaidResolver`, `VisitLoader`, `VisitSession`, `MapComponent`s, `TransferOutbox` kinds, `Saves.DownloadOf`, `RaidsState`.
- Protocol only grows: new `live.proto` (service `Live`), `ErrorCode` 28–31 (`HOST_OFFLINE`, `LIVE_ACTIVE`, `LIVE_GONE`, `LIVE_DISABLED`), `ServerEvent.body` 70–75 (`LiveInvite`, `LiveStarted`, `LiveEnded`, `LiveFallback`, `LivePeerLost`, `LivePeerBack`), `LoginReply` += `repeated LiveSession pending_live_ends = 9`, `PlayerFlags` += `bool auto_accept_live = 4`, `Auth` += `rpc SetAtHome (AtHomeRequest) returns (Empty)` (`message AtHomeRequest { bool at_home = 1; }`).
- Storage: migration `Live` (`live_sessions`; `players.AutoAcceptLive`). `Session` (in-memory registry) += `AtHome`.
- Config `Live:*` defaults: `Enabled` true, `InviteSeconds` 60, `ReconnectWaitMinutes` 5, `GuestPauses` 10, `GuestPauseSeconds` 60, `MaxFrameBytes` 262144, `DeltasPerSecond` 20, `OrdersPerSecond` 30, `HeartbeatTimeoutSeconds` 10 (tests lower it to 1). Heartbeat 2 s client-side; host paused in `Waiting`.
- Relay: never interprets frames; drops a frame over `MaxFrameBytes` or over rate with a `Notice`; per-receiver queue 64 frames, overflow drops oldest `Delta` frames only.
- Guest pawns are always Real (escrow in `raids.pawns` for `LiveRaid`, `live_sessions.pawns` for `LiveVisit`); every pawn or thing movement ends as a mailbox parcel via the 3a `Report`.
- Delta cadence: host every 6 ticks (1 s on pause); guest catch-up ≤ 60 ticks per frame, `ResyncRequest` when behind by > 600 ticks; IDs in deltas are the host's (`newIds: false` on the guest).
- Game code on the main thread only; `LiveClient` queues frames, `MainThread` drains once per frame.
- TDD per task; `dotnet test Rimlink.sln` green before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no attribution lines. Branch `live` from `main` after 3a is merged. Mod/client version `0.4.0`.
- Russian + English Keyed strings for every new UI string.

## Review Focus

1. The guest orders a pawn that died in a delta still in flight: the host answers `OrderResult(ok=false, "dead")`, the guest shows the refusal and the mirror shows the corpse; no exception on either side (Task 5 host `Apply` test on the Verse-free model, Task 6 manual).
2. `ResyncRequest` arrives while the guest holds a pause: the host saves and uploads on pause, `SnapshotReady` carries the new version, the guest reloads and the pause state survives (Task 2 test `ResyncDuringGuestPauseKeepsThePause`, relay level; Task 6 manual).
3. The host saves with guest pawns on the map and then crashes: at the next host login `pending_live_ends` names the session, the host client serialises the owned pawns and reports, the guest gets `RaidReturn`; nothing is duplicated because the host's `LastOutgoingTick` is stamped by the report (Task 2 test `HostLostEndsTheSessionAndPendsTheEndForTheHost`, Task 5 step on `pending_live_ends`).
4. Two `Propose` calls in a row from the same guest, or a `Propose` while a session is running: `LIVE_ACTIVE`; the second attempt neither escrows pawns twice nor sends a second invite (Task 2 test `SecondProposeIsLiveActive`).
5. A delta references a thing absent from the guest's snapshot (spawned between the host's save and the guest's load): the guest applies `things_spawned` blobs first within a frame and ignores `things_changed` for unknown IDs with a counter; when the counter grows past 100 in a minute it requests `Resync` (Task 4 codec test `UnknownIdsAreCountedNotThrown`).

---

### Task 1: Protocol, storage, presence, options

**Files:**
- Create: `src/Rimlink.Protocol/Protos/live.proto`
- Modify: `common.proto` (ErrorCode 28–31), `events.proto` (70–75), `auth.proto` (`LoginReply.pending_live_ends`, `SetAtHome`), `raids.proto` (`PlayerFlags.auto_accept_live`), `Rimlink.Protocol.csproj`
- Modify: `Storage/Entities/Entities.cs` (`LiveSession`, `Player.AutoAcceptLive`), `RimlinkDbContext.cs`, `Hosting/ServerOptions.cs` (`LiveOptions`), `Program.cs`, `Application/Sessions/Session.cs` (`AtHome`), `Grpc/AuthGrpcService.cs` (`SetAtHome`)
- Create: `Storage/Migrations/<ts>_Live.cs`
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`, `tests/Rimlink.Server.Tests/Live/LiveOptionsTests.cs`, `tests/Rimlink.Server.Tests/Auth/PresenceTests.cs` (`SetAtHomeIsVisibleToTheRegistry`)

**Interfaces:**
- Produces proto:
  ```proto
  enum LiveKind { LIVE_RAID = 0; LIVE_VISIT = 1; }
  enum LiveState { LIVE_PROPOSED = 0; LIVE_STARTING = 1; LIVE_RUNNING = 2; LIVE_WAITING = 3; LIVE_ENDING = 4; LIVE_ENDED = 5; LIVE_FALLBACK = 6; }
  enum LiveEndReason { END_NORMAL = 0; END_GUEST_LOST = 1; END_HOST_LOST = 2; END_HOST_ABORTED = 3; END_GUEST_CANCELLED = 4; END_ADMIN = 5; }
  message LiveProposal { string client_request_id = 1; int64 host_player_id = 2; LiveKind kind = 3; PawnList pawns = 4; RaidTiming timing_fallback = 5; RaidArrival arrival = 6; int64 game_tick = 7; }
  message LiveSession { int64 id = 1; LiveKind kind = 2; PlayerRef host = 3; PlayerRef guest = 4; LiveState state = 5; int32 snapshot_version = 6; int64 raid_id = 7; LiveEndReason end_reason = 8; google.protobuf.Timestamp created_at = 9; }
  message LiveAnswer { int64 session_id = 1; bool accept = 2; }   message LiveRef { int64 session_id = 1; }   message AutoAcceptRequest { bool enabled = 1; }
  message Cell { int32 x = 1; int32 z = 2; }   message Target { oneof t { int64 thing_id = 1; Cell cell = 2; } }
  message PawnDelta { int64 thing_id = 1; Cell cell = 2; int32 rot = 3; bool drafted = 4; bool downed = 5; bool dead = 6; string job_def = 7; Target job_target = 8; int32 health_pct = 9; string mental_state_def = 10; int64 carried_thing_id = 11; Cell path_next_cell = 12; }
  message ThingSpawned { int64 thing_id = 1; Cell cell = 2; ItemBlob item = 3; PawnBlob pawn = 4; }   message ThingChanged { int64 thing_id = 1; Cell cell = 2; int32 stack_count = 3; int32 hp = 4; }
  message CellChange { Cell cell = 1; string kind = 2; string value = 3; }   message FireChange { Cell cell = 1; float size = 2; bool gone = 3; }
  message DamageEvent { int64 attacker_id = 1; int64 victim_id = 2; float amount = 3; string body_part_def = 4; string damage_def = 5; }
  message GuestMessage { string text = 1; Target look = 2; bool letter = 3; }
  message Delta { int64 tick = 1; int32 speed = 2; int32 paused_by = 3; int32 snapshot_seq = 4; repeated PawnDelta pawns = 5; repeated ThingSpawned things_spawned = 6; repeated int64 things_despawned = 7; repeated ThingChanged things_changed = 8; repeated CellChange cells = 9; repeated FireChange fires = 10; repeated DamageEvent damage = 11; repeated GuestMessage messages = 12; repeated PawnBlob guest_pawn_blobs = 13; }
  message Order { int64 seq = 1; int64 pawn_id = 2; oneof o { bool draft = 3; Cell move_to = 4; Target attack = 5; TakeJob take_job = 6; WorkPriority work_priority = 7; Schedule schedule = 8; int64 equip = 9; int64 wear = 10; int64 drop = 11; int32 set_speed = 12; bool pause = 13; } }
  message TakeJob { string job_def = 1; Target a = 2; Target b = 3; Target c = 4; int32 count = 5; bool force = 6; }   message WorkPriority { string work_def = 1; int32 priority = 2; }   message Schedule { int32 hour = 1; string assignment_def = 2; }
  message OrderResult { int64 seq = 1; bool ok = 2; string reason = 3; }
  message Hello { int32 role = 1; int32 snapshot_seq = 2; }   message SnapshotReady { int32 save_version = 1; }   message EndMsg { LiveEndReason reason = 1; }
  message LiveClientMsg { int64 session_id = 1; oneof m { Hello hello = 2; Empty heartbeat = 3; Delta delta = 4; Order order = 5; OrderResult order_result = 6; Empty resync_request = 7; SnapshotReady snapshot_ready = 8; EndMsg end = 9; } }
  message LiveServerMsg { oneof m { Delta delta = 1; Order order = 2; OrderResult order_result = 3; Empty resync_request = 4; SnapshotReady snapshot_ready = 5; EndMsg end = 6; Empty peer_lost = 7; Empty peer_back = 8; Notice notice = 9; Hello peer_hello = 10; } }
  service Live { rpc Propose (LiveProposal) returns (LiveSession); rpc Respond (LiveAnswer) returns (LiveSession); rpc Cancel (LiveRef) returns (Empty); rpc Current (Empty) returns (LiveSession); rpc Stream (stream LiveClientMsg) returns (stream LiveServerMsg); rpc SetAutoAccept (AutoAcceptRequest) returns (PlayerFlags); }
  ```
- Produces entity `LiveSession { Id, Kind, HostPlayerId, GuestPlayerId, State (LiveStateDb), RaidId long?, Pawns byte[]?, SnapshotVersion int?, CreatedAt, StartedAt?, EndedAt?, EndReason int?, ClientRequestId string? }` (unique filtered `GuestPlayerId, ClientRequestId`); `LiveOptions`; `Session.AtHome bool` set by `SetAtHome`.

- [ ] **Step 1: Failing tests** — `SchemaTests.LiveMigrationCreatesTheTable`, `LiveOptionsTests.DefaultsMatchTheSpec`/`SettingsOverrideDefaults`, `PresenceTests.SetAtHomeIsVisibleToTheRegistry` (login → `SetAtHome(true)` → `SessionRegistry.Find(playerId).AtHome` true; logout clears).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~LiveOptions|FullyQualifiedName~Presence"` — Expected: FAIL.
- [ ] **Step 3: Implement**; migration `dotnet ef migrations add Live`.
- [ ] **Step 4: Run** — Expected: PASS; solution green.
- [ ] **Step 5: Commit** — `git commit -m "Add the live session protocol, storage, presence and options"`.

---

### Task 2: Live sessions — propose, respond, fallback, cancel, timeouts, pending ends

**Files:**
- Create: `src/Rimlink.Server/Application/Live/LiveSessionService.cs`, `.../Live/LiveTimeoutService.cs` (every 5 s), `src/Rimlink.Server/Grpc/LiveGrpcService.cs` (unary calls; `Stream` in Task 3)
- Modify: `Program.cs`, `Grpc/AuthGrpcService.cs` (`LoginReply.PendingLiveEnds`), `Application/Raids/RaidService.cs` (`Launch` accepts `controller`; `Report` closes the live session when `raid_id` matches), `Application/Admin/*` (`live list|end`), `Rest/AdminEndpoints.cs`
- Test: `tests/Rimlink.Server.Tests/Live/LiveSessionTests.cs`, `tests/Rimlink.Server.Tests/Live/LiveEndTests.cs`, `tests/Rimlink.Server.Tests/Support/LiveFixtures.cs`

**Interfaces:**
- Consumes: Task 1; `RaidRules.CheckLaunch`, `RaidService.Launch/Report`, `SessionRegistry` (`AtHome`), `MailboxService`, `EventBus`, `AuditLog`.
- Produces `LiveSessionService(db, raids, rules, registry, events, options)`:
  - `Task<LiveSession> Propose(long guestId, LiveProposal p)` — gate (`LIVE_DISABLED`), `SELF_TRADE`, `RECIPIENT_UNKNOWN`, `HOST_OFFLINE` (no session or `!AtHome`), `LIVE_ACTIVE` (guest or host has a session not Ended/Fallback), for `LIVE_RAID`: `RaidRules.CheckLaunch` and a `raids` row via `RaidService.Launch(controller: Live, timing: p.TimingFallback)` in state Queued (escrow + `LastOutgoingTick` stamp); for `LIVE_VISIT`: pawns into `Pawns`, stamp as well; idempotent by `ClientRequestId`; `AutoAcceptLive` → `Starting` + `LiveStarted` to both; else `Proposed` + `LiveInvite` to the host with `InviteSeconds`.
  - `Task<LiveSession> Respond(long hostId, long sessionId, bool accept)` — Proposed → Starting (`LiveStarted`) or → Fallback (`Fallback(session)`); `LIVE_GONE` otherwise.
  - `Task Cancel(long playerId, long sessionId)` — guest before Starting → Fallback with reason `END_GUEST_CANCELLED` is *not* a fallback: the raid row is Cancelled and pawns returned (`RaidReturn`); host in Running/Waiting → `Ending(END_HOST_ABORTED)` (host client degrades at once).
  - `Task Fallback(LiveSession s)` — `LIVE_RAID`: the raid row stays Queued with `controller = Ai` (the 3a pipeline takes over); `LIVE_VISIT`: pawns returned as `RaidReturn` parcel, guest gets `LiveFallback` (client then offers the 3a copy visit).
  - `Task MarkRunning(sessionId)` (Task 3 on guest `Hello`), `Task MarkWaiting(sessionId, lostPlayerId)`, `Task MarkEnding(sessionId, reason)` (`LiveEnded` to both; for `END_HOST_LOST` nothing more until the host returns), `Task Ended(sessionId)` (called from `RaidService.Report` for `LiveRaid`, or from a visit report path: `Report` with `raid_id == 0` and `session_id` — add `int64 live_session_id = 6` to `RaidReport`; outcomes become `RaidReturn` parcels, loot ignored for visits).
  - `Task<List<LiveSession>> PendingEnds(long hostId)` (Ending sessions of the host) — surfaced in `LoginReply`.
  - `LiveTimeoutService`: Proposed older than `InviteSeconds` → `Fallback`; Waiting older than `ReconnectWaitMinutes` → `MarkEnding(GuestLost|HostLost)`.
- `LiveFixtures`: `LiveServer()` (= `RaidServer()` + `Live:InviteSeconds` 2), `Connected(fixture, login)` with `SetAtHome(true)`, `Propose(host, kind, pawns = 2)`.

- [ ] **Step 1: Failing tests**, `LiveSessionTests.cs`: `ProposeInvitesAnOnlineHost` (bob at home → Proposed, bob stream `LiveInvite`, nova `LastOutgoingTick` stamped, raid row Queued controller Live), `ProposeToAnOfflineOrAwayHostIsHostOffline`, `AutoAcceptStartsAtOnce`, `AcceptStartsDeclineFallsBack` (decline → `LiveFallback` to nova, raid row controller Ai, bob `Pending` lists it), `InviteTimeoutFallsBack` (2 s), `SecondProposeIsLiveActive`, `VisitFallbackReturnsThePawns`, `GuestCancelBeforeStartReturnsThePawns`, `HostAbortEnds`, `PeacefulAndCooldownRulesApply`. `LiveEndTests.cs`: `ReportForALiveRaidEndsTheSessionAndParcelsTheGuest`, `VisitReportReturnsAllLivingPawnsAndIgnoresLoot`, `HostLostEndsTheSessionAndPendsTheEndForTheHost` (`MarkEnding(HostLost)` → bob's next `LoginReply.PendingLiveEnds` has it; his `Report` → Ended).
- [ ] **Step 2: Run** `--filter FullyQualifiedName~Live` — Expected: FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** — Expected: PASS; solution green.
- [ ] **Step 5: Commit** — `git commit -m "Add live sessions with invites, fallback, timeouts and pending ends"`.

---

### Task 3: Frame relay

**Files:**
- Create: `src/Rimlink.Server/Application/Live/LiveRelay.cs`, `.../Live/FrameLimiter.cs`, `.../Live/LiveMetrics.cs`
- Modify: `Grpc/LiveGrpcService.cs` (`Stream`), `Program.cs`
- Test: `tests/Rimlink.Server.Tests/Live/LiveRelayTests.cs`

**Interfaces:**
- Produces `LiveRelay` (singleton): `Channel Attach(long sessionId, long playerId, Role role)` (replaces an existing channel of the same player: old one completed with `End(END_GUEST_CANCELLED)`-free "replaced" close; the peer gets `peer_back` if it was in `Waiting`), `void Forward(long sessionId, long fromPlayerId, LiveClientMsg msg)` (route to the peer's bounded queue: capacity 64; on overflow drop the oldest queued `Delta`; `Order`, `OrderResult`, `Hello`, `SnapshotReady`, `ResyncRequest`, `End` are never dropped), `void Detach(sessionId, playerId)` → peer gets `peer_lost`, `LiveSessionService.MarkWaiting`; a `Hello` from the guest → `MarkRunning`; `End` → `MarkEnding(reason)` and both channels completed.
- `FrameLimiter(options)`: `bool Allow(kind, size, now)` — size ≤ `MaxFrameBytes`, token buckets `DeltasPerSecond`/`OrdersPerSecond`; a refused frame is answered with `Notice` on the sender's channel.
- Heartbeat: a channel without any frame for 10 s is detached by `LiveTimeoutService` (uses `LastFrameAt` on the channel).
- `LiveMetrics`: `rimlink_live_sessions_total{kind,end_reason}`, `rimlink_live_frames_total{dir}`, `rimlink_live_active`.

- [ ] **Step 1: Failing tests**, `LiveRelayTests.cs` (two real streams over `ServerFixture`): `DeltaReachesOnlyThePeer`, `GuestHelloMarksRunningAndHostSeesPeerHello`, `OversizedFrameIsDroppedWithANotice` (300 KB delta), `TooManyDeltasAreDropped` (25 in one second → at least one `Notice`, peer got ≤ 20), `OverflowDropsOldDeltasNotOrders` (host sends 100 deltas + 1 order while the guest does not read → guest then reads the order and ≤ 64 frames), `HeartbeatTimeoutMarksWaitingAndReconnectResumes` (`Live:HeartbeatTimeoutSeconds` 1 → guest stops → host `peer_lost` + session Waiting → guest reattaches → host `peer_back`, Running), `WaitingExpiresToEnding` (`Live:ReconnectWaitMinutes` 0: expires on the next timeout sweep), `ResyncDuringGuestPauseKeepsThePause` (guest `resync_request` while a `Delta{paused_by=guest}` was last → host receives the request, session stays Running).
- [ ] **Step 2: Run** `--filter FullyQualifiedName~LiveRelay` — Expected: FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** — Expected: PASS; solution green.
- [ ] **Step 5: Commit** — `git commit -m "Relay live session frames between host and guest with limits"`.

---

### Task 4: Client-core — LiveClient and delta codec

**Files:**
- Create: `src/Rimlink.Client.Core/Live/LiveClient.cs`, `.../Live/FrameQueue.cs`, `.../Live/DeltaModel.cs` (Verse-free model: `MirrorPawn`, `MirrorThing`, `MirrorMap` dictionaries), `.../Live/DeltaCodec.cs` (`Diff(prev, next) → Delta[]` split by size; `Apply(MirrorMap, Delta)` returns `UnknownIds` count), `.../Live/LiveState.cs`
- Modify: `Net/RimlinkConnection.cs` (`LiveApi`), `State/SharedState.cs` (`Live`, events 70–75), `Economy/TransferOutbox.cs` (`OutboxKind.Propose`)
- Test: `tests/Rimlink.Client.Tests/Live/LiveClientTests.cs`, `tests/Rimlink.Client.Tests/Live/DeltaCodecTests.cs`

**Interfaces:**
- `LiveClient(RimlinkConnection, ILog)`: `Task<LiveSession> Propose(...)`, `Respond`, `Cancel`, `Current`, `SetAutoAccept`; `Task Open(long sessionId, Role role, int snapshotSeq, CancellationToken)` (starts the stream, sends `Hello`, heartbeat every 2 s, reconnects with `ReconnectPolicy` up to 5 min, raises `PeerLost/PeerBack/Ended`); `void Send(LiveClientMsg)` (bounded outgoing queue 256; `Order`/`Delta` over the local rate are dropped with a log line); `FrameQueue Incoming` (`TryDequeue`, `Count`); `event Action<LiveEndReason> Ended`.
- `DeltaCodec.Diff(MirrorMap prev, MirrorMap next, int maxBytes) → List<Delta>`; `DeltaCodec.Apply(MirrorMap map, Delta d) → ApplyResult { int UnknownIds }` applies `things_spawned` before `things_changed`/`pawns`.
- `LiveState`: `LiveSession? Current`, `PendingInvite`, `Flags.AutoAcceptLive`, events.

- [ ] **Step 1: Failing tests** — `DeltaCodecTests`: `DiffThenApplyReproducesTheMap` (random map of 500 pawns/things, 50 changes), `LargeDiffIsSplitUnderMaxBytes`, `UnknownIdsAreCountedNotThrown`, `SpawnedBeforeChangedWithinAFrame`; `LiveClientTests` (real server): `OpenSendsHelloAndHeartbeats`, `OrdersReachTheHostAndResultsComeBack`, `ReconnectAfterADropResumesTheSession` (server `Live:HeartbeatTimeoutSeconds` 1; close the channel transport → reconnect → `PeerBack` seen by the host), `ProposeEntryReplaysOnce` (outbox kind `Propose`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests --filter FullyQualifiedName~Live` — Expected: FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** — Expected: PASS; solution green.
- [ ] **Step 5: Commit** — `git commit -m "Add the live client, frame queue and delta codec"`.

---

### Task 5: Game, host side — ownership, spawning, delta collection, orders, tempo, outcome

**Files:**
- Create: `src/Rimlink.Client/Game/Live/LiveHost.cs`, `.../Live/MapComponent_LiveOwners.cs`, `.../Live/DeltaCollector.cs` (Verse → `MirrorMap` snapshot + event hooks), `.../Live/OrderExecutor.cs`, `.../Live/LiveRules.cs`, `.../Live/LiveTempo.cs`, `src/Rimlink.Client/Game/Patches/Patch_Live_HostHooks.cs` (`GenSpawn.Spawn`, `Thing.DeSpawn`, `Thing.TakeDamage`, `Pawn.Kill`, `Fire.Tick` postfixes feeding `DeltaCollector` when a session runs; `Selector.Select` / `Pawn.GetGizmos` ownership guards), `src/Rimlink.Client/UI/Live/Dialog_LiveInvite.cs`, `.../UI/Live/LiveHud.cs`
- Modify: `RimlinkGameComponent.cs` (`LiveHost.Tick()`, invite handling, `pending_live_ends` on `FinalizeInit`, presence `SetAtHome` on map enter/leave), `Game/Raids/RaidResolver.cs` (`ResolveNow(ownerId)` entry for live ends; visit mode: all living → `FLED`, `inventory.DropAll`), `Game/SaveSync.cs` (`live-snapshot` reason, wait helper), `Languages`

**Interfaces:**
- `LiveHost.Begin(LiveSession s, PawnList pawns)`: spawn guests (`DeserializePawn(newIds: true)`; raid → `RimlinkFactions.Raiders`, no lord; visit → `Faction.OfPlayer` + owner mark; `arrival` edge/pods), `MapComponent_LiveOwners.Mark(pawn, guestId)`, pause, `TriggerSave("live-snapshot")` → wait upload → `LiveClient.Send(SnapshotReady(version))`, `Open(session, Host)`; on guest `Hello` → unpause, `MarkRunning` is server-side.
- `LiveHost.Tick()`: every 6 ticks `DeltaCollector.Collect()` → `DeltaCodec.Diff` → `Send`; every 10 s full scan + `guest_pawn_blobs`; drains incoming: `Order` → `OrderExecutor.Apply` → `OrderResult`; `ResyncRequest` → snapshot again; `PeerLost` → `LiveTempo.WaitingPause(ReconnectWaitMinutes)`; `PeerBack` → resume; `Ended(reason)` → `End(reason)`.
- `OrderExecutor.Apply(Order o) → OrderResult`: ownership check (`LiveOwners.OwnerOf(pawn) == guestId`), dead/downed → refusal, `LiveRules.Forbidden(kind, jobDef)`, then the vanilla calls from spec §5; `SetWorkPriority`/`Schedule` only for visits.
- `LiveTempo`: `Effective = min(host, guest)` applied to `TickManager.CurTimeSpeed` via a prefix on the setter that stores the host's wish separately; guest pause counter and 60 s auto-unpause; `WaitingPause` not cancellable except by `Abort`.
- `LiveHost.End(reason)`: `RaidResolver.ResolveNow(guestId, visit: kind == Visit)` → report through outbox → `TriggerSave("live-ended")`; degrade on `GuestLost`: raid → `LordMaker.MakeNewLord(... LordJob_AssaultColony ...)` over the owned pawns and hand over to the 3a `ActiveRaids`; visit → owned pawns despawn and `ResolveNow`.
- `pending_live_ends` at login: `RimlinkGameComponent.FinalizeInit` → for each: owned pawns on the map → `ResolveNow` → report.

- [ ] **Step 1: Build check** — mod builds.
- [ ] **Step 2: In-game (two clients, `-savedatafolder`)** — host accepts an invite, guests spawn, `Delta` frames in the log every 6 ticks (`live: sent N frames`), orders from a stub guest (use the Task 4 `LiveClient` from a test console or the guest build of Task 6) execute (`order ok`), host abort → report → guest's mailbox has `RaidReturn`.
- [ ] **Step 3: Commit** — `git commit -m "Host side of live sessions: ownership, deltas, orders, tempo, outcome"`.

---

### Task 6: Game, guest side — MirrorMode, input, orders, session UI

**Files:**
- Create: `src/Rimlink.Client/Game/Live/LiveGuest.cs`, `.../Live/MirrorMode.cs`, `.../Live/MirrorApplier.cs` (`Delta` → Verse: positions with interpolation, flags, spawns/despawns with `newIds: false`, cells, fires, damage effects), `src/Rimlink.Client/Game/Patches/Patch_Live_Mirror.cs` (`TickManager.TickManagerUpdate` prefix, `Pawn.Tick` prefix, `Fire.Tick`/`Plant.TickLong`/`ThingWithComps.TickLong` prefixes, `TickManager.CurTimeSpeed` setter, `FloatMenuMakerMap` postfix replacing actions, `Pawn.GetGizmos` postfix replacing draft/attack/equip, `Selector` guard, `Designator`/zone/bill gizmo hiding), `src/Rimlink.Client/UI/Live/Dialog_LiveWait.cs`
- Modify: `Game/Visits/VisitSession.cs` + `VisitLoader.cs` (live variant: no re-faction for a raid guest's own pawns — they are already in the snapshot as the host spawned them; owner mark from the snapshot's `MapComponent_LiveOwners`), `Game/Raids/RaidOutgoing.cs` (`Live raid`/`Live visit` options → `LiveGuest.Propose`), `UI/Overlay.cs` (session HUD: tick lag, speed, pause counter, "End"), `Languages`

**Interfaces:**
- `LiveGuest.Propose(hostId, kind, pawns, timingFallback, arrival)`: `Outgoing.Run` pipeline with `LiveProposal` (outbox kind `Propose`); on `Proposed` → `Dialog_LiveWait` (cancel → `Cancel`); on `LiveStarted` → `VisitSession.BeginLive(session)` (download `snapshot_version` by the session ticket, load with `MirrorMode.Pending = session`); on `LiveFallback` → message, raid: nothing more (3a pipeline), visit: offer the 3a copy visit.
- `MirrorMode.Enter(session)` after load: patches active, `LiveClient.Open(session, Guest, snapshotSeq)`, unpause only when deltas arrive; `MirrorApplier.Apply(Delta)` once per frame; `Exit(reason)`: `Open` closed, temp save deleted, own save reloaded; `ResyncRequest` on lag > 600 ticks or unknown-id counter > 100/min → wait `SnapshotReady` → re-download and reload keeping the session.
- Input mapping per spec §4; `OrderResult` refusals as messages; HUD shows lag and the pause budget.

- [ ] **Step 1: Build check** — mod builds.
- [ ] **Step 2: In-game (two clients)** — full live raid: invite accepted, guest sees the host's map with its own raiders, drafts, moves, attacks; host colonists shoot back; guest pawn downed → mirror shows it; guest pause twice → host paused with "guest" indicator; guest closes the game → host waits 5 min (set `Live:ReconnectWaitMinutes` 1 for the test) → degrades to an AI raid → resolves → guest's mailbox has `RaidReturn` + `Loot`. Live visit: guest pawn hauls by work priority, buys a shop item, ends the visit; pawns return intact.
- [ ] **Step 3: Commit** — `git commit -m "Guest side of live sessions: mirror mode, orders and session UI"`.

---

### Task 7: UI, docs, checklist, version

**Files:**
- Modify: `UI/Economy/Dialog_Economy.cs` (settings: `Auto-accept live sessions`; Raids tab shows live sessions), `mod/Languages/*`, `mod/About/About.xml` (`0.4.0`), `ClientInfo.cs`, `docs/player-guide.md` (section 7 "Live raids and visits"), `docs/server-setup.md` (`Live:*`, admin, metrics), `deploy/appsettings.example.json`, `README.md`, `docs/manual-checklist.md` (section "Живые сессии")

- [ ] **Step 1: Implement** UI and docs.
- [ ] **Step 2: Manual checklist** on two clients: every line of spec §7 (raid, visit, guest drop with/without return, host drop + `pending_live_ends`, resync, decline → 3a fallback, pause budget).
- [ ] **Step 3: Run** `dotnet test Rimlink.sln` green; mod builds.
- [ ] **Step 4: Commit** — `git commit -m "Add the live session UI, documentation and checklist; version 0.4.0"`.
