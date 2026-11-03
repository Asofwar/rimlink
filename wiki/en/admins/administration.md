---
pageId: admins/administration
contentRevision: 1
---

# Panel and moderation

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Open HTTPS /admin/ and sign in with legacy Admin:ApiToken. The panel exposes players, status, economy, raids and audit history. Use a separate API token for integrations rather than copying a cookie.

Start by reading player/status/actions and confirming IDs. Kick disconnects; mute restricts chat; ban denies login; economy freeze and raids freeze control separate subsystems. Record reasons in the audit.

Rollback selects a specific save version; reset creates a new colony timeline and clears related permissions. Archive retains a colony separately; restore-colony restores it subject to context checks. Coop rollback clears pawn assignments; revocation ends its session. Administrators may withdraw influence with an expected version, but cannot enable it for a player.

## Limits and safety {#limits}

Reset, rollback, recreateworld and restore have destructive consequences: obtain confirmation and a verified backup first. Do not compensate a failure with wallet +n before inspecting escrow. Unfreezing does not repair a damaged save.

## See also {#related}

- [CLI reference](./cli.md)
- [REST API reference](./api.md)
- [Backup and recovery](./backup-recovery.md)
