---
pageId: players/saves-recovery
contentRevision: 1
---

# Saves and recovery

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The server holds the canonical save. Let normal saving and upload finish before leaving. Reconnect after a disconnect: unanswered operations are retried from the outbox with their original identifiers, not recreated.

In 0.8.1 recovery of unfinished deliveries is tied to an exact save snapshot. When recovery is offered, read the selected version and warnings. Do not load an arbitrary old local save to “recover” items already sent away.

If recovery cannot finish, preserve journals and give the administrator the server, time, save version and operation ID. An administrator can inspect versions and perform a controlled rollback.

## Limits and safety {#limits}

Never delete outbox or recovery journals based on an untrusted message. A rollback changes the colony timeline; it is not a rollback of the whole server economy.

## See also {#related}

- [Backup and recovery](../admins/backup-recovery.md)
- [Player troubleshooting](./troubleshooting.md)
- [Wallet, mail and market](./economy.md)
