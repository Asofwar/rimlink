# Rimlink Core — план реализации: клиент (мод RimWorld 1.7)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Мод RimWorld 1.7, который подключается к серверу Rimlink, проходит вход и проверку модпака, играет в общем мире с чужими поселениями на карте, отправляет сейвы и общается в чате.

**Architecture:** Четыре слоя: `Net` (gRPC, без Verse), `State` (локальная копия общего состояния и очередь событий), `Game` (только главный поток, через `GameComponent`), `UI`. Harmony-патчи только для хуков сохранения, поселения и главного меню.

**Tech Stack:** .NET 10 (рантайм игры), Grpc.Net.Client 2.84.0, Google.Protobuf 3.36.2, Lib.Harmony 2.4.3 (компиляция; рантайм — мод Harmony), `Assembly-CSharp`/`UnityStubs` из установленного RimWorld 1.7, xunit 2.9.3 для `Net`/`State`.

**Spec:** `docs/superpowers/specs/2026-10-28-rimlink-core-design.md`

**Зависит от:** серверный план Task 1 (спайк) — состав DLL, которые везёт мод, берётся из `spike/grpc-in-game/README.md`. Если спайк провалился, Task 2 этого плана переписывается под `SslStream`+MessagePack, остальные задачи не меняются.

## Global Constraints

- Только RimWorld 1.7; packageId `asofwar.rimlink`; `loadAfter` `brrainz.harmony`; `modDependencies` Harmony.
- Все обращения к `Verse`/`RimWorld`/`UnityEngine` только с главного потока; слои `Net` и `State` не ссылаются на `Assembly-CSharp`.
- Harmony: ни одного безусловного `return false` в prefix; патчи только на `GameDataSaveLoader.SaveGame(string)` (postfix), `SettleUtility.AddNewHome(PlanetTile, Faction)` (postfix), `SettlementAbandonUtility.TryAbandonViaInterface(MapParent)` (prefix с условием «наше поселение в сетевой игре»), `MainMenuDrawer.DoMainMenuControls(Rect, bool)` (postfix).
- Переподключение: задержки 1, 2, 4, 8, 16, 32, 60 с, всего не дольше 2 минут.
- Сетевые сейвы в `Saves/Rimlink/<server-id>/`, одиночные сейвы и `Prefs` мод не трогает.
- Все строки UI через `Translate()`, ключи с префиксом `Rimlink.`, языки `English` и `Russian`.
- Мод везёт только те DLL, что перечислены в вердикте спайка; `Microsoft.Extensions.Logging.Abstractions` и `System.Diagnostics.DiagnosticSource` не везёт.

## Review Focus

1. Обрыв сети во время загрузки сейва на сервер: локальный файл цел, при следующем входе клиент предлагает отправить его (тест в Task 5).
2. Событие о поселении на тайле, где у клиента уже есть объект (повтор после переподключения): объект не дублируется (тест в Task 3).
3. Отпечаток сертификата сервера изменился: подключение останавливается с предупреждением, а не падает молча (тест в Task 2).
4. Сервер отвечает `MODPACK_MISMATCH` с пустым токеном: окно с диффом, кнопки работают, повторный вход после перезапуска (ручной чек-лист, Task 6).
5. `SessionReplaced` при входе с другого места: игра ставится на паузу, окно «вход с другого места», без автопереподключения (тест в Task 2).

---

### Task 1: Каркас мода и сборка

**Files:**
- Create: `src/Rimlink.Client/Rimlink.Client.csproj`, `src/Rimlink.Client/RimlinkMod.cs`, `mod/About/About.xml`, `mod/About/Preview.png`, `mod/Languages/English/Keyed/Rimlink.xml`, `mod/Languages/Russian/Keyed/Rimlink.xml`, `scripts/Build-ClientMod.ps1`, `tests/Rimlink.Client.Tests/Rimlink.Client.Tests.csproj`
- Modify: `Rimlink.sln`, `.github/workflows/ci.yml` (в CI собираются только `Rimlink.Client.Core` и его тесты; `Rimlink.Client` требует установленной игры)

**Interfaces:**
- Проект делится на две сборки: `src/Rimlink.Client.Core` (слои `Net` и `State`, без игры, собирается в CI) и `src/Rimlink.Client` (слои `Game`, `UI`, Harmony; ссылается на Core и игру). Тесты — на Core.
- `RimlinkMod : Mod` с `ModSettings` (`ServerList`), `[StaticConstructorOnStartup] static class RimlinkStartup` — создаёт `Harmony("asofwar.rimlink").PatchAll()`.
- `Build-ClientMod.ps1 -RimWorldDir <dir> [-InstallTo <Mods>]` — сборка Release, копирование `mod/` + `Assemblies/` в `build/mod/Rimlink`, DLL по списку спайка.

- [ ] **Step 1:** Проекты и `About.xml` (`name` Rimlink, `author` Asofwar, `packageId asofwar.rimlink`, `supportedVersions 1.7`, зависимость Harmony).
- [ ] **Step 2:** `dotnet build src/Rimlink.Client -p:RimWorldDir=D:\SteamLibrary\steamapps\common\RimWorld` — 0 ошибок; `scripts/Build-ClientMod.ps1 -InstallTo D:\SteamLibrary\steamapps\common\RimWorld\Mods` — папка с DLL.
- [ ] **Step 3:** Запуск игры с модом Harmony и Rimlink: в логе `[Rimlink] loaded`, ошибок нет.
- [ ] **Step 4: Commit** — `"Add client mod skeleton and build script"`.

### Task 2: Слой Net

**Files:**
- Create: `src/Rimlink.Client.Core/Net/ServerEndpoint.cs`, `Net/CertificatePinStore.cs`, `Net/RimlinkConnection.cs`, `Net/ConnectionState.cs`, `Net/ReconnectPolicy.cs`, `Net/ILog.cs`
- Test: `tests/Rimlink.Client.Tests/Net/ReconnectPolicyTests.cs`, `Net/CertificatePinStoreTests.cs`, `Net/RimlinkConnectionTests.cs` (против in-process gRPC-сервера на `Rimlink.Server` через `WebApplicationFactory` — тесты клиента ссылаются на серверный тестовый проект `Support`)

**Interfaces:**
- `ServerEndpoint { string Id; string Host; int Port; string? PinnedSha256; }` (`Id` = `host_port`).
- `CertificatePinStore(string filePath)`: `PinCheck Check(ServerEndpoint, X509Certificate2)` → `Trusted | FirstSeen(fingerprint) | Changed(old, new)`; `Save(endpointId, fingerprint)`. Используется в `SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback`; при `FirstSeen`/`Changed` соединение не открывается, пока UI не подтвердит (`TaskCompletionSource<bool>` через `IPinPrompt`).
- `RimlinkConnection(ServerEndpoint, ICredentialProvider, IPinPrompt, ILog)`: `Task<LoginReply> ConnectAsync(LoginRequest, CancellationToken)`; `ConnectionState State` (`Disconnected, Connecting, Connected, Reconnecting, Replaced, Failed`) + `event Action<ConnectionState>`; `event Action<ServerEvent> EventReceived`; свойства `Auth.AuthClient Auth`, `World.WorldClient World`, `Saves.SavesClient Saves`, `Chat.ChatClient Chat`, `Modpack.ModpackClient Modpack`, `Admin.AdminClient Admin` на одном канале с `CallCredentials`, добавляющим `authorization: Bearer <token>`; `Task DisconnectAsync()`. При обрыве стрима событий — `ReconnectPolicy` (1,2,4,8,16,32,60 с, суммарно ≤ 120 с), повторный `Login` теми же учётными данными; при `SessionReplaced` в стриме — `State = Replaced`, без переподключения.
- `ReconnectPolicy.Delays() : IEnumerable<TimeSpan>` и `Deadline = 120 s`.

- [ ] **Step 1: Тесты.** `ReconnectPolicyDelaysAreOneToSixtyWithinTwoMinutes` (сумма ≤ 120 с, последовательность `1,2,4,8,16,32,60`); `PinStoreFirstSeenThenTrustedThenChanged` (Review Focus 3); `ConnectLoginsAndReceivesNotice`; `SessionReplacedStopsReconnect` (Review Focus 5: второй вход → у первого `State == Replaced`, попыток `Login` больше нет — считать вызовы через фейковый `ICredentialProvider`); `DroppedStreamReconnectsAndResubscribes` (остановить `WebApplicationFactory`-хост, поднять новый на том же адресе — или проще: сервер делает `Kick`, клиент получает `Unavailable` и переподключается, `State` проходит `Reconnecting → Connected`).
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add client network layer with pinning and reconnect"`.

### Task 3: Слой State

**Files:**
- Create: `src/Rimlink.Client.Core/State/SharedState.cs`, `State/EventInbox.cs`, `State/ChatLog.cs`, `State/PlayersOnline.cs`, `State/WorldObjectsIndex.cs`
- Test: `tests/Rimlink.Client.Tests/State/SharedStateTests.cs`

**Interfaces:**
- `EventInbox`: `Enqueue(ServerEvent)` (сетевой поток) и `DrainTo(Action<ServerEvent> apply, int max = 256)` (главный поток); `ConcurrentQueue`.
- `SharedState.Apply(ServerEvent)` — обновляет `PlayersOnline` (`IReadOnlyList<PlayerRef>`), `ChatLog` (кольцевой буфер 500), `WorldObjectsIndex` (`Dictionary<long, WorldObject>` + `TryGetByTile(int)`), и поднимает `event Action<WorldObject> ObjectAdded / Action<long> ObjectRemoved` (слой `Game` подписан). Повтор `WorldObjectAdded` с тем же `id` — no-op без события (Review Focus 2). `Reset()` при отключении. `LoadSnapshot(WorldObjects, ChatHistory, players)` после входа.

- [ ] **Step 1: Тесты.** `DuplicateWorldObjectAddedIsIgnored`; `ChatLogKeepsLastFiveHundred`; `RemovedObjectDisappearsFromTileIndex`; `ResetClearsEverything`; `DrainAppliesInOrderAndStopsAtMax`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add client shared state and event inbox"`.

### Task 4: Слой Game: сессия, пульс событий, чужие поселения

**Files:**
- Create: `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `Game/RimlinkSession.cs`, `Game/ForeignSettlements.cs`, `Game/PlayerFactions.cs`, `Game/WorldSetup.cs`, `Game/Patches/Patch_SettleUtility_AddNewHome.cs`, `Game/Patches/Patch_SettlementAbandonUtility.cs`, `mod/Defs/WorldObjectDefs/Rimlink_PlayerSettlement.xml`, `mod/Defs/FactionDefs/Rimlink_PlayerFaction.xml`

**Interfaces:**
- `RimlinkSession` (static): `RimlinkConnection? Connection`, `SharedState State`, `EventInbox Inbox`, `ServerEndpoint? Endpoint`, `LoginReply? Login`, `bool Active`.
- `RimlinkGameComponent : GameComponent` — `GameComponentUpdate()`: `Inbox.DrainTo(State.Apply)`; при `Connection.State == Reconnecting` → `Find.TickManager.Pause()` и баннер; при `Replaced`/`Failed` → окно и выход в меню.
- `ForeignSettlements`: подписка на `State.ObjectAdded/ObjectRemoved`; создаёт `WorldObject` def `Rimlink_PlayerSettlement` на `tile` с фракцией из `PlayerFactions.GetOrCreate(PlayerRef)` (одна `Faction` на игрока, `FactionDef Rimlink_PlayerFaction`, имя и цвет из `PlayerRef`, `hidden=false`, без взаимодействий); удаляет по `id`. Свои поселения не создаёт.
- `WorldSetup.CreateFromServer(WorldInfo)` — генерирует планету по `WorldParams` (`Current.Game.World = WorldGenerator.GenerateWorld(...)` с теми же параметрами) при входе без сейва; `WorldSetup.CollectParams() : (WorldParams, int[] npcTiles)` при создании мира админом (`Find.WorldObjects.Settlements` не игрока).
- Патчи: postfix `AddNewHome` — если `Active` и `faction == Faction.OfPlayer` → `World.Settle(tile, name)`; при `TILE_TAKEN`/`SETTLEMENT_LIMIT` — удалить созданное поселение и показать ошибку. Prefix `TryAbandonViaInterface` — если `Active` и поселение игрока, после подтверждения вызвать `World.Abandon(objectId)` и вернуть `true` (оригинал выполняется).

- [ ] **Step 1:** Реализация по Interfaces (авто-тестов на этот слой нет).
- [ ] **Step 2: Ручная проверка** (два клиента на одной машине: Steam-аккаунт + пароль-аккаунт, сервер локально): админ создаёт мир → второй входит, видит ту же планету → первый основывает поселение → у второго появляется объект с именем и цветом; покидание убирает; переподключение не дублирует. Записать в `docs/manual-checklist.md`.
- [ ] **Step 3: Commit** — `"Add game session component and foreign settlements"`.

### Task 5: Сейвы на клиенте

**Files:**
- Create: `src/Rimlink.Client.Core/Net/SaveTransfer.cs`, `src/Rimlink.Client/Game/SaveSync.cs`, `Game/Patches/Patch_GameDataSaveLoader_SaveGame.cs`
- Test: `tests/Rimlink.Client.Tests/Net/SaveTransferTests.cs`

**Interfaces:**
- `SaveTransfer(RimlinkConnection)`: `Task<SaveInfo> UploadAsync(string rwsPath, SaveHeader meta, CancellationToken)` — zstd-сжатие в поток, SHA-256, header + куски по 1 МБ; `Task<string> DownloadAsync(string targetDir, CancellationToken)` — в temp, распаковка, rename в `<targetDir>/current.rws`.
- `SaveSync`: `SaveFilePath` = `Saves/Rimlink/<endpoint.Id>/current.rws`; `OnSaved(string fileName)` (из postfix `SaveGame`, только если файл наш) — ставит задачу `UploadAsync` в фон, результат пишет в `PendingUpload` при ошибке; `PendingUpload : bool` хранится в `Saves/Rimlink/<id>/pending.flag`; при входе, если флаг есть и локальный файл новее серверного (`SaveInfo.created_at`), окно «отправить локальный сейв?». `TriggerSave(string reason)` вызывает `GameDataSaveLoader.SaveGame(SaveFilePath)` при основании/покидании поселения и выходе в меню.

- [ ] **Step 1: Тесты.** `UploadThenDownloadRoundTripsThroughServer`; `UploadFailureLeavesLocalFileAndSetsPendingFlag` (Review Focus 1: сервер отвечает `SAVE_INVALID` → файл цел, `pending.flag` создан); `HeaderCarriesSeedFactionAndTick`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add client save upload, download and pending retry"`.

### Task 6: UI: главное меню, серверы, вход, модпак, чат, игроки

**Files:**
- Create: `src/Rimlink.Client/UI/Dialog_ServerList.cs`, `UI/Dialog_Connect.cs`, `UI/Dialog_Wait.cs`, `UI/Dialog_Error.cs`, `UI/Dialog_PinPrompt.cs`, `UI/Dialog_ModpackDiff.cs`, `UI/Dialog_Chat.cs`, `UI/Dialog_Players.cs`, `UI/Dialog_AdminCommand.cs`, `UI/MainMenuPatch.cs`, `UI/ModpackActions.cs`, `UI/SteamAuth.cs`, `UI/LoginFlow.cs`
- Modify: `mod/Languages/*/Keyed/Rimlink.xml`

**Interfaces:**
- `LoginFlow.Run(ServerEndpoint)`: подключение (`Dialog_Wait` с кнопкой отмены → `CancellationToken`) → `SteamAuth.GetTicket()` (`SteamUser.GetAuthSessionTicket(byte[1024], 1024, out len)`, если `SteamManager.Initialized`; иначе `Dialog_Connect` просит логин и пароль) → `ConnectAsync` → по `LoginReply`: `modpack_diff` заполнен → `Dialog_ModpackDiff`; `!world_exists` и не админ → `Dialog_Error(WORLD_NOT_CREATED)`; админ без мира → обычное создание мира, в конце `World.Create`; `has_save` → `SaveTransfer.DownloadAsync` → `GameDataSaveLoader.LoadGame`; иначе `WorldSetup.CreateFromServer` → выбор места высадки.
- `ModpackActions`: `SubscribeMissing(diff)` (`SteamUGC.SubscribeItem` для `workshop_id != 0`), `OpenUrls(diff)`, `ApplyServerOrder(diff.server_list)` (копия `ModsConfig.xml` в `ModsConfig.rimlink-backup.xml`, затем `ModsConfig.SetActiveToList(...)` + `ModsConfig.Save()`), `Restart()` (`GenCommandLine.Restart()`).
- `Dialog_Chat`: ввод (`Limits.ChatMaxLength`), список из `State.ChatLog`, теги экранируются (`<` → `&lt;`); `/`-команды → `Admin.Execute`. `Dialog_Players`: онлайн из `State.PlayersOnline`, кнопка «написать». Кнопка «Мультиплеер» — postfix `DoMainMenuControls`, ряд с «Новая игра».
- Все тексты через ключи `Rimlink.*` в обоих языках.

- [ ] **Step 1:** Реализация по Interfaces.
- [ ] **Step 2: Ручной чек-лист** (`docs/manual-checklist.md`): отмена ожидания; неверный пароль → окно с текстом; модпак-дифф → подписка → перезапуск → вход (Review Focus 4); pinning при первом подключении и при смене сертификата; чат общий и личный, лимит 5/5 с показывает сообщение; команда `/kick` от модератора; обрыв сети (выдернуть кабель/закрыть сервер) → пауза, баннер, восстановление; вход с другого места → окно, выход в меню.
- [ ] **Step 3: Commit** — `"Add client UI: server list, login flow, modpack, chat, players"`.

### Task 7: Документация игрока и сборка релиза

**Files:**
- Create: `docs/player-guide.md`, `docs/manual-checklist.md` (свести), `scripts/Package-Release.ps1`
- Modify: `README.md`

- [ ] **Step 1:** `player-guide.md`: установка (Harmony + Rimlink), подключение, модпак, что делать при ошибках. `Package-Release.ps1` собирает `rimlink-client-<ver>.zip` и `rimlink-server-<ver>-<rid>.zip` (publish `linux-x64`, `win-x64`).
- [ ] **Step 2:** Полный прогон `docs/manual-checklist.md` на двух клиентах — все пункты отмечены.
- [ ] **Step 3: Commit** — `"Add player guide and release packaging"`.
