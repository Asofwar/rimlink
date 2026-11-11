---
pageId: players/visits
contentRevision: 8
---

# Visits and shops

Applies to: Rimlink 0.10.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Choose a visit on the world map, via a caravan or an available action. An ordinary visit loads a copy of another colony, not the host's current authoritative simulation. Guest pawns remain guests; use the end-visit action to return.

You cannot simply take arbitrary goods home from the visited copy. Permitted exchange uses the host's shop and server operations: purchases are wallet-funded and tracked separately from the copied map state.

For real-time cooperation, choose a live visit and wait for host consent. It is a separate mode with different return and connection behavior.

## Ordinary visits and buying from a shelf {#visit-flow}

1. At a foreign settlement, choose **Visit** by caravan/pods, not real raid or live.
2. Your home is saved. A copy of the host map loads with copies of your pawns; an offline host's real map does not start running.
3. Tour the colony. Goods picked up from the floor do not travel home. Use **Buy** only for items marked **For sale**, paying from the wallet.
4. **End visit** restores your home map. Purchases arrive there as server parcels.

To sell at home, select an item → **For sale…**, set a price and save. The shelf publishes with that save. Sold goods are collected from stocks on next login. This differs from a global market lot.

## If the visited map cannot load {#visit-load}

The host may have a mod or DLC you lack; validation should stop before loading and identify dependencies. Never remove XML objects from a foreign save to force entry. Disabled visits or no host colony require another action, not forced loading.

On return, check your home map and mailbox. A visit copy must not overwrite home. For real-time interaction request a live visit, which requires an online consenting host and different rules for returning actual guest pawns.

Visits and ordinary live sessions cannot start from a temporary guild battle map; an earlier queued callback must not load it as the home colony either. Capture works only on servers where the administrator has enabled it; the battle viewer and home return were verified in the real game on isolated stands on 10–11 November 2026.

## Limits and safety {#limits}

Visits default to a 12-hour limit. Never replace your home save with a visited copy. Blocking messages does not itself prohibit visiting.

## See also {#related}

- [Live: the active map](./live.md)
- [Wallet, mail and market](./economy.md)
- [Saves and recovery](./saves-recovery.md)
