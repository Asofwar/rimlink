---
pageId: players/visits
contentRevision: 1
---

# Visits and shops

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Choose a visit on the world map, via a caravan or an available action. An ordinary visit loads a copy of another colony, not the host's current authoritative simulation. Guest pawns remain guests; use the end-visit action to return.

You cannot simply take arbitrary goods home from the visited copy. Permitted exchange uses the host's shop and server operations: purchases are wallet-funded and tracked separately from the copied map state.

For real-time cooperation, choose a live visit and wait for host consent. It is a separate mode with different return and connection behavior.

## Limits and safety {#limits}

Visits default to a 12-hour limit. Never replace your home save with a visited copy. Blocking messages does not itself prohibit visiting.

## See also {#related}

- [Live: the active map](./live.md)
- [Wallet, mail and market](./economy.md)
- [Saves and recovery](./saves-recovery.md)
