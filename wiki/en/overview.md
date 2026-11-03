---
pageId: overview
contentRevision: 1
---

# How Rimlink works

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The server stores the shared world, settlement metadata, saves and transactions. Players develop their own colonies at their own pace; other settlements appear on the planet with presence indicators. Saves are uploaded and restored on the next login.

Chat, gifts, markets, auctions and procurement connect colonies without a shared simulation. Visits and asynchronous raids use a map copy. Live mode sends the host's active state to a guest; persistent coop additionally stores permissions for specific pawns.

## Limits and safety {#limits}

Offline colonies are not continuously simulated. Live does not make the guest the save owner. Alliances and influence do not grant control of another colony.

## See also {#related}

- [Live: the active map](./players/live.md)
- [Persistent shared colony](./players/coop.md)
- [Wallet, mail and market](./players/economy.md)
