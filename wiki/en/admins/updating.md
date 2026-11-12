---
pageId: admins/updating
contentRevision: 10
---

# Updating server and clients

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

For 0.9.2 verify the exact runtime 10.0.14 for both server and CLI before replacement. Retain the original executor and its isolated qualified runtime 10.0.13 for an unfinished pre-DDL restore plan. The 10.0.14 executor cannot replace that frozen witness. A proven post-schema continuation may read the old witness without executing DDL; follow the outpost recovery procedure.

1. Read release changes and align server, mod and modpack versions.
2. Announce maintenance, end sessions gracefully and create a consistent backup. Record the current version and settings.
3. Test the update on a separate data copy. EF migrations apply at startup; keep the old snapshot until verification.
4. Replace the server, then update the client mod with the game closed. Rebuild/republish a permitted collection when requirements change.
5. Check login, save download/upload, mail and modpack. Reopen access only after successful checks.

If it fails, stop the new server. Restore a compatible old-server/data-backup set; merely replacing binaries after migration does not guarantee compatibility.

## Checkpoint and rollback decision {#update-proof}

Before updating, pin binaries/image by version or digest, configuration, collection and a consistent backup. Test migrations, login, save, replay/mail and TLS on a copy. A mod update may require reimporting requirements and rebuilding a permitted ZIP, not just replacing server DLLs.

If it fails, stop the new version and never let clients use two world copies. Restore a compatible old-binaries/data-backup pair, not just an executable over migrated data. Record operations after the checkpoint; they may need reconciliation.

Completion requires fresh game login/save acceptance, not merely a running container.

## Limits and safety {#limits}

Protocol version changes only for incompatible contracts, but a matching protocol does not remove modpack requirements. Do not update a shared game directory used by someone else's session. Package publication requires a separate operator decision.

## See also {#related}

- [Backup and recovery](./backup-recovery.md)
- [Versions and compatibility](../compatibility.md)
- [Managing modpacks](./modpacks.md)
