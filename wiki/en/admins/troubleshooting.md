---
pageId: admins/troubleshooting
contentRevision: 8
---

# Administrator troubleshooting

Applies to: Rimlink 0.10.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

**Startup failure:** check SDK/runtime, DataDir permissions, occupied port, certificate and the Options validation error. Do not “fix” it by deleting the database.

**Panel works but game fails:** check HTTP/2/gRPC, proxy, TLS and game versions. Loading HTML does not verify the whole transport.

**401/403:** check legacy versus named token, scopes, cookie CSRF and session expiry; do not grant admin to an integration without cause.

**Stalled settlement:** inspect freezes, debt, threats, expiry and escrow ID; examine audit/state before retrying a mutation.

**Restore failure:** verify the server is stopped, the backup is intact and database/saves match; preserve current data before further action.

## Order of server-side investigation {#incident-triage}

1. Record version, exact time and IDs; reproduce with safe reads.
2. Isolate the layer: process/port → TLS/HTTP2 → auth/scope → feature state → save/disk. A 403 is not an unreachable port.
3. Read the Options error or redacted log. Preserve DB/files/client journal for save failures; never delete bases/deltas.
4. Test a hypothesis on an isolated copy, changing one causal factor.
5. Repeat the original login/save/replay scenario after fixing it, not only health.

Stop dangerous interventions when evidence is insufficient. A missing row in a capped API list or public feed does not prove an operation never happened.

## Limits and safety {#limits}

Do not dump environment secrets into a shared ticket. Do not terminate unrelated processes. If the cause is unknown, collect exact refusal, version, time and a redacted log excerpt; one /health response does not prove a fix.

## See also {#related}

- [Monitoring and event feed](./monitoring.md)
- [Backup and recovery](./backup-recovery.md)
- [Player troubleshooting](../players/troubleshooting.md)
