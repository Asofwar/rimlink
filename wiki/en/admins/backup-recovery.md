---
pageId: admins/backup-recovery
contentRevision: 4
---

# Backup and recovery

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Use a consistent database-and-saves backup. Online backup: `rimlink-tools backup --data <DataDir> --out <backup.tar.zst>`; without --out it goes under backups. POST /api/admin/backups creates one, GET /backups lists them, GET /backups/{name} downloads one. Automatic defaults are Backup:IntervalHours=24 and Keep=7.

Before restoring, stop the server, preserve the current directory separately and test the selected backup in isolation. `rimlink-tools restore --data <DataDir> --from <backup.tar.zst>` replaces database and saves. After startup check /health, save versions, login and unfinished deliveries. Do not connect real players to a test restore.

admin rollback for one player is not whole-server recovery. Coordinate it with escrow state and client recovery journals.

## Isolated backup restoration, step by step {#restore-drill}

1. Create a consistent archive with the supported backup tool; record time, server version, size and checksum. Keep a copy away from the server's only disk.
2. Choose a **new test directory**, not production DataDir. Ensure no server uses it. Preserve configuration/certificates separately.
3. Run locally:

~~~sh
rimlink-tools restore --data ./restore-drill --from ./copies/rimlink-selected.tar.zst
~~~

4. Start a compatible version with DataDir=./restore-drill, a separate port and external access blocked. Do not send real clients to this copy.
5. Check health, world status, save version list, and opening current/selected older test-player saves; reconcile counters/mail against the chosen point. File existence alone is insufficient.
6. Record outcomes before planning production restore, with shutdown, preservation of current data and confirmation.

Restore **replaces the target database and saves**. This is a procedure, not a claim that your server was tested.

## What does whole-server restore not repair? {#backup-scope}

The archive aligns database and save files under a lock. Do not make independent live copies or delete save-storage.lock. Older versions can depend on delta bases; restoring one .rws.zst does not restore history.

Whole-server rollback also rewinds economy/social records, while clients retain journals of newer actions. Preserve that evidence and plan reconciliation before allowing logins. “Clear everyone's outbox” is not a universal solution.

Check external modpack artifacts, source mods/licenses, service settings and secrets separately. Backup:Keep includes panel-created archives; a manual backup can evict the oldest retained one.

## Limits and safety {#limits}

Never delete save-storage.lock: the tool holds it with the storage lock through snapshot/packing. Older versions are not necessarily full .rws.zst files: they may be deltas. Test restorability, not just archive existence.

## See also {#related}

- [Updating server and clients](./updating.md)
- [Storage and escrow](../developers/storage.md)
- [Saves and recovery](../players/saves-recovery.md)
