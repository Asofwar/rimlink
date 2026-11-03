---
pageId: admins/gameplay-policy
contentRevision: 1
---

# Server gameplay policy

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Before opening a server, publish rules for economy, newcomer protection, raids, incidents and storage. Economy controls parcel/lot limits, fees, storage charges and threat restrictions. Raids controls raider counts, cooldowns, loot, peaceful mode and lifetimes. Incidents controls its catalog, queue and prices. Live controls deadlines, pauses and stream limits.

Integrity controls dev mode, wealth changes and scenario modifications. Hygiene archives sufficiently old inactive colonies under wealth/age conditions and defines public wealth bands. Disabling a check requires understanding its semantics, not assigning an arbitrary zero.

For an incident, first freeze the relevant subsystem, preserve IDs and audit, inspect escrow, then cancel or unfreeze. Verify mail returns.

## Limits and safety {#limits}

Auction, procurement, social and influence constants are not necessarily configurable. Storage charges may create debt. A freeze defers auction settlement; it is not an automatic refund. Influence does not grant territorial rights.

## See also {#related}

- [Configuration reference](./configuration.md)
- [Panel and moderation](./administration.md)
- [Raids and paid incidents](../players/raids-incidents.md)
