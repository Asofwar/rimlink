---
pageId: players/auctions
contentRevision: 1
---

# Auctions

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Select items, a starting price and a duration. Auctions accept items, not pawns. The seller can cancel before the first bid. Bidding uses the wallet: the entire current bid is reserved and a displaced bidder is refunded.

At settlement the winner receives a parcel and the seller receives proceeds minus any fee. A bid in the last 30 seconds extends the end by 30 seconds, up to 5 minutes total. Refresh the list to see the current deadline.

The seller need not be online for settlement. A freeze defers settlement until permitted; an administrator can cancel a problematic auction.

### Bid or buy out

Open the auctions tab in the economy window. To sell, choose items, starting price, duration in hours and an optional buyout price (0 disables buyout). Confirm posting and wait for the save/escrow transfer. To buy, refresh the list and inspect the goods and current price: a normal bid reserves wallet funds until settlement. **Buyout** at the seller's configured price ends bidding immediately; check the winner's mailbox and remaining balance. If funds are insufficient, deposit first instead of sending the seller a separate transfer outside the auction.

## Limits and safety {#limits}

The API allows 60 seconds–30 days; the game UI allows 1–720 hours. Reset/archive cancels related escrow. Sellers cannot rely on manual cancellation after bidding starts.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Server gameplay policy](../admins/gameplay-policy.md)
