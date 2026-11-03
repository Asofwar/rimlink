---
pageId: developers/build
contentRevision: 1
---

# Building the project

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Install the SDK pinned in global.json; package versions are centralized in Directory.Packages.props. From the repository root run:

`dotnet build Rimlink.sln`

`dotnet test Rimlink.sln -nologo -v q`

The mod is outside the solution: close the game and use PowerShell scripts/Build-ClientMod.ps1 -RimWorldDir &lt;game&gt; -InstallTo &lt;game/Mods&gt;. -BundleHarmony is only for local runs without separate Harmony, never releases. scripts/Package-Release.ps1 produces release ZIPs.

For the wiki: npm --prefix wiki ci --ignore-scripts, then run test:checks, check:content, check:reference, build and check:links. Node 24 is pinned separately. BridgeTools requires its separate Build-BridgeTools.ps1 and an installed game.

## Limits and safety {#limits}

Do not replace a running game's DLLs or restart someone else's instance. A solution build proves neither mod compilation nor gameplay. Keep bin/obj/data/saves/secrets and generated wiki output out of commits.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Contributing and wiki maintenance](./contributing.md)
- [Hosting and startup](../admins/hosting.md)
