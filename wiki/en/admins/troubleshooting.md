---
pageId: admins/troubleshooting
contentRevision: 1
---

# Administrator troubleshooting

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Startup failure:** check SDK/runtime, DataDir permissions, occupied port, certificate and the Options validation error. Do not “fix” it by deleting the database.

**Panel works but game fails:** check HTTP/2/gRPC, proxy, TLS and game versions. Loading HTML does not verify the whole transport.

**401/403:** check legacy versus named token, scopes, cookie CSRF and session expiry; do not grant admin to an integration without cause.

**Stalled settlement:** inspect freezes, debt, threats, expiry and escrow ID; examine audit/state before retrying a mutation.

**Restore failure:** verify the server is stopped, the backup is intact and database/saves match; preserve current data before further action.

## Limits and safety {#limits}

Do not dump environment secrets into a shared ticket. Do not terminate unrelated processes. If the cause is unknown, collect exact refusal, version, time and a redacted log excerpt; one /health response does not prove a fix.

## See also {#related}

- [Monitoring and event feed](./monitoring.md)
- [Backup and recovery](./backup-recovery.md)
- [Player troubleshooting](../players/troubleshooting.md)
