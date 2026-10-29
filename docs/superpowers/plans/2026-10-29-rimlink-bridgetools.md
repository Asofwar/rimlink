# Rimlink BridgeTools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An agent drives Rimlink's game flows (login, new colony, raids, visits, live sessions, economy, chat) on the two GABS-managed RimWorld instances through `rimlink/*` bridge tools, without pixel clicks.

**Architecture:** A companion DLL `Rimlink.BridgeTools` built on `RimBridgeServer.Sdk`, deployed alone into `<RimWorld>\BridgeTools\Rimlink\` and bound at runtime to the already-loaded `Rimlink.Client`. The mod gains a small seam (optional parameters only): preset credentials and pin answers for `LoginFlow.Run`, which now returns a `LoginOutcome`, and a public `Dialog_LiveInvite.Answer`. Pure login/pin policies live in `Rimlink.Client.Core` with xUnit tests; game code is verified live through GABS.

**Tech Stack:** .NET 10, RimBridgeServer 2.3.0 SDK (installed DLL), Lib.Harmony 3.0.0-rc.3, RimWorld 1.7 (`Assembly-CSharp.dll`, `UnityStubs.dll`), xUnit, GABS 1.1.2.

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-bridgetools-design.md`

## Global Constraints

- No RimBridgeServer reference anywhere under `src/`; seam changes in `src/Rimlink.Client` are optional parameters / new overloads only, defaults keep today's UI behaviour.
- `tools/Rimlink.BridgeTools/Rimlink.BridgeTools.csproj`, `net10.0`, not in `Rimlink.sln`; every reference `Private=false` / `ExcludeAssets=runtime`; SDK by HintPath `$(RimWorldDir)\Mods\RimBridgeServer\1.6\Assemblies\RimBridgeServer.Sdk.dll`.
- The deployed folder `<RimWorld>\BridgeTools\Rimlink\` holds exactly `Rimlink.BridgeTools.dll`; `scripts/Build-BridgeTools.ps1` refuses otherwise. `Package-Release.ps1` and `Build-ClientMod.ps1` unchanged.
- Protocol, `ProtocolVersion.Current`, `<modVersion>` and `ClientInfo.Version` unchanged.
- Tool names `rimlink/<snake_case>`; parameters are scalars (`string`, `int`, `bool`). Ruling against spec §4 `items: [{def, count}]`: lists are comma-separated strings — `pawns="Thing_Human12,Thing_Human13"` (ids are `GetUniqueLoadID()`, as `rimworld/list_colonists` returns) or names; `items="Silver:100,Steel:50"` — because array/object parameter binding of the SDK is unverified.
- Every result is an anonymous object `{ success, error?, ... }`; never a protobuf message or a `Verse` object.
- Game state is read and written only inside `ctx.MainThread.InvokeAsync`; waits are a loop of `ctx.MainThread.InvokeAsync(predicate)` + `ctx.Game.NextFrameAsync()` (not `RunUntilAsync`, which takes the game-control lease).
- Extension tools time out after 60 000 ms in the host. Tools that wait take `timeoutSec`; callers pass `_rimBridgeTimeoutMs` ≥ `timeoutSec*1000 + 10000` and GABS `timeout` ≥ that in seconds. Tool descriptions say so.
- Passwords never appear in a result or a log line.
- `dotnet test Rimlink.sln -nologo -v q` green, `scripts/Build-ClientMod.ps1` and `scripts/Build-BridgeTools.ps1` build, before every commit. Commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, subject only, no trailers. Branch `asofwar/sweet-chatterjee-6d68a5` (holds the spec commit).
- Live checks: the games `rimworld-host`/`rimworld-guest`, the Mods/BridgeTools folders, `D:\Build\rimlink-gabs` and the server on 25560 belong to session `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`; message it and wait for a window before deploying or starting a game. Deploy only with the game closed.

## Review Focus

1. A second copy of `Rimlink.Client.dll` (or Client.Core/Protocol/Grpc) next to the companion: the extension binds to it and sees an always-empty `RimlinkSession` while the real session is live. Expected: the deploy script refuses a non-empty foreign folder, and `rimlink/status` reports `clientAssembly` (location of the `Rimlink.Client` it bound to) so a mismatch is visible (Task 1 verification).
2. Wrong password with `register=auto`: login refused, registration refused with "login already taken". Expected: `Failed` with that message after exactly two server calls, no third attempt, no `Dialog_Login` (Task 2 test `RegisterIsTriedOnceAfterARefusedLogin`, Task 4 live check).
3. Server with a join password and a missing or wrong `serverPassword`: expected `Failed` "wrong server password" and no `Dialog_ServerPassword` (Task 3 step on `Interactive`; Task 2 test `ServerPasswordIsPassedThrough`).
4. `start_colony` on a non-admin while the server has no world yet, or on a random tile another player already settled: expected an early `success=false` "the server has no world yet" and, for the tile, a retry with a new random tile (Task 6 steps; Task 9 guest run).
5. Any tool in the wrong state (`connect` while a game is loaded or a session is active, `start_colony` without a session, `live_answer` with no invite, `raid` with an unknown player or pawn, `send` with an unknown def): expected `success=false` with the reason and no change to the game (each task's precondition step; Task 9 calls two of them).

---

### Task 1: Project, deploy script, `rimlink/status`, load spike

**Files:**
- Create: `tools/Rimlink.BridgeTools/Rimlink.BridgeTools.csproj`
- Create: `tools/Rimlink.BridgeTools/Support/Bridge.cs`
- Create: `tools/Rimlink.BridgeTools/StatusTools.cs`
- Create: `scripts/Build-BridgeTools.ps1`

**Interfaces:**
- Produces `Support/Bridge.cs` (namespace `Rimlink.BridgeTools`):
  - `static Task<T> Bridge.OnMain<T>(IRimBridgeContext ctx, Func<T> func)` — `ctx.MainThread.InvokeAsync(func)`.
  - `static Task<bool> Bridge.WaitUntil(IRimBridgeContext ctx, Func<bool> condition, int timeoutSec)` — loop: `if (await OnMain(ctx, condition)) return true; await ctx.Game.NextFrameAsync();` until the stopwatch passes `timeoutSec`.
  - `static object Bridge.Fail(string error, object? details = null)` → `new { success = false, error, details }`.
  - `static bool Bridge.TryEnterFlow(string name)` / `static void Bridge.ExitFlow()` — one running `connect`/`start_colony` at a time (`Interlocked` on a static int); `Bridge.RunningFlow` (string?) for the error text.
  - `static string? Bridge.TopWindowType()` — `Find.WindowStack.Windows.LastOrDefault()?.GetType().Name` (main thread only).
- Produces tool `rimlink/status`.

- [ ] **Step 1: Write `Rimlink.BridgeTools.csproj`**

  `RootNamespace`/`AssemblyName` `Rimlink.BridgeTools`; `RimWorldDir` default `D:\SteamLibrary\steamapps\common\RimWorld` (same as the client); `ProjectReference ..\..\src\Rimlink.Client\Rimlink.Client.csproj` with `Private=false`; game references `Assembly-CSharp`, `UnityStubs` `Private=false`; `PackageReference Lib.Harmony ExcludeAssets="runtime"`; `Reference RimBridgeServer.Sdk` HintPath as in Global Constraints, `Private=false`; a `CheckBridgeSdk` target like the client's `CheckRimWorld` that errors with "RimBridgeServer.Sdk.dll not found at '<path>'. Install RimBridgeServer into <RimWorld>\Mods." when missing. `CopyLocalLockFileAssemblies` false.

- [ ] **Step 2: Write `Support/Bridge.cs`** with the members above.

- [ ] **Step 3: Write `StatusTools.cs`**: `public sealed class StatusTools` with `[Tool("rimlink/status", Description = "Rimlink session state: program state, top window, connection, player, world, players, economy, raids, live session, visit, outbox", ResultDescription = "success plus the state snapshot")] public Task<object> Status(IRimBridgeContext ctx)`. Everything inside one `OnMain`. Fields: `programState`, `topWindow`, `clientAssembly` (`typeof(RimlinkSession).Assembly.Location`), `clientVersion` (`ClientInfo.Version`), `session` = null when `!RimlinkSession.Active`, else `{ endpoint = Endpoint.Display + host:port, connection = LastState.ToString(), player = {id, name, role}, world = {exists, seed}, players = online names+ids, settlements = State.Objects.All where Owner != null → {owner, ownerId, tile, name}, economy = {balance, pendingParcels = Pending.Count, myLots = MyLots.Count}, raids = {incoming = Incoming.Count, mine = Mine.Count, peaceful = Flags.Peaceful}, live = {invite = PendingInvite?.Session.Id, sessionId = Current?.Id, kind, state, role = host|guest|null}, visit = VisitSession.InVisit, outbox = Outbox count }`. Check `PlayerFlags` field names in `raids.proto` while writing; use the existing names.

- [ ] **Step 4: Write `scripts/Build-BridgeTools.ps1`**

  Params `-RimWorldDir` (default as above), `-InstallTo` (default `<RimWorldDir>\BridgeTools`), `-NoInstall`. `dotnet build tools\Rimlink.BridgeTools\Rimlink.BridgeTools.csproj -c Release -p:RimWorldDir=...`; target folder `<InstallTo>\Rimlink`; if it exists and holds any file other than `Rimlink.BridgeTools.dll`/`.pdb`, `throw "Refusing to deploy: <folder> holds <files>. The companion folder must contain only Rimlink.BridgeTools.dll (a copy of Rimlink.Client.dll there would load a second, empty session)."`; copy only `Rimlink.BridgeTools.dll` (and its `.pdb`); print the deployed path. Header comment in the style of `Build-ClientMod.ps1`.

- [ ] **Step 5: Build**

  Run (PowerShell): `.\scripts\Build-BridgeTools.ps1 -NoInstall`
  Expected: `Build succeeded`, `Rimlink.BridgeTools.dll` in `tools\Rimlink.BridgeTools\bin\Release\net10.0`, and that folder holds no `Rimlink.Client.dll` copy. Also `dotnet test Rimlink.sln -nologo -v q` still green.
  Refusal check: create an empty `$env:TEMP\bt-check\Rimlink\Rimlink.Client.dll`, run `.\scripts\Build-BridgeTools.ps1 -InstallTo $env:TEMP\bt-check` → throws `Refusing to deploy`; delete `$env:TEMP\bt-check`.

- [ ] **Step 6: Live load spike**

  Message `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d`: "BridgeTools spike: need rimworld-host closed for one deploy into <RimWorld>\BridgeTools\Rimlink, then a start; ~10 min." After the window is granted: `.\scripts\Build-BridgeTools.ps1`; `games_start rimworld-host`; `games_call_tool rimbridge/get_bridge_status` → the companion record for `Rimlink.BridgeTools` has no load/register error; `games_tool_names` lists `rimlink/status`; `games_call_tool rimlink/status` → `success=true`, `programState = "Entry"`, `clientAssembly` under `Mods\Rimlink\`.
  Record in the ledger: `Task 1: Ruling: companion binds to the mod's Rimlink.Client via the host resolver fallback — <evidence>`. If the record shows a load error, stop and report (approach failure).

- [ ] **Step 7: Commit**

  `git add tools/Rimlink.BridgeTools scripts/Build-BridgeTools.ps1` → `git commit -m "Add the BridgeTools companion project with a Rimlink status tool"`

### Task 2: Preset login and pin policies (client core, TDD)

**Files:**
- Create: `src/Rimlink.Client.Core/Net/PresetLogin.cs`, `src/Rimlink.Client.Core/Net/LoginOutcome.cs`
- Test: `tests/Rimlink.Client.Tests/Net/PresetLoginTests.cs`

**Interfaces:**
- Produces (namespace `Rimlink.Client.Net`):
  - `sealed record LoginAttempt(string Login, string Password, bool Register, string PlayerName)` with `ToString()` that omits the password (as `PasswordLogin` does).
  - `sealed class PresetLogin(string login, string password, string? playerName, bool registerIfRefused, string? serverPassword)`: `string Login`, `string? ServerPassword`, `int Attempts`, `LoginAttempt? Next()` — 1st call: login attempt (`Register=false`); 2nd: register attempt if `registerIfRefused`, else null; then null forever. Blank `playerName` → login.
  - `sealed class PresetPinPrompt(bool trustChanged) : IPinPrompt` — `Confirm` returns `previous is null || trustChanged`; `string? Decision` = `"trusted new certificate"`, `"trusted changed certificate"` or `"refused changed certificate"`.
  - `enum LoginOutcomeKind { Loading, NewColony, ModpackDiff, Failed, Cancelled }`, `sealed record LoginOutcome(LoginOutcomeKind Kind, string? Message = null)`.
- Ruling (deviation from spec §3, which left the pin answer in the companion): `PresetPinPrompt` lives in Client.Core so its policy is unit-tested; it holds no game or bridge types.

- [ ] **Step 1: Write the failing tests** (`PresetLoginTests`, style of `LoginPlanTests`):
  - `FirstAttemptIsALogin`: `new PresetLogin("nova", "pw", "Nova", true, null).Next()` equals `new LoginAttempt("nova", "pw", false, "Nova")`.
  - `RegisterIsTriedOnceAfterARefusedLogin`: with `registerIfRefused: true` → `Next()` ×3 gives login, `Register=true`, null; `Attempts == 2`.
  - `NoRegisterWhenNotAllowed`: `registerIfRefused: false` → second `Next()` is null.
  - `BlankPlayerNameFallsBackToLogin`: playerName `"  "` → `PlayerName == "nova"`.
  - `ServerPasswordIsPassedThrough`: `ServerPassword == "join"`.
  - `AttemptNeverPrintsThePassword`: `Next()!.ToString()` does not contain `"pw"`.
  - `PinPromptTrustsNewAndChangedOnlyWhenAllowed`: `new PresetPinPrompt(false).Confirm(ep, "aa", null)` → true; `(false)` with previous `"bb"` → false and `Decision == "refused changed certificate"`; `(true)` with previous → true.

- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter PresetLoginTests` → FAIL (types not defined).

- [ ] **Step 3: Implement** the types above in `PresetLogin.cs` (login, attempt, pin prompt) and `LoginOutcome.cs`.

- [ ] **Step 4: Run** the filter → PASS; then `dotnet test Rimlink.sln -nologo -v q` → green.

- [ ] **Step 5: Commit** `git commit -m "Add preset login and pin policies for unattended logins"`

### Task 3: Seam in the mod

**Files:**
- Modify: `src/Rimlink.Client/UI/SteamAuth.cs` (`GameCredentials`)
- Modify: `src/Rimlink.Client/UI/LoginFlow.cs`
- Modify: `src/Rimlink.Client/UI/Dialog_ServerList.cs` (discard the returned task)
- Modify: `src/Rimlink.Client/UI/Live/Dialog_LiveInvite.cs`

**Interfaces:**
- Consumes: `PresetLogin`, `LoginOutcome` (Task 2).
- Produces:
  - `GameCredentials()` (explicit, unchanged behaviour) and `GameCredentials(PresetLogin preset)`; `bool Interactive` (false with a preset).
  - `static Task<LoginOutcome> LoginFlow.Run(ServerEndpoint endpoint, GameCredentials? credentials = null, IPinPrompt? pins = null)`.
  - `public void Dialog_LiveInvite.Answer(bool accept)`.

- [ ] **Step 1: `GameCredentials(PresetLogin preset)`**: sets `PreferPassword = true`, `ServerPassword = preset.ServerPassword`. In `BuildLogin`, when a preset exists: `password ??= preset.Next() is { } a ? new PasswordLogin(a.Login, a.Password, a.Register, a.PlayerName) : null;` instead of `MainThread.Ask(... Dialog_Login ...)`; the null branch keeps throwing `OperationCanceledException("login cancelled")`.

- [ ] **Step 2: `LoginFlow.Run`**: `credentials ??= new GameCredentials()`, `pins ??= new GamePinPrompt()`; pass `pins` to `new RimlinkConnection(...)`; `return Task.Run<LoginOutcome>(async () => ...)`. Returns: modpack diff → `ModpackDiff`; `UploadLocalThenLoad` success and `DownloadThenLoad` → `Loading`; `NewColony` (also via `AbandonThenNewColony` and the refused-upload fallbacks) → `NewColony`; `OperationCanceledException` and the cancelled `RpcException` → `credentials.Interactive || credentials.LastError is null ? Cancelled : Failed(credentials.LastError)`; other `RpcException` → `Failed(ErrorMessage(ex))`; `Exception` → `Failed(ex.Message)`. `Fail(...)` stays as it is (same dialogs as today).

- [ ] **Step 3: `Login(...)`**: the `WrongPassword` filter becomes `ErrorCodeOf(ex) == ErrorCode.WrongPassword && !serverPasswordAsked && credentials.Interactive`, so a preset login never opens `Dialog_ServerPassword` and fails with the server's "wrong server password".

- [ ] **Step 4: Callers and the invite dialog**: `grep -rn "LoginFlow.Run" src` — every existing call discards the task (`_ = LoginFlow.Run(server);`). `Dialog_LiveInvite.Answer` → `public`, with a one-line summary ("Also called by dev tooling").

- [ ] **Step 5: Build and test**

  Run: `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'` (no `-InstallTo`) → builds; `dotnet test Rimlink.sln -nologo -v q` → green.

- [ ] **Step 6: Commit** `git commit -m "Let the login flow run with preset credentials and report its outcome"`

### Task 4: `rimlink/connect`

**Files:**
- Create: `tools/Rimlink.BridgeTools/ConnectTools.cs`

**Interfaces:**
- Consumes: `LoginFlow.Run`, `GameCredentials(PresetLogin)`, `PresetPinPrompt`, `LoginOutcome` (Tasks 2–3); `Bridge.*` (Task 1).
- Produces: `[Tool("rimlink/connect")] Task<object> Connect(IRimBridgeContext ctx, string login, string password, string host = "127.0.0.1", int port = 25560, string? name = null, string? playerName = null, string register = "auto", string? serverPassword = null, bool trustChangedPin = false, int timeoutSec = 120)`; `static Task<bool> ConnectTools.WaitForMap(IRimBridgeContext ctx, int timeoutSec)` (reused by Task 6).

- [ ] **Step 1: Preconditions** (one `OnMain`): `ProgramState.Entry`, `!RimlinkSession.Active`, `register` is `auto` or `never`, `login`/`password` non-blank, `TryEnterFlow("connect")` → otherwise `Fail(<reason>)`.

- [ ] **Step 2: Server entry**: `new ServerEndpoint(host.Trim(), port, name ?? host)`; if `RimlinkMod.Settings.Servers` lacks its `ToSettingsLine()`, add and `Write()` (as `Dialog_ServerList` does).

- [ ] **Step 3: Run the flow**: inside `OnMain`, call `LoginFlow.Run(endpoint, new GameCredentials(new PresetLogin(login, password, playerName, register == "auto", serverPassword)), pins)`; await the returned task with `Task.WhenAny(task, Task.Delay(timeoutSec s))` outside the main thread. Timeout → `Fail("login did not finish in <n> s", new { topWindow })`.

- [ ] **Step 4: Result**: `{ success = Kind is Loading or NewColony, outcome = Kind.ToString(), message, pin = pins.Decision, registered = preset.Attempts == 2 && success }`. For `Loading`: `WaitForMap` (`ProgramState.Playing && Find.CurrentMap != null && !LongEventHandler.AnyEventNowOrWaiting`) within the remaining time; include `map = Find.CurrentMap.Tile`. For `NewColony`: `nextStep = "rimlink/start_colony"`. Always `ExitFlow()` in `finally`.

- [ ] **Step 5: Build** `.\scripts\Build-BridgeTools.ps1 -NoInstall` → succeeds.

- [ ] **Step 6: Live check** (window from the checklist session; deploy mod with `Build-ClientMod.ps1 -InstallTo ...` and BridgeTools, game closed): on `rimworld-host` `rimlink/connect {login:"bt-a", password:"bt-pass", register:"auto", _rimBridgeTimeoutMs:140000}` with GABS `timeout:150` → `success=true`, `outcome` `NewColony` (or `Loading` if `bt-a` exists), `pin` "trusted new certificate" or null when already pinned; then `rimworld/go_to_main_menu`; `rimlink/connect {login:"bt-a", password:"wrong", register:"auto"}` → `success=false`, `outcome=Failed`, `message` contains "login already taken"; `rimlink/connect` while a session is active → `success=false`. Record results in the ledger. Use throwaway accounts (`bt-*`), not the checklist's `admin`/`Nova`, unless the checklist session agrees.

- [ ] **Step 7: Commit** `git commit -m "Add the connect tool: unattended login with optional registration"`

### Task 5: Main-menu and page tools

**Files:**
- Create: `tools/Rimlink.BridgeTools/Support/MenuCapture.cs`, `tools/Rimlink.BridgeTools/UiTools.cs`

**Interfaces:**
- Produces:
  - `static class MenuCapture`: `static void Ensure()` — patches `OptionListingUtility.DrawOptionListing` once with a postfix (Harmony id `rimlink.bridgetools`); the postfix, in `ProgramState.Entry`, appends the drawn `List<ListableOption>` items to the current frame's capture (cleared when `Time.frameCount` changes); `static IReadOnlyList<ListableOption> Last` — the last completed frame's options.
  - `static Page? Pages.Top()` — `Find.WindowStack.Windows.OfType<Page>().LastOrDefault()`; `static bool Pages.TryNext(Page page, out string? refusal)` — `AccessTools.Method(page.GetType(), "CanDoNext")` then `"DoNext"` (protected; refusal text = newest `Messages` entry if any); `static bool Pages.TryBack(Page page)`.
  - Tools `rimlink/menu_options`, `rimlink/click_menu_option {label}`, `rimlink/page_state`, `rimlink/page_action {action = "next"|"back"|"random_site"}`.

- [ ] **Step 1: `MenuCapture` and `Pages`** (in `Support/`, `Pages` may share `MenuCapture.cs` or its own file).

- [ ] **Step 2: Tools**:
  - `menu_options`: precondition `ProgramState.Entry`; `Ensure()`, `await ctx.Game.FramesAsync(2)`; returns `options = [{ index, label }]`.
  - `click_menu_option`: same capture, label match case-insensitive and trimmed; unknown → `Fail("no menu option '<label>'", new { options })`; else `option.action()` inside `OnMain`; returns `{ success, clicked = label, topWindow }` after one frame.
  - `page_state`: `{ page = type name, title = page.PageTitle, canBack = invoke CanDoBack, selectedTile = page is Page_SelectStartingSite ? Find.WorldInterface.SelectedTile.tileId : null }`; no page → `Fail("no page open", new { topWindow })`.
  - `page_action`: `next` → `TryNext`; `back` → `TryBack`; `random_site` (only on `Page_SelectStartingSite`) → `Find.WorldInterface.SelectedTile = TileFinder.RandomStartingTile()` and camera jump as the vanilla button does; returns the new `page_state`.

- [ ] **Step 3: Build** → succeeds. Live check deferred to Task 6 (same window).

- [ ] **Step 4: Commit** `git commit -m "Add main-menu and page tools for UI the layout snapshot cannot see"`

### Task 6: `rimlink/start_colony`

**Files:**
- Create: `tools/Rimlink.BridgeTools/Support/NewColonyDriver.cs`, `tools/Rimlink.BridgeTools/ColonyTools.cs`

**Interfaces:**
- Consumes: `Pages` (Task 5), `ConnectTools.WaitForMap` (Task 4), `Bridge.*`.
- Produces: `[Tool("rimlink/start_colony")] Task<object> StartColony(IRimBridgeContext ctx, string? scenario = null, string? storyteller = null, string? difficulty = null, string? seed = null, int tile = -1, int timeoutSec = 300)`; `NewColonyDriver.RunAsync(IRimBridgeContext ctx, Options o, CancellationToken deadline) → Task<(bool ok, string? error, List<string> steps, int tile)>`.

- [ ] **Step 1: Preconditions**: `RimlinkSession.Active`; `ProgramState.Entry`; `TryEnterFlow("start_colony")`; non-admin with `RimlinkSession.World is not { Exists: true }` → `Fail("the server has no world yet: an admin starts the first colony")`; no `Page_SelectScenario` open → open `new Page_SelectScenario()` (as `LoginFlow` does).

- [ ] **Step 2: Driver loop** (each iteration inside `OnMain`, then one frame; bound by the deadline and 60 iterations):

  ```
  if Playing and CurrentMap != null and no long event: done
  if LongEventHandler.AnyEventNowOrWaiting: wait
  if top window is Dialog_MessageBox with buttonAAction (a page's confirmation): invoke buttonAAction, close it, step "confirmed: <text start>"
  page = Pages.Top(); if null: wait
  prepare(page); if page is Page_CreateWorldParams: invoke CanDoNext (queues generation, returns false), step "world generation", then wait until that page is closed; admin: also wait RimlinkSession.World.Exists
  else if !Pages.TryNext(page, out refusal): fail "<page> refused next: <refusal>"
  ```

  `prepare` per type (fields via `AccessTools.Field`/`Traverse`, names checked against `ilspycmd -t <Type>` first, `Dialog_MessageBox.buttonAAction` included). Tiles in 1.7 are `PlanetTile`; convert with `(int)` as `WorldSetup.CollectParams` does when comparing with `WorldObject.Tile`.
  - `Page_SelectScenario`: `curScen` = `ScenarioLister.AllScenarios()` match on `name` or `ScenarioDef.defName` (case-insensitive), default `ScenarioDefOf.Crashlanded.scenario`; unknown → fail listing names.
  - `Page_SelectStoryteller`: `storyteller` (`DefDatabase<StorytellerDef>` by defName/label, default `StorytellerDefOf.Cassandra`), `difficulty` (default `DifficultyDefOf.Rough`), `difficultyValues = new Difficulty(difficulty)`, `Find.GameInitData.permadeathChosen = true`, `permadeath = false`.
  - `Page_ChooseIdeoPreset`: private enum field `presetSelection` = `Classic` (value 0) via `AccessTools.Field` + `Enum.ToObject`.
  - `Page_CreateWorldParams`: if world not on server and `seed` given → field `seedString = seed`; otherwise untouched (the Rimlink patch applies server parameters).
  - `Page_SelectStartingSite`: `tile >= 0` → that tile; else up to 20 × `TileFinder.RandomStartingTile()` until `TileFinder.IsValidTileForNewSettlement(t)`; set `Find.WorldInterface.SelectedTile`; none valid → fail.
  - other pages: nothing (step "next on <type>").

- [ ] **Step 3: After start**: `WaitForMap`, then wait for `State.Objects.All.Any(o => o.Owner?.PlayerId == MyPlayerId && o.Tile == Find.CurrentMap.Tile)`; result `{ success, tile, colony = Faction.OfPlayer.Name, steps }`; timeout → `Fail(..., new { steps, topWindow })`. `ExitFlow()` in `finally`.

- [ ] **Step 4: Build** → succeeds.

- [ ] **Step 5: Live check** (window; deploy; host instance): `rimlink/menu_options` lists the Rimlink "Multiplayer" label; `rimlink/click_menu_option {label:<it>}` → `topWindow = Dialog_ServerList`; `rimworld/close_window`; `rimlink/connect` as `bt-a` → `NewColony`; `rimlink/start_colony {_rimBridgeTimeoutMs:320000}` with GABS `timeout:330` → `success=true`, steps include scenario, storyteller, ideology, world generation (or locked), site, pawns; server log `settled tile`; separately on the site page `rimlink/page_action {action:"random_site"}` then `page_state.selectedTile` changes. Ledger entry with results.

- [ ] **Step 6: Commit** `git commit -m "Add the start-colony tool that walks the new-colony pages"`

### Task 7: Raids, visits, live sessions

**Files:**
- Create: `tools/Rimlink.BridgeTools/Support/Targets.cs`, `tools/Rimlink.BridgeTools/PlayTools.cs`

**Interfaces:**
- Produces `Support/Targets.cs`:
  - `static PlayerRef? Targets.Player(string nameOrId)` — over `State.Players.Online` and `State.Objects.All` owners (as `BlobDebugActions.Players()`), excluding me; id by `long.TryParse`, name case-insensitive.
  - `static List<Pawn>? Targets.Pawns(string csv, out string? error)` — current map, `Faction.OfPlayer` humanlike or animal pawns; token matches `GetUniqueLoadID()` or `LabelShort`/`Name.ToStringFull` (case-insensitive); unknown or ambiguous tokens → error naming them.
  - `static object Targets.Started(string message)` → `new { success = true, started = true, message }`.
- Produces tools: `rimlink/raid {player, pawns, mode="snapshot"|"real", timing="instant"|"warned"}`, `rimlink/visit {player, pawns}`, `rimlink/live_propose {player, pawns, kind="raid"|"visit"}`, `rimlink/live_answer {accept}`, `rimlink/live_end`.

- [ ] **Step 1: `Targets.cs`.**

- [ ] **Step 2: Tools** (each: precondition `RimlinkSession.Active`, `Playing`, `Find.CurrentMap != null`; unknown enum value → `Fail` listing allowed values):
  - `raid` → `RaidOutgoing.LaunchFromCaravanless(Find.CurrentMap, player, pawns, mode == "real" ? PawnMode.PawnReal : PawnMode.PawnSnapshot, timing == "warned" ? RaidTiming.TimingWarned : RaidTiming.TimingInstant)`.
  - `visit` → `VisitSession.Begin(player, pawns)`; `VisitSession.InVisit` already true → `Fail("a visit is already running")`.
  - `live_propose` → `LiveGuest.ProposeFromMap(Find.CurrentMap, player, kind == "visit" ? LiveKind.LiveVisit : LiveKind.LiveRaid, pawns, RaidTiming.TimingInstant)`.
  - `live_answer` → the open `Dialog_LiveInvite` (`Find.WindowStack.WindowOfType<Dialog_LiveInvite>()`) `.Answer(accept)`; none → `Fail("no live invite is open", new { pendingInvite = State.Live.PendingInvite?.Session.Id })`.
  - `live_end` → `LiveHost.Active` → `LiveHost.Abort()`; else `LiveGuest.Session is not null` → `LiveGuest.EndByPlayer()`; else `Fail("no live session")`.
  - Each success returns `Targets.Started(...)` plus the latest `[Rimlink]`-side hint: `{ pawns = names }`.

- [ ] **Step 3: Build** → succeeds. Live check in Task 9.

- [ ] **Step 4: Commit** `git commit -m "Add raid, visit and live-session tools"`

### Task 8: Economy and chat

**Files:**
- Create: `tools/Rimlink.BridgeTools/EconomyTools.cs`, `tools/Rimlink.BridgeTools/ChatTools.cs`
- Modify: `tools/Rimlink.BridgeTools/Support/Targets.cs`

**Interfaces:**
- Consumes: `Targets.*` (Task 7).
- Produces: `static List<Outgoing.Pick>? Targets.Items(Map map, string csv, bool anywhere, out string? error)` — parse `def:count`; `ThingDef` by defName (case-insensitive); source `anywhere ? map.listerThings.AllThings.Where(t => t.Spawned && t.def.EverHaulable)` : `TradeUtility.AllLaunchableThingsForTrade(map)`; take stacks until the count is met; not enough → error with the available amount.
  Tools `rimlink/send {player, items?, pawns?, silver=0, anywhere=false}`, `rimlink/post_lot {price, items?, pawn?, anywhere=false}`, `rimlink/market {seller?}`, `rimlink/buy {lotId}`, `rimlink/deposit {silver, anywhere=false}`, `rimlink/chat {count=20}`, `rimlink/chat_send {text, to?}`.

- [ ] **Step 1: `Targets.Items`.**

- [ ] **Step 2: Economy tools**:
  - `send` → `Outgoing.SendItems(map, p.PlayerId, p.Name, picks, pawns, silver)`; nothing to send → `Fail`.
  - `post_lot` → exactly one of `items`/`pawn` else `Fail`; `Outgoing.PostLot(map, picks, pawn, price)`.
  - `deposit` → `Outgoing.Deposit(map, silver)`; `anywhere` sets `BlobDebugActions.PickAnywhere = true` for the call (restored after) and needs `Prefs.DevMode` (`Deposit` checks both) → without dev mode `Fail("anywhere needs dev mode")`.
  - `market` → `await RimlinkSession.Economy!.Lots(new MarketQuery { SellerPlayerId = seller?.PlayerId ?? 0 }, ct)` off the main thread; projection `{ id, seller = Seller.Name, price, items = Items.Items.Select(i => $"{i.DefName} x{i.Count}"), pawn = Pawn?.Name }`.
  - `buy` → `await Economy.Buy(lotId, ct)`; `RpcException` → `Fail(EconomyClient.MessageOf(ex))`; success `{ success, parcelId = parcel.Id }` (delivery arrives through the usual mailbox flow).

- [ ] **Step 3: Chat tools**: `chat` → last `count` of `State.Chat.Messages` as `{ id, from = From.Name, to = ToPlayerId, text, at }`; `chat_send` → `to` resolved by `Targets.Player` (0 = public) → `await Connection.Chat.SendAsync(new SendRequest { Text = text, ToPlayerId = to })`; blank text → `Fail`.

- [ ] **Step 4: Build** → succeeds.

- [ ] **Step 5: Commit** `git commit -m "Add economy and chat tools"`

### Task 9: Two-player live run, documentation

**Files:**
- Modify: `AGENTS.md` (layout row for `tools/`; new section "Driving the game from an agent (GABS + Rimlink.BridgeTools)")
- Modify: `docs/manual-checklist.md` (one line under «Клиент»)
- Create: `C:\Users\Admin\AppData\Local\Claude-Data\claude_pzs8sxrjxfjjc\side-session-notes\local_76666afd-3a4a-4ad8-8458-66aa6bf6165d\rimbridgeserver-upstream-gaps.md` (outside the repo, not committed)

- [ ] **Step 1: Live run** (window for both instances; deploy mod + BridgeTools with games closed). Host: `connect` `bt-a` → `start_colony` (or `Loading` when it already exists). Guest: `connect` `bt-b` (register auto) → `start_colony` on the locked world; `rimlink/status` on both shows the other in `players` and `settlements`. Then: `chat_send` both ways → `chat` shows each line once; `send {player:"bt-a", items:"Silver:50", anywhere:true}` from guest → host `status.economy.pendingParcels` grows after its next delivery; `raid {player:"bt-a", pawns:<one guest colonist id>, mode:"snapshot"}` → server `Launch 200`; `live_propose {player:"bt-a", kind:"visit", pawns:<id>}` → host `live_answer {accept:true}` → both `status.live.state` running → guest `live_end` → ended; `rimworld/go_to_main_menu` on the guest → log `Stored save v<n>`. Wrong-state calls: `live_answer` with no invite and `raid {player:"nobody"}` → `success=false`. Note every result in the ledger.

- [ ] **Step 2: AGENTS.md section** (English, terse like the rest): build/deploy (`.\scripts\Build-BridgeTools.ps1`, game closed, restart after deploy), the one-DLL rule and why, `rimbridge/get_bridge_status` when tools are missing, timeouts (`_rimBridgeTimeoutMs` + GABS `timeout`), the tool list (one line each), the typical two-player sequence from Step 1, pitfalls (Steam running; `set_text_field` only `instant`; main menu and starting-site buttons via `rimlink/menu_options` / `rimlink/page_action`). Layout table row: `tools/Rimlink.BridgeTools` — dev-only RimBridgeServer companion, not in the solution, never shipped.

- [ ] **Step 3: Checklist line** under «Клиент»: `- [x] **Инструменты агента (BridgeTools).** <date>: connect/start_colony на двух экземплярах, чат, передача, рейд, живой визит через GABS без кликов.` (or `[ ]` with what failed).

- [ ] **Step 4: Upstream note** in the shared notes folder: `get_ui_layout` misses `OptionListingUtility` main-menu options and `Page_SelectStartingSite.DoCustomBottomButtons`; `set_text_field` `typed` times out on mod dialogs while `instant` works; extension tools default to a 60 s timeout. Each with the reproduction from this run.

- [ ] **Step 5: Verify and commit** `dotnet test Rimlink.sln -nologo -v q` green, both builds succeed → `git add AGENTS.md docs/manual-checklist.md` → `git commit -m "Document the BridgeTools companion for agents and record the live run"`
