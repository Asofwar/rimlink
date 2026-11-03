---
pageId: players/economy
contentRevision: 1
---

# Wallet, mail and market

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

<img class="wiki-art" src="/illustrations/exchange.svg" alt="Exchanges use server escrow; deliveries arrive through the mailbox." width="960" height="440" />
<p class="wiki-caption">Exchanges use server escrow; deliveries arrive through the mailbox.</p>

## Usage {#usage}

Normal operations require a comms console and powered trade beacons; goods are selected from their served area. The wallet is a server silver balance, not a physical stack. Depositing moves silver into that balance; withdrawal arrives through a server parcel.

List items or a pawn as an ordinary lot, set a price and review its contents. The buyer pays from the wallet, goods enter delivery and the seller receives proceeds minus any fee. Unsold lots expire after 30 days by default.

Check mail after a transaction or disconnect. The server holds outgoing goods in escrow until settlement and returns them through parcels.

## Limits and safety {#limits}

By default storage costs 0.5% per day after 3 free days. Debt, map threats, freezes or forbidden defNames can restrict operations. Fees and limits are server-configurable.

## See also {#related}

- [Gifts and two-way trades](./gifts-trades.md)
- [Auctions](./auctions.md)
- [Orders, contracts and shared goals](./procurement.md)
