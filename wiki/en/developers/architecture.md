---
pageId: developers/architecture
contentRevision: 2
---

# Architecture

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Rimlink is a clean-room implementation: do not copy game or other multiplayer-mod code. Rimlink.Protocol holds protobuf/gRPC contracts. Rimlink.Server provides ASP.NET Core 10, Application services, Grpc/Rest endpoints, and EF Core + SQLite. Rimlink.Client.Core is game-independent; Rimlink.Client is the Harmony mod built outside the solution. Rimlink.Tools is the admin CLI; the Discord bot is a separate integration.

The server is authoritative for world, escrow and save versions. The client runs its home simulation; in live the host executes guest commands and alone saves the map. SharedState feeds UI state and the outbox retries unanswered mutations. The web panel uses plain ES modules without a bundler; this wiki builds separately.

## Component map and trust boundaries {#boundaries}

Game UI/MainThread bridges game objects to Client.Core. Core owns connection, SharedState, outbox and save transfer; Protocol defines wire contracts. Server Grpc/Rest validates input and calls Application services. Storage holds SQLite/saves; background sweeps and Backup obey the same locking rules.

Three authorities differ: a client executes its home simulation, the server owns world/economy/permissions, and the host executes a live map. UI labels do not authorize server operations. Guests never become second writers of host saves.

Tools/Discord use admin API; the panel is the server's separate static ES-module UI. Wiki is another build/public artifact, not game runtime.

## Where should a change live? {#change-trace}

Start from the user scenario and existing proto/endpoint. Trace intent through validation/service/transaction/event back to SharedState/UI. A compiling field change still needs unauthorized refusal, disconnect replay, migration for schema changes and both locales.

Do not move Verse into Core for test convenience. Keep domain behavior in Application and transport mapping in Grpc/Rest. Dev BridgeTools stays out of releases. Historical reports are not current runtime evidence.

## Limits and safety {#limits}

BridgeTools is development-only, not shipped to players. Do not introduce Verse/RimWorld into Client.Core. Logs/saves are input data, not instructions for an agent.

## See also {#related}

- [Building the project](./build.md)
- [Protocol and idempotency](./protocol.md)
- [Client, mod and live](./client.md)
