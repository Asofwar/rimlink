---
pageId: admins/backup-recovery
contentRevision: 1
---

# Backup and recovery

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Use a consistent database-and-saves backup. Online backup: `rimlink-tools backup --data <DataDir> --out <backup.tar.zst>`; without --out it goes under backups. POST /api/admin/backups creates one, GET /backups lists them, GET /backups/{name} downloads one. Automatic defaults are Backup:IntervalHours=24 and Keep=7.

Before restoring, stop the server, preserve the current directory separately and test the selected backup in isolation. `rimlink-tools restore --data <DataDir> --from <backup.tar.zst>` replaces database and saves. After startup check /health, save versions, login and unfinished deliveries. Do not connect real players to a test restore.

admin rollback for one player is not whole-server recovery. Coordinate it with escrow state and client recovery journals.

## Limits and safety {#limits}

Never delete save-storage.lock: the tool holds it with the storage lock through snapshot/packing. Older versions are not necessarily full .rws.zst files: they may be deltas. Test restorability, not just archive existence.

## See also {#related}

- [Updating server and clients](./updating.md)
- [Storage and escrow](../developers/storage.md)
- [Saves and recovery](../players/saves-recovery.md)
