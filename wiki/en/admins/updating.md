---
pageId: admins/updating
contentRevision: 1
---

# Updating server and clients

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. Read release changes and align server, mod and modpack versions.
2. Announce maintenance, end sessions gracefully and create a consistent backup. Record the current version and settings.
3. Test the update on a separate data copy. EF migrations apply at startup; keep the old snapshot until verification.
4. Replace the server, then update the client mod with the game closed. Rebuild/republish a permitted collection when requirements change.
5. Check login, save download/upload, mail and modpack. Reopen access only after successful checks.

If it fails, stop the new server. Restore a compatible old-server/data-backup set; merely replacing binaries after migration does not guarantee compatibility.

## Limits and safety {#limits}

Protocol version changes only for incompatible contracts, but a matching protocol does not remove modpack requirements. Do not update a shared game directory used by someone else's session. Package publication requires a separate operator decision.

## See also {#related}

- [Backup and recovery](./backup-recovery.md)
- [Versions and compatibility](../compatibility.md)
- [Managing modpacks](./modpacks.md)
