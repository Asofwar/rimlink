---
pageId: glossary
contentRevision: 10
---

# Glossary

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

| Term | Meaning |
|---|---|
| Canonical save | Colony state accepted by the server |
| Escrow | Items, pawns or silver held by the server until an operation settles |
| Mail / parcel | Delivery or return of escrow to a recipient |
| Outbox | Local journal of unanswered outgoing requests |
| Live / host / guest | Active-map session with one authoritative host |
| Alliance / guild | Social group; guilds reuse existing alliances |
| defName | Technical item identifier such as Steel |
| Scope | Set of administrative API permissions |
| Pin | Fingerprint of a trusted TLS certificate |

### Recovery identifiers

- **SaveGeneration / generation**: a colony lifetime; reset makes previous local saves stale.
- **SaveWriteEpoch / write epoch**: authority to write the current timeline; rollback changes the epoch and fences stale sessions without creating a new colony.
- **Save version**: a server snapshot number; neither the software version nor generation/epoch.

## Which state terms must not be confused during recovery? {#state-terms}

| Term | Not the same as | Practical consequence |
|---|---|---|
| Rimlink version | Save version | Updating the mod does not select a colony snapshot |
| ProtocolVersion | RimWorld version | It checks a network contract, not DLC availability |
| SaveGeneration | SaveWriteEpoch | Reset starts a colony lifetime; rollback changes write authority |
| Escrow | Mail | An escrowed offer has not yet reached its recipient |
| request_id | Trade id | Replaying intent uses the original request_id |
| Host | Dedicated server | In live, host means the map owner's game |
| Tick | Real hours | Pausing a map does not pause a server offer deadline |
| Alliance | Coop | Social membership grants no pawn control |

## Limits and safety {#limits}

Configuration keys, commands, error codes and defNames are not translated. A visit copy and a real raid pawn have different risks.

## See also {#related}

- [Raids and paid incidents](./players/raids-incidents.md)
- [TLS, authentication and permissions](./admins/security-auth.md)
