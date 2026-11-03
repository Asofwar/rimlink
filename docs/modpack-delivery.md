# Server-hosted modpacks (GOG and Steam)

The upcoming 0.8.0 client can download a complete prepared collection from the same pinned Rimlink server. Steam is not used for this download. This does not provide a game licence, Core, DLC or a new authentication method. A GOG player still needs the administrator to enable `Auth:AllowPassword`; a Steam-only server stays Steam-only. Administrators retain the intentional modpack-check bypass.

## Prepare and publish

Use a **separate prepared directory** containing only immediate mod folders whose authors allow redistribution. Include every required non-official mod and dependency, including the matching Rimlink version. Do not point the packer at the game, Workshop cache, personal Config or save directories. Missing, duplicate, unsupported or non-redistributable required mods mean the collection must not be published. Rights are an administrator confirmation, not something a checksum can prove.

PowerShell, with the administrator token already in the local environment:

```powershell
rimlink-tools modpack package requirements --server https://localhost:25560 --token $env:RIMLINK_ADMIN_TOKEN --insecure --output D:\Rimlink\PackageRequirements.json
rimlink-tools modpack package pack --source D:\Rimlink\RedistributableMods --requirements D:\Rimlink\PackageRequirements.json --output D:\Rimlink\Packages\v1
rimlink-tools modpack package publish --from D:\Rimlink\Packages\v1 --server https://localhost:25560 --token $env:RIMLINK_ADMIN_TOKEN --insecure --confirm-redistribution --confirm-shared-settings
rimlink-tools modpack package status --server https://localhost:25560 --token $env:RIMLINK_ADMIN_TOKEN --insecure
```

`--insecure` is the existing CLI option for a self-signed certificate; the game uses its normal certificate pin prompt. The output directory and requirements file must be new. The archive contains no installer or external download URL. Publishing uploads bytes; the server never opens a path supplied by the CLI.

If `Server:ServerPassword` is empty, publishing additionally requires `--confirm-public`: anyone reaching that server can request download access. `--confirm-shared-settings` explicitly permits distributing the server's approved `ModConfig` snapshot, which may contain values the administrator should review first. It never reads personal or arbitrary server Config files. These settings are included as a bounded separate snapshot, not inside the archive.

To stop distribution:

```powershell
rimlink-tools modpack package unpublish --server https://localhost:25560 --token $env:RIMLINK_ADMIN_TOKEN --insecure
```

Changing the mod list, approved settings or effective access policy revokes download grants. After changing the collection, export fresh requirements, rebuild and publish a new package. An already prepared package cannot be published against a different collection/settings digest. Repeating publication of the current package is idempotent. No new configuration keys are required.

Capabilities and download access use the same applied authentication options as ordinary login. Editing authentication settings requires a server restart, as before; this feature adds no separate hot-reload policy.

## Install in the game

Open **Multiplayer → Download server modpack** beside the saved server, or use that action in the mismatch dialog before login. Confirm the server certificate before entering its server password. Review the complete mod list, archive/expanded sizes and reuse/install actions. Confirm trust in mod code and, separately, the shared settings. Cancelling does not activate a partial download.

The client streams and can resume its own partial archive. It verifies the complete SHA-256, metadata, file count, sizes and paths before committing. New folders are separate top-level `Mods/rimlink-managed-…` directories. A single exact installed copy can be reused. Different or duplicate unmanaged copies block installation with their paths; they are never overwritten. Extra gameplay mods are disabled in the future list, without deleting their folders.

Only unchanged, previously managed folders can be retired. A folder containing assemblies loaded in the running game cannot be replaced, even if the operating system would allow a rename. Close the game and resolve that update manually; this feature installs no external privileged updater.

The client stages the full approved settings set before its **one** restart, including a handle from a newly installed mod. Personal Config and the outbox stay separate. After restarting, one join intent is consumed; account passwords are not saved. Success requires matching loaded mods/settings and successful login. An administrator's bypass alone never marks the installation verified. Authentication or connection failure keeps installed files and backups for retry or rollback, without a restart loop.

A failed full verification stops ordinary joining before session/outbox replay and shows a reason. A separate manual administrator login retains its intentional bypass, while the delivery remains unverified. Settings for an inactive Allowed mod stay in the verified complete snapshot without requiring that absent mod to expose a running handle.

## Recovery and storage

The install lock and ownership file are under the game's `.rimlink-modpack` sibling of `Mods`. A pending marker prevents another profile from starting a conflicting installation. Each profile's `Rimlink/install-journal.json` records intent and completed steps. Game-side staging/backups remain on the game drive; Config-side staging/backups remain on the profile drive. `ModsConfig.xml` is replaced last. There is no claim of one atomic transaction across two drives.

Recovery runs before normal joining. **Roll back installation** is available in the server list for a pending journal. Externally changed files or backups stop automatic recovery and preserve the versions for manual resolution. Do not delete the journal, pending marker or backups to force a join. If a DLL from a folder to be restored is already loaded, close the game before resolving that folder.

Archives are at most 2 GiB, expanded content 8 GiB, 512 mods, 100,000 entries and 512 MiB per file; compression ratios above 100:1 are refused. Manifest size is 1 MiB. The client checks space for download, extraction, backups and a 512 MiB reserve. The server retains the current and previous packages plus a bounded pending upload; expired upload candidates are reaped on the next offer. Tokens are memory-only, package/catalog-bound, expire after 15 minutes and are revoked on publication or policy changes. There are at most 64 grants and four transfers, one transfer per grant.

Server archives live under `DataDir/modpack-delivery`. Existing save-backup archives do not include them: after restoring such a backup, missing or damaged delivery files make delivery unavailable, while ordinary login continues to work. Republish from the original prepared collection. This keeps optional multi-gigabyte mod archives out of every save backup.

## API

Admin routes under `/api/admin/modpack/delivery` use the existing bearer scopes or panel cookie/CSRF rules. `GET /requirements` and `/publication` require `read`; package offer, streamed ZIP upload, publication and unpublish require `admin`. The public bootstrap is `/api/modpack-delivery/capabilities`, then `POST /access` with the server password if required. Only access grants permit package `manifest`, `settings` and `archive` reads. Archive responses use a strong SHA-256 ETag, Range and If-Range. Credentials never appear in URLs, resume metadata or journals; redirects and HTTP decompression are disabled.

Actual GOG runtime acceptance remains unverified on this workstation because no GOG copy is installed. Fixture tests and an isolated Steam-copy check are separate evidence, not a GOG certification.
