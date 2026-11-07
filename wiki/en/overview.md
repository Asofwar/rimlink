---
pageId: overview
contentRevision: 3
---

# How Rimlink works

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

<ol class="flow-diagram" aria-label="Process diagram"><li><strong>Home map</strong><span>A player simulates and saves their colony.</span></li><li><strong>Shared server</strong><span>World, permissions, wallets and escrow.</span></li><li><strong>Live host</strong><span>Sole map executor and save writer.</span></li><li><strong>Guest mirror</strong><span>Orders for owned/assigned pawns, not a second save.</span></li></ol>

The server stores the shared world, settlement metadata, saves and transactions. Players develop their own colonies at their own pace; other settlements appear on the planet with presence indicators. Saves are uploaded and restored on the next login.

Chat, gifts, markets, auctions and procurement connect colonies without a shared simulation. Visits and asynchronous raids use a map copy. Live mode sends the host's active state to a guest; persistent coop additionally stores permissions for specific pawns.

## Who simulates the world and owns the save? {#ownership}

“Persistent” describes the server world and state between logins, not background simulation of every map. The server stores the planet, accounts, settlements, economy, queued events and accepted colony versions. The owner's running game simulates the home map. Offline colonists do not mine or advance through game days.

| Mode | Simulation authority | Saved result |
|---|---|---|
| Home colony | Your client | Home save accepted by the server |
| Ordinary visit | Local copy of a foreign map | The copy never replaces your home save |
| Asynchronous raid | Defender's game when they play | Raid outcome and permitted returns |
| Live / coop | Host's game | Only the host writes the canonical save |

Independent colonies can run at different speeds. Server deadlines, such as offer expiry, are not map ticks. An offline seller can be paid on the server without their colony advancing a week.

## Which interactions are actually shared? {#boundaries}

Chat and social relationships connect players; goods and silver move through server escrow and mail. A raid or paid event can wait for its target's next login. Live enables simultaneous interaction; persistent coop adds saved permissions for host-owned pawns.

Choose a mode by purpose: an independent colony, a tour, combat, or control of the host's colonists? Friendship grants no colony control; alliance membership does not replace coop consent; influence colors do not lock territory. These systems share some rules, not permission ownership.

## Limits and safety {#limits}

Offline colonies are not continuously simulated. Live does not make the guest the save owner. Alliances and influence do not grant control of another colony.

## See also {#related}

- [Live: the active map](./players/live.md)
- [Persistent shared colony](./players/coop.md)
- [Wallet, mail and market](./players/economy.md)
