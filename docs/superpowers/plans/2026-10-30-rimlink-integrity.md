# Rimlink Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Non-admins cannot use dev mode while connected, a save with dev mode on and a wealth jump freeze that player's economy (with an admin alert), the world's storyteller and difficulty hold for every colony, and quitting to the OS saves and uploads like leaving to the menu.

**Architecture:** The server gains an `Integrity` options section, a per-player economy freeze (columns on `Player`, checked by `EconomyGate.RequireOpenFor`), and an `IntegrityService` that `SaveStore.Store` calls after each upload: it reads the dev-mode flag from the header and runs the pure `WealthCheck` (net gain per game day against the previous version, minus parcels delivered in between). The mod gets a `DevModeGuard`, a dev-mode flag saved in `RimlinkGameComponent`, storyteller patches driven by `WorldInfo.storyteller_locked`, and a `QuitGuard` on `Application.wantsToQuit` that reuses the leave-to-menu flow.

**Tech Stack:** .NET 10, ASP.NET Core gRPC, EF Core + SQLite, xUnit, Lib.Harmony 3.0.0-rc.3, RimWorld 1.7 (Unity `Application.wantsToQuit`).

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-integrity-design.md`

## Global Constraints

- Protocol only grows: `ErrorCode.PLAYER_FROZEN = 32`; `SaveHeader.dev_mode = 8`; `PawnBlob.market_value_x100 = 8`; `LoginReply.dev_mode_allowed = 10`; `WorldParams.difficulty_values = 12` (zstd Scribe XML of `Difficulty`); `WorldInfo.storyteller_locked = 4`. Other branches may take the same numbers: the branch that lands later renumbers at rebase.
- Options section `Integrity` (`IntegrityOptions`, env `Rimlink__Integrity__*`): `DevMode = AdminsOnly | Everyone` (default `AdminsOnly`), `WealthWarnPerDay = 20000`, `WealthFreezePerDay = 100000` (0 disables either; both > 0 → freeze ≥ warn), `StorytellerPolicy = Locked | Free` (default `Locked`). Every key in `deploy/appsettings.example.json` and `docs/server-setup.md`.
- One EF migration named `Integrity`: `Player.EconomyFrozenAt` (`DateTime?`), `Player.EconomyFreezeReason` (`string?`), `SaveVersion.DevMode` (bool), `SaveVersion.WealthTotal`, `SaveVersion.WealthItems` (long). `dotnet ef migrations add Integrity --project src/Rimlink.Server` (no `-nologo`).
- Wealth formula (spec §4): `imported` = parcels to the player with `DeliveredTick` in (`base.GameTick`, `new.GameTick`]: silver + Σ item `market_value_x100 × count / 100` + Σ pawn `market_value_x100 / 100`; `net = new.WealthTotal − base.WealthTotal − imported`; `days = max(1, (new.GameTick − base.GameTick) / 60000.0)`; `rate = net / days`. Skip: no base, `new.GameTick ≤ base.GameTick`, `base.WealthTotal == 0`, player frozen. Role Admin: warn only, never frozen.
- Audit actions: `integrity-devmode`, `integrity-wealth` (actor `system`, account 0), `player-economy-freeze`, `player-economy-unfreeze` (admin). Feed type `integrity.alert` `{player:{id,name}, kind:"devmode"|"wealth-warn"|"wealth-freeze", version, net, days, rate, frozen}` (`net`/`days`/`rate` null for `devmode`).
- Freeze reasons (server text, shown in `Notice` and refusals; numbers formatted with `CultureInfo.InvariantCulture`): `dev mode was on in save v{N}`, `wealth +{rate:N0}/day in save v{N}`; notices `Your trading is frozen: {reason}` / `Your trading is open again`.
- A frozen player is refused `PLAYER_FROZEN` as initiator of: transfer, deposit, withdraw, post lot, market buy, shop buy, raid launch. A frozen seller's lots are hidden from `ListLots` of others and refuse `Buy`; a frozen host's shop refuses `Buy`. Deliveries to them and withdrawing their own lot stay allowed. Global freeze is checked first.
- `src/Rimlink.Client.Core` keeps no `Verse`/`RimWorld` reference; game objects on the main thread only (`UI.MainThread.Post` from background tasks).
- New keyed strings, English and Russian: `Rimlink.DevMode.Refused`, `Rimlink.Storyteller.Locked`, `Rimlink.Storyteller.Applied`.
- No version bump. `dotnet test Rimlink.sln -nologo -v q` green and the mod building before every commit. Commits by `Asofwar <7397608+Asofwar@users.noreply.github.com>`, imperative subject, no trailers. Branch `asofwar/integrity`; no merge, no push.
- Game runs only on the s3 stand: game copy `D:\Build\rimlink-s3\RimWorld`, saves `D:\Build\rimlink-s3\{host,guest}`, server DataDir `D:\Build\rimlink-s3\server` on port 25600, GABS `rimlink-s3-host` / `rimlink-s3-guest`, accounts s3-admin/s3-pass-1 (AdminLogins) and s3-guest/s3-pass-2. Never D:\Build\rimlink-gabs, D:\Build\rimlink-bt, the Steam Mods folder, or ports 25560/25570/25580/25590. Deploy: `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-s3\RimWorld' -InstallTo 'D:\Build\rimlink-s3\RimWorld\Mods'` and `.\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-s3\RimWorld'` with both s3 games stopped.

## Review Focus

1. A transfer or lot posted while connected, unanswered, then replayed at the next login after the player was frozen: `PLAYER_FROZEN` must be a definitive refusal (the things go back into the colony), not "not now" like `EconomyFrozen`, or the outbox replays forever. Test in Task 7 (`IsDefinitiveForPlayerFrozen`).
2. An admin rolls a player back to a version saved before the migration (`WealthTotal = 0` on the version): the next upload must skip the wealth check, and `Rollback` must leave `Player.WealthTotal/Items` alone instead of zeroing raid protection. Test in Task 2 (`RollbackToAVersionWithoutWealthKeepsThePlayerWealth`) and Task 5 (`SkipsWhenTheBaseHasNoWealth`).
3. Two autosaves a few hundred ticks apart after a big legitimate purchase: the parcel's `DeliveredTick` falls between them, `days` is floored at 1, so neither save may alert. Test in Task 6 (`DeliveredPurchaseBetweenSavesDoesNotAlert`).
4. Dev mode switched on from the main-menu Options after login but before a game exists (new-colony pages): no `GameComponentUpdate` runs there, so only the setter prefix protects it. In-game check in Task 9, step "toggle in Options on the scenario page".
5. Quit requested while the leave-to-menu flow (or a running upload) is already in progress, or twice in a row (Alt+F4 pressed again during "Uploading…"): one save, one wait, one quit, no second `SaveGame`. In-game check in Task 11 (press Alt+F4 twice).

---

### Task 1: Integrity options, the dev-mode flag at login, the storyteller lock in world info

**Files:**
- Modify: `src/Rimlink.Server/Hosting/ServerOptions.cs` (new `IntegrityOptions`, `DevModePolicy`, `StorytellerPolicy`)
- Modify: `src/Rimlink.Server/Program.cs` (bind + validate on start)
- Modify: `src/Rimlink.Protocol/Protos/auth.proto` (`LoginReply.dev_mode_allowed = 10`), `world.proto` (`WorldParams.difficulty_values = 12` bytes, `WorldInfo.storyteller_locked = 4`)
- Modify: `src/Rimlink.Server/Grpc/AuthGrpcService.cs` (fill `DevModeAllowed`), `src/Rimlink.Server/Application/World/WorldService.cs` (fill `StorytellerLocked` in `Get` and `Create`)
- Modify: `deploy/appsettings.example.json`, `docs/server-setup.md` (new `Integrity` section)
- Test: `tests/Rimlink.Server.Tests/Hosting/IntegrityOptionsTests.cs`, `tests/Rimlink.Server.Tests/World/StorytellerLockTests.cs`

**Interfaces:**
- Produces: `public enum DevModePolicy { AdminsOnly, Everyone }`, `public enum StorytellerPolicy { Locked, Free }`, `public sealed class IntegrityOptions { const string Section = "Integrity"; DevModePolicy DevMode = AdminsOnly; long WealthWarnPerDay = 20000; long WealthFreezePerDay = 100000; StorytellerPolicy StorytellerPolicy = Locked; static bool IsValid(IntegrityOptions o); bool DevModeAllowed(Role role) }` (role is `Storage.Entities.Role`).

- [ ] **Step 1: Write the failing tests**
  - `IntegrityOptionsTests.DefaultsAreTheSpecValues` — `new IntegrityOptions()`: `AdminsOnly`, 20000, 100000, `Locked`.
  - `IntegrityOptionsTests.FreezeBelowWarnIsInvalid` — `IsValid` false for warn 50000 / freeze 10000; true for warn 0 / freeze 10, warn 10 / freeze 0, negative values false.
  - `IntegrityOptionsTests.ServerRefusesToStartWithInvalidThresholds` — fixture `.WithSetting("Integrity:WealthWarnPerDay","50000").WithSetting("Integrity:WealthFreezePerDay","10")`: creating the channel throws `OptionsValidationException`.
  - `IntegrityOptionsTests.LoginReplySaysWhoMayUseDevMode` — with `Auth:AdminLogins:0=admin`: admin login `DevModeAllowed == true`, `nova` `false`; with `Integrity:DevMode=Everyone`: `nova` `true`.
  - `StorytellerLockTests.WorldInfoCarriesThePolicy` — default fixture: `World.Get` → `StorytellerLocked == true`; `Integrity:StorytellerPolicy=Free` → `false`; `WithoutWorld()` + admin `Create` → returned info `StorytellerLocked == true` and `Params.DifficultyValues` round-trips the bytes sent.

- [ ] **Step 2: Run them, expect FAIL** — `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~IntegrityOptionsTests|FullyQualifiedName~StorytellerLockTests" -nologo -v q`; Expected: build errors (`IntegrityOptions`, `DevModeAllowed`, `StorytellerLocked` unknown).

- [ ] **Step 3: Implement** the options class, `AddOptions<IntegrityOptions>().Bind(...).Validate(IntegrityOptions.IsValid, "Integrity:WealthFreezePerDay must be 0 or at least Integrity:WealthWarnPerDay, and neither negative").ValidateOnStart()`, the two proto fields, and the two fills from `IOptions<IntegrityOptions>`. Docs: the four keys with defaults and one line each, as in the table of spec §1.

- [ ] **Step 4: Run, expect PASS**, then the whole suite: `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.

- [ ] **Step 5: Commit** — `git commit -m "Add the Integrity options and tell clients about dev mode and the storyteller lock"`

### Task 2: Storage — migration, per-version wealth and dev-mode flag, rollback moves the wealth

**Files:**
- Modify: `src/Rimlink.Server/Storage/Entities/Entities.cs` (`Player.EconomyFrozenAt`, `Player.EconomyFreezeReason`, `SaveVersion.DevMode`, `SaveVersion.WealthTotal`, `SaveVersion.WealthItems`)
- Create: migration `Integrity` under `src/Rimlink.Server/Storage/Migrations/`
- Modify: `src/Rimlink.Protocol/Protos/saves.proto` (`SaveHeader.dev_mode = 8`)
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`Store` copies `DevMode`, `WealthTotal`, `WealthItems` from the header onto the new version; `Rollback` sets `Player.WealthTotal/Items` from the target when its `WealthTotal > 0`)
- Modify: `src/Rimlink.Server/Rest/AdminEndpoints.cs` (`GET /players/{id}/saves` items gain `devMode`, `wealthTotal`), `src/Rimlink.Server/Application/Admin/AdminCommandRunner.cs` (`saves` lines gain ` dev` when flagged)
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`, `tests/Rimlink.Server.Tests/Saves/SaveUploadTests.cs`

- [ ] **Step 1: Write the failing tests**
  - `SchemaTests.IntegrityColumnsExist` — `players` has `EconomyFrozenAt`, `EconomyFreezeReason`; `save_versions` has `DevMode`, `WealthTotal`, `WealthItems` (same PRAGMA approach as the existing schema tests).
  - `SaveUploadTests.UploadStoresWealthAndDevModeOnTheVersion` — header `WealthTotal 40000, WealthItems 9000, DevMode true`: the version row has the three values; REST `players/{id}/saves` item has `devMode: true`, `wealthTotal: 40000`.
  - `SaveUploadTests.RollbackMovesThePlayerWealth` — v1 wealth 30000 (tick 1000), v2 wealth 90000 (tick 2000); rollback to v1 → `Player.WealthTotal == 30000`.
  - `SaveUploadTests.RollbackToAVersionWithoutWealthKeepsThePlayerWealth` — set v1's `WealthTotal = 0` in the DB, v2 90000, rollback to v1 → `Player.WealthTotal == 90000`.
- [ ] **Step 2: Run, expect FAIL** (missing columns / members).
- [ ] **Step 3: Implement**: entities, `dotnet ef migrations add Integrity --project src/Rimlink.Server`, proto field, the `Store`/`Rollback`/REST/chat changes.
- [ ] **Step 4: Run, expect PASS**; whole suite green.
- [ ] **Step 5: Commit** — `git commit -m "Store wealth and the dev-mode flag on every save version"`

### Task 3: Per-player economy freeze in the gate and the services

**Files:**
- Modify: `src/Rimlink.Protocol/Protos/common.proto` (`PLAYER_FROZEN = 32`)
- Create: `src/Rimlink.Server/Application/Economy/PlayerFreeze.cs` (scoped; registered in `Program.cs`)
- Modify: `src/Rimlink.Server/Application/Economy/EconomyGate.cs` (inject `RimlinkDbContext`; new `RequireOpenFor`)
- Modify: `TransferService.Send`, `WalletService.Deposit` / `Withdraw`, `MarketService.Post` / `Buy` / `List`, `ShopService.Buy`, `RaidService.Launch` — replace `gate.RequireOpen()` with `await gate.RequireOpenFor(<initiator>)`; `MarketService.Withdraw`, `RaidService.Announce` and the wallet `Get`/`History` keep `RequireOpen()`
- Test: `tests/Rimlink.Server.Tests/Economy/PlayerFreezeTests.cs`

**Interfaces:**
- Produces:
  - `EconomyGate.Task RequireOpenFor(long playerId)` — `RequireOpen()` first, then `PLAYER_FROZEN` (`FailedPrecondition`) with message `"your trading is frozen: " + reason` when the player's `EconomyFrozenAt` is set.
  - `EconomyGate.Task RequireSellerOpen(long sellerId)` — `PLAYER_FROZEN` with `"this seller's trading is frozen"`; used by `MarketService.Buy` and `ShopService.Buy` (host).
  - `PlayerFreeze.Task<bool> Set(long playerId, bool frozen, string? reason)` — updates both columns (`EconomyFrozenAt = now` / null, reason / null), sends the player the `Notice` from Global Constraints, returns false when the state did not change (no notice then).
  - `PlayerFreeze.Task<bool> IsFrozen(long playerId)`.
- `MarketService.List` drops lots whose seller is frozen unless `query.SellerPlayerId` is the caller's own id: add a `long viewerId` parameter (`List(MarketQuery query, long viewerId)`), callers pass the session player. `MarketService.Buy` calls `RequireSellerOpen(lot.SellerPlayerId)` right after loading the lot; `ShopService.Buy` calls `RequireSellerOpen(r.HostPlayerId)` after its own gate check.

- [ ] **Step 1: Write the failing tests** (economy fixture, players `nova` and `kai`, `PlayerFreeze.Set` through a service scope):
  - `FrozenPlayerCannotSendDepositWithdrawPostBuyOrShop` — `nova` frozen with reason "test": each of `Send`, `Deposit`, `Withdraw`, `Post`, `Buy` (of `kai`'s lot) and shop `Buy` (while visiting `kai`) throws `RpcException` with `ErrorDetail().Code == ErrorCode.PlayerFrozen` and message containing "test".
  - `FrozenPlayerCannotLaunchARaid` — raid fixture, frozen attacker `Launch` → `PlayerFrozen`.
  - `FrozenSellersLotsAreHiddenAndNotForSale` — `kai` posts, then frozen: `nova`'s `List` has no `kai` lot, `kai`'s own `List(SellerPlayerId = kai)` still has it, `nova`'s `Buy` → `PlayerFrozen`.
  - `FrozenHostsShopDoesNotSell` — `nova` visits frozen `kai`, shop `Buy` → `PlayerFrozen`.
  - `FrozenPlayerStillReceivesAndWithdrawsOwnLot` — `kai` sends `nova` a gift after `nova` is frozen: the parcel is in `nova`'s mailbox; `nova`'s own lot posted before the freeze can be withdrawn (parcel returned).
  - `GlobalFreezeIsReportedFirst` — global freeze + player freeze: `Send` → `EconomyFrozen`.
  - `UnfreezeOpensTradingAgainAndNotifiesOnce` — `Set(true)` twice returns true then false; `Set(false)` → `Send` works; the player's event stream got exactly the two notices.
- [ ] **Step 2: Run, expect FAIL** (`ErrorCode.PlayerFrozen`, `PlayerFreeze` unknown).
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run, expect PASS**; whole suite green (existing economy, raid and shop tests must still pass unchanged).
- [ ] **Step 5: Commit** — `git commit -m "Freeze one player's economy without stopping deliveries to them"`

### Task 4: Admin surface for the player freeze (operations, REST, CLI, chat, panel)

**Files:**
- Modify: `src/Rimlink.Server/Application/Admin/AdminOperations.cs` (`SetPlayerEconomyFrozen`)
- Modify: `src/Rimlink.Server/Rest/AdminDirectoryEndpoints.cs` (`POST /players/{id:long}/economy`, body `EconomyFreezeBody(bool Frozen, string? Reason)`, scope Admin)
- Modify: `src/Rimlink.Server/Application/Admin/AdminDirectory.cs` (`PlayerCard` gains `DateTime? EconomyFrozenAt, string? EconomyFreezeReason`)
- Modify: `src/Rimlink.Server/Application/Admin/AdminCommandRunner.cs` (`freeze <player> [reason]`, `unfreeze <player>`, help text)
- Modify: `src/Rimlink.Tools/Commands/AdminCommand.cs` (`player-freeze <id> [reason]`, `player-unfreeze <id>`)
- Modify: `src/Rimlink.Server/wwwroot/admin/views/player.js` (state badge with reason and time; freeze/unfreeze in the economy block with a reason input), `wwwroot/i18n/en.json`, `ru.json`
- Modify: `docs/server-setup.md` (route, CLI, chat commands)
- Test: `tests/Rimlink.Server.Tests/Admin/PlayerFreezeAdminTests.cs`, `tests/Rimlink.Server.Tests/Tools/AdminCommandTests.cs`

**Interfaces:**
- Consumes: `PlayerFreeze.Set` (Task 3).
- Produces: `AdminOperations.Task SetPlayerEconomyFrozen(Actor actor, long playerId, bool frozen, string? reason)` — `actor.Require(Role.Admin)`, `PlayerFreeze.Set`, audit `player-economy-freeze` (details = reason, default `"by admin"`) or `player-economy-unfreeze`, target = the player's account.

- [ ] **Step 1: Write the failing tests**
  - `PlayerFreezeAdminTests.RestFreezeAndUnfreeze` — `POST players/{nova}/economy {frozen:true, reason:"dupe"}` → 200; `GET players/{nova}` has `economyFrozenAt` set, `economyFreezeReason == "dupe"`; `nova`'s `Send` → `PlayerFrozen`; `{frozen:false}` → `economyFrozenAt` null; `GET actions` has `player-economy-freeze` then `player-economy-unfreeze`.
  - `PlayerFreezeAdminTests.ModeratorCannotFreeze` — chat command `/freeze nova` from a moderator → refused (same assertion style as the existing role checks); from an admin → "economy frozen for nova".
  - `AdminCommandTests.PlayerFreezePostsTheBody` — `player-freeze 7 dupe x` posts `players/7/economy` with `{frozen:true, reason:"dupe x"}`; `player-unfreeze 7` posts `{frozen:false}` (same fake-REST approach as the file's existing tests).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement**; panel: in `factsList` add `card.economyFrozenAt ? badge(t('player.economyFrozen', { reason, at }), 'bad') : null`; in `economyBlock` a reason input and a freeze/unfreeze `busyButton` posting `players/${id}/economy`; in `savesBlock` a `dev mode` badge on versions with `devMode` (field from Task 2). New i18n keys `player.economyFrozen`, `player.freezeEconomy`, `player.unfreezeEconomy`, `player.economyFrozenDone`, `player.economyOpenDone`, `player.saveDevMode` in both files.
- [ ] **Step 4: Run, expect PASS**; whole suite green (panel i18n key-parity test included).
- [ ] **Step 5: Commit** — `git commit -m "Let admins freeze one player's economy from REST, CLI, chat and the panel"`

### Task 5: Pure wealth check

**Files:**
- Create: `src/Rimlink.Server/Application/Integrity/WealthCheck.cs`
- Test: `tests/Rimlink.Server.Tests/Integrity/WealthCheckTests.cs`

**Interfaces:**
- Produces:
  - `public enum WealthVerdict { None, Warn, Freeze }`
  - `public sealed record WealthResult(WealthVerdict Verdict, long Net, double Days, long Rate)`
  - `public static class WealthCheck { public static WealthResult? Evaluate(long baseWealth, long baseTick, long newWealth, long newTick, long imported, IntegrityOptions options, bool isAdmin); }` — null when skipped (`baseWealth == 0`, `newTick <= baseTick`); `Freeze` downgrades to `Warn` for admins; a threshold of 0 never fires.

- [ ] **Step 1: Write the failing tests**
  - `RateIsNetGainPerDay` — base 50000 @ 0, new 110000 @ 120000, imported 20000 → `Net 40000, Days 2, Rate 20000`, `Warn` (threshold 20000 inclusive).
  - `DaysAreAtLeastOne` — 1000 ticks apart, net 30000 → `Days 1, Rate 30000`.
  - `FreezeAtTheSecondThreshold` — rate 100000 → `Freeze`; 99999 → `Warn`; 19999 → `None`.
  - `AdminsAreOnlyWarned` — rate 500000, `isAdmin` → `Warn`.
  - `ZeroThresholdsNeverFire` — warn 0, freeze 0, rate 10^9 → `None`; warn 0, freeze 100000, rate 150000 → `Freeze`.
  - `SkipsWhenTheBaseHasNoWealth`, `SkipsWhenTheTickDidNotAdvance` — null.
  - `ImportsLargerThanTheGainGiveANegativeNet` — net −5000 → `None`.
- [ ] **Step 2: Run, expect FAIL.**  **Step 3: Implement.**  **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "Add the wealth jump check"`

### Task 6: IntegrityService on every upload (dev-mode flag, wealth alerts, feed)

**Files:**
- Create: `src/Rimlink.Server/Application/Integrity/IntegrityService.cs` (scoped; registered in `Program.cs`)
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (read the current version as base before writing; after commit `await integrity.OnStored(playerId, baseVersion, version)` in `try/catch` that logs and swallows)
- Modify: `src/Rimlink.Server/Application/Feed/FeedData.cs` (`FeedTypes.IntegrityAlert = "integrity.alert"` in `All`; `record IntegrityAlertData(FeedPlayer Player, string Kind, int Version, long? Net, double? Days, long? Rate, bool Frozen)`)
- Modify: `src/Rimlink.Server/Application/Admin/Actor.cs` (`Actor.System = new(0, 0, Role.Admin, "system")`)
- Modify: `src/Rimlink.Protocol/Protos/economy.proto` (`PawnBlob.market_value_x100 = 8`)
- Modify: `docs/server-setup.md` (feed type, what triggers a freeze, the trust model in one paragraph)
- Test: `tests/Rimlink.Server.Tests/Integrity/IntegrityServiceTests.cs`

**Interfaces:**
- Consumes: `WealthCheck.Evaluate` (Task 5), `PlayerFreeze` (Task 3), `IntegrityOptions` (Task 1), `SaveVersion` columns (Task 2).
- Produces: `IntegrityService.Task OnStored(long playerId, SaveVersion? baseVersion, SaveVersion stored)`; `IntegrityService.Task<long> ImportedBetween(long playerId, long fromTickExclusive, long toTickInclusive)` (silver + items + pawns of delivered parcels, values ÷ 100).
- Order inside `OnStored`: frozen already → return; dev-mode flag and not `options.DevModeAllowed(role)` → audit `integrity-devmode`, feed `devmode`, freeze with reason, return; then wealth: `Warn` → audit `integrity-wealth` (`net`, `days`, `rate`, `version` in details), feed `wealth-warn`; `Freeze` → same with `wealth-freeze` and freeze.

- [ ] **Step 1: Write the failing tests** (save fixture + `fixture.Probe()` for the feed; parcels inserted through a DB scope with `DeliveredAt`/`DeliveredTick`):
  - `DevModeSaveFreezesAPlayer` — `nova` uploads with `DevMode = true` → frozen with reason `dev mode was on in save v1`; feed `integrity.alert` `kind == "devmode"`, `frozen == true`; audit `integrity-devmode` by `system`; the upload itself succeeded.
  - `DevModeSaveOfAnAdminIsOnlyRecorded` — admin uploads with `DevMode = true` → not frozen, no `integrity.alert`, version `DevMode == true`.
  - `DevModeEveryoneRecordsOnly` — `Integrity:DevMode=Everyone`, `nova` flagged → not frozen.
  - `WealthJumpWarns` — v1 50000 @ 1000, v2 80000 @ 61000 → feed `wealth-warn` with `rate == 30000`, `frozen == false`; not frozen.
  - `WealthJumpFreezes` — v2 200000 @ 61000 → `wealth-freeze`, frozen with reason `wealth +150,000/day in save v2`.
  - `DeliveredPurchaseBetweenSavesDoesNotAlert` — v1 50000 @ 1000; parcel to `nova` with items worth 40000 (`market_value_x100` 4_000_000, count 1) delivered at tick 30000; v2 85000 @ 31000 → no alert.
  - `PawnValueCountsAsImported` — same with a pawn blob `market_value_x100` 3_000_000 → net reduced by 30000.
  - `FrozenPlayerGetsNoNewAlerts` — frozen first, then two jumping uploads → no `integrity.alert`, no new audit rows.
  - `FirstSaveIsNotChecked` — first upload with wealth 10^7 → no alert.
- [ ] **Step 2: Run, expect FAIL.**  **Step 3: Implement.**  **Step 4: Run, expect PASS**; whole suite green.
- [ ] **Step 5: Commit** — `git commit -m "Check every upload for dev mode and wealth jumps and alert admins"`

### Task 7: Client core — dev-mode header, pawn value, freeze refusals

**Files:**
- Modify: `src/Rimlink.Client.Core/Net/SaveTransfer.cs` (`SaveMeta(..., bool DevMode = false)` → `SaveHeader.DevMode`)
- Modify: `src/Rimlink.Client.Core/Economy/TransferOutbox.cs` (`IsDefinitive`: `PlayerFrozen` is definitive; comment says why)
- Test: `tests/Rimlink.Client.Tests/Net/SaveTransferTests.cs`, `tests/Rimlink.Client.Tests/Economy/` (the outbox test file)

- [ ] **Step 1: Write the failing tests**
  - `SaveTransferTests.DevModeFlagReachesTheServer` — upload with `new SaveMeta(seed, "Nova", 777, DevMode: true)` → the stored version row has `DevMode == true`.
  - `IsDefinitiveForPlayerFrozen` — an `RpcException` carrying `ErrorCode.PlayerFrozen` → true; `EconomyFrozen` → still false.
- [ ] **Step 2: Run, expect FAIL.**  **Step 3: Implement.**  **Step 4: Run, expect PASS**; whole suite green.
- [ ] **Step 5: Commit** — `git commit -m "Send the dev-mode flag with saves and treat a frozen player as a refusal"`

### Task 8: Mod — reproduce the lost save on quit (no code)

Systematic-debugging step before Task 11; findings go into the ledger.

- [ ] **Step 1:** Build and deploy the branch's mod and companion to s3 (no quit handling exists yet); start the server on port 25600 with defaults. Host `rimlink/connect` + load the colony, note the server's current version and tick (`sqlite3 D:\Build\rimlink-s3\server\rimlink.db "select Number, GameTick from save_versions where PlayerId=<id> and IsCurrent=1"`).
- [ ] **Step 2:** Play 2 in-game hours (`rimworld/play_for`), then `(Get-Process RimWorldWin64 | ? { $_.Path -like 'D:\Build\rimlink-s3\*' }).CloseMainWindow()`. Expected (bug): the process exits, no new version on the server, `Player.log` has no `save uploaded` after the close, the local `Rimlink_*.rws` is older than the close.
- [ ] **Step 3:** Same with the menu: Escape → "Quit to OS" → confirm. Record both outcomes in `.superpowers/sdd/2026-10-30-rimlink-integrity/progress.md`.

### Task 9: Mod — dev-mode guard and the saved flag

**Files:**
- Create: `src/Rimlink.Client/Game/DevModeGuard.cs`, `src/Rimlink.Client/Game/Patches/Patch_Prefs_DevMode.cs`
- Modify: `src/Rimlink.Client/UI/LoginFlow.cs` (after `RimlinkSession.Begin`: `MainThread.Post(DevModeGuard.OnSessionBegan)`), `src/Rimlink.Client/Game/RimlinkSession.cs` (`End` calls `DevModeGuard.OnSessionEnded()`; `DevModeAllowed => Login?.DevModeAllowed ?? true`)
- Modify: `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (`public bool DevModeSinceUpload` saved as `rimlinkDevModeSinceUpload`; `GameComponentUpdate` sets it when `Prefs.DevMode` before the session check, then `DevModeGuard.Tick()`)
- Modify: `src/Rimlink.Client/Game/SaveSync.cs` (`OnSaved` passes the flag in `SaveMeta`; after a successful upload `MainThread.Post` clears it if the same `Current.Game` is still loaded)
- Modify: `src/Rimlink.Client/Game/Economy/ThingBlobs.cs` (`SerializePawn` fills `MarketValueX100 = (long)(p.MarketValue * 100f)`)
- Modify: `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml` (`Rimlink.DevMode.Refused`: "Dev mode is reserved for admins on this server." / «На этом сервере режим разработчика доступен только админам.»)
- Modify: `docs/player-guide.md` (dev mode, frozen trading), `docs/manual-checklist.md` (new items)

**Interfaces:**
- Produces: `public static class DevModeGuard { static bool Blocked { get; } /* Active && !DevModeAllowed */ static void OnSessionBegan(); static void OnSessionEnded(); static void Tick(); }` — `OnSessionBegan` remembers `Prefs.DevMode` in a static `bool? restore`, sets `Prefs.DevMode = false`, clears `LudeonTK.DebugTools.curTool`; `Tick` repeats the switch-off when `Blocked && Prefs.DevMode`; `OnSessionEnded` sets `Prefs.DevMode = restore` and clears it.
- `Patch_Prefs_DevMode`: `[HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Setter)]` prefix `bool Prefix(bool value)` → when `value && DevModeGuard.Blocked`: `Messages.Message("Rimlink.DevMode.Refused".Translate(), MessageTypeDefOf.RejectInput, historical: false)`, return false.

- [ ] **Step 1:** Implement; `dotnet test Rimlink.sln -nologo -v q` green; build and deploy to s3 (games stopped).
- [ ] **Step 2: In game** (server default `AdminsOnly`): guest with dev mode on in its Prefs logs in → dev mode off, debug buttons gone; on the scenario page, Options → dev mode checkbox → stays off with the message (Review Focus 4); in the colony the same; leave to the menu → dev mode back on. Host (admin) keeps dev mode.
- [ ] **Step 3: In game, recorded only:** server with `Rimlink__Integrity__DevMode=Everyone`; guest turns dev mode on in its colony and gets an upload (wait for the autosave, or `rimbridge/run_lua` calling `Find.Autosaver.DoAutosave()`, which the mod redirects to the multiplayer file) → its new version has `DevMode = 1` (`sqlite3 … "select Number, DevMode from save_versions where PlayerId=<guest>"`), guest not frozen; the next upload after that has `DevMode = 0` (flag cleared after upload).
- [ ] **Step 4: In game, frozen:** still on `Everyone`, guest turns dev mode on again, then the server is stopped → the guest's "connection failed" dialog → to menu (`SaveLocallyForLater` writes the flagged file and the pending flag); restart the server with defaults (`AdminsOnly`); guest logs in → `login plan: UploadLocalThenLoad`, dev mode forced off, `players.EconomyFrozenAt` set with `dev mode was on in save vN`, the guest sees the `Notice`; `rimlink/send` is refused with the reason; the host's chat `/unfreeze Nova-s3` → the guest's send works.
- [ ] **Step 5:** Checklist items written and ticked; commit — `git commit -m "Keep dev mode off for non-admins while connected and flag saves made with it"`

### Task 10: Mod — locked storyteller and difficulty

**Files:**
- Modify: `src/Rimlink.Client/Game/WorldSetup.cs` (`CollectParams` fills `DifficultyValues` from `Find.Storyteller.difficulty`; new `ServerStoryteller()`; new `ApplyStoryteller(Storyteller target)`)
- Create: `src/Rimlink.Client/Game/Patches/Patch_Page_SelectStoryteller.cs` (prefixes on `DoWindowContents` and `CanDoNext`, both writing the page's private `storyteller`, `difficulty`, `difficultyValues` through `Traverse`), `Patch_Page_SelectStorytellerInGame.cs` (postfix on `DoWindowContents`)
- Modify: `src/Rimlink.Client/Game/RimlinkGameComponent.cs` (first `Playing` frame of a loaded own colony: `WorldSetup.EnforceStorytellerOnce()`; skipped in a visit and in `Live.MirrorMode.Active`)
- Modify: both `Rimlink.xml` (`Rimlink.Storyteller.Locked`: "Storyteller and difficulty are set by {0}." / «Рассказчик и сложность заданы сервером {0}.»; `Rimlink.Storyteller.Applied`: "The server's storyteller and difficulty were applied: {0}, {1}." / «Применены рассказчик и сложность сервера: {0}, {1}.»)
- Modify: `docs/player-guide.md`, `docs/manual-checklist.md`

**Interfaces:**
- Produces: `public sealed record ServerStorytellerSettings(StorytellerDef Def, DifficultyDef Difficulty, Difficulty? Values)`; `WorldSetup.ServerStorytellerSettings? ServerStoryteller()` — null when not locked, no world, or a def is missing (`Log.Warning` once per session); `Values` is the deserialised `difficulty_values`, or `new Difficulty(def)` for a non-custom def, or null for Custom without stored values (then values are not enforced). `WorldSetup.bool ApplyStoryteller(Storyteller target)` — sets `def` (calling `Notify_DefChanged` when it changed), `difficultyDef`, and `difficulty` via `CopyFrom`-style field copy when `Values` is set; returns true when anything changed; after a change recaches `costListForDifficulty` and `RecipeDefGenerator.ResetRecipeIngredientsForDifficulty()` as vanilla `PreClose` does.
- `Difficulty` round trip: zstd Scribe XML through the same helpers `ThingBlobs` uses for a single `IExposable` (add `ThingBlobs.SaveExposable(IExposable)` / `LoadExposable<T>(byte[]) where T : IExposable, new()` if the existing `Save`/`Load` are typed to `Thing`).

- [ ] **Step 1:** Implement; tests green; deploy to s3.
- [ ] **Step 2: In game, new colony:** a fresh server DataDir; host creates the world on Phoebe / Custom with threat scale 50% → the world row's `Params` holds `difficulty_values`; guest's new-colony storyteller page shows Phoebe / Custom and the label; clicking Randy then Next (and the `rimlink/start_colony` path with its Cassandra/Rough defaults) still starts on Phoebe / Custom 50% (bridge Lua: `Find.Storyteller.def.defName`, `Find.Storyteller.difficulty.threatScale`).
- [ ] **Step 3: In game, change and load:** in the guest's colony Escape → storyteller → pick Randy → it reverts to Phoebe with the label. Server restarted with `Rimlink__Integrity__StorytellerPolicy=Free`: guest switches to Randy in game and saves (uploaded); server back to defaults; guest logs in and loads → message `Applied`, storyteller Phoebe / Custom 50%.
- [ ] **Step 4:** Checklist items; commit — `git commit -m "Hold every colony to the world's storyteller and difficulty"`

### Task 11: Mod — save and upload on quit to the OS

**Files:**
- Create: `src/Rimlink.Client/Game/QuitGuard.cs`
- Modify: `src/Rimlink.Client/Game/SaveSync.cs` (`public static void SaveUploadAndEnd(Action then)` — the body of today's `Patch_GenScene_GoToMainMenu.Prefix` after its guard: save in the own colony, long event `Rimlink.Flow.Uploading` waiting ≤ 60 s, `Pending.Set()` when not uploaded, `RimlinkGameComponent.EndSession()`, then `then()`)
- Modify: `src/Rimlink.Client/Game/Patches/Patch_GenScene_GoToMainMenu.cs` (calls `SaveSync.SaveUploadAndEnd(() => { leaving = false; GenScene.GoToMainMenu(); })`)
- Modify: `src/Rimlink.Client/RimlinkMod.cs` (`RimlinkStartup` registers `Application.wantsToQuit += QuitGuard.WantsToQuit`)
- Modify: `docs/player-guide.md`, `docs/manual-checklist.md` (item from the table «Выгрузка при выходе в ОС»)

**Interfaces:**
- Produces:
  - `SaveSync.SaveUploadAndEnd(Action then)` keeps the running flow in a static (`Action? leaveThen`, non-null while a flow runs). Called while a flow runs: it replaces `leaveThen` with `then` and starts nothing (a quit during leave-to-menu quits instead of opening the menu). The flow clears `leaveThen` before invoking it.
  - `public static class QuitGuard { public static bool WantsToQuit(); }` — returns true when `!RimlinkSession.Active` and no flow runs; otherwise calls `SaveSync.SaveUploadAndEnd(Application.Quit)` and returns false. A second Alt+F4 during "Uploading…" lands in the "flow runs" branch (Review Focus 5). After `EndSession` the continuation's `Application.Quit()` re-enters `WantsToQuit`, which now returns true.

- [ ] **Step 1:** Implement; tests green; deploy to s3.
- [ ] **Step 2: In game:** repeat Task 8 steps 2 and 3 → each time "Uploading…" shows, the process exits within seconds, the server has a new current version with a tick later than the noted one, `Player.log` has `save uploaded as vN`. Then with the server stopped: close → exits after ≤ 60 s, `pending` flag file present, next login logs `login plan: UploadLocalThenLoad`. Press Alt+F4 twice during "Uploading…" → one `saving`, one upload, one exit. Leave to menu still works as before.
- [ ] **Step 3:** Checklist items; commit — `git commit -m "Save and upload the colony when the game is closed"`

### Task 12: Two-player run, docs pass, README

**Files:**
- Modify: `docs/manual-checklist.md` (section «Целостность (ветка `asofwar/integrity`)» with the runs of Tasks 9–11 and the wealth runs below), `README.md` (status line), `docs/server-setup.md` (final read-through)

- [ ] **Step 1: In game (wealth):** server `AdminsOnly`, defaults; host (admin) spawns 50 000 silver by dev mode and autosaves → admin audit `integrity-wealth`, feed `wealth-warn` (`GET /api/admin/events?after=`), not frozen. Server with `Integrity:DevMode=Everyone`, `WealthFreezePerDay=100000`: guest spawns 200 000 silver → `wealth-freeze`, guest frozen, its lots vanish from the host's market list, the panel card shows the badge; the host buys a guest lot before the freeze, guest's next save → no alert (purchase imported).
- [ ] **Step 2:** `dotnet test Rimlink.sln -nologo -v q` green; `.\scripts\Build-ClientMod.ps1` builds; docs consistent with behaviour.
- [ ] **Step 3: Commit** — `git commit -m "Record the integrity runs in the checklist"`
