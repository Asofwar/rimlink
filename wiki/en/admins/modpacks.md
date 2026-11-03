---
pageId: admins/modpacks
contentRevision: 1
---

# Managing modpacks

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. Prepare a tested set in a separate game installation. Record ModsConfig.xml, load order, versions and hashes.
2. Run modpack import --from &lt;ModsConfig.xml&gt; --mods-dir &lt;folders&gt; --dry-run. After review, add --server/--token to upload requirements.
3. modpack defs collects permitted ThingDef/PawnKindDef names; an empty list disables that check. Import shared settings only for selected --mod entries loading below Rimlink.
4. For server delivery, run modpack package requirements --output &lt;new-file&gt;, then package pack --source &lt;redistributable-folders&gt; --requirements &lt;file&gt; --output &lt;new-directory&gt;.
5. Review archive.zip, manifest.json, licenses and settings. package publish --from &lt;directory&gt; requires --confirm-redistribution; --confirm-public and --confirm-shared-settings acknowledge those disclosures. Inspect package status; unpublish removes publication.

Test a clean client after changing the collection.

## Limits and safety {#limits}

Do not package the game directory, Core/DLC or mod secrets. Pack requires the complete prepared collection and a new output directory. Publishing is a separate external operator action. Requirement changes may invalidate a previously published package.

## See also {#related}

- [CLI reference](./cli.md)
- [Server modpacks](../players/modpacks.md)
- [Updating server and clients](./updating.md)
