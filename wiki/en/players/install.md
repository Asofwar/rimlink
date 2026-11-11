---
pageId: players/install
contentRevision: 7
---

# Installing the mod

Applies to: Rimlink 0.10.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Download a Rimlink archive from the repository releases. Close RimWorld. Extract it so that the Rimlink directory directly contains About and Assemblies, not another nested Rimlink folder. Install Harmony 3.x separately; release packages do not bundle Harmony.

Enable Harmony and Rimlink in the game's mod list, respecting dependencies and server instructions. Restart and verify that **Multiplayer** appears. For load failures, first check versions and directory layout, then inspect the game log. Install the server modpack separately or through its offered downloader.

### Install from a release

1. Open [Rimlink releases](https://github.com/Asofwar/rimlink/releases) and choose the client mod archive for your server version, not the server or source archive.
2. Exit RimWorld. In Steam, use **Manage → Browse local files** to locate the game directory. Put the extracted Rimlink folder under its `Mods` directory: `Mods/Rimlink/About/About.xml` and `Mods/Rimlink/Assemblies` must exist. Avoid `Mods/Rimlink/Rimlink` nesting.
3. Install Harmony 3.x (separate mod). Start the game with Steam running if Harmony comes from Workshop. In **Mods**, put Harmony first, then Core and enabled DLC, then dependencies/gameplay mods and Rimlink as required by the server collection.
4. Enable Rimlink, apply the list and restart. Verify **Multiplayer** is available in the main menu before connecting.
5. Connect to the server and complete its modpack check. Reaching the menu alone does not prove your collection matches.

If Rimlink is absent from the list, check the folder nesting and About metadata. If it is listed but fails to load, verify RimWorld 1.7, Harmony 3.x and the matching Rimlink release; do not solve an assembly error by copying random DLLs into the mod.

## How do I verify that the client mod is installed? {#verify-install}

Before launching, verify that the Rimlink folder contains About and Assemblies. A server archive containing a .NET executable is not the game mod. Do not mix release contents or enable two Rimlink copies.

Check three signs after startup: Rimlink appears in Mods, dependencies have no errors, and **Multiplayer** appears in the main menu. Then pass the chosen server's collection check. The button alone does not prove collection compatibility.

If an update seems ignored, close the game and verify which installation folder you edited. Different installations/profiles can use different mod lists; a running process holds DLLs. Preserve Config and local Rimlink journals before reinstalling. Deleting saves is not a fix for a missing menu.

## Limits and safety {#limits}

Do not replace DLLs while the game is running. Workshop Harmony may be unavailable without Steam running. Server-delivered mods require redistribution permission.

## See also {#related}

- [Versions and compatibility](../compatibility.md)
- [Server modpacks](./modpacks.md)
- [Player troubleshooting](./troubleshooting.md)
