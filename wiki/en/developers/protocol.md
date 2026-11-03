---
pageId: developers/protocol
contentRevision: 1
---

# Protocol and idempotency

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The protocol only grows: add fields with new numbers, messages, proto files, ErrorCodes and ServerEvent bodies. Never reuse numbers. events.proto imports feature protos; features must not import events.proto and create a cycle.

Mutations use client_request_id. Unknown outcomes after disconnect are retried through the outbox with the same ID and original intent; a new ID means a new operation. Validate names, sizes, paths, limits and legal state at the boundary. Rate/size refusals return typed ErrorCodes rather than escaping crashes.

ProtocolVersion.Current is 1 and changes for incompatible contracts, not every release. Additive wire compatibility does not mean an old client displays a new feature.

## Limits and safety {#limits}

A schema does not establish authorization: enforce permissions on the server. Influence expected version/context and requestId protect against delayed operations. Do not log secret-bearing payloads for debugging.

## See also {#related}

- [Storage and escrow](./storage.md)
- [Client, mod and live](./client.md)
- [REST API reference](../admins/api.md)
