---
pageId: admins/modpacks
contentRevision: 3
---

# Managing modpacks

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. Prepare a tested set in a separate game installation. Record ModsConfig.xml, load order, versions and hashes.
2. Run modpack import --from &lt;ModsConfig.xml&gt; --mods-dir &lt;folders&gt; --dry-run. After review, add --server/--token to upload requirements.
3. modpack defs collects permitted ThingDef/PawnKindDef names; an empty list disables that check. Import shared settings only for selected --mod entries loading below Rimlink.
4. For server delivery, run modpack package requirements --output &lt;new-file&gt;, then package pack --source &lt;redistributable-folders&gt; --requirements &lt;file&gt; --output &lt;new-directory&gt;.
5. Review archive.zip, manifest.json, licenses and settings. package publish --from &lt;directory&gt; requires --confirm-redistribution; --confirm-public and --confirm-shared-settings acknowledge those disclosures. Inspect package status; unpublish removes publication.

Test a clean client after changing the collection.

## Treat the collection as a release {#modpack-release}

Test the collection on a separate client first. Import --dry-run previews requirements; upload only after review. Supply Steam/local/Workshop roots so hashes/versions match loaded files. Import shared settings only for selected mods: they can contain personal paths or secrets.

Requirements and a delivery ZIP are distinct. Changing requirements can invalidate an older ZIP. Rebuild from redistributable source folders, review manifest/archive and settings consent separately. Do not include Core/DLC or game files.

## Accept a new collection and withdraw publication {#delivery-accept}

On a clean test client verify diff → download → verification → consent → restart → login. Separately test an unmanaged conflicting copy and refusal without overwrite. This does not authorize deleting the user's Mods folder.

Unpublish removes availability; it does not automatically revert installed clients or imported requirements. Retain earlier requirements, package source and settings for coordinated rollback. A DataDir backup does not replace preservation of source mods/licenses and external delivery roots.

## Limits and safety {#limits}

Do not package the game directory, Core/DLC or mod secrets. Pack requires the complete prepared collection and a new output directory. Publishing is a separate external operator action. Requirement changes may invalidate a previously published package.

## See also {#related}

- [CLI reference](./cli.md)
- [Server modpacks](../players/modpacks.md)
- [Updating server and clients](./updating.md)
