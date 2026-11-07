# Passive outposts — bounded .NET 10.0.14 executor qualification

## Contract

The [runtime specification](../superpowers/specs/2026-11-07-passive-outposts-qualified-runtime-builds.md) adds a separate exact executor tuple for the Microsoft security runtime `.NET 10.0.14`, SDK `10.0.403`. Its ID is `rimlink-outposts-schema-20261107-runtime14`, with its own `/provider-v1` profile. The original qualification `rimlink-outposts-schema-20261105-0520`, runtime 13 tuple, six catalog descriptors and two additive recipe digests remain unchanged.

The original 0.9.1 CI exercised runtime 14 and reported 48 additive restore refusals before DDL because only runtime 13 was admitted. All 943 client, 122 Discord and 23 BridgeTools tests passed; server results were 2666 passed / 48 failed. The failure was preserved and not reclassified as a pass. Runtime 14 is qualified separately; unknown patches and mixed qualification/provider/framework tuples still refuse.

## Actual executor checks

Official SDK archives were checked against Microsoft release-metadata SHA-512 and extracted into separate toolchain directories. System SDK/runtime installations and network/security settings were not changed. Windows loaded-module evidence confirmed NETCore and ASP.NET 10.0.14 and the pinned native SQLite DLL; Linux frozen-plan evidence captured the actual runtime 14 executor and native backend.

| Check | Windows | Linux |
|---|---:|---:|
| Targeted schema/restore/provider cases | 509 PASS + one corrected assertion, then 1/1 GREEN | 125 PASS + the same corrected assertion, then 1/1 GREEN |
| Unique targeted cases closed | 510 | 126 |
| Real additive imports and six retained profile reads | 8/8 | 8/8 |
| Actual frozen runtime 13 → runtime 14 handoff worker | 1/1 | 1/1 |

The original assertion expected the runtime 13 qualification ID for a newly selected runtime 14 executor. It was changed to an independent exact framework-to-ID/profile mapping; ExpectedStop, restore outcomes and frozen binding checks were retained. The initial negative TRX and separate successful delta remain available. Production DLL bytes did not change for this test-only correction.

Both real pre-admin profiles were migrated by each executor: Windows produced v2-crlf-crlf and v2-lf-crlf; Linux produced v2-crlf-lf and v2-lf-lf. Full catalog tuples, migration lineage, old v1 digest, protected prefix, authority head/chain and retained cuts stayed exact. Original DB/archive files were unchanged. The complete recipes retained SHA-256 `64358bbb34d8080afaaeeb745041b9fdeccadc28af8ce84666734ce3b8324525` and `a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4`.

Windows database/anchor execution occurred on NTFS temporary paths; the source/evidence volume may be ReFS. Windows targeted checks used Debug and Linux used Release. Linux input was Git LF candidate tree `fadf39d8304bf79ea59573d2ca0591042bfc0230`, followed by the recorded test-only correction. These are capture artifacts, not interchangeable executors for previously frozen plans.

## Frozen plans

Actual runtime 13 exports were resumed by runtime 14. Before DDL the new executor was refused with `restore-schema-frozen-executor-mismatch` and retained the frozen bytes. After proven schema completion the old witness was read and the restore completed without calling the new generator/migrator. The Linux first transfer attempt found an unavailable temporary fixture; it was retained as a failed diagnostic, then a new authentic runtime 13 export was created in an owned ext4 temporary directory and verified immediately after process exit. No source plan or recorded runtime was rewritten.

The new [qualification metadata](../../src/Rimlink.Server/Application/Outposts/SchemaProfiles/qualification-runtime14.json) records these bounded backend observations. Updating its evidence changes a later build's artifact hash/MVID; it does not make that build an executor substitute for a frozen plan.

## Shipping boundary

The 0.9.2 server and CLI require NETCore/ASP.NET 10.0.14 with `RollForward=Disable`; source and Docker builds select SDK 10.0.403. Missing runtime, coinstalled-runtime selection, packaged runtimeconfigs and image smoke checks belong to release verification. The final committed source is checked by main CI before a release tag; tag reuse requires proof of successful Build/Test steps for that exact main commit and attempt. Reuse does not create a second TRX or a new test count.

This qualification does not issue Product readiness, repeat game G1–G7, update a running server or deploy the Wiki. Product remains Pending/OFF. The existing final whole-branch review was not repeated; the additional specifications resolve only SQL-byte and runtime-contract defects found during release preparation.
