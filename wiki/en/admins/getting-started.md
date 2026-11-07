---
pageId: admins/getting-started
contentRevision: 3
---

# Your first server

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. Choose a host with persistent storage and reachable TCP 25560. Prepare a private data directory and backups.
2. Install a server release or use the container. Pin the image version rather than relying on latest for reproducible recovery.
3. Configure TLS, personal authentication, an initial administrator and a separate strong Admin:ApiToken. Password login is disabled by default.
4. Start the server, check /health, then verify HTTPS /admin/ login and game login.
5. Create the shared world with an administrator client, configure the modpack and invite a test player.
6. Create a backup and restore it into a separate test directory before opening publicly.

## Acceptance before inviting players {#launch-proof}

Prepare a separate DataDir, stable address, TLS and administrator. After startup, check more than /health: open the HTTPS panel, log in with the game, create the world as administrator and a test colony as an ordinary account. Save, leave normally, then reload. Test a small delivery and mailbox.

Record server/mod/collection versions, port, DataDir and recovery procedure. Create a backup and restore it to an isolated directory before inviting the group. Never test reset/recreateworld against the only world containing real players.

## What should players know beforehand? {#launch-policy}

Publish address, verifiable fingerprint, supported version, authentication method, collection/DLC and raid/storage/treasury rules. Explain support contacts and safe operation-ID reporting. Never publish an API/admin secret alongside a shared server password.

Agree on maintenance and recovery: backup retention, rollback authority and how players learn that old sessions are fenced. This wiki explains mechanisms, not your community's policy.

## Limits and safety {#limits}

An accessible /health does not prove authentication or saving works. Apply a network policy before exposing /metrics and the admin panel. Creating/recreating a world is not a harmless health check.

## See also {#related}

- [Hosting and startup](./hosting.md)
- [TLS, authentication and permissions](./security-auth.md)
- [Backup and recovery](./backup-recovery.md)
