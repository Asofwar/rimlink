# Passive outposts — schema profile qualification (Task 15, WIP)

## Scope and result

Six exact, code-owned SQLite schema descriptors are recognized without changing any historical SQL, domain/v1 digest, Genesis, transition prefix, retained source database or original backup cut. This qualification covers catalog recognition and the additive admin-receipt migration only. It does **not** complete Task 15, activate domain/v2, enable the product capability, or establish G1–G7 gameplay acceptance.

The authoritative mechanical ruling is [admin/readiness clarification §10.7](../superpowers/specs/2026-11-05-passive-outposts-admin-readiness-clarification.md). [Qualification metadata](../../src/Rimlink.Server/Application/Outposts/SchemaProfiles/qualification.json) records descriptor/fixture hashes, original legacy cuts, exact migration recipes and per-executor observations; generated databases and raw logs remain ignored under the worktree build directory.

## Actual fixtures and executor matrix

The [cross-executor worker](../../tests/Rimlink.Server.Tests/Outposts/OutpostCrossExecutorSchemaTests.cs) creates an actual legacy Initialize/head and coherent protected backup, then exports the archive's exact SQLite bytes. An imported fixture is copied into an owned temporary directory, verified against its retained cut, migrated with the real [additive migration](../../src/Rimlink.Server/Storage/Migrations/20261105014436_OutpostAdminCommands.cs), and checked without changing the old v1 digest, complete protected prefix, old catalog tuples or retained inputs.

| Exact profile suffix | Catalog | Read-only Windows | Read-only Linux |
|---|---:|---:|---:|
| v1-crlf | 173 objects / 22 migrations | 1/1 PASS | 1/1 PASS |
| v1-lf | 173 objects / 22 migrations | 1/1 PASS | 1/1 PASS |
| v2-crlf-crlf | 181 objects / 23 migrations | 1/1 PASS | 1/1 PASS |
| v2-lf-lf | 181 objects / 23 migrations | 1/1 PASS | 1/1 PASS |
| v2-crlf-lf | 181 objects / 23 migrations | 1/1 PASS | 1/1 PASS |
| v2-lf-crlf | 181 objects / 23 migrations | 1/1 PASS | 1/1 PASS |

All IDs have the prefix `rimlink-outposts-schema/`. Counts are diagnostic, not admission rules. Actual tuple order is BINARY; all SQLite autoindices, internal catalog objects, nullable SQL and migration lineage are retained verbatim. No OS-based profile choice, newline normalization, schema repair or trial-hash fallback is used.

Four real migration edges separately passed (one worker test each): CRLF→CRLF on Windows, LF→LF on Linux, CRLF→LF on Linux and LF→CRLF on Windows. The mixed directions first failed with `outpost-schema-profile-mismatch`, then passed after their exact observed descriptors were admitted. Toolchain: SDK 10.0.402, .NET 10.0.13, EF Core/Microsoft.Data.Sqlite 10.0.14, native SQLite 3.53.3; Linux checks used Ubuntu-24.04/ext4 and two CPUs. Source and target IDs plus recipe SHA-256 are recorded per edge, not inferred from the later host OS.

## Targeted regression evidence

- Windows: 49/49 PASS across [domain-format/schema tests](../../tests/Rimlink.Server.Tests/Outposts/OutpostDomainFormatTests.cs), [frozen digest tests](../../tests/Rimlink.Server.Tests/Outposts/OutpostDomainDigestTests.cs) and [admin-receipt migration tests](../../tests/Rimlink.Server.Tests/Storage/OutpostAdminMigrationTests.cs).
- Linux, two CPUs: the same 49/49 PASS; the six per-profile read-only checks above are separate runs, not an invented aggregate suite count.
- Genuine RED→GREEN cases include missing migration lineage, removal of a real SQLite autoindex, and `sqlitex_` shadow objects previously hidden by SQL LIKE wildcard semantics. Unknown schemas and bounds violations still refuse without repair; real SQLite I/O exceptions are not swallowed by the validator.

## Remaining work / limitations

The complete format fold, explicit domain/v1→v2 activation and its audit/receipt closure, admin operations, backup/restore format dispatch, readiness/seal and actual G1–G7 acceptance remain unfinished. No full Task 15 solution suite or actual game acceptance is claimed here. The previously observed Windows SQLITE_IOERR_TRUNCATE (1546) and intermittent restore-publication startup refusal remain unrooted; these schema checks do not establish their repair. No production data, shared game, publication or deployment was touched.
