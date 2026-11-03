---
pageId: players/raids-incidents
contentRevision: 2
---

# Raids and paid incidents

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Before raiding, choose a target, pawns and mode. Snapshot mode uses copies; real mode risks actual pawns. Review the confirmation, defender warning and loot rules. Warned raids give preparation time (60,000 ticks by default); instant raids halve the allowed loot.

Paid incidents are ordered from the server's available catalog. Price and warning depend on kind and intensity. The server checks peaceful mode, alliance membership, newcomer protection, cooldown and the target queue. Cancellation and refusals settle through escrow, not manual duplicate grants.

Inspect operation results and mail afterward: returning your own pawns and permitted loot is not the same as keeping an arbitrary map-copy save.

## How do I choose raid risk before dispatch? {#raid-choice}

At a foreign settlement, choose a raid with a caravan/pods and select participants. **Real** removes actual pawns: survivors return wounded with inventory, captives stay with the defender. **Copies** keeps originals home but reduces loot. **Warned** gives the defender one game day to prepare; **Instant** reduces loot. Target wealth and server caps further limit allowed loot.

The defender's own game processes an ordinary raid when they play; simultaneous online presence is unnecessary. Inspect the Raids tab and resulting mail. Equipment carried by real raiders is not capped loot; copy equipment does not become real returned items.

Peaceful mode blocks attacks both ways. Disabling it takes 24 hours by default; rechecking keeps protection. Newcomer, low-wealth and alliance protection are separate rules. Chat blocking grants none of these.

## Paid incidents: ordering, unknown responses and refunds {#incident-order}

Open economy → **Events**: select target, catalog entry, level and pods where offered. Review the server quote, then confirm. Hostile levels use half/normal/one-and-a-half/double base threat points. Hostile levels 3–4 warn half a game day ahead.

Hostile/weather incidents obey raid protection and cooldowns; friendly entries have a separate short cooldown. Default target queue is two events. If an incident cannot run, expires before the target plays, or is cancelled by an administrator, payment is refunded. A successful event does not pay its target.

For an unknown response, use **Retry original order**. Do not change target or purchase again until persisted intent resolves. **My orders** shows outcomes; an awaiting hostile incident also blocks goods export.

## Limits and safety {#limits}

Defaults: 10 raiders, 1 active raid, 8-hour cooldown and 24 hours per pair, 3-day newcomer protection, minimum wealth 20,000, loot 10% capped at 5,000, kidnapping disabled. Leaving peaceful mode takes 24 hours. Allies cannot order harmful incidents against each other.

## See also {#related}

- [Live: the active map](./live.md)
- [Visits and shops](./visits.md)
- [Server gameplay policy](../admins/gameplay-policy.md)
