# Rimlink Modpack Dialog, Rejoin and Server Mod Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A player whose mods do not match the server fixes everything with one button and is logged back in after the game restarts; the server compares mods by category (missing, version, files, extra, order, settings) and shares the settings of whitelisted mods.

**Architecture:** The client sends a richer mod list at login (load position, a hash of each mod's `Defs/Patches/Assemblies` files, content flags, a hash of every mod settings file in effect). The server's `ModpackComparer` builds a categorized `ModpackDiff`, including the server's settings files for whitelisted mods. The game draws the categories, and "Fix and restart" applies the server order, stores the server settings for a settings-path redirect read at startup, leaves a join intent and restarts; the main menu consumes the intent once.

**Tech Stack:** .NET 10, gRPC/protobuf, ASP.NET Core minimal APIs, EF Core + SQLite, System.CommandLine, Lib.Harmony 3.0.0-rc.3, RimWorld 1.7, Steamworks.NET, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-modpack-join-design.md`

## Global Constraints

- Protocol only grows: new field numbers and messages exactly as spec §2 (`ModRef` 3–7, `LoginRequest.mod_settings = 6`, `RegisterRequest.mod_settings = 5`, `ModpackEntry.files_hash = 7`, `VersionMismatch.side = 4`, `ModpackDiff` 5–7, `ErrorCode.MODPACK_CONFIGS_INVALID = 32`). `ProtocolVersion.Current`, `<modVersion>` and `ClientInfo.Version` unchanged. Numbers may collide with parallel branches: the later branch renumbers at rebase.
- Limits in `Rimlink.Protocol.Limits`: `MaxModConfigBytes = 256 * 1024`, `MaxModConfigsTotalBytes = 768 * 1024`, `MaxModConfigs = 64` (ruling against the spec draft's 2 MB: the configs travel in `LoginReply`, capped by `MaxMessageBytes` = 1 MB; the spec was amended).
- `Rimlink.Protocol` and `Rimlink.Client.Core` stay free of `Verse`/`RimWorld`/Unity references.
- Settings files are copied byte for byte (no XML re-serialisation, no line-ending or BOM changes): the client's hash after "Fix and restart" must equal the server's.
- The player's own `Config/Mod_*.xml` files are never read, written, moved or deleted by Rimlink.
- Passwords are never written to disk (join intent keeps only the login name).
- Admins skip the modpack check (unchanged). An empty modpack turns the whole check off, settings included.
- Every user-facing string is a keyed string in `mod/Languages/English/Keyed/Rimlink.xml` and `mod/Languages/Russian/Keyed/Rimlink.xml`.
- Game objects only on the main thread (`MainThread.Post`); network and file scans on background tasks.
- Before every commit: `dotnet test Rimlink.sln -nologo -v q` green; from Task 6 on also `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-bt\RimWorld'` and `.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-bt\RimWorld' -NoInstall` build. Commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, no trailers. No merge, no push.
- Live checks only on the BT stand (`D:\Build\rimlink-bt`, server :25570, GABS `rimlink-bt-host`/`rimlink-bt-guest`).

## Review Focus

1. An old client (no `files_hash`, no `mod_settings`) against a server with file hashes, a non-trivial order and settings configs: expected exactly today's result (only missing/version/extra), never a refusal for order, files or settings.
2. Order with allowed mods, auto-allowed client mods and Core/DLC entries interleaved, and duplicated `_steam` ids: only required mods present on both sides count; a client that has every required mod in the server's relative order logs in.
3. Settings round trip: server config written by the client and read back through the redirect gives the same SHA-256 as the server row (byte-identical write, no BOM/newline change), so the "Settings" category does not come back after the restart.
4. A diff with configs near the limits still fits `LoginReply` under `Limits.MaxMessageBytes`; an oversized set is refused at `SetConfigs` with `MODPACK_CONFIGS_INVALID`, never at login.
5. The join intent can never cause a restart loop (one `Take` per launch, file deleted on read even when stale or unreadable), and a broken `active.json` never stops the game from starting (no redirect, one log line).

---

### Task 1: Protocol additions and `ModContent`

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/common.proto`, `src/Rimlink.Protocol/Protos/auth.proto`, `src/Rimlink.Protocol/Protos/modpack.proto`, `src/Rimlink.Protocol/Limits.cs`
- Create: `src/Rimlink.Protocol/ModContent.cs`
- Test: `tests/Rimlink.Client.Tests/Mods/ModContentTests.cs`

**Interfaces:**
- Produces: proto types `ModSettingsHash`, `FixSide`, `FileMismatch`, `ModConfig`, `ModConfigList`, `ErrorCode.ModpackConfigsInvalid`, new fields per Global Constraints; `Modpack.SetConfigs/GetConfigs` RPCs.
- Produces: `namespace Rimlink.Protocol; public sealed record ModContentInfo(string FilesHash, bool HasDefs, bool HasPatches, bool HasAssemblies); public static class ModContent { public const string NoSettings = "none"; public const string RimlinkPackageId = "asofwar.rimlink"; public static ModContentInfo Scan(string modRoot); public static string Hash(byte[] content); }` — `Hash` = lower-case hex SHA-256.

- [ ] **Step 1: Write the failing tests** in `ModContentTests` (temp dir per test, deleted in `Dispose`):
  - `HashCoversDefsPatchesAndAssembliesAtAnyDepth`: files in `Defs/a.xml`, `1.7/Patches/p.xml`, `Common/Assemblies/x.dll` → `HasDefs`, `HasPatches`, `HasAssemblies` all true; changing any of those files changes `FilesHash`.
  - `OtherFoldersDoNotChangeTheHash`: adding `Textures/t.png`, `Languages/English/Keyed/k.xml`, `About/About.xml` leaves `FilesHash` equal and all flags false for a mod made only of them.
  - `FolderNamesAreCaseInsensitiveAndOrderIndependent`: `defs/` vs `Defs/` and files created in different orders give the same hash.
  - `RenamingAFileChangesTheHash` (path is part of the hash).
  - `EmptyModHasTheEmptySetHash`: no counted files → `FilesHash == ModContent.Hash([])`-style constant of an empty input stream, flags false.
  - `HashIsLowerHexSha256`: `ModContent.Hash("abc"u8.ToArray()) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo --filter FullyQualifiedName~ModContentTests` — Expected: build error, `ModContent` does not exist.
- [ ] **Step 3: Add the proto fields/messages/RPCs and limits exactly as spec §2 and Global Constraints; implement `ModContent.Scan`:** enumerate all files under `modRoot`; a file counts when any directory segment of its path relative to `modRoot` equals `Defs`, `Patches` or `Assemblies` (ordinal ignore case); sort counted relative paths (lower case, `/`) ordinal; SHA-256 over, per file, UTF-8 path bytes, one `0` byte, the file length as 8 bytes little-endian, the content. `Scan` of a missing directory throws `DirectoryNotFoundException`.
- [ ] **Step 4: Run** the same filter — Expected: PASS; then `dotnet test Rimlink.sln -nologo -v q` — Expected: all green (proto additions are compatible).
- [ ] **Step 5: Commit** `git commit -m "Add mod content hashing and the protocol for categorized modpack diffs and server mod settings"`

### Task 2: Server storage, settings whitelist service, gRPC and REST

**Files:**
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs`, `src/Rimlink.Server/Storage/RimlinkDbContext.cs`, `src/Rimlink.Server/Application/Modpack/ModpackService.cs`, `src/Rimlink.Server/Grpc/ModpackGrpcService.cs`, `src/Rimlink.Server/Rest/AdminEndpoints.cs`
- Create: migration `ModpackFilesAndConfigs` (`dotnet ef migrations add ModpackFilesAndConfigs --project src/Rimlink.Server`)
- Test: `tests/Rimlink.Server.Tests/Modpack/ModpackConfigsTests.cs`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`

**Interfaces:**
- Consumes: Task 1 proto types and limits.
- Produces: `ModpackEntry.FilesHash` (`string?`, column `FilesHash`); entity `ModpackConfig { long Id; string PackageId; string Handle; byte[] Content; string Hash; }`, table `modpack_configs`, unique index `(PackageId, Handle)`; `DbSet<ModpackConfig> ModpackConfigs`.
- Produces: `ModpackService.GetConfigs() → Task<List<Protocol.V1.ModConfig>>` (content and hash included); `ModpackService.SetConfigs(IReadOnlyList<Protocol.V1.ModConfig> configs, Role actorRole, string actorName) → Task`; `ModpackService.Set` stores `FilesHash` (empty → null) and `ToProto` returns it.
- Produces: REST `GET /api/admin/modpack/configs` → `[{packageId, handle, hash, size}]` (`ApiScope.Read`); `PUT /api/admin/modpack/configs` with `{configs: [{packageId, handle, contentBase64}]}` (admin scope, like `PUT /modpack`); `ModpackEntryBody` gains `string? FilesHash`.

- [ ] **Step 1: Write the failing tests** in `ModpackConfigsTests` (real server via `ServerFixture`, admin logged in, modpack set first with `asofwar.rimlink` at position 2):
  - `AdminSetsAndReadsConfigs`: `SetConfigs` two configs for mods at positions 3 and 4 → `GetConfigs` returns both with `hash == ModContent.Hash(content)` computed by the server (the client-sent hash is ignored).
  - `ReplacingTheSetDropsOldRows`, `EmptySetTurnsSyncOff`.
  - `RefusedWhenModIsNotInTheModpack`, `RefusedWhenModLoadsBeforeRimlink` (position 1 < Rimlink's 2), `RefusedWhenAFileIsTooLarge` (`MaxModConfigBytes + 1`), `RefusedWhenTheTotalIsTooLarge` (four files of 200 KB), `RefusedWhenThereAreTooManyConfigs` (65) — each `RpcException` with `ErrorCode.ModpackConfigsInvalid` read by `EconomyClient.CodeOf`-style trailer parsing (use the existing test helper for error codes) and a reason in the message; nothing stored.
  - `PlayerCannotSetConfigs` → `PermissionDenied`.
  - `RestPutAndGetConfigs`: `PUT /api/admin/modpack/configs` with base64 content, `GET` lists `{packageId, handle, hash, size}` without content.
  - `ModpackKeepsFilesHash`: `Set` with `files_hash` → `Get` returns it; REST `PUT /api/admin/modpack` with `filesHash` too.
  - `SchemaTests`: table `modpack_configs` and column `FilesHash` on the modpack table exist after migration.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests -nologo --filter "FullyQualifiedName~ModpackConfigsTests|FullyQualifiedName~SchemaTests"` — Expected: build errors (`SetConfigs`/`ModpackConfigs` missing).
- [ ] **Step 3: Implement** entities, DbSet, the migration, `ModpackService` methods, gRPC overrides (admin check as `Set`), REST endpoints. Validation order in `SetConfigs`: count, each size, total size, then every `package_id` (normalized with `ModpackComparer.Normalize`) must be a modpack entry whose `Position` is greater than the `ModContent.RimlinkPackageId` entry's (if Rimlink is not in the modpack, refuse with "Rimlink is not in the modpack"); failures throw `RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.ModpackConfigsInvalid, reason)`. Replace the set in one transaction; log `Modpack configs set by {Actor}: {Count} files ({Bytes} bytes)`.
- [ ] **Step 4: Run** the filter — Expected: PASS; full suite green.
- [ ] **Step 5: Commit** `git commit -m "Store mod file hashes and a whitelist of server mod settings, with gRPC and REST"`

### Task 3: Categorized comparison and the login check

**Files:**
- Modify: `src/Rimlink.Server/Application/Modpack/ModpackComparer.cs`, `src/Rimlink.Server/Application/Auth/LoginResult.cs` (`LoginContext`), `src/Rimlink.Server/Application/Auth/LoginGate.cs`, `src/Rimlink.Server/Grpc/AuthGrpcService.cs` (register path passes `mod_settings`)
- Test: `tests/Rimlink.Server.Tests/Modpack/ModpackComparerTests.cs`, `tests/Rimlink.Server.Tests/Modpack/ModpackLoginTests.cs`

**Interfaces:**
- Consumes: Task 1 types; Task 2 `ModpackService.GetConfigs`.
- Produces: `ModpackComparer.Compare(IReadOnlyList<ModpackEntry> server, IReadOnlyList<ModRef> client, IReadOnlyList<ModConfig>? configs = null, IReadOnlyList<ModSettingsHash>? settings = null) → ModpackDiff?` (old two-argument calls keep compiling and behaving); `LoginContext` gains `IReadOnlyList<ModSettingsHash> ModSettings` (from `LoginRequest.ModSettings` / `RegisterRequest.ModSettings`).

- [ ] **Step 1: Write the failing tests** (unit, `ModRef` helper extended with `position`, `filesHash`, flags):
  - Version side: client `1.2.0` vs server `1.1.9` → `Side == FixSide.Server`; client `1.1` vs server `1.2` → `Player`; `1.2-beta` vs `1.2` (not numeric) → `Player`; numeric compare by components (`1.10 > 1.9`).
  - Files: same version, both hashes, different → one `FileMismatch {package_id, version}`; empty hash on either side → none.
  - Auto-allow: extra mod with `files_hash` set and all flags false → not in `Extra`; same mod with `has_defs` → in `Extra`; with `has_assemblies` only → in `Extra`; an old client (no `files_hash` on any mod) with a flagless extra → in `Extra`.
  - Order: required `a,b,c` at positions 0,1,2 vs client `a,c,b` → `OrderDiffers`; client `a,x,b,c` where `x` is allowed/auto-allowed/extra → no order difference from `x`; server allowed mod out of place → ignored; old client → never `OrderDiffers`.
  - Settings: config `(m, MyMod.Settings)` hash `H`, client `ModSettingsHash(m, MyMod.Settings, H2)` → `Configs` holds the server config with content; equal hash → none; client has no entry for that pair (mod missing) → none; client sent no `mod_settings` at all → settings not checked.
  - Only order differs → diff not null (refusal) with `Missing/Extra/WrongVersion/Files/Configs` empty.
  - Existing tests still pass unchanged.
  - Login integration (`ModpackLoginTests`): `OldClientLogsInDespiteHashesOrderAndConfigs` (Review Focus 1); `NewClientWithWrongOrderIsRefusedWithOrderOnly`; `NewClientWithDifferentSettingsGetsTheServerConfigInTheDiff`; `RegisterChecksTheSameWay` (register with `mod_settings`); `AdminSkipsTheCheck`; `DiffWithConfigsAtTheLimitFitsTheReply` (64 configs totalling `MaxModConfigsTotalBytes` → login reply received, size under `Limits.MaxMessageBytes`).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests -nologo --filter FullyQualifiedName~Modpack` — Expected: FAIL (new assertions) / build errors for the new overload.
- [ ] **Step 3: Implement** per spec §3: "new client" = any `ModRef` with non-empty `files_hash`; order = server required entries ordered by `Position`, filtered to ids present on the client, mapped to the client's `position`, differs when not strictly increasing; version compare splits on `.` and needs every component to parse as `int` on both sides; diff is null only when all six categories are empty. `LoginGate` calls `Compare(await modpack.Get(), ctx.Mods, await modpack.GetConfigs(), ctx.ModSettings)`.
- [ ] **Step 4: Run** the filter — Expected: PASS; full suite green.
- [ ] **Step 5: Commit** `git commit -m "Compare mods by category: version side, files, order, auto-allowed content-free mods and server settings"`

### Task 4: CLI — file hashes on import, settings whitelist commands

**Files:**
- Modify: `src/Rimlink.Tools/Commands/ModpackCommand.cs`
- Create: `src/Rimlink.Tools/Commands/ModpackConfigsCommand.cs`
- Test: `tests/Rimlink.Server.Tests/Tools/ModpackImportTests.cs`, `tests/Rimlink.Server.Tests/Tools/ModpackConfigsCommandTests.cs`

**Interfaces:**
- Consumes: `ModContent.Scan` (Task 1); REST endpoints (Task 2).
- Produces: `ModpackCommand.Entry` gains `string FilesHash` (last positional parameter), filled by `ModContent.Scan(modRoot).FilesHash` for every active mod whose folder was found, `""` otherwise; `ModpackConfigsCommand.Collect(string configDir, IReadOnlyList<string> modsDirs, IReadOnlyList<string> packageIds) → List<ConfigFile>`, `public sealed record ConfigFile(string PackageId, string Handle, string Path, byte[] Content)`; commands `modpack configs import --config-dir --mods-dir… --mod… [--clear] [--dry-run] --server --token [--insecure]` and `modpack configs list --server --token [--insecure]`.

- [ ] **Step 1: Write the failing tests** (temp dirs):
  - `ImportFillsFilesHash`: `ModsConfig.xml` with two mods, one folder with `About.xml` + `Defs/x.xml` → its entry's `FilesHash == ModContent.Scan(folder).FilesHash`; the mod without a folder → `""`.
  - `CollectMapsFolderNamesToPackageIds`: mods dir `Mods/2009463077/About/About.xml` (`<packageId>Author.Mod</packageId>`), config dir with `Mod_2009463077_ModSettingsClass.xml`, `Mod_2009463077_Other.xml`, `Mod_999_Other.xml` → for `--mod author.mod` two `ConfigFile`s with `PackageId == "author.mod"`, handles `ModSettingsClass` and `Other`, exact bytes.
  - `CollectFailsForAModWithoutSettingsFiles` (`InvalidOperationException` naming the mod) and `CollectFailsForAnUnknownPackageId`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests -nologo --filter "FullyQualifiedName~ModpackImportTests|FullyQualifiedName~ModpackConfigsCommandTests"` — Expected: build errors.
- [ ] **Step 3: Implement.** `import` prints every file (`<packageId> <handle> <bytes>`) and the warning "Mod settings may hold secrets (Twitch tokens and the like): check these files before sharing them with every player." before uploading; `--dry-run` stops there; `--clear` uploads an empty set. Handle = file name without the `Mod_<folder>_` prefix and `.xml` suffix. Wire under `modpack` next to `ModpackDefsCommand`.
- [ ] **Step 4: Run** the filter — Expected: PASS; full suite green.
- [ ] **Step 5: Commit** `git commit -m "Hash mod files on modpack import and add the mod settings whitelist commands"`

### Task 5: Client core — join intent, server settings store, diff view, scan cache

**Files:**
- Create: `src/Rimlink.Client.Core/Mods/JoinIntentStore.cs`, `src/Rimlink.Client.Core/Mods/ServerSettingsStore.cs`, `src/Rimlink.Client.Core/Mods/ModpackDiffView.cs`, `src/Rimlink.Client.Core/Mods/ModContentCache.cs`
- Test: `tests/Rimlink.Client.Tests/Mods/JoinIntentStoreTests.cs`, `ServerSettingsStoreTests.cs`, `ModpackDiffViewTests.cs`, `ModContentCacheTests.cs`

**Interfaces:**
- Produces (namespace `Rimlink.Client.Mods`):
  - `public sealed record JoinIntent(string Host, int Port, string Display, string? Login, DateTime CreatedUtc)`; `public sealed class JoinIntentStore(string path) { public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15); public void Save(JoinIntent intent); public JoinIntent? Take(DateTime nowUtc); }` — `Take` always deletes the file (also when stale or unreadable) and returns the intent only when `nowUtc - CreatedUtc <= MaxAge`; JSON via `System.Text.Json`.
  - `public sealed record ServerSettingsSet(string Server, string Display, IReadOnlyList<SettingsKey> Mods)`; `public readonly record struct SettingsKey(string PackageId, string Handle)`; `public sealed class ServerSettingsStore(string root) { public static string ServerKey(string host, int port); public string PathFor(string server, SettingsKey key); public void Activate(string server, string display, IEnumerable<ModConfig> configs); public ServerSettingsSet? ReadActive(); public void Clear(); }` — files at `<root>/<server>/Mod_<packageId>_<handle>.xml`, written with `File.WriteAllBytes(config.Content.ToByteArray())`; `active.json` at `<root>/active.json`; `ReadActive` returns null for a missing or broken file (never throws); `Activate` keeps earlier files of other servers.
  - `public enum DiffCategory { Missing, Version, Files, Extra, Order, Settings }`; `public sealed record DiffRow(string PackageId, string Detail, FixSide Side)`; `public sealed record DiffSection(DiffCategory Category, IReadOnlyList<DiffRow> Rows)`; `public static class ModpackDiffView { public static IReadOnlyList<DiffSection> Sections(ModpackDiff diff); }` — non-empty sections only, in enum order; `Detail`: missing → version and source (`Steam`, URL or empty), version → `"<client> → <server>"`, files → version, extra → `""`, settings → handle; `Order` has no rows.
  - `public sealed class ModContentCache { public void Start(IEnumerable<(string PackageId, string Root)> mods); public Task<IReadOnlyDictionary<string, ModContentInfo>> Ready { get; } }` — scans on a background task; a mod whose scan throws is left out of the dictionary (spec §9).
- [ ] **Step 1: Write the failing tests:** `Take` fresh → intent, file gone; `Take` 16 minutes later → null, file gone; second `Take` → null; broken JSON → null, file gone; `Activate` → `ReadActive` round-trips keys, bytes at `PathFor` equal the config content exactly (content with a BOM and CRLF stays byte-identical, hash equals `ModContent.Hash`), broken `active.json` → null, `Clear` → null; `Sections` order and details for a diff with all six categories, empty diff → empty list; cache with one good and one missing folder → dictionary holds the good one only.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo --filter FullyQualifiedName~Rimlink.Client.Tests.Mods` — Expected: build errors.
- [ ] **Step 3: Implement** the four types.
- [ ] **Step 4: Run** the filter — Expected: PASS; full suite green.
- [ ] **Step 5: Commit** `git commit -m "Add the join intent, server settings store, diff view and mod scan cache to the client core"`

### Task 6: Game — richer login, settings redirect, dialog, fix and restart, rejoin

**Files:**
- Modify: `src/Rimlink.Client/RimlinkMod.cs`, `src/Rimlink.Client/UI/ModpackActions.cs`, `src/Rimlink.Client/UI/SteamAuth.cs`, `src/Rimlink.Client/UI/Dialog_ModpackDiff.cs`, `src/Rimlink.Client/UI/MainMenuPatch.cs`, `src/Rimlink.Client/UI/Dialog_Login.cs`, `src/Rimlink.Client/UI/LoginFlow.cs`, `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`
- Create: `src/Rimlink.Client/Game/Patches/Patch_LoadedModManager_GetSettingsFilename.cs`, `src/Rimlink.Client/UI/ServerSettings.cs`

**Interfaces:**
- Consumes: Task 5 types; Task 1 `ModContent`.
- Produces (game side, no automated tests; verified in Task 7):
  - `RimlinkMod` ctor, before `GetSettings`: `ServerSettings.Load(new ServerSettingsStore(<SaveData>/Rimlink/server-settings))`, then a separate `new Harmony("asofwar.rimlink.settings").Patch(AccessTools.Method(typeof(LoadedModManager), "GetSettingsFilename"), postfix: …)` (the class has no `[HarmonyPatch]`, so `PatchAll` skips it), then `ModContentCache.Start` over `LoadedModManager.RunningMods` (`PackageId` normalized like `ModpackComparer.Normalize`, `RootDir`).
  - `ServerSettings.Redirect(string modIdentifier, string handle, string original) → string`: maps `modIdentifier` (folder name) to a package id through `ModLister.AllInstalledMods` (`FolderName`), returns `store.PathFor(...)` for keys in the active set, else `original`. `ServerSettings.Active` (set or null) for the main menu.
  - `ModpackActions.RunningMods()` → `Task<IEnumerable<ModRef>>` with `position`, and `files_hash`/flags from the cache; `ModpackActions.RunningModSettings()` → `ModSettingsHash` per running `Mod` instance (`LoadedModManager.ModHandles`, file from the private `GetSettingsFilename` via `AccessTools`, so the redirect applies), `ModContent.Hash(File.ReadAllBytes)` or `ModContent.NoSettings`. `GameCredentials.BuildLogin` fills both (awaits the cache).
  - `Dialog_ModpackDiff(ModpackDiff diff, ServerEndpoint endpoint, string? login)`: draws `ModpackDiffView.Sections` with the category headers and hints (keys below), buttons Subscribe / Open links / Fix and restart / Close. Fix and restart: (1) while any subscribed missing Workshop item is not `Installed` or is `Downloading`/`NeedsUpdate` (`SteamUGC.GetItemState`), show "Downloading N mods…" and wait (re-check every frame, no blocking); (2) if `diff.Configs` non-empty, `store.Activate(ServerKey(host, port), display, diff.Configs)`, on IOException show the error and stop; (3) `ApplyServerOrder` keeping client mods that the server would auto-allow (no defs/patches/assemblies per the cache) appended after the server list; (4) `new JoinIntentStore(<SaveData>/Rimlink/join-intent.json).Save(...)`; (5) `GenCommandLine.Restart()`.
  - `MainMenuPatch.Prefix`: once per launch, `Take(DateTime.UtcNow)`; an intent starts `LoginFlow.Run(endpoint from the saved server list or a new ServerEndpoint(host, port, display), new GameCredentials { PrefillLogin = intent.Login })`; when `ServerSettings.Active` is set, inserts an option "Mod settings of {0} in effect: use mine" that asks for confirmation, calls `store.Clear()` and offers `GenCommandLine.Restart()`.
  - `Dialog_Login(TaskCompletionSource<PasswordLogin?> answer, string? error = null, string? login = null)` pre-fills the login; `GameCredentials.PrefillLogin` passes it.
  - Keyed strings (EN and RU): `Rimlink.Modpack.Missing.Hint`, `.Version.Hint.Player`, `.Version.Hint.Server`, `.Files`, `.Files.Hint`, `.Extra.Hint`, `.Order`, `.Order.Hint`, `.Settings`, `.Settings.Hint`, `.FixRestart`, `.Downloading`, `.SettingsWriteFailed`, `Rimlink.MainMenu.ServerSettings`, `Rimlink.MainMenu.ServerSettingsConfirm`; existing `Rimlink.Modpack.Missing/WrongVersion/Extra` kept.

- [ ] **Step 1: Implement** the items above (game code: no RED step; the test is Task 7's run).
- [ ] **Step 2: Build** `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-bt\RimWorld'` and `.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-bt\RimWorld' -NoInstall` — Expected: both succeed; `dotnet test Rimlink.sln -nologo -v q` green.
- [ ] **Step 3: Commit** `git commit -m "Show the modpack diff by category, fix and restart into the server, and use the server's mod settings"`

### Task 7: In-game run on the BT stand, docs, checklist

**Files:**
- Modify: `docs/player-guide.md`, `docs/server-setup.md`, `docs/manual-checklist.md`, `AGENTS.md` (only if BridgeTools tools change), `tools/Rimlink.BridgeTools/StatusTools.cs` (only if the run needs the diff in `rimlink/status`)

- [ ] **Step 1: Deploy** mod and BridgeTools to `D:\Build\rimlink-bt\RimWorld` (games closed), start the BT server from this worktree (Release exe, `Rimlink__Server__Port=25570`, `DataDir D:/Build/rimlink-bt/server`, admin token), import the modpack with `rimlink modpack import --from <guest Config>\ModsConfig.xml --mods-dir D:\Build\rimlink-bt\RimWorld\Mods <workshop content dir> --server https://127.0.0.1:25570 --token … --insecure`, and a settings whitelist for one mod with settings that loads below Rimlink.
- [ ] **Step 2: Run each case on the guest** (copy `Player.log` and `~/.gabs/rimlink-bt-guest/launch.log` aside before each): a required mod disabled; two required mods swapped; one byte changed in a mod's `Defs` file; an extra mod with defs; an extra translation-only mod (must log in); the whitelisted mod's setting changed. Expected each time: the dialog shows exactly the category; "Fix and restart" restarts; after restart the game logs in by itself (server log `Login bt-guest`/`Nova-bt`) without the dialog; for settings, the redirected file's hash equals the server row (`sqlite3 D:\Build\rimlink-bt\server\rimlink.db "select Hash from modpack_configs"`) and the player's own `Config/Mod_*.xml` is unchanged (hash before/after). Restore the guest's mods after each case. After a restart, reattach GABS (`games_connect rimlink-bt-guest` or `games_start`).
- [ ] **Step 3: Check the "use mine" option** in the main menu: clears `active.json`, offers restart; after restart the player's own settings are in effect and the next login shows "Settings".
- [ ] **Step 4: Write docs:** player guide (categories and who fixes, fix and restart, rejoin, server settings and "use mine"), server setup (`modpack import` file hashes, `modpack configs import/list`, secrets warning, Rimlink above synced mods, limits), checklist entries with the run notes; restore the BT stand's modpack/whitelist to empty at the end.
- [ ] **Step 5: Commit** `git commit -m "Record the modpack dialog, rejoin and server settings runs and document them"`
