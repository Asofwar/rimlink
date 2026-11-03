---
pageId: players/modpacks
contentRevision: 1
---

# Server modpacks

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The difference dialog compares server requirements with installed mods and load order. Read the mismatch list first. The server may require exact files and shared settings, not merely matching mod names.

If server delivery is available, inspect the offered package before downloading it. Downloading and applying are separate: close the game before replacing in-use files. Restart after applying, then repeat the check. Do not overwrite a verified package with an arbitrary Workshop version.

Without a published archive, install requirements following the administrator's instructions. Back up your configuration before replacing settings.

### Workshop: fix the mismatch

1. Read each category: missing mods, wrong versions, changed files, forbidden mods, load order and shared settings. A newer local version can mean the server must update; the client cannot invent an older Workshop revision.
2. Choose **Fix and restart**. It subscribes to missing Workshop mods, waits for Steam, orders the list, disables refused extras and stages the shared settings. The old list is backed up as `Config/ModsConfig.rimlink-backup.xml`.
3. After the restart, Rimlink resumes joining. A password account still asks for its password: it is not stored. If files still differ, send the mismatch details to the administrator instead of bypassing the check.
4. Server settings remain active on subsequent starts, including single-player games. Use the main-menu **Mod settings of [server] in effect: use mine** action and restart to return to personal settings. Personal settings files are not overwritten.

### Server archive: download, verify, recover

1. Beside the saved server choose **Download server modpack**, or use the mismatch dialog. Verify the certificate before entering the server password. Review mods, archive/expanded sizes and reuse/install actions; separately approve mod code and shared settings.
2. Let the client finish its resumable download and SHA-256/path/size verification. New mods use separate `Mods/rimlink-managed-…` folders. An exact installed copy can be reused; conflicting or duplicate unmanaged copies block installation and show their paths. They are never overwritten. Extra gameplay mods are disabled, not deleted.
3. The complete settings snapshot is staged before one restart. Matching loaded mods/settings and successful login are required for verification. Failure preserves installed files and backups for retry; no restart loop is expected.
4. If interrupted, use **Roll back installation** from the server list for a pending journal. Preserve `Rimlink/install-journal.json`, the pending marker and backups. External modifications stop automatic recovery: resolve the indicated files manually with the game closed. Loaded assemblies cannot be replaced or restored safely.

## Limits and safety {#limits}

The game, Core and DLC are not included. A downloader working without Steam is not proof of tested GOG gameplay. Do not bypass a hash mismatch by editing the manifest.

## See also {#related}

- [Versions and compatibility](../compatibility.md)
- [Managing modpacks](../admins/modpacks.md)
- [Installing the mod](./install.md)
