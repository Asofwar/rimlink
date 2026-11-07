---
pageId: faq
contentRevision: 3
---

# Frequently asked questions

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Must everyone play together?** Not for independent colonies or asynchronous economy; yes for live and coop.

**Can I continue offline?** Canonical state belongs to the server. Do not substitute a local save: it may conflict with items and transactions already settled.

**Where is a transfer after a disconnect?** Reconnect and inspect mail, market and operation state first. The outbox retries an unanswered request with the same identifier.

**Does an alliance color mean territorial ownership?** No. It is an informational overlay of voluntarily published influence.

## Do we need a server and a shared schedule? {#choosing}

**Can one player invite another without a server?** Rimlink requires a dedicated server. It may run on a participant's computer, but both games must reach it. The browser panel administers that server; it is not the game.

**Must everyone be online together?** Not for commerce, messages, queued raids or independent colonies. Live and coop require the host online at home. Their colony cannot continue autonomously after they leave.

**Can we share everything?** Coop grants control of explicitly assigned pawns, not wallets, item transfers, admin authority or save ownership.

**Is a subscription required?** The mod and wiki are free. The game, DLC and any paid hosting are separate purchases, not included with Rimlink.

## Why are familiar workarounds unsafe? {#mistakes}

**Restore goods using an old save?** No: another player may already have received them. Resolve the original operation through replay/reconnection.

**Delete the outbox to clear an error?** No: it is evidence of an unfinished request. Preserve the folder and diagnose the refusal.

**Trust a changed certificate because the server name is familiar?** Verify the new fingerprint with the administrator through another channel first.

**Does a guild color prevent entry?** No. Influence is informational, not land ownership.

**Do old promotional images show gameplay?** Concept art presents the idea. Wiki diagrams are not claimed as game UI; a verified gameplay screenshot gallery has not yet been collected.

## Limits and safety {#limits}

Deleting journals or manually duplicating transfers can cause loss or duplication. Give an administrator the operation ID and time, never a password or token.

## See also {#related}

- [Saves and recovery](./players/saves-recovery.md)
- [Player troubleshooting](./players/troubleshooting.md)
- [Guild influence](./players/guild-influence.md)
