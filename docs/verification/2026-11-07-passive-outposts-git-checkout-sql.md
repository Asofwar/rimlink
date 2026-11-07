# Passive outposts — SQL bytes from a fresh Git checkout

## Failure and fix

The first 0.9.0 tagged CI build at `6ecc6771dba4b6818e7a702f2cc927507b4f01d2` built successfully but failed server startup with `outpost-schema-profile-mismatch`. Its fresh Git LF sources produced LF inside four immutable triggers. The earlier Linux runs copied Windows source bytes and therefore did not establish fresh-checkout behavior. Windows Release passed 3795/3795 on that preserved source input; the failed Linux CI reported 2119 failures with the original results retained. The release remained a draft.

The [bounded specification](../superpowers/specs/2026-11-07-passive-outposts-migration-sql-bytes.md) preserves the existing exact-byte contract. Two migration SQL blocks now use explicit `\r\n`: transition update/delete triggers and administrative-command update/delete triggers. Existing profile JSON, qualification provenance, IDs, digests, schema matching and activation are unchanged. No migration ID or repair path was added. Product remains Pending/OFF.

## Executed checks

The pre-commit runtime patch was identified by Git tree `0839a43292ef5cf84726f334f21b5254c1be0d0d` over base `6ecc677`. It contains the two migration-source changes and their regression tests. Windows used an isolated Git LF worktree; Linux used normalized Git tree bytes on Ubuntu-24.04/ext4, without old `bin`/`obj` or a CRLF working-tree rsync.

- Original LF input: both exact `UpOperations.Sql` regressions failed. A separate real Linux database differed from the nearest v1 profile only in two triggers, and from v2 only in four triggers; the migration lineage and object counts matched.
- Fixed input: 79/79 targeted schema, domain-digest, admin-migration and additive restore/crash/backout tests passed on each executor. Four LF-only trigger variants still refuse without changing the catalog.
- Fresh databases matched complete existing descriptors and lineage: Windows v1-crlf (173 objects / 22 migrations) and v2-crlf-crlf (181 / 23); Linux v1-lf and v2-lf-lf with the same counts. Counts are diagnostic only; the validator matched all tuples and migration IDs.
- Each executor passed two real additive imports and read-only validation of all six original profiles: 8/8 per executor. The four edges were CRLF→CRLF and LF→CRLF on Windows, and CRLF→LF and LF→LF on Linux. The worker checked the frozen v1 digest, protected prefix, authority head/chain and retained backup cut. Original fixture databases and archives stayed unchanged.

The complete generated additive recipes retained their previously qualified SHA-256 values:

| Executor recipe | SHA-256 |
|---|---|
| CRLF | `64358bbb34d8080afaaeeb745041b9fdeccadc28af8ce84666734ce3b8324525` |
| LF | `a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4` |

Toolchain: .NET SDK 10.0.402, EF Core/Microsoft.Data.Sqlite 10.0.14, SQLite 3.53.3. Windows targeted checks used Debug; the Windows eight-case matrix and Linux checks used Release. These are distinct artifacts. The subsequent 0.9.1 version change updates component metadata and documentation; the final tagged CI checks the committed Release source.

## Boundaries

The original [qualification record](../../src/Rimlink.Server/Application/Outposts/SchemaProfiles/qualification.json) remains historical capture provenance; its migration-source hash was not overwritten. Equal SQL and recipes do not authorize a new assembly to replace an executor already bound into a frozen restore plan. No old restore plan was edited or adopted.

This correction does not repeat G1–G7, issue a Product qualification seal, update a running server, publish the Wiki site or establish browser acceptance. The one final whole-branch review remains the existing review; the additional specification resolves only this migration-byte defect. Raw failures, catalogs, recipes and test results are retained as local build evidence.
