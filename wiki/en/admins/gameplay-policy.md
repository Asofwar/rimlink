---
pageId: admins/gameplay-policy
contentRevision: 11
---

# Server gameplay policy

Applies to: Rimlink 0.10.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Before opening a server, publish rules for economy, newcomer protection, raids, incidents and storage. Economy controls parcel/lot limits, fees, storage charges and threat restrictions. Raids controls raider counts, cooldowns, loot, peaceful mode and lifetimes. Incidents controls its catalog, queue and prices. Live controls deadlines, pauses and stream limits.

Integrity controls dev mode, wealth changes and scenario modifications. Hygiene archives sufficiently old inactive colonies under wealth/age conditions and defines public wealth bands. Disabling a check requires understanding its semantics, not assigning an arbitrary zero.

For an incident, first freeze the relevant subsystem, preserve IDs and audit, inspect escrow, then cancel or unfreeze. Verify mail returns.

## Example of a coherent server policy {#policy-example}

Decide raids, newcomer/low-wealth protection, peaceful exit delay and available hostile incidents before launch. Explain real/copies and warned/instant; chat blocking is not raid protection.

Economic rules should disclose fees, pawn/perishable storage charges, debt, threat restrictions and treasury distribution on disbanding. Offline colonies are not continuously simulated by the server. Influence is an opt-in overlay, not land ownership. These boundaries matter more than promises of automatic behavior.

## Freeze procedure during an incident {#freeze-runbook}

Record time, player, ID and state; freeze the relevant subsystem and preserve audit. Inspect wallets/escrow/parcels and pending client replay. Freeze does not undo settled operations; auctions may defer settlement.

Choose a supported cancellation/return or remove the cause. Verify recipients/mail before unfreezing and watch the queue. Do not grant guessed silver compensation: a later automatic return could pay twice.

## Limits and safety {#limits}

Auction, procurement, social and influence constants are not necessarily configurable. Storage charges may create debt. A freeze defers auction settlement; it is not an automatic refund. Influence does not grant territorial rights.


In the guild economy, shared stock and old-stock obligations are excluded from personal ServerWealth. After a claim, confirmed manifest value is counted once for its fixed recipient until typed receipt acceptance. IntegrityService recognizes the proven import in physical wealth growth; unexplained excess still triggers checks. Real-game qualification remains incomplete.

## See also {#related}

- [Configuration reference](./configuration.md)
- [Panel and moderation](./administration.md)
- [Raids and paid incidents](../players/raids-incidents.md)

The guild campaign scheduler is implemented as WIP: it prepares a booked slot, technically closes a missed slot, and never cancels a committed Start. Calls revalidate the common authority; an ordinary server is admitted with `GuildCapture:Enabled=true` and an activated V3. A battle occupies its physical executor until confirmed cleanup, including cancellation before Start.

NoAttackerReady imposes twenty-four hours for the directed attacking-guild → defending-guild pair across all of that defender's outposts, measured from the immutable terminal timestamp. It requires a healthy original arbiter and acknowledged delivery of the current Ready offer. Missing offers/acknowledgements, initial/runtime/profile problems, or uncertain backend state cause technical cancellation without this penalty. Receipt retry does not extend cooldown; this receipt does not prohibit the reverse direction.

HOME in isolated acceptance permits setup, initial map preparation, viewing without ticks and real pre-Start cancellation with cleanup. Start, commands, Final Apply and claims are blocked. SLICE requires a separate append-only record after native HOME and money-negative checks. The presence of a dev host or a successful build does not prove HOME acceptance or enable Product.
