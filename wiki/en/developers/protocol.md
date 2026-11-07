---
pageId: developers/protocol
contentRevision: 5
---

# Protocol and idempotency

Applies to: Rimlink 0.9.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The protocol only grows: add fields with new numbers, messages, proto files, ErrorCodes and ServerEvent bodies. Never reuse numbers. events.proto imports feature protos; features must not import events.proto and create a cycle.

Mutations use client_request_id. Unknown outcomes after disconnect are retried through the outbox with the same ID and original intent; a new ID means a new operation. Validate names, sizes, paths, limits and legal state at the boundary. Rate/size refusals return typed ErrorCodes rather than escaping crashes.

ProtocolVersion.Current is 1 and changes for incompatible contracts, not every release. Additive wire compatibility does not mean an old client displays a new feature.

## A mutation contract is more than a protobuf field {#mutation-contract}

Specify input, authority, state predicate, atomic result and retry behavior for a mutation. Persist client_request_id with original data until a definite outcome. Timeout is not proof of server rollback. Reusing the ID must retain result semantics; changing its payload must not create a new operation.

Test size/name/permission refusal before effects, concurrent requests and replay after commit before response. Live rows must be read inside LiveStateLock; checking outside races.

Wire compatibility, UI version and collection hash are separate. A new message does not authorize unsupported old-client UX without defined behavior.

## Limits and safety {#limits}

A schema does not establish authorization: enforce permissions on the server. Influence expected version/context and requestId protect against delayed operations. Do not log secret-bearing payloads for debugging.

## See also {#related}

- [Storage and escrow](./storage.md)
- [Client, mod and live](./client.md)
- [REST API reference](../admins/api.md)
