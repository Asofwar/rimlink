---
pageId: players/gifts-trades
contentRevision: 11
---

# Gifts and two-way trades

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Gift:** select a player and the transfer contents. The recipient accepts, refuses or postpones the decision; refusal returns escrow to the sender. Unanswered gifts are declined after 7 days by default. Gift preferences and blocking affect new offers.

**Trade:** open an offer and add items, pawns, silver and a note. The other party edits their side. Any edit invalidates earlier confirmations: both parties must confirm the same revision. Cancellation or expiry returns each contribution to its owner. Review the final contents before confirming.

After a disconnect, refresh trade state first. Do not send the same goods as a new gift while the first operation is unresolved.

## Gifts: dispatch, decision and return {#gift-flow}

Open **Send** at the comms console, choose the recipient, beacon-area goods/pawns and wallet silver. Recheck identity and contents: a pawn really leaves the colony. Wait for snapshot and response, then inspect operation state.

The recipient gets a letter: **Accept** permits delivery; **Refuse** returns goods and credits silver to the sender's wallet; **Postpone** keeps the decision pending. Mailbox offers the same actions. Unanswered gifts return after a week by default. Auto-accept gifts is a separate preference, not agreement to trades.

Blocking does not erase an already pending gift: it can still be refused. Do not send a second copy just because the recipient is offline.

## Two-way trades: what exactly do both parties confirm? {#trade-flow}

1. Use **Trades → New trade**, or **Trade** on a player with a settlement. Offline recipients are allowed.
2. Add goods/pawns, silver and a note. They enter server escrow, but nothing changes hands before both confirmations.
3. The partner changes their side. Review the complete current offer, not only the last message.
4. **Change my side** **adds** goods. To replace them, request return of the previously offered goods; they arrive by mail. Silver and note are new totals.
5. An edit requires the other side to confirm the new revision. If the offer changed while the window was open, stale confirmation is not accepted.
6. Two confirmations of one revision settle into parcels. Either side may cancel, returning each escrow to its owner; expiry works similarly.

Threat/debt prevents adding new outflow. Return, confirm and cancel remain completion paths. After a drop, refresh the same trade first.

## Limits and safety {#limits}

The default trade lifetime is 7 days with at most 5 open trades. Closed trading and gift refusal are separate preferences. Accepting goods never requires sharing a password.


Unreleased guild economy uses separate typed resource delivery acceptance. An ordinary mailbox receipt cannot confirm it: the client creates resources only after authorization and completes delivery after the exact snapshot is accepted. This path has not passed real-game acceptance and remains disabled.

## See also {#related}

- [Wallet, mail and market](./economy.md)
- [Chat, messages and pings](./chat-pings.md)
- [Saves and recovery](./saves-recovery.md)
