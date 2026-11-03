---
pageId: developers/storage
contentRevision: 2
---

# Storage and escrow

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Schema changes require an EF Core migration and a schema test. Run dotnet ef migrations add &lt;Name&gt; --project src/Rimlink.Server (without -nologo). Migrations apply at startup.

Only SaveFiles knows filenames: &lt;n&gt;.rws.zst is full, &lt;n&gt;.d&lt;k&gt;.zst is a delta against base-&lt;k&gt;.rws.zst. Size/Sha256 describe full content even when stored as a delta. Only OpenCurrent guarantees rebuilding the current version; never assume a full file exists for every older row. Verify hashes before trusting an existing file.

Escrow retains outgoing items/pawns/silver until settlement and returns them through mail. Live state changes under LiveStateLock with the row read inside it. Backup holds PerStoreLock and save-storage.lock through the database snapshot and save packing.

### Colony generation versus write epoch

`SaveGeneration` changes on reset so an old local save cannot become a new colony's first version. `SaveWriteEpoch` changes on administrative rollback within the same generation so an old session/outgoing operation cannot overwrite the administrator's selected timeline. The save version number is a separate sequential identifier, not either counter.

Upload checks header generation/epoch, recovery proof and the epoch captured by the session; mismatch yields SaveGenerationMismatch or SaveWriteEpochMismatch. After rollback the client must receive current authority and a snapshot, not replay stale outbox intent with rewritten metadata. Regression scenario: begin an old upload, roll back, then continue that upload; it must be refused without changing the current save.

## Investigating a damaged save safely {#storage-repair}

Preserve version rows and an immutable file copy first, then open full content through SaveFiles. Do not derive an older path from its number: it may be a delta. Size/Sha256 describe full reconstructed bytes, not delta size.

Full+delta can signal interrupted rebuilding. Compare raw contents/metadata before deleting a supposedly spare file. A matching filename does not prove its hash. Backup/restore share the storage lock; manual SQL/copying outside it destroys consistency evidence.

Schema changes need migrations and schema regression tests. Reproduce repairs on a copy and name unrecoverable/recovered versions explicitly rather than promising complete recovery.

## Limits and safety {#limits}

Do not delete the lock file. A full file plus spare delta may be an interrupted rebuild: compare raw contents and metadata first. A zstd prefix and input must not share one array. Never commit *.db or data directories.

## See also {#related}

- [Backup and recovery](../admins/backup-recovery.md)
- [Protocol and idempotency](./protocol.md)
- [Testing and acceptance](./testing.md)
