---
pageId: players/auctions
contentRevision: 5
---

# Auctions

Applies to: Rimlink 0.9.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Select items, a starting price and a duration. Auctions accept items, not pawns. The seller can cancel before the first bid. Bidding uses the wallet: the entire current bid is reserved and a displaced bidder is refunded.

At settlement the winner receives a parcel and the seller receives proceeds minus any fee. A bid in the last 30 seconds extends the end by 30 seconds, up to 5 minutes total. Refresh the list to see the current deadline.

The seller need not be online for settlement. A freeze defers settlement until permitted; an administrator can cancel a problematic auction.

### Bid or buy out

Open the auctions tab in the economy window. To sell, choose items, starting price, duration in hours and an optional buyout price (0 disables buyout). Confirm posting and wait for the save/escrow transfer. To buy, refresh the list and inspect the goods and current price: a normal bid reserves wallet funds until settlement. **Buyout** at the seller's configured price ends bidding immediately; check the winner's mailbox and remaining balance. If funds are insufficient, deposit first instead of sending the seller a separate transfer outside the auction.

## Example: reserved bids and outbid refunds {#auction-example}

With a wallet of 500, a bid of 200 reserves the whole 200, leaving 300 available. Raising **your own** bid to 250 requires another 50, not a second 250. If someone outbids you, the reserved silver returns immediately, not through mail.

Refresh the card before bidding and inspect contents, price and deadline. The seller cannot bid. A bid in the last 30 seconds adds 30 seconds, up to five minutes beyond the original deadline. Trust the refreshed deadline, not an old screenshot.

Buying out at the displayed price closes the auction immediately. An ordinary bid does not deliver the item immediately.

## Where is the result after expiry, freeze or cancellation? {#auction-settlement}

The server settles without either participant online. The winner receives mail; the seller receives wallet proceeds less fees. No bids returns items to the seller. A full mailbox does not discard committed auction goods.

The seller may cancel before the first bid, not afterward. An administrator can cancel a disputed auction and refund both escrows. Freeze defers settlement; do not duplicate the lot. Reset/archive closes affected auctions with returns. Check completed entries in **My auctions / bids** and your balance before requesting intervention.

## Limits and safety {#limits}

The API allows 60 seconds–30 days; the game UI allows 1–720 hours. Reset/archive cancels related escrow. Sellers cannot rely on manual cancellation after bidding starts.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Server gameplay policy](../admins/gameplay-policy.md)
