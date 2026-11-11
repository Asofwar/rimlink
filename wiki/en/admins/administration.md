---
pageId: admins/administration
contentRevision: 7
---

# Panel and moderation

Applies to: Rimlink 0.10.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Open HTTPS /admin/ and sign in with legacy Admin:ApiToken. The panel exposes players, status, economy, raids and audit history. Use a separate API token for integrations rather than copying a cookie.

Start by reading player/status/actions and confirming IDs. Kick disconnects; mute restricts chat; ban denies login; economy freeze and raids freeze control separate subsystems. Record reasons in the audit.

Rollback selects a specific save version; reset creates a new colony timeline and clears related permissions. Archive retains a colony separately; restore-colony restores it subject to context checks. Coop rollback clears pawn assignments; revocation ends its session. Administrators may withdraw influence with an expected version, but cannot enable it for a player.

## Investigate a missing-transfer report {#moderation-case}

Find the player by exact name/#id, inspect the card and correlate persistent IDs, not display names alone. Match report time with actions, parcels, lots/trades and wallet debits. Determine whether client snapshot/replay finished. Read-only tokens redact private fields; missing data there does not mean no account exists.

Read first, then perform one suitable action with a reason: trade cancellation, lot/parcel refund or moderation. Verify its result and delivery. Never combine colony rollback and manual compensation without reconciling server economy.

## Choose rollback, reset or archive {#admin-danger}

Rollback selects an older map version and advances the write epoch. Reset begins a new colony lifetime and revokes related permissions. Archive frees an offline colony tile with a separate record; restore-colony needs a free tile and intact archive. Recreateworld is not a single-pawn repair.

Obtain confirmation, a consistent backup and a notification plan before destructive actions. After rollback require menu/relogin; after reset reject the separated old save. Other players' settled state does not rewind merely because an old map returned.

## Limits and safety {#limits}

Reset, rollback, recreateworld and restore have destructive consequences: obtain confirmation and a verified backup first. Do not compensate a failure with wallet +n before inspecting escrow. Unfreezing does not repair a damaged save.

## See also {#related}

- [CLI reference](./cli.md)
- [REST API reference](./api.md)
- [Backup and recovery](./backup-recovery.md)
