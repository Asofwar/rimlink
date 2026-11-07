---
pageId: compatibility
contentRevision: 4
---

# Versions and compatibility

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Use RimWorld 1.7, Rimlink 0.9.1 and Harmony 3.x. Server and mod versions must be compatible; the current network protocol is 1. Use a release package and the administrator's modpack.

Load order, package IDs, versions, file contents and mod settings may be checked at connection time. The difference dialog explains what to change. Back up local data and close the game before updating.

## How do I check a setup before joining? {#checklist}

Check four independent layers: RimWorld 1.7; Rimlink 0.9.1 on client and server; Harmony 3.x; and the chosen server's collection. Matching mod names is insufficient: package IDs, versions, order, file contents and shared settings can differ.

1. Obtain the Rimlink version and required DLC from the administrator.
2. Avoid two active copies of one packageId: a Workshop copy and local folder must not compete.
3. Restart after changing the collection. Loaded DLLs cannot be reliably swapped in place.
4. Pass the mismatch dialog. Visits/live must also load the host map's dependencies.

“Newer” is not automatically compatible with an older server. If Workshop has advanced, use an administrator-approved package or coordinate a server update; never edit the expected hash.

## What is verified and what is not promised? {#verified}

Automated server tests do not establish compatibility with every game mod. Mods affecting serialization, pawns, map simulation or UI require collection-specific checks. Before a coordinated upgrade, test login, colony loading, an item transfer and the intended live scenario on a test server.

Non-Steam package delivery was checked with an isolated copy of a Steam installation. That is not acceptance of an actual GOG runtime. GOG requires enabled password authentication and legally redistributable dependencies; Core/DLC are never delivered.

## Limits and safety {#limits}

Steam authentication requires Steam to be running. GOG needs password login enabled and a redistributable server package; an actual GOG runtime has not yet been verified. Rimlink does not redistribute Core or DLC.

## See also {#related}

- [Installing the mod](./players/install.md)
- [Server modpacks](./players/modpacks.md)
- [Updating server and clients](./admins/updating.md)
