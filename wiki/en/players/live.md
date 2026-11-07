---
pageId: players/live
contentRevision: 5
---

# Live: the active map

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

<img class="wiki-art" src="/illustrations/live.svg" alt="The owner simulates the map; the guest sends orders and receives updates." width="960" height="440" />
<p class="wiki-caption">The owner simulates the map; the guest sends orders and receives updates.</p>

## Usage {#usage}

The guest proposes a live raid or visit with selected pawns. The host accepts the invitation (60 seconds by default). Once loaded, the guest sees a map mirror; commands execute on the host, who remains authoritative for simulation and saving.

The slower requested speed wins. The guest has a separate pause budget (10 pauses of 60 seconds by default). Letter-triggered pauses use the stricter participant preference. The guest can leave; the host can abort.

After losing connection, wait for PeerBack/reconnection. Retries continue every 60 seconds; after two minutes the game offers the menu. Desynchronization calls for a fresh snapshot, not manual map editing.

## Live session: guest and host actions {#live-flow}

1. The guest launches a live raid/visit by caravan or pods. The host must be online at home.
2. The host has one minute to answer. Refused/timed-out raids continue as ordinary raids with the chosen timing; live-visit pawns return by mail.
3. The guest saves home and loads a mirror. The host pauses until the mirror is ready.
4. The guest controls **their own** pawns, work and schedules. Orders execute on the host; other objects are view-only.
5. Watch the upper-right overlay for state, tick and pauses. The slower requested speed wins; H/G mark each preference.
6. The guest uses **End session**, the host **Abort session**. Returned pawns and permitted loot come from the server outcome, not a copied mirror save.

An outpost worker controlled by the host during a visit remains a mirror of the guest’s pawn. A protected return uses the original worker UUID and the server receipt.

The home save written before loading the mirror includes the UUID registry and protected receipts, even when the live session has already started. If that registry is ambiguous, mirror loading stops before the home save is written.

The host ends when all guest pawns are dead, captured or gone. A living downed/carried pawn may still keep the session active.

## Pauses, mirror lag and disconnection {#live-failure}

The default guest budget is ten pauses of up to a minute. The host does not spend it. Letter pauses use the stricter Never/Major/Any threat preference, consume no guest budget, and clear when a speed is selected. This replaces vanilla automatic letter pauses during the session.

A lagging mirror requests a fresh snapshot; do not manually edit maps into agreement. If a peer drops, the other waits several minutes. After expiry, a lost raider is handed to host AI; a lost host reports completion when their game next logs in. Returns can therefore be delayed: do not spawn replacement pawns.

Coop uses a similar mirror, but assigned pawns belong to the host and are never guest mail escrow.

## Limits and safety {#limits}

The host must stay online. Live does not create an independent second save. Do not end a session by deleting journals or manually granting pawns.

## See also {#related}

- [Persistent shared colony](./coop.md)
- [Raids and paid incidents](./raids-incidents.md)
- [Player troubleshooting](./troubleshooting.md)
