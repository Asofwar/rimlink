# Rimlink Core — план реализации: протокол, сервер, инструменты

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Выделенный сервер Rimlink с gRPC-API игры, REST-админкой и CLI, покрытый тестами и упакованный в Docker, плюс спайк, подтверждающий транспорт внутри RimWorld 1.7.

**Architecture:** Один Kestrel-хост (HTTP/2 + TLS) обслуживает gRPC-сервисы игры и REST `/api/admin/*`. Вся бизнес-логика в application-сервисах (`Rimlink.Server/Application/*`), к которым gRPC и REST — тонкие адаптеры. Состояние в SQLite (EF Core), сейвы блобами на диске, активные сессии в памяти.

**Tech Stack:** .NET 10, Grpc.AspNetCore 2.84.0, Grpc.Tools 2.85.0, Google.Protobuf 3.36.2, Microsoft.EntityFrameworkCore.Sqlite 10.0.14, Serilog.AspNetCore 10.0.0, prometheus-net.AspNetCore 8.2.1, Konscious.Security.Cryptography.Argon2 1.3.1, ZstdSharp.Port 0.8.8, System.CommandLine 2.0.14, xunit 2.9.3, Microsoft.AspNetCore.Mvc.Testing 10.0.14.

**Spec:** `docs/superpowers/specs/2026-10-28-rimlink-core-design.md`

## Global Constraints

- Только RimWorld 1.7; сервер — `net10.0`, `Nullable` enable, `ImplicitUsings` enable, все файлы UTF-8.
- Порт по умолчанию 25560; TLS обязателен (`Tls:Required=true`), без HTTPS-эндпоинта сервер не стартует.
- HTTP/2 keep-alive: `KeepAlivePingDelay` 30 с, `KeepAlivePingTimeout` 10 с.
- Лимиты: сейв ≤ 64 МБ кусками по 1 МБ, остальные вызовы ≤ 1 МБ; чат 500 символов, 5 за 5 с, хранится 500, выдаётся 50; загрузка сейва 1 за 30 с; ≤ 5 неавторизованных соединений с IP; таймаут входа 30 с.
- Токен сессии — 32 случайных байта (base64url); один аккаунт — одна активная сессия.
- Сейвы: последние 10 версий плюс по одной в день за 7 дней; запись атомарная (temp + rename).
- `MaxSettlementsPerPlayer` по умолчанию 1; тайл занят не более чем одним поселением.
- Пароли — Argon2id (64 МБ, 3 итерации, 2 потока, соль 16 байт, хеш 32 байта).
- Имена: `^[\p{L}\p{N} _\-.']{2,32}$`, без управляющих символов; файлы и каталоги именуются только числовыми ID.
- Версия протокола — `int32`, константа `ProtocolVersion.Current = 1` в `Rimlink.Protocol`.
- `.proto`: `package rimlink.v1; option csharp_namespace = "Rimlink.Protocol.V1";` события через `oneof`; существующие поля не меняют смысла.
- Коммиты: только описание изменения, автор Asofwar, без атрибуций.

## Review Focus

1. Обрыв загрузки сейва после N кусков: временный файл удалён, предыдущая версия остаётся текущей (тест в Task 11).
2. Два одновременных `Settle` на один тайл: ровно один успех, второй — `FAILED_PRECONDITION/TILE_TAKEN` (тест в Task 10).
3. Бан игрока онлайн: стрим событий завершается, старый токен даёт `UNAUTHENTICATED` (тест в Task 8).
4. Клиент перестал читать события: отключается только он, остальные получают события (тест в Task 7).
5. Второй вход в тот же аккаунт: первая сессия получает `SessionReplaced`, её токен недействителен (тест в Task 8).

---

### Task 1: Спайк — gRPC внутри RimWorld 1.7

**Files:**
- Create: `spike/grpc-in-game/Spike.Server/Program.cs`, `Spike.Server/Spike.Server.csproj`, `spike/grpc-in-game/ping.proto`
- Create: `spike/grpc-in-game/Spike.Mod/SpikeMod.cs`, `Spike.Mod/Spike.Mod.csproj`, `spike/grpc-in-game/mod/About/About.xml`, `spike/grpc-in-game/README.md`

**Interfaces:**
- Produces: решение «транспорт gRPC подтверждён» или «откат к SslStream+MessagePack» (меняет Task 3 и слой `Net` клиента).

- [ ] **Step 1: Сервер.** `ping.proto`: `service Ping { rpc Echo(EchoRequest) returns (EchoReply); }`, `EchoRequest{string text=1;}`, `EchoReply{string text=1; string server_time=2;}`. `Spike.Server`: ASP.NET Core, Kestrel на `https://127.0.0.1:25560`, HTTP/2, dev-сертификат (`dotnet dev-certs https --trust`), `Echo` возвращает `text` и `DateTime.UtcNow.ToString("O")`.
- [ ] **Step 2: Проверка сервера.** `dotnet run --project spike/grpc-in-game/Spike.Server` и `grpcurl -insecure -d '{"text":"hi"}' 127.0.0.1:25560 rimlink.spike.Ping/Echo` (или `dotnet-grpc` клиент в тесте). Ожидание: JSON с `text: "hi"`.
- [ ] **Step 3: Мод.** `Spike.Mod.csproj`: `net10.0`, ссылки `Assembly-CSharp`, `UnityStubs` из `$(RimWorldDir)` (`Private=false`), пакеты `Grpc.Net.Client 2.84.0`, `Google.Protobuf 3.36.2`, `Grpc.Tools 2.85.0`, `CopyLocalLockFileAssemblies=true`. Класс `[StaticConstructorOnStartup] static class SpikeMod` без Harmony: в статическом конструкторе запускает `Task.Run`, который создаёт `GrpcChannel.ForAddress("https://127.0.0.1:25560", new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_,_,_,_) => true } } })`, вызывает `Echo`, пишет результат или `ex.ToString()` в `Path.Combine(GenFilePaths.SaveDataFolderPath, "rimlink-spike.txt")` и в `Log.Message`.
- [ ] **Step 4: Сборка мода только с нужными DLL.** В папку `mod/Assemblies/` класть `Spike.Mod.dll`, `Grpc.Net.Client.dll`, `Grpc.Net.Common.dll`, `Grpc.Core.Api.dll`, `Google.Protobuf.dll`. **Не класть** `Microsoft.Extensions.Logging.Abstractions.dll` и `System.Diagnostics.DiagnosticSource.dll` — игра их уже везёт (10.0.25 и 42.42). `About.xml`: `packageId` `asofwar.rimlinkspike`, `supportedVersions` `1.7`.
- [ ] **Step 5: Прогон.** Скопировать `mod/` в `D:\SteamLibrary\steamapps\common\RimWorld\Mods\RimlinkSpike`, включить мод, запустить игру при работающем `Spike.Server`. Ожидание: файл `rimlink-spike.txt` с `hi` и временем сервера. Если `TypeLoadException`/`FileLoadException` — записать в README, какой DLL не хватило или конфликтует, добавить или убрать и повторить.
- [ ] **Step 6: Вердикт в `spike/grpc-in-game/README.md`:** список DLL, которые нужно везти с модом, и фраза «gRPC работает» либо «не работает: причина». **Если не работает — остановиться и вернуть вопрос транспорта на согласование**, план ниже не выполнять.
- [ ] **Step 7: Commit** — `git add spike && git commit -m "Add gRPC-in-game spike with verdict"`.

### Task 2: Каркас репозитория и проект Protocol

**Files:**
- Create: `Rimlink.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`
- Create: `src/Rimlink.Protocol/Rimlink.Protocol.csproj`, `src/Rimlink.Protocol/Protos/common.proto`, `src/Rimlink.Protocol/ProtocolVersion.cs`, `src/Rimlink.Protocol/Limits.cs`
- Create: `.github/workflows/ci.yml`, `.editorconfig`

**Interfaces:**
- Produces: `Rimlink.Protocol.ProtocolVersion.Current` (int, = 1); `Rimlink.Protocol.Limits` с константами `MaxSaveBytes = 64 * 1024 * 1024`, `SaveChunkBytes = 1024 * 1024`, `MaxMessageBytes = 1024 * 1024`, `ChatMaxLength = 500`, `ChatStored = 500`, `ChatOnLogin = 50`, `NameRegex = @"^[\p{L}\p{N} _\-.']{2,32}$"`.
- Produces: `common.proto`: `enum ErrorCode { UNSPECIFIED=0; WORLD_NOT_CREATED=1; TILE_TAKEN=2; SETTLEMENT_LIMIT=3; MODPACK_MISMATCH=4; BANNED=5; SERVER_FULL=6; WRONG_PASSWORD=7; PROTOCOL_VERSION=8; NAME_INVALID=9; SAVE_INVALID=10; MUTED=11; }`, `message ErrorDetail { ErrorCode code=1; string message=2; }`, `message PlayerRef { int64 player_id=1; string name=2; string faction_name=3; uint32 color_argb=4; }`.

- [ ] **Step 1:** `global.json` SDK `10.0.402` (`rollForward: latestFeature`); `Directory.Build.props`: `net10.0`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` false, `LangVersion latest`; `Directory.Packages.props` с версиями из Tech Stack (central package management).
- [ ] **Step 2:** `Rimlink.Protocol.csproj` — библиотека, `<Protobuf Include="Protos/**/*.proto" GrpcServices="Both" />`, пакеты `Google.Protobuf`, `Grpc.Tools` (`PrivateAssets=all`), `Grpc.Net.Common`. Файлы `common.proto`, `ProtocolVersion.cs`, `Limits.cs` по Interfaces.
- [ ] **Step 3:** `dotnet build src/Rimlink.Protocol` — ожидание: `Ошибок: 0`, в `obj/` сгенерирован `Common.cs`.
- [ ] **Step 4:** `ci.yml`: on push/PR; `actions/setup-dotnet` 10.0.x; `dotnet build Rimlink.sln`; `dotnet test`; job `docker` на `tags: v*` (заполняется в Task 15).
- [ ] **Step 5: Commit** — `"Add solution skeleton and Protocol project"`.

### Task 3: Хост сервера: конфигурация, TLS, health, логи, метрики

**Files:**
- Create: `src/Rimlink.Server/Rimlink.Server.csproj`, `Program.cs`, `Hosting/ServerOptions.cs`, `Hosting/TlsStartupCheck.cs`, `Hosting/SelfSignedCertificate.cs`, `Hosting/KestrelSetup.cs`, `appsettings.json`
- Create: `tests/Rimlink.Server.Tests/Rimlink.Server.Tests.csproj`, `tests/Rimlink.Server.Tests/Support/ServerFixture.cs`, `tests/Rimlink.Server.Tests/HostTests.cs`

**Interfaces:**
- Produces: `ServerOptions` (section `Server`): `Port` (25560), `DataDir` (`./data`), `MaxPlayers` (50), `ServerPassword` (string?), `ProtocolVersion` (read-only = `ProtocolVersion.Current`); `TlsOptions` (`Tls`): `Required` (true), `CertificatePath`, `KeyPath`, `SelfSignedPath` (`<DataDir>/tls/server.pfx`); `AuthOptions` (`Auth`): `AllowPassword` (false), `SteamWebApiKey`, `AdminSteamIds` (long[]), `AdminLogins` (string[]); `AdminOptions` (`Admin`): `ApiToken`.
- Produces: `ServerFixture : WebApplicationFactory<Program>` с `UseSetting("Tls:Required","false")`, `Server:DataDir` = временная папка на тест, метод `GrpcChannel CreateChannel()` (`GrpcChannel.ForAddress(client.BaseAddress, new() { HttpClient = CreateDefaultClient() })`). `Program` должен быть `public partial class Program`.

- [ ] **Step 1: Тест.** `HostTests.HealthEndpointReturnsOk`: `GET /health` → 200, тело содержит `Healthy`. `HostTests.StartupFailsWithoutTlsWhenRequired`: фабрика с `Tls:Required=true` и без сертификатов и без HTTPS-эндпоинта → `Assert.Throws<InvalidOperationException>` при `CreateClient()` с сообщением, содержащим `TLS`.
- [ ] **Step 2:** Запустить — ожидание: не компилируется (нет проекта).
- [ ] **Step 3: Реализация.** `Program.cs`: Serilog (консоль + `<DataDir>/logs/server-.log` ротация по дню), `AddGrpc(o => o.MaxReceiveMessageSize = Limits.MaxMessageBytes)`, `AddHealthChecks`, `UseHttpMetrics` + `MapMetrics("/metrics")`, `MapHealthChecks("/health")`. `KestrelSetup.Configure(KestrelServerOptions, ServerOptions, TlsOptions)`: `ListenAnyIP(Port, l => { l.Protocols = Http2; l.UseHttps(cert); })`, `Limits.Http2.KeepAlivePingDelay = 30 s`, `KeepAlivePingTimeout = 10 s`. `SelfSignedCertificate.LoadOrCreate(string pfxPath) : X509Certificate2` — RSA 2048, CN `rimlink`, срок 10 лет, пароль пустой, права файла только владельцу. `TlsStartupCheck.Run(IServer, TlsOptions)` — `IHostedService`, при `Required` и отсутствии `https://` в `IServerAddressesFeature` бросает `InvalidOperationException("TLS is required but no HTTPS endpoint is configured")`.
- [ ] **Step 4:** `dotnet test tests/Rimlink.Server.Tests --filter HostTests` — PASS.
- [ ] **Step 5: Commit** — `"Add server host with TLS enforcement, health and metrics"`.

### Task 4: Хранилище: EF Core, схема, миграции

**Files:**
- Create: `src/Rimlink.Server/Storage/RimlinkDbContext.cs`, `Storage/Entities/*.cs` (Account, Session, Ban, Player, World, WorldObject, ChatMessage, ModpackEntry, SaveVersion, AdminAction), `Storage/Migrations/*`, `Storage/DataDirectory.cs`, `Storage/Names.cs`
- Test: `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs`, `Storage/NamesTests.cs`

**Interfaces:**
- Produces: сущности (ключи `long Id`): `Account{SteamId? (long), OwnerSteamId?, Login?, PasswordHash?, Role (enum Player/Moderator/Admin), CreatedAt, LastSeenAt}`; `Player{AccountId, Name, FactionName, ColorArgb, LastIp}`; `Ban{AccountId?, SteamId?, Ip?, Reason, ExpiresAt?, IssuedByAccountId, CreatedAt}`; `World{Seed, Params (bytes protobuf), NpcTiles (string csv), CreatedAt}` (одна строка); `WorldObject{Type (string), OwnerPlayerId, Tile (int), Name, Payload (bytes), CreatedAt}`; `ChatMessage{FromPlayerId?, ToPlayerId?, Text, Kind (enum Player/System), SentAt}`; `ModpackEntry{Position, PackageId, Version, WorkshopId?, Url?, Kind (Required/Allowed)}`; `SaveVersion{PlayerId, Number, GameTick, Size, Sha256, CreatedAt, IsCurrent}`; `AdminAction{ActorAccountId, Action, TargetAccountId?, Details, At}`; `Session{AccountId, Ip, StartedAt, EndedAt?}` (история).
- Produces: `DataDirectory(string root)` с `Saves(long playerId) : string` → `<root>/saves/<playerId>`, `Logs`, `Tls`, `Archive`; создаёт каталоги. `Names.IsValid(string?) : bool` по `Limits.NameRegex` + `char.IsControl`.
- Индексы: `Account.SteamId` unique, `Account.Login` unique (collation NOCASE), `WorldObject.Tile` unique **partial** `WHERE Type = 'settlement'`, `SaveVersion(PlayerId, Number)` unique.

- [ ] **Step 1: Тесты.** `SchemaTests.MigrationsCreateAllTables` — in-memory SQLite (`DataSource=:memory:` с открытым соединением), `Database.Migrate()`, `SELECT name FROM sqlite_master` содержит все 10 таблиц. `SchemaTests.SecondSettlementOnSameTileIsRejected` — две `WorldObject{Type="settlement", Tile=100}` → `DbUpdateException` (`SqliteException.SqliteErrorCode == 19`). `NamesTests`: `Theory` — валидны `"Nova"`, `"Игрок 1"`, `"J.R.'s"`; невалидны `"a"`, 33 символа, `"a\nb"`, `"../x"`.
- [ ] **Step 2:** Запуск — FAIL (типов нет).
- [ ] **Step 3:** Реализация по Interfaces; `dotnet ef migrations add Initial -o Storage/Migrations`; в `Program` при старте `Migrate()`, `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000`. Partial index через `HasFilter("\"Type\" = 'settlement'")`.
- [ ] **Step 4:** Тесты PASS.
- [ ] **Step 5: Commit** — `"Add SQLite storage schema and migrations"`.

### Task 5: Сессии, пароли и вход по логину

**Files:**
- Create: `src/Rimlink.Protocol/Protos/auth.proto`
- Create: `src/Rimlink.Server/Application/Sessions/Session.cs`, `SessionRegistry.cs`, `Application/Auth/PasswordHasher.cs`, `Application/Auth/AuthService.cs`, `Application/Auth/LoginResult.cs`, `Grpc/AuthGrpcService.cs`, `Grpc/SessionInterceptor.cs`, `Grpc/RpcErrors.cs`
- Test: `tests/Rimlink.Server.Tests/Auth/PasswordHasherTests.cs`, `Auth/PasswordLoginTests.cs`

**Interfaces:**
- `auth.proto`: `service Auth { rpc Login(LoginRequest) returns (LoginReply); rpc Register(RegisterRequest) returns (LoginReply); rpc Logout(Empty) returns (Empty); }`. `LoginRequest{int32 protocol_version=1; oneof credential { bytes steam_ticket=2; PasswordCredential password=3; } string server_password=4; repeated ModRef mods=5; }`, `PasswordCredential{string login=1; string password=2;}`, `RegisterRequest{string login=1; string password=2; string player_name=3;}`, `ModRef{string package_id=1; string version=2;}` (объявить в `common.proto`), `LoginReply{string session_token=1; PlayerRef player=2; Role role=3; bool world_exists=4; bool has_save=5; bool has_settlement=6; ModpackDiff modpack_diff=7; }` (`ModpackDiff` объявляется в Task 12, здесь поле резервируется номером 7 и добавляется в Task 12).
- Produces: `Session { string Token; long AccountId; long PlayerId; Role Role; string Ip; CancellationTokenSource Cts; }`; `SessionRegistry` (singleton): `Session Start(Account, Player, string ip)` (вытесняет старую: `Remove` + `Cts.Cancel()`, возвращает старую через `out Session? replaced`), `bool TryGet(string token, out Session)`, `void End(string token)`, `IReadOnlyCollection<Session> All`, `Session? ByAccount(long accountId)`.
- Produces: `PasswordHasher.Hash(string) : string` (формат `argon2id$<salt-b64>$<hash-b64>`), `Verify(string password, string stored) : bool`.
- Produces: `AuthService.LoginWithPassword(string login, string password, string ip, LoginContext ctx) : Task<LoginResult>`, `Register(...)`, где `LoginResult` = `Success(Session, Player)` | `Failure(ErrorCode, string)`; `LoginContext { int ProtocolVersion; string? ServerPassword; IReadOnlyList<ModRef> Mods; }`. Проверки в порядке: версия → (бан, лимит, пароль сервера — добавляются в Task 8) → учётные данные.
- Produces: `SessionInterceptor : Interceptor` — для всех методов кроме `Auth.Login/Register` читает `authorization: Bearer <token>`, кладёт `Session` в `HttpContext.Items["session"]`, иначе `UNAUTHENTICATED`. `RpcErrors.Failed(ErrorCode, string) : RpcException` со `StatusCode.FailedPrecondition` и `ErrorDetail` в trailers (`rimlink-error-bin`).

- [ ] **Step 1: Тесты.** `PasswordHasherTests.VerifyAcceptsOriginalAndRejectsOther` (включая кириллический пароль `"пароль123"` против `"пароль124"`). `PasswordLoginTests` (через `ServerFixture` + gRPC): `RegisterThenLoginReturnsTokenAndPlayer`; `WrongPasswordIsUnauthenticated` (`StatusCode.Unauthenticated`); `RegisterIsDisabledByDefault` (`Auth:AllowPassword` не задан → `PermissionDenied`); `CallWithoutTokenIsUnauthenticated` (`Auth.Logout` без метаданных); `OldProtocolVersionIsRejected` (`protocol_version=0` → `FailedPrecondition`, `ErrorDetail.Code == PROTOCOL_VERSION`).
- [ ] **Step 2:** FAIL.
- [ ] **Step 3:** Реализация по Interfaces. Argon2id через `Konscious.Security.Cryptography.Argon2id` с параметрами из Global Constraints. `Register` создаёт `Account{Login, PasswordHash, Role}` (Admin, если логин в `Auth:AdminLogins`) и `Player{Name=player_name, FactionName=player_name}`; имя проверяется `Names.IsValid` → `NAME_INVALID`.
- [ ] **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add sessions, password hashing and login by login/password"`.

### Task 6: Вход через Steam

**Files:**
- Create: `src/Rimlink.Server/Application/Auth/ISteamTicketVerifier.cs`, `SteamWebApiTicketVerifier.cs`
- Modify: `Application/Auth/AuthService.cs` (метод `LoginWithSteam`), `Grpc/AuthGrpcService.cs`
- Test: `tests/Rimlink.Server.Tests/Auth/SteamLoginTests.cs`, `tests/Rimlink.Server.Tests/Support/FakeSteamVerifier.cs`

**Interfaces:**
- `ISteamTicketVerifier.Verify(byte[] ticket, CancellationToken) : Task<SteamIdentity?>`, `SteamIdentity{long SteamId; long OwnerSteamId; bool VacBanned;}`.
- `SteamWebApiTicketVerifier(HttpClient, IOptions<AuthOptions>)`: GET `https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/?key=<key>&appid=294100&ticket=<hex>`; успех при `response.params.result == "OK"`; `null` при любой ошибке с логом Warning.
- `AuthService.LoginWithSteam(byte[] ticket, string ip, LoginContext) : Task<LoginResult>` — аккаунт по `SteamId`, создаётся при первом входе с `Player.Name` = `"Player<steamid последние 4>"` (игрок меняет имя в Task 13 операцией `RenamePlayer`), роль Admin, если SteamId в `Auth:AdminSteamIds`.

- [ ] **Step 1: Тесты.** `SteamLoginTests.ValidTicketCreatesAccountAndLogsIn` (fake возвращает `SteamId=76561198000000001`); `InvalidTicketIsUnauthenticated` (fake → null); `OwnerSteamIdIsStoredForFamilySharing` (`Account.OwnerSteamId == 76561198000000002`); `SteamWebApiTicketVerifierParsesOkResponse` — `HttpClient` с `MockHttpMessageHandler`, тело `{"response":{"params":{"result":"OK","steamid":"76561198000000001","ownersteamid":"76561198000000001","vacbanned":false}}}`.
- [ ] **Step 2:** FAIL.
- [ ] **Step 3:** Реализация; `ServerFixture` регистрирует `FakeSteamVerifier` вместо реального через `ConfigureTestServices`.
- [ ] **Step 4:** PASS.
- [ ] **Step 5: Ручная проверка с реальным ключом** (нужен Web API key с `https://steamcommunity.com/dev/apikey`): записать в `docs/manual-checklist.md` пункт «Steam-вход: настоящий билет из спайк-мода (`SteamUser.GetAuthSessionTicket`) → `Login` → 200». Выполняется при первом прогоне клиента (клиентский план, Task 2). Если Steam отвечает `Invalid ticket` для непубликаторского ключа, записать это в README сервера как требование и оставить пароль-вход как обязательный запасной путь.
- [ ] **Step 6: Commit** — `"Add Steam ticket login"`.

### Task 7: Стрим событий и рассылка

**Files:**
- Create: `src/Rimlink.Protocol/Protos/events.proto`
- Create: `src/Rimlink.Server/Application/Events/EventBus.cs`, `Application/Events/EventQueue.cs`, `Grpc/EventsGrpcService.cs`
- Modify: `Application/Sessions/Session.cs` (поле `EventQueue Events`)
- Test: `tests/Rimlink.Server.Tests/Events/EventStreamTests.cs`

**Interfaces:**
- `events.proto`: `service Events { rpc Subscribe(Empty) returns (stream ServerEvent); }`, `ServerEvent { int64 sequence=1; google.protobuf.Timestamp at=2; oneof body { PlayerJoined player_joined=10; PlayerLeft player_left=11; SessionReplaced session_replaced=12; ChatMessageEvent chat=20; WorldObjectAdded world_object_added=30; WorldObjectRemoved world_object_removed=31; Notice notice=40; ServerShutdown shutdown=41; SaveRolledBack save_rolled_back=42; } }` — типы тел объявляются в соответствующих задачах, здесь `PlayerJoined{PlayerRef player=1;}`, `PlayerLeft{PlayerRef player=1;}`, `SessionReplaced{}`, `Notice{string text=1;}`, `ServerShutdown{int32 in_seconds=1; string reason=2;}`.
- `EventQueue`: `Channel<ServerEvent>` bounded 1024, `FullMode = Wait`; `bool TryWrite(ServerEvent)`; `IAsyncEnumerable<ServerEvent> ReadAll(CancellationToken)`.
- `EventBus` (singleton): `void Broadcast(ServerEvent)`, `void SendTo(long playerId, ServerEvent)`, `void BroadcastExcept(long playerId, ServerEvent)`; при неудачной записи в очередь за 5 с (`WaitToWriteAsync` с таймаутом) — `SessionRegistry.End(token)` + `Cts.Cancel()` и лог `Warning "slow consumer"`.
- `EventsGrpcService.Subscribe` пишет в `responseStream` из `session.Events.ReadAll(linked(ctx.CancellationToken, session.Cts.Token))`; при отмене из `Cts` завершает стрим с `StatusCode.Unavailable`.

- [ ] **Step 1: Тесты.** `EventStreamTests.BroadcastReachesAllSubscribers` (два клиента, `Notice`, оба читают `sequence` возрастающие); `SlowConsumerIsDisconnectedOthersKeepReceiving` (клиент A подписан, но не читает; 1100 `Broadcast`; клиент B получил всё; у A следующий вызов `Logout` → `Unauthenticated`); `SubscribeWithoutSessionIsUnauthenticated`.
- [ ] **Step 2:** FAIL.
- [ ] **Step 3:** Реализация по Interfaces; `sequence` — `Interlocked.Increment` в `EventBus`.
- [ ] **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add server event stream with per-session queues"`.

### Task 8: Проверки входа: баны, лимит, пароль сервера, одна сессия, лимит на IP

**Files:**
- Create: `src/Rimlink.Server/Application/Auth/BanService.cs`, `Application/Auth/LoginGate.cs`, `Hosting/UnauthenticatedConnectionLimiter.cs`
- Modify: `Application/Auth/AuthService.cs`, `Grpc/AuthGrpcService.cs`, `Application/Sessions/SessionRegistry.cs`
- Test: `tests/Rimlink.Server.Tests/Auth/LoginGateTests.cs`

**Interfaces:**
- `BanService`: `Task<Ban?> FindActive(long? accountId, long? steamId, long? ownerSteamId, string ip)`; `Task Ban(BanRequest)`, `Task Pardon(long banId)`; бан по аккаунту также завершает сессию (`SessionRegistry.End` + `Cts.Cancel()`).
- `LoginGate.Check(Account? account, SteamIdentity? steam, string ip, LoginContext ctx) : LoginResult?` — порядок: протокол → бан → лимит игроков (`SessionRegistry.All.Count >= MaxPlayers` → `SERVER_FULL`) → пароль сервера (`ctx.ServerPassword != ServerOptions.ServerPassword` при заданном → `WRONG_PASSWORD`). Модпак добавляется в Task 12 между баном и лимитом.
- `SessionRegistry.Start` при вытеснении: старой сессии `Events.TryWrite(SessionReplaced)`, затем `Cts.Cancel()`.
- `UnauthenticatedConnectionLimiter` — `ConnectionDelegate` middleware Kestrel: счётчик соединений на IP, у которых нет сессии; >5 → закрыть; соединение без успешного `Login` за 30 с закрывается. Сессия помечает соединение авторизованным через `IConnectionItemsFeature`.
- Вход по Steam при `Ban.SteamId == steam.SteamId || Ban.SteamId == steam.OwnerSteamId` → `BANNED`.

- [ ] **Step 1: Тесты.** `BannedAccountCannotLogin` (`FailedPrecondition`, `BANNED`, сообщение с причиной); `BanWhileOnlineEndsStreamAndInvalidatesToken` (Review Focus 3: подписка → `BanService.Ban` → стрим завершается `Unavailable`, `Logout` старым токеном → `Unauthenticated`); `ServerFullAfterMaxPlayers` (`Server:MaxPlayers=1`); `WrongServerPasswordRejected`; `SecondLoginReplacesFirst` (Review Focus 5: первая сессия читает `SessionReplaced`, её токен → `Unauthenticated`, вторая работает); `IpBanBlocksLogin` (`Ban{Ip="127.0.0.1"}`); `FamilySharingOwnerBanBlocksLogin`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5:** Ограничитель соединений проверяется вручную: 6 `openssl s_client -connect 127.0.0.1:25560` без данных — шестой закрывается, первые пять закрываются через 30 с. Записать в `docs/manual-checklist.md`.
- [ ] **Step 6: Commit** — `"Add login gate: bans, player limit, server password, single session"`.

### Task 9: Лимиты частоты

**Files:**
- Create: `src/Rimlink.Server/Grpc/RateLimitInterceptor.cs`, `Hosting/RateLimitPolicy.cs`
- Test: `tests/Rimlink.Server.Tests/RateLimitTests.cs`

**Interfaces:**
- `RateLimitPolicy.For(string method) : (int permits, TimeSpan window)?` — таблица: `/rimlink.v1.Chat/Send` 5/5 с, `/rimlink.v1.Saves/Upload` 1/30 с, `/rimlink.v1.World/Settle` 1/10 с, `/rimlink.v1.Auth/Login` 10/60 с (по IP), остальное 30/10 с.
- `RateLimitInterceptor` — `PartitionedRateLimiter<(string key, string method)>` (`System.Threading.RateLimiting`, `FixedWindow`), ключ = accountId или IP до входа; отказ → `StatusCode.ResourceExhausted`.
- REST: `AddRateLimiter` с политикой `admin` 60/мин на токен.

- [ ] **Step 1: Тесты.** `SixthChatMessageWithinFiveSecondsIsResourceExhausted` (сервис Chat появится в Task 11 — тест пишется здесь на `Auth.Logout` с временной политикой 3/10 с через `UseSetting("RateLimit:Test","3")` и переписывается в Task 11 на чат); `LimitsAreIndependentPerAccount`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add per-account rate limiting interceptor"`.

### Task 10: Мир и поселения

**Files:**
- Create: `src/Rimlink.Protocol/Protos/world.proto`
- Create: `src/Rimlink.Server/Application/World/WorldService.cs`, `Grpc/WorldGrpcService.cs`
- Modify: `Protos/events.proto` (`WorldObjectAdded{WorldObject object=1;}`, `WorldObjectRemoved{int64 object_id=1;}`), `Application/Auth/LoginGate.cs` (`WORLD_NOT_CREATED` для не-админа при отсутствии мира)
- Test: `tests/Rimlink.Server.Tests/World/WorldTests.cs`

**Interfaces:**
- `world.proto`: `service World { rpc Create(CreateWorldRequest) returns (WorldInfo); rpc Get(Empty) returns (WorldInfo); rpc ListObjects(Empty) returns (WorldObjects); rpc Settle(SettleRequest) returns (WorldObject); rpc Abandon(AbandonRequest) returns (Empty); }`. `CreateWorldRequest{WorldParams params=1; repeated int32 npc_tiles=2;}`, `WorldParams{string seed=1; float planet_coverage=2; int32 rainfall=3; int32 temperature=4; int32 population=5; float pollution=6; string scenario_def=7; string storyteller_def=8; string difficulty_def=9;}`, `WorldInfo{bool exists=1; WorldParams params=2; repeated int32 npc_tiles=3;}`, `WorldObject{int64 id=1; string type=2; PlayerRef owner=3; int32 tile=4; string name=5; bytes payload=6;}`, `WorldObjects{repeated WorldObject objects=1;}`, `SettleRequest{int32 tile=1; string name=2;}`, `AbandonRequest{int64 object_id=1;}`.
- `WorldService`: `Task<World> Create(WorldParams, int[] npcTiles, Session actor)` (только Admin; если мир есть → `FAILED_PRECONDITION`); `Task<World?> Get()`; `Task<WorldObject> Settle(Session, int tile, string name)` — проверки: мир есть, тайл не в `NpcTiles`, число поселений игрока `< MaxSettlementsPerPlayer` (→ `SETTLEMENT_LIMIT`), имя валидно; вставка в транзакции, `SqliteException` код 19 → `TILE_TAKEN`; после коммита `EventBus.BroadcastExcept(owner, WorldObjectAdded)`; `Task Abandon(Session, long objectId)` — владелец или Admin, иначе `PERMISSION_DENIED`; событие `WorldObjectRemoved`.

- [ ] **Step 1: Тесты.** `NonAdminLoginFailsUntilWorldCreated` (`WORLD_NOT_CREATED`); `AdminCreatesWorldAndOthersCanLogin`; `SettleBroadcastsToOtherPlayers`; `SettleOnNpcTileIsTileTaken`; `SecondSettlementExceedsLimit`; `ConcurrentSettleOnSameTileSucceedsOnce` (Review Focus 2: `Task.WhenAll` двух `Settle` на tile 777 → одно `WorldObject`, одна `RpcException` с `TILE_TAKEN`); `AbandonByStrangerIsPermissionDenied`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add world creation and player settlements"`.

### Task 11: Сейвы: загрузка, проверка, версии, откат

**Files:**
- Create: `src/Rimlink.Protocol/Protos/saves.proto`
- Create: `src/Rimlink.Server/Application/Saves/SaveStore.cs`, `Application/Saves/SaveValidator.cs`, `Application/Saves/SaveRetention.cs`, `Grpc/SavesGrpcService.cs`
- Modify: `Protos/events.proto` (`SaveRolledBack{int32 version=1;}`), `Protos/auth.proto` (`has_save` заполняется)
- Test: `tests/Rimlink.Server.Tests/Saves/SaveUploadTests.cs`, `Saves/SaveValidatorTests.cs`, `Saves/SaveRetentionTests.cs`, `tests/Rimlink.Server.Tests/Support/SaveFixtures.cs`

**Interfaces:**
- `saves.proto`: `service Saves { rpc Upload(stream SaveChunk) returns (SaveInfo); rpc Download(Empty) returns (stream SaveChunk); rpc List(Empty) returns (SaveVersions); }`. `SaveChunk{ oneof part { SaveHeader header=1; bytes data=2; } }`, `SaveHeader{int64 game_tick=1; string world_seed=2; string faction_name=3; int64 total_size=4; bytes sha256=5;}` (размер и хеш — сжатых данных), `SaveInfo{int32 version=1; int64 size=2; google.protobuf.Timestamp created_at=3; bool is_current=4;}`, `SaveVersions{repeated SaveInfo versions=1;}`. `Download` первым сообщением шлёт `header`.
- `SaveStore(DataDirectory, RimlinkDbContext)`: `Task<SaveVersion> Store(long playerId, SaveHeader, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken)` — пишет в `<saves>/<playerId>/upload-<guid>.tmp`, считает размер и SHA-256, при любом несовпадении или отмене удаляет temp и бросает `SaveRejected(ErrorCode.SAVE_INVALID, reason)`; затем `SaveValidator.Validate(tempPath, header, world.Seed, player.FactionName)`; `File.Move(temp, "<n>.rws.zst")`, транзакция: новая `SaveVersion{IsCurrent=true}`, у прежней `IsCurrent=false`, `SaveRetention.Apply`. `Task<(SaveVersion, Stream)?> OpenCurrent(long playerId)`; `Task Rollback(long playerId, int version)` (событие `SaveRolledBack` игроку).
- `SaveValidator.Validate(string zstPath, SaveHeader, string worldSeed, string factionName)` — `ZstdSharp.DecompressionStream` → `XmlReader` (`DtdProcessing.Prohibit`, `MaxCharactersFromEntities=0`): корень `savegame`, первый `<seedString>` == `worldSeed`, внутри `<faction>` с `<def>PlayerColony</def>` элемент `<name>` == `factionName`; иначе `SaveRejected`. Читает не более 64 МБ распакованных.
- `SaveRetention.Select(IReadOnlyList<SaveVersion> all, DateTime now) : IReadOnlyList<SaveVersion> toDelete` — оставляет 10 последних по номеру плюс самую позднюю за каждый из 7 последних календарных дней (UTC); текущую не удаляет никогда.
- `SaveFixtures.MakeSave(seed, faction, tick) : byte[]` — минимальный XML RimWorld-формы, сжатый zstd.

- [ ] **Step 1: Тесты.** `UploadThenDownloadRoundTrips`; `UploadWithWrongSeedIsSaveInvalidAndNothingStored`; `CancelledUploadLeavesNoTempFileAndKeepsCurrent` (Review Focus 1: отправить header + 3 куска, `CancellationTokenSource.Cancel()`; `Directory.GetFiles(saves)` без `*.tmp`, `IsCurrent` у версии 1); `HashMismatchIsRejected`; `OversizedHeaderIsRejected` (`total_size = 65 MB`); `RetentionKeepsTenLatestAndSevenDaily` (чистый юнит на `SaveRetention.Select` с 40 версиями за 20 дней); `RollbackMarksVersionCurrentAndNotifiesPlayer`; `SecondUploadWithinThirtySecondsIsResourceExhausted`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add save upload, validation, retention and rollback"`.

### Task 12: Модпак

**Files:**
- Create: `src/Rimlink.Protocol/Protos/modpack.proto`
- Create: `src/Rimlink.Server/Application/Modpack/ModpackService.cs`, `Application/Modpack/ModpackComparer.cs`, `Grpc/ModpackGrpcService.cs`
- Modify: `Protos/auth.proto` (`ModpackDiff modpack_diff=7` в `LoginReply`), `Application/Auth/LoginGate.cs`
- Test: `tests/Rimlink.Server.Tests/Modpack/ModpackComparerTests.cs`, `Modpack/ModpackLoginTests.cs`

**Interfaces:**
- `modpack.proto`: `service Modpack { rpc Get(Empty) returns (ModpackList); rpc Set(ModpackList) returns (Empty); }`. `ModpackList{repeated ModpackEntry entries=1;}`, `ModpackEntry{int32 position=1; string package_id=2; string version=3; uint64 workshop_id=4; string url=5; EntryKind kind=6;}`, `enum EntryKind{REQUIRED=0; ALLOWED=1;}`, `ModpackDiff{repeated ModpackEntry missing=1; repeated ModRef extra=2; repeated VersionMismatch wrong_version=3; repeated ModpackEntry server_list=4;}`, `VersionMismatch{string package_id=1; string client_version=2; string server_version=3;}`.
- `ModpackComparer.Compare(IReadOnlyList<ModpackEntry> server, IReadOnlyList<ModRef> client) : ModpackDiff?` — ключ `package_id` в нижнем регистре с отброшенным суффиксом `_steam`, версии сравниваются как строки после `Trim()`; запись с пустой `version` совпадает по id; `ALLOWED` отсутствовать может; клиентские моды вне списка → `extra`. `null`, если расхождений нет. Пустой список сервера = проверка отключена.
- `ModpackService.Set(ModpackList, Session actor)` — только Admin; `Get()`. `LoginGate`: после бана — `Compare`; при диффе `MODPACK_MISMATCH` c `ModpackDiff` в `LoginReply` (вход отклонён, но ответ не ошибка gRPC: `LoginReply.session_token` пустой, `modpack_diff` заполнен — так клиент получает список без второго вызова).

- [ ] **Step 1: Тесты.** `ModpackComparerTests`: `ExactMatchIsNull`; `MissingRequiredIsReported`; `MissingAllowedIsFine`; `SteamSuffixAndCaseAreIgnored` (`"Brrainz.Harmony_steam"` против `"brrainz.harmony"`); `WrongVersionIsReported`; `EmptyServerVersionMatchesAny`; `ExtraClientModIsReported`. `ModpackLoginTests`: `MismatchReturnsDiffWithoutToken`; `AdminBypassesModpackCheck`; `NonAdminCannotSetModpack`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add modpack list and login-time comparison"`.

### Task 13: Чат

**Files:**
- Create: `src/Rimlink.Protocol/Protos/chat.proto`, `src/Rimlink.Server/Application/Chat/ChatService.cs`, `Grpc/ChatGrpcService.cs`
- Modify: `Protos/events.proto` (`ChatMessageEvent{int64 id=1; PlayerRef from=2; int64 to_player_id=3; string text=4; MessageKind kind=5; google.protobuf.Timestamp at=6;}`, `enum MessageKind{PLAYER=0; SYSTEM=1;}`), `Application/Sessions/SessionRegistry.cs` (системные `PlayerJoined/PlayerLeft` + чат-событие «вошёл/вышел»), `tests/.../RateLimitTests.cs` (переписать на чат)
- Test: `tests/Rimlink.Server.Tests/Chat/ChatTests.cs`

**Interfaces:**
- `chat.proto`: `service Chat { rpc Send(SendRequest) returns (Empty); rpc History(Empty) returns (ChatHistory); }`, `SendRequest{string text=1; int64 to_player_id=2;}` (0 — общий канал), `ChatHistory{repeated ChatMessageEvent messages=1;}`.
- `ChatService.Send(Session, string text, long toPlayerId)` — `Trim`, пусто или >500 → `INVALID_ARGUMENT`; мут (`Player.MutedUntil` — добавить колонку миграцией `AddMute`) → `MUTED`; сохраняет, обрезает таблицу до 500 последних; общий → `Broadcast`, личное → `SendTo(to)` и `SendTo(from)`. `History()` — последние 50 общих плюс личные текущего игрока. `System(string text)` — `Kind=SYSTEM`, `Broadcast`.

- [ ] **Step 1: Тесты.** `MessageReachesEveryone`; `DirectMessageReachesOnlyRecipientAndSender`; `HistoryReturnsFiftyLatest` (отправить 60, получить 50 последних); `TooLongMessageIsInvalidArgument` (501 символ); `MutedPlayerGetsMuted`; `SixthMessageInFiveSecondsIsResourceExhausted`; `JoinProducesSystemMessage`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add chat with history, direct messages and mutes"`.

### Task 14: Администрирование: операции, gRPC, REST, чат-команды, журнал

**Files:**
- Create: `src/Rimlink.Protocol/Protos/admin.proto`
- Create: `src/Rimlink.Server/Application/Admin/IAdminOperations.cs`, `AdminOperations.cs`, `Application/Admin/ChatCommandParser.cs`, `Grpc/AdminGrpcService.cs`, `Rest/AdminController.cs`, `Rest/AdminTokenAuthHandler.cs`, `Rest/Dto/*.cs`
- Modify: `Application/Chat/ChatService.cs` (текст с `/` → `ChatCommandParser`), `Program.cs`
- Test: `tests/Rimlink.Server.Tests/Admin/AdminOperationsTests.cs`, `Admin/AdminRestTests.cs`, `Admin/ChatCommandTests.cs`

**Interfaces:**
- `IAdminOperations` (все методы принимают `Actor actor` = `{long AccountId; Role Role}` и пишут `AdminAction`): `ListOnline() : IReadOnlyList<OnlinePlayer>`; `Kick(long playerId, string reason)`; `Ban(BanRequest)`; `Pardon(long banId)`; `ListBans()`; `Mute(long playerId, TimeSpan)`; `Unmute(long playerId)`; `RollbackSave(long playerId, int version)`; `ListSaves(long playerId)`; `ResetPlayer(long playerId)` (сейвы и объекты игрока переносятся в `<DataDir>/archive/<playerId>-<timestamp>/`, объекты удаляются с событиями); `Broadcast(string text)`; `SetRole(long accountId, Role)`; `RenamePlayer(long playerId, string name, string factionName)` (игрок может для себя); `SaveAll()` (запрос `Notice` «сохранитесь» всем — у ядра нет принудительного сейва); `RecreateWorld()` (архивирует все сейвы и объекты в `<DataDir>/archive/world-<timestamp>/`, удаляет строку `World`, кикает всех с `Notice`); `Shutdown(int inSeconds, string reason)` (`ServerShutdown` всем, затем `IHostApplicationLifetime.StopApplication` по таймеру). Moderator: `Kick`, `Mute`, `Unmute`, `ListOnline`; остальное Admin → иначе `PERMISSION_DENIED`.
- `admin.proto`: `service Admin { rpc Execute(AdminCommand) returns (AdminReply); }`, `AdminCommand{string name=1; repeated string args=2;}`, `AdminReply{string text=1;}` — общий для чат-команд и клиентского UI; `ChatCommandParser.Parse(string line) : AdminCommand?` (`/kick Nova spam` → `name="kick", args=["Nova","spam"]`, кавычки для имён с пробелами).
- REST под `/api/admin/`: `GET players/online`, `POST players/{id}/kick`, `POST bans`, `DELETE bans/{id}`, `GET bans`, `POST players/{id}/mute`, `GET players/{id}/saves`, `POST players/{id}/saves/{version}/rollback`, `POST players/{id}/reset`, `POST broadcast`, `GET modpack`, `PUT modpack`, `POST shutdown`, `GET actions` (журнал). Авторизация: заголовок `Authorization: Bearer <Admin:ApiToken>`, actor = `{AccountId=0, Role=Admin}`; без токена — 401; JSON (`System.Text.Json`, camelCase).

- [ ] **Step 1: Тесты.** `AdminOperationsTests`: `KickEndsSessionAndLogsAction`; `ModeratorCannotBan` (`PermissionDenied`); `ResetPlayerArchivesSavesAndRemovesObjects`; `ShutdownBroadcastsAndStopsHost` (через `IHostApplicationLifetime.ApplicationStopping` с `inSeconds=0`). `ChatCommandTests`: `KickViaChatCommandWorksForModerator`; `UnknownCommandReturnsHelp`; `PlayerCannotRunKick`. `AdminRestTests`: `MissingTokenIs401`; `BanViaRestThenPlayerCannotLogin`; `ActionsLogListsBan`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5: Commit** — `"Add admin operations with gRPC, REST and chat commands"`.

### Task 15: CLI `rimlink-tools`

**Files:**
- Create: `src/Rimlink.Tools/Rimlink.Tools.csproj`, `Program.cs`, `Commands/BackupCommand.cs`, `Commands/RestoreCommand.cs`, `Commands/ModpackCommand.cs`, `Commands/AdminCommand.cs`, `Backup/BackupArchive.cs`
- Test: `tests/Rimlink.Server.Tests/Tools/BackupArchiveTests.cs` (проект Tools референсится из тестов)

**Interfaces:**
- `rimlink-tools backup --data <dir> --out <file.tar.zst>`: `VACUUM INTO` базы во временный файл, затем архив (`tar` через `System.Formats.Tar`, zstd) с `rimlink.db` и `saves/`; `restore --data <dir> --from <file>` только при остановленном сервере (проверка lock-файла `<DataDir>/server.lock`, который создаёт `Program` при старте).
- `rimlink-tools modpack import --from ModsConfig.xml --mods-dir <dir> --server <url> --token <t>` — читает активные `packageId` из `ModsConfig.xml`, версии из `About.xml` каждого мода, `PublishedFileId.txt` как `workshop_id`, отправляет `PUT /api/admin/modpack`.
- `rimlink-tools admin <command> [args] --server <url> --token <t>` — тонкая обёртка над REST (kick, ban, pardon, bans, mute, rollback, reset, broadcast, shutdown, actions).
- `BackupArchive.Create(string dataDir, string outFile)`, `BackupArchive.Restore(string file, string dataDir)` живут в `src/Rimlink.Server/Backup/` (Tools ссылается на Server), чтобы сервер мог делать бэкапы сам: `BackupScheduler : BackgroundService` с `Backup:IntervalHours` (0 = выключено, по умолчанию 24) и `Backup:Keep` (7) пишет в `<DataDir>/backups/`.

- [ ] **Step 1: Тесты.** `BackupThenRestoreReproducesDatabaseAndSaves` (создать базу с одной записью и файл сейва, backup во временный файл, restore в другую папку, сравнить); `RestoreRefusesWhenServerLockPresent`; `SchedulerKeepsOnlyLatestSevenBackups`.
- [ ] **Step 2:** FAIL. **Step 3:** Реализация. **Step 4:** PASS.
- [ ] **Step 5:** Ручная проверка `modpack import` на `D:\SteamLibrary\steamapps\common\RimWorld` → записать в `docs/manual-checklist.md`.
- [ ] **Step 6: Commit** — `"Add rimlink-tools CLI for backups, modpack import and admin"`.

### Task 16: Docker, конфигурация, документация

**Files:**
- Create: `deploy/Dockerfile`, `deploy/docker-compose.yml`, `deploy/appsettings.example.json`, `docs/server-setup.md`, `docs/manual-checklist.md` (собрать пункты из Task 6, 8, 15)
- Modify: `.github/workflows/ci.yml` (job docker → GHCR `ghcr.io/asofwar/rimlink-server`), `README.md`

**Interfaces:** образ запускается `USER app`, `WORKDIR /data`, `VOLUME /data`, `EXPOSE 25560`, `HEALTHCHECK` на `https://127.0.0.1:25560/health` с `--insecure`; конфиг через переменные окружения `Rimlink__Server__Port` и т.д. (`AddEnvironmentVariables("Rimlink__")`).

- [ ] **Step 1:** Dockerfile многоступенчатый: `sdk:10.0` publish `-r linux-x64 --self-contained false`, runtime `aspnet:10.0`; `docker build -f deploy/Dockerfile -t rimlink-server .` и `docker run --rm -p 25560:25560 -v ./data:/data rimlink-server` → в логе `Listening on https://[::]:25560`, `curl -k https://127.0.0.1:25560/health` → `Healthy`.
- [ ] **Step 2:** `docs/server-setup.md`: установка, сертификаты (свой или самоподписанный и pinning), Steam Web API key, первый админ, модпак через CLI, бэкапы, REST-токен. `README.md` — краткий статус и ссылки.
- [ ] **Step 3:** `dotnet test Rimlink.sln` — все PASS; `docker build` в CI зелёный на тестовом теге.
- [ ] **Step 4: Commit** — `"Add Docker image, deployment config and server docs"`.
