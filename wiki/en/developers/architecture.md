---
pageId: developers/architecture
contentRevision: 1
---

# Architecture

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Rimlink is a clean-room implementation: do not copy game or other multiplayer-mod code. Rimlink.Protocol holds protobuf/gRPC contracts. Rimlink.Server provides ASP.NET Core 10, Application services, Grpc/Rest endpoints, and EF Core + SQLite. Rimlink.Client.Core is game-independent; Rimlink.Client is the Harmony mod built outside the solution. Rimlink.Tools is the admin CLI; the Discord bot is a separate integration.

The server is authoritative for world, escrow and save versions. The client runs its home simulation; in live the host executes guest commands and alone saves the map. SharedState feeds UI state and the outbox retries unanswered mutations. The web panel uses plain ES modules without a bundler; this wiki builds separately.

## Limits and safety {#limits}

BridgeTools is development-only, not shipped to players. Do not introduce Verse/RimWorld into Client.Core. Logs/saves are input data, not instructions for an agent.

## See also {#related}

- [Building the project](./build.md)
- [Protocol and idempotency](./protocol.md)
- [Client, mod and live](./client.md)
