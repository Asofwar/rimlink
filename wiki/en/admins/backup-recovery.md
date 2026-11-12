---
pageId: admins/backup-recovery
contentRevision: 21
---

# Backup and recovery

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

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

## Does current backup support the new guild V3? {#guild-domain-v3}

Version 0.10.0 contains a separate v3 backup envelope, R3 restore plan, and versioned completion. Old version 2 manifests explicitly retain V1/V2 admission. Never bypass refusal by changing a version number or restoring only an ownership table.

V3 restore preserves the owner, stock, fractional remainder, and previous alliance's obligations from the selected backup. Production resumes from a new checkpoint without accruing resources during downtime. A scheduled campaign with no executor assignment ends. If the result was applied or the server recorded a cancellation before or after Start, an occupied arena retains its original assignment and receipt. The new boundary blocks requests from the previous executor. Releasing the arena requires cleanup confirmation.

If the destination database contains an active assignment, RESTORE refuses before changing the boundary. A source assignment needs a verified result or cancellation. In an isolated RimWorld 0.10.0 run, a replacement arbiter released an occupied arena with a `ProvedReplacement` receipt after the operator stopped a started battle. In a separate run on a different port, both accepted receipts survived and the pending parcel delivered 25 Steel. In-game recovery of an applied result with an occupied arena and of an interrupted materialization still needs verification. The guild economy runs only with `GuildCapture:Enabled=true` and an activated V3. See [development status](../developers/testing.md#guild-capture-product).

Restore preflight refuses an archive with more than 512 pending guild parcels. Claims use the same global limit. An accepted canonical receipt frees capacity.

An accepted guild delivery keeps its exact receipt in the selected home save and is not issued again. A pending parcel keeps its original recipient, payload and server-held value. If delivery had started, the cut must contain the exact authorized before-save with no receipt for that parcel. The old attempt then closes, and delivery under the new boundary requires a fresh accepted save. A different or missing before-save blocks restore.

For a terminal cut, keep the original arbiter process at the main menu after it has archived the assignment's cleanup. When RESTORE completes, place `restore-cleanup.json` in that process's `StateDirectory`. Set `Version` to 1, copy `BattleId`, `Attempt`, `Fence` and `OriginalBoundary` from the selected assignment, and set `TargetBoundary` to the confirmed new boundary. The arbiter stops its old lease heartbeat, verifies the exact archived checkpoint and attests Entry against a fresh server challenge.

To qualify assignments for replacement, run the server and arbiter on the same Windows host under the same Windows account. Use a loopback TLS endpoint with the server certificate pinned. Before the original arbiter connects, create `guild-executor-local.json` in the server's `DataDir` with `Executable` set to the absolute path of the arbiter executable. The server verifies the local process during Hello and records its identity in the assignment.

After RESTORE, add `ReplacementBattleId` and `ReplacementBoundary` to `guild-executor-local.json` for the selected battle and new boundary. Start the replacement at Entry with the original boundary in its operator config and a separate `StateDirectory` with no unfinished assignment. In its `restore-cleanup.json`, include the same fields as above plus `ReplacementAssignmentId` and `ReplacementStartDigest`. Use the exact Start digest in lowercase hexadecimal, or an empty string for cancellation before Start. The server releases the arena only after it proves the original process exited and verifies the replacement at Entry. Assignments without a recorded process identity require separate recovery.

After `RestoreCleaned`, remove the request file and the two replacement selection fields. Keep `Executable` to record the identities of future assignments. Use the new `RestoreBoundary` in the arbiter configuration for its next start. In the isolated RimWorld run, the replacement released an arena occupied after pre-Start cancellation. It then restarted with the new boundary and resumed ordinary polling. Both players loaded their original colonists, and the accepted 13 Steel remained in the home save without another delivery.

Colony rollback also refuses a recipient's save generation once a guild resource has been issued to it, including after a delivery authorization or receipt has been committed.

## See also {#related}

- [Updating server and clients](./updating.md)
- [Storage and escrow](../developers/storage.md)
- [Saves and recovery](../players/saves-recovery.md)

An acceptance world records its stand, bootstrap, source, world and boundary in the common V3 chain and backup manifest. Ordinary Product restore refuses that lineage even if the manifest marker is removed.

The development acceptance host has a separate offline command `restore <source-root> <target-root> <archive-relative-path> <archive-sha256>`. Stop the source SLICE server. Select the exact archive under its `evidence/` directory and a newly provisioned empty target with a separate port, data directory, anchor and three game profiles. Both stands must use the same verified source, executor profile and player identities. The command binds the selected archive to the target and allocates a new installation and boundary. Repeat the exact command to resume. The target starts only with the committed restore and matching admission record. HTTP restore and reset stay disabled.
