---
pageId: developers/client
contentRevision: 4
---

# Client, mod and live

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Client.Core uses async/cancellable network calls, SharedState for UI and an outbox for unanswered mutations. It has no Verse/RimWorld references. The mod touches game objects only on the main thread: post background results through MainThread.Post. Keep one Harmony patch per method and explain why it exists.

ThingBlobs transfers pawns/items. Entry into a world with possible ID collisions needs newIds:true and ResetMapIndex before spawning a previously spawned blob. References outside the blob must be reconciled to the receiving world. Pawn equality uses thingIDNumber: never Destroy a deserialized copy equal to a live pawn.

A live mirror is not an independent simulation. Coop rights over owner pawns differ from ownership of visiting guest pawns; cleanup must not mail owner pawns away.

## UI intent and the main-thread boundary {#client-intent}

UI captures selected data and creates intent; Core sends a cancellable request and updates SharedState. Network callbacks must not read/create Verse objects; apply game results through MainThread.Post. Cancelling a wait differs from cancelling an accepted server operation.

Transfers persist journals and an exact snapshot before mutation. Replay original intent instead of rebuilding goods from the current selection. For receipt, save the delivered map before acknowledging. Core tests do not verify Scribe/ThingBlobs or map loading.

## Pawns: IDs, relations and return {#pawn-lifecycle}

A deserialized pawn can equal a live one by thingIDNumber; destroying the copy may affect the real pawn. On cross-world entry regenerate IDs/map index and reconcile external faction/ideo/policy references. Use existing PawnExit/RelationLedger return paths to preserve bilateral relations and UI caches.

Distinguish object owner, command-authorized player and map writer. Coop assigns host pawns; live visits transfer guest pawns. Shared cleanup must never mail host pawns to a partner. Verify this with two actual clients.

## Limits and safety {#limits}

RimWorld 1.7 uses Waylith/CoreCLR, not Unity; UnityEngine is a stub. Inspect the actual game type before guessing behavior. Local compilation/unit tests do not replace two real clients for gameplay changes.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Persistent shared colony](../players/coop.md)
- [Protocol and idempotency](./protocol.md)
