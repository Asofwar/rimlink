# Rimlink Relations on Return Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pawn of this colony that leaves the game for good and later comes back (put back after a refusal, an unsold lot, raid survivors, guests after a live session, a gift back, a returned captive) gets its direct relations back on both sides.

**Architecture:** A Verse-free relation book in client-core (`RelationBook`: entries keyed by a pawn fingerprint, edges by thing id, restore rules) is unit-tested; the mod wraps it in a saved `GameComponent` (`RelationLedger`) that records a pawn's relations in `PawnExit.Leave` and replays them in `RelationLedger.Rejoin` at every homecoming site, through a Verse adapter of the book's `IRelationWorld`.

**Tech Stack:** .NET 10, xUnit, Lib.Harmony 3.0.0-rc.3 (`AccessTools` for one private field), RimWorld 1.7 (`Assembly-CSharp.dll`).

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-relations-on-return-design.md`

## Global Constraints

- Protocol, server, storage and `ThingBlobs.cs` do not change (the ideo-repoint session edits `ThingBlobs.DeserializePawn`; its new optional parameter leaves every call site here valid).
- `src/Rimlink.Client.Core` keeps no `Verse`/`RimWorld` reference; game objects are touched on the main thread only.
- Fingerprint key (spec §2): `"<raceDef>|<gender>|<birthAbsTicks>|<fullName>"` from `pawn.def.defName`, `pawn.gender`, `pawn.ageTracker.BirthAbsTicks`, `pawn.Name?.ToStringFull ?? ""`.
- Restored: every non-implied `DirectPawnRelation` with its original `startTicks`; not restored: memories, roles, ownership, master, ideo/policies.
- `Record`/`Rejoin` never throw into the caller: `Log.Error("[Rimlink] …")` and continue; each restore and each skip is one `RimlinkLog.Instance.Info` line.
- Nothing runs while `VisitSession.InVisit` (the loaded game is another colony).
- No new user-facing strings (log lines only): no `Languages` change. No version bump (branch on `polish`).
- The shared game setup (GABS `rimworld-host`/`rimworld-guest`, `D:\SteamLibrary\steamapps\common\RimWorld\Mods\Rimlink`, server on 25560, `D:\Build\rimlink-gabs`) is reserved by session `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`: build without `-InstallTo`, send it the branch and the scenarios for every in-game check.
- `dotnet test Rimlink.sln -nologo -v q` green and the mod building before every commit; commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no trailers. Branch `asofwar/cool-austin-217d7c` (holds the spec commit, based on `polish`).

## Review Focus

1. The returned pawn's `thingIDNumber` is changed after `DeserializePawn` already ran `SetFaction`/ideo/components: nothing may have hashed it by id yet (`Thing` equality is by id). Expected: the pawn spawns, joins the colonist bar, is selectable, and a save + reload logs no duplicate-id or unresolved-reference error for it (Task 2 step 4, scenario D).
2. A mate died while the pawn was away (corpse in a grave, or a dead world pawn): the lookup must still find it and the relation comes back with the dead pawn, as vanilla keeps it (Task 2 adapter `Index`, scenario C).
3. A save and reload between leaving and returning, including a relation def that no longer exists after a mod was removed: the ledger survives Scribe and the unknown def is skipped, the rest restored (Task 1 test `ApplySkipsUnknownDefs`, Task 2 `ExposeData`, scenario B).
4. A married pair leaves in one raid and the survivors come back in either order, or in two parcels on different days: the marriage comes back on the second arrival (Task 1 tests `ApplyDefersAnEdgeToAPawnStillAway`, `DeferredEdgeIsRestoredWhenTheSecondReturns`, scenario E).
5. A foreign pawn (gift, raider, live guest) must never receive this colony's relations: only a key match restores, and `Rejoin` is not called on foreign arrivals (`RaidIncoming`, `LiveHost`, `VisitSession`, `MirrorApplier`) (Task 1 test `TakeWithAnUnknownKeyReturnsNull`; reviewer checks the call sites).

---

### Task 1: Client-core relation book

**Files:**
- Create: `src/Rimlink.Client.Core/Relations/RelationBook.cs` (namespace `Rimlink.Client.Relations`)
- Test: `tests/Rimlink.Client.Tests/Relations/RelationBookTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RelationEdge(string Def, int OtherId, int StartTicks, bool HeldByOther)` — `HeldByOther = false`: the relation sits in the leaver's `directRelations`; `true`: in the other pawn's.
  - `public sealed class RelationEntry { public string Key = ""; public int ThingId; public int LeftTick; public List<RelationEdge> Edges = []; }`
  - `public interface IRelationWorld { bool PawnExists(int id); bool DefExists(string def); bool HasRelation(int holderId, string def, int otherId); bool IsLoveRelation(string def); bool HasOtherLovePartnerSince(int pawnId, int exceptId, int sinceTick); void AddRelation(int holderId, string def, int otherId, int startTicks); }`
  - `public enum EdgeOutcome { Restored, Deferred, OtherGone, AlreadyThere, NewPartner, UnknownDef, Failed }`, `public sealed record EdgeResult(RelationEdge Edge, EdgeOutcome Outcome, string? Error = null)`
  - `public sealed class RelationBook`: `List<RelationEntry> Entries`; `static string KeyOf(string raceDef, string gender, long birthAbsTicks, string fullName)`; `void Record(RelationEntry entry)` (drops an entry with no edges, replaces one with the same `Key`); `RelationEntry? Take(string key)` (removes it); `bool IsAway(int thingId)`; `List<EdgeResult> Apply(RelationEntry entry, int pawnId, IRelationWorld world)`; `int Prune(Func<int, bool> pawnExists)`.

`Apply` per edge, in this order (holder/other = `pawnId`/`OtherId`, swapped when `HeldByOther`): `!DefExists` → `UnknownDef`; `!PawnExists(OtherId)` and `IsAway(OtherId)` → append `edge with { OtherId = pawnId, HeldByOther = !HeldByOther }` to that entry unless an equal edge is there → `Deferred`; `!PawnExists(OtherId)` → `OtherGone`; `HasRelation(holder, def, other)` → `AlreadyThere`; `IsLoveRelation(def) && HasOtherLovePartnerSince(OtherId, pawnId, entry.LeftTick)` → `NewPartner`; else `AddRelation(holder, def, other, StartTicks)` → `Restored`. An exception from the world for one edge → `Failed` with `ex.Message`, the next edge goes on (spec §5). `Prune` removes entries none of whose edges has `pawnExists(OtherId) || IsAway(OtherId)`.

- [ ] **Step 1: Write the failing tests** with a `FakeWorld : IRelationWorld` (sets of pawn ids, of `(holder, def, other)` relations, of love defs, of `(pawnId, exceptId)` new partners; `Added` list of `(holder, def, other, start)`; `DefExists` false only for `"Gone"`):
  - `RecordReplacesAnEntryWithTheSameKey` — two `Record`s with key `"Human|Male|5|A B"`: `Entries.Count == 1`, the second's `ThingId` kept; `Record` of an entry with no edges leaves `Entries` empty.
  - `TakeWithAnUnknownKeyReturnsNull` — `Take("x") == null`, `Entries` unchanged; `Take` of a known key removes it.
  - `ApplyRestoresEdgesFromBothSides` — entry `ThingId 10, LeftTick 100`, edges `("Spouse", 20, 50, false)`, `("Parent", 30, 7, true)`; pawns 20, 30: `Added == [(10,"Spouse",20,50), (30,"Parent",10,7)]`, both `Restored`.
  - `ApplySkipsAGoneOtherAndAnExistingRelation` — other 40 absent → `OtherGone`; relation `(10,"Lover",20)` present → `AlreadyThere`; `Added` empty.
  - `ApplySkipsALoveRelationWhenTheOtherFoundANewPartner` — love def `"Spouse"`, new partner `(20, 10)` → `NewPartner`, no add; a `"Parent"` edge in the same entry is still `Restored`.
  - `ApplySkipsUnknownDefs` — edge def `"Gone"` → `UnknownDef`, the next edge restored.
  - `ApplyGoesOnAfterAFailingEdge` — `FakeWorld.AddRelation` throws for def `"Boom"` → that edge `Failed` with the message, the next edge `Restored`.
  - `ApplyDefersAnEdgeToAPawnStillAway` — entries A (`ThingId 10`, `("Spouse", 20, 50, false)`) and B (`ThingId 20`, `("Lover", 30, 9, false)`); `Apply(Take(A.Key), 10, world)` with pawn 20 absent → `Deferred`, B's edges contain `("Spouse", 10, 50, true)`; applying A again with the same state adds no second copy.
  - `DeferredEdgeIsRestoredWhenTheSecondReturns` — continue: pawn 10 now exists, `Apply(Take(B.Key), 20, world)` → `Added` contains `(10,"Spouse",20,50)`.
  - `PruneDropsEntriesWhoseOthersAreAllGone` — entry with others {40} absent → removed, returns 1; entry whose only other is another entry's `ThingId` → kept.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter RelationBook` — Expected: FAIL (build error, `RelationBook` not defined).
- [ ] **Step 3: Implement** `RelationBook.cs` with the interfaces above; summary comments in the style of `Live/DeltaModel.cs`.
- [ ] **Step 4: Run** the same command — Expected: PASS, 10 tests; then `dotnet test Rimlink.sln -nologo -v q` — Expected: green.
- [ ] **Step 5: Commit** — `git commit -m "Add a game-independent book of the relations a pawn leaves behind"`.

---

### Task 2: Game — the ledger, leaving and coming home

**Files:**
- Create: `src/Rimlink.Client/Game/Economy/RelationLedger.cs`
- Modify: `Game/Economy/PawnExit.cs` (`Leave`), `Game/Economy/Outgoing.cs` (lines 103, 135, 169, 269 `Remove` → `Leave`; `Restore` and `Contents` rejoin), `Game/Raids/RaidOutgoing.cs` (`Contents` rejoin), `Game/Raids/RaidResolver.cs:217` (captive `Remove` → `Leave`), `Game/Economy/Incoming.cs` (`Unpack` rejoin), `Game/Economy/BlobDebugActions.cs` (round-trip uses `Leave` + `Rejoin`)
- Test: mod build; in-game scenario A (below)

**Interfaces:**
- Consumes: Task 1 `RelationBook`, `RelationEntry`, `RelationEdge`, `IRelationWorld`, `EdgeOutcome`.
- Produces:
  - `public sealed class RelationLedger : GameComponent` (`RelationLedger(Verse.Game game)`): `ExposeData` saves the book under `"rimlinkRelations"` as `List<SavedEntry>` (`SavedEntry : IExposable` with `key`, `thingId`, `leftTick` and parallel `Scribe_Collections` lists `defs`, `others`, `starts`, `heldByOther`), `Prune` (Verse index) before saving; `public static void Record(Pawn pawn)`; `public static void Rejoin(Pawn pawn)`. Both: return when `VisitSession.InVisit` or the component is missing; whole body in `try/catch` → `Log.Error("[Rimlink] relations …: " + ex)`.
  - `Record`: edges = `pawn.relations.DirectRelations` (`HeldByOther false`) + for each pawn in the private `Pawn_RelationsTracker.pawnsWithDirectRelationsWithMe` (`AccessTools.FieldRefAccess`), its `DirectRelations` with `otherPawn == pawn` and `!def.reflexive` (`HeldByOther true`, `OtherId` = holder's id); `LeftTick = Find.TickManager.TicksGame`; log `relations recorded for <LabelShort>: <n>`.
  - `Rejoin`: `Take(key)`; null → return. If `pawn.thingIDNumber != entry.ThingId` and no indexed pawn has `entry.ThingId`, set `pawn.thingIDNumber = entry.ThingId` (else log that the id stays). `Apply(entry, pawn.thingIDNumber, adapter)`; log one line per result (`relations: <LabelShort> <def> <other> <outcome>`). Then for `Find.RelationshipRecords.Records` entry `entry.ThingId`: each referencing child that now has a direct `Parent` to `pawn` gets `RemoveRelation(record)` and `record.RemoveReference(child)`.
  - Private adapter `VerseWorld : IRelationWorld` over a `Dictionary<int, Pawn>` index built once per call from `PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead` plus `Corpse.InnerPawn` of every corpse on every map, graves and shelves included (`ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.Corpse), corpses)`), plus the returning pawn; first id wins (`TryAdd`). `AddRelation`: remember both pawns' `relations.nextMarriageNameChange`, `holder.relations.AddDirectRelation(def, other)`, set `startTicks` on `holder.relations.GetDirectRelation(def, other)` and, when `def.reflexive`, on `other.relations.GetDirectRelation(def, holder)`, put both name changes back. `IsLoveRelation` = `LovePartnerRelationUtility.IsLovePartnerRelation`; `HasOtherLovePartnerSince` scans the pawn's `DirectRelations` for a love def with `otherPawn.thingIDNumber != exceptId && startTicks > sinceTick`; defs via `DefDatabase<PawnRelationDef>.GetNamedSilentFail`.
  - `PawnExit.Leave(Pawn pawn)`: `RelationLedger.Record(pawn); Remove(pawn);` — summary: a pawn of this colony leaving for good; foreign pawns and copies keep `Remove`.
  - Homecoming sites call `RelationLedger.Rejoin(p)` right after `ThingBlobs.DeserializePawn(…)` and before any `PutBack`/drop pod/spawn: `Outgoing.Restore`, every pawn in `Outgoing.Contents` (`TransferRequest`, `PostRequest`, `LiveProposal`), `RaidOutgoing.Contents`, `Incoming.Unpack`, the dev round-trip (which also replaces `PawnExit.Remove(pawn)` by `PawnExit.Leave(pawn)`; its comment updated).

- [ ] **Step 1: Implement** the files above.
- [ ] **Step 2: Build** `dotnet build src/Rimlink.Client -c Release -nologo -v q` — Expected: 0 errors; `dotnet test Rimlink.sln -nologo -v q` — Expected: green.
- [ ] **Step 3: Grep check** `rg -n "PawnExit\.(Remove|Leave)|RelationLedger\.Rejoin" src/Rimlink.Client` — Expected: `Leave` only in `Outgoing.cs` (4), `RaidResolver.cs` (captive), `BlobDebugActions.cs`; `Rejoin` only in `Outgoing.cs`, `RaidOutgoing.cs`, `Incoming.cs`, `BlobDebugActions.cs`; `Remove` stays in `LiveHost.cs`, `MirrorApplier.cs`, `RaidResolver.cs` survivors.
- [ ] **Step 4: In-game scenario A** (sent to the setup owner session with the branch; host instance, dev mode): colonist with a spouse, a child and a bonded animal → `Blob round-trip selected thing` on the colonist. Expected: log `relations recorded for <name>: n` and one `Restored` line per relation, no `Tried to add the same relation twice`; the Social tab of the colonist, the spouse and the child, and the animal's bond, show the relations; save, reload: still there, no error about the colonist's id.
- [ ] **Step 5: Commit** — `git commit -m "Give a pawn that comes home its relations back"`.

---

### Task 3: Documentation, checklist and the two-instance run

**Files:**
- Modify: `docs/player-guide.md` (section 4 or 5: one paragraph — a pawn of yours that comes home keeps its family, partners and bonded animals; while it is away, your colonists do not show it in their Social tab; relations started in another colony do not come home), `docs/manual-checklist.md` (new section «Связи при возвращении» with scenarios A–E and their log evidence), `AGENTS.md` ("RimWorld 1.7 facts": `Pawn.Discard` runs `ClearAllRelations` on both sides and removes everyone's memories of the pawn; a pawn of the colony leaving for good goes through `PawnExit.Leave`, which records its relations, and every homecoming calls `RelationLedger.Rejoin`)
- Test: in-game scenarios B–E through the setup owner session

- [ ] **Step 1: Write** the documentation above.
- [ ] **Step 2: Send** the setup owner session (`local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`) the branch and scenarios:
  - B1 (`Outgoing.Restore`): ban the guest with `Rimlink.Tools`, send a married colonist to them → refused at once, the colonist is put back married, the spouse shows the marriage.
  - B2 (`Outgoing.ReturnRefused`, if the run can leave a send unanswered, e.g. the server killed right after the save upload): save, quit, ban the recipient, start the server, load → the replay is refused (any definitive refusal, not `ECONOMY_FROZEN`) and the pod brings the colonist back married.
  - C: the spouse dies and is buried while the colonist is away (dev kill after sending); the colonist comes back → the relation to the dead spouse is there.
  - D: after any return, save and reload → no error about the returned pawn's id; the pawn is in the colonist bar and selectable.
  - E: a Real raid by a married pair from host on guest; both survive and come home by `RaidReturn` → married again; their child at home shows both parents; repeat with one partner coming home in a later parcel if the run allows.
  - Expected for each: the `[Rimlink] relations` log lines and the Social tabs as described.
- [ ] **Step 3: Record** the results in `docs/manual-checklist.md`; `dotnet test Rimlink.sln -nologo -v q` — Expected: green.
- [ ] **Step 4: Commit** — `git commit -m "Describe relations on return and add them to the manual checklist"`.
