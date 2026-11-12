---
pageId: players/procurement
contentRevision: 10
---

# Procurement: orders, contracts and shared goals

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Buy order:** specify a defName, quantity and unit price. The server reserves price × quantity; suppliers fulfill portions and are paid immediately.

**Directed contract:** only the selected supplier sees the offer and must deliver the entire requested quantity at once.

**Shared goal:** contributors' resources remain in escrow until the goal is complete. The reward is then distributed proportionally; integer remainders go in ascending supplierId order, followed by the market fee. Cancellation returns resources to contributors and funds to the organizer.

Check the remaining quantity and deadline before confirming: an oversized delivery is not automatically split.

## How do I create an order and supply resources? {#procurement-steps}

Open the comms console → economy → **Procurement**. The organizer chooses a mode, technical defName (for example Steel), quantity, unit price/fund and deadline. Review the reserved amount and confirm; silver must already be in the wallet.

A supplier opens an available card, refreshes remaining quantity, selects beacon-area items and reviews the total. Buy orders accept partial remaining quantities; directed contracts require the entire amount from the named supplier. Over-quota delivery is rejected **in full**; the server does not split opaque blobs.

Accepted buy-order resources go to the organizer by mail; suppliers receive immediate payment less the market fee. Check net payout, contents and participation history. Refresh other public changes manually. An unknown response blocks new mutations until **Retry request** resolves the original intent.

## Shared goals: when are rewards paid and how is rounding handled? {#goal-example}

Example before fees: quota 3 Steel, fund 10; A supplies 1, B supplies 2. Integer shares are 3 and 6, leaving 1 silver. It goes to the lower supplier ID, producing 4/6 or 3/7. Market fees apply afterward. Until the quota is full, neither resources nor fund are released.

Cancellation/expiry of an incomplete goal returns resources to suppliers and the fund to the organizer. A partial contribution is not a free gift. Perishable storage may create supplier debt on normal settlement/cancellation; admin cancellation and reset/archive waive holding fees for those returns. Final delivery checks earlier suppliers' freezes: do not “repair” a completed-looking card through manual grants.

Matching uses defName and quantity only. A promise of a particular quality/material is not enforced by this interface. Pawns and claims of NPC kills are not accepted.

## Limits and safety {#limits}

Only defName and quantity are matched: no quality, material, condition or pawn filters. Limits are 1,000,000 units, 720 hours, 1,000,000,000 silver and 100 deliveries; open orders are capped at min(20, MaxLotsPerPlayer). A previous supplier's freeze may prevent final goal settlement.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Panel and moderation](../admins/administration.md)
