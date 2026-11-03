---
pageId: players/gifts-trades
contentRevision: 1
---

# Gifts and two-way trades

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Gift:** select a player and the transfer contents. The recipient accepts, refuses or postpones the decision; refusal returns escrow to the sender. Unanswered gifts are declined after 7 days by default. Gift preferences and blocking affect new offers.

**Trade:** open an offer and add items, pawns, silver and a note. The other party edits their side. Any edit invalidates earlier confirmations: both parties must confirm the same revision. Cancellation or expiry returns each contribution to its owner. Review the final contents before confirming.

After a disconnect, refresh trade state first. Do not send the same goods as a new gift while the first operation is unresolved.

## Limits and safety {#limits}

The default trade lifetime is 7 days with at most 5 open trades. Closed trading and gift refusal are separate preferences. Accepting goods never requires sharing a password.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Chat, messages and pings](./chat-pings.md)
- [Saves and recovery](./saves-recovery.md)
