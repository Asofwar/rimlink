---
pageId: players/procurement
contentRevision: 1
---

# Procurement: orders, contracts and shared goals

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Buy order:** specify a defName, quantity and unit price. The server reserves price × quantity; suppliers fulfill portions and are paid immediately.

**Directed contract:** only the selected supplier sees the offer and must deliver the entire requested quantity at once.

**Shared goal:** contributors' resources remain in escrow until the goal is complete. The reward is then distributed proportionally; integer remainders go in ascending supplierId order, followed by the market fee. Cancellation returns resources to contributors and funds to the organizer.

Check the remaining quantity and deadline before confirming: an oversized delivery is not automatically split.

## Limits and safety {#limits}

Only defName and quantity are matched: no quality, material, condition or pawn filters. Limits are 1,000,000 units, 720 hours, 1,000,000,000 silver and 100 deliveries; open orders are capped at min(20, MaxLotsPerPlayer). A previous supplier's freeze may prevent final goal settlement.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Panel and moderation](../admins/administration.md)
