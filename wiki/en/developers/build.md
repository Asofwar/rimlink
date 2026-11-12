---
pageId: developers/build
contentRevision: 10
---

# Building the project

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Use SDK 10.0.403 selected by global.json with rollForward=disable. Build in an isolated toolchain directory and record the loaded runtime as well as the SDK. Server and CLI runtimeconfigs require NETCore and ASP.NET 10.0.14 with RollForward=Disable. The mod uses the installed game runtime; rebuild it and the development companion when RimWorld changes.

Install the SDK pinned in global.json; package versions are centralized in Directory.Packages.props. From the repository root run:

`dotnet build Rimlink.sln`

`dotnet test Rimlink.sln -nologo -v q`

The mod is outside the solution: close the game and use PowerShell scripts/Build-ClientMod.ps1 -RimWorldDir &lt;game&gt; -InstallTo &lt;game/Mods&gt;. -BundleHarmony is only for local runs without separate Harmony, never releases. scripts/Package-Release.ps1 produces release ZIPs.

For the wiki: npm --prefix wiki ci --ignore-scripts, then run test:checks, check:content, check:reference, build and check:links. Node 24 is pinned separately. BridgeTools requires its separate Build-BridgeTools.ps1 and an installed game.

## From checkout to an assembled mod {#build-track}

1. Read global.json, install the matching SDK and inspect dotnet --info. Do not upgrade central dependencies to hide a local error.
2. Run dotnet build Rimlink.sln and dotnet test Rimlink.sln -nologo -v q from the root. This builds server/core/tools/tests, not the game mod.
3. The mod needs installed RimWorld 1.7 with Assembly-CSharp.dll and PowerShell. Close your own test game and pass -RimWorldDir plus a **new -OutputDir** to Build-ClientMod.ps1. Without -InstallTo it only assembles output; do not touch another session's Mods.
4. Inspect output About, Languages and Assemblies. Never bundle Harmony in a release; -BundleHarmony is local-only.
5. Installing into a test copy and running two clients is a separate acceptance stage, not implied by compilation.

The script clears OutputDir: never point it at source, the game or your only data copy.

## Limits and safety {#limits}

Do not replace a running game's DLLs or restart someone else's instance. A solution build proves neither mod compilation nor gameplay. Keep bin/obj/data/saves/secrets and generated wiki output out of commits.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Contributing and wiki maintenance](./contributing.md)
- [Hosting and startup](../admins/hosting.md)
