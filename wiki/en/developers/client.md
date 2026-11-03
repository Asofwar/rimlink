---
pageId: developers/client
contentRevision: 1
---

# Client, mod and live

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Client.Core uses async/cancellable network calls, SharedState for UI and an outbox for unanswered mutations. It has no Verse/RimWorld references. The mod touches game objects only on the main thread: post background results through MainThread.Post. Keep one Harmony patch per method and explain why it exists.

ThingBlobs transfers pawns/items. Entry into a world with possible ID collisions needs newIds:true and ResetMapIndex before spawning a previously spawned blob. References outside the blob must be reconciled to the receiving world. Pawn equality uses thingIDNumber: never Destroy a deserialized copy equal to a live pawn.

A live mirror is not an independent simulation. Coop rights over owner pawns differ from ownership of visiting guest pawns; cleanup must not mail owner pawns away.

## Limits and safety {#limits}

RimWorld 1.7 uses Waylith/CoreCLR, not Unity; UnityEngine is a stub. Inspect the actual game type before guessing behavior. Local compilation/unit tests do not replace two real clients for gameplay changes.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Persistent shared colony](../players/coop.md)
- [Protocol and idempotency](./protocol.md)
