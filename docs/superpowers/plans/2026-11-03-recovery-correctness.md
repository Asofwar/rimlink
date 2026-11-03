# План Rimlink 0.8.1: восстановление сейвов и escrow

**Цель:** исправить все 10 находок аудита без новых продуктовых возможностей.
**База:** `404a67edd62e4114046500f3e0eab7ff4e306abb`, main/v0.8.0.
**Worktree:** `D:/Build/rimlink-recovery-fixes`, ветка `asofwar/recovery-fixes`. Все пути ниже относительно этого корня.
**Спецификация:** `docs/superpowers/specs/2026-11-03-recovery-correctness-design.md`.
**Архитектура:** существующие SaveSync/PendingUpload/TransferOutbox, явные receipts в XML неизменяемого снимка, write epoch для rollback, SQLite-транзакции и scoped ключи повторов. Без нового transport, очереди сообщений, фонового сервиса или transaction framework.
**Исполнение:** основная сессия GPT-6.1 Sol. Astra не пишет реализацию. Пользователь уже разрешил все исправления; новый brainstorming и выбор метода исполнения не требуются.

## Global Constraints

- Один dedicated .NET 10 процесс, SQLite, до 50 игроков. Никаких новых зависимостей и распределенных механизмов.
- Протокол только расширяется; номера указаны в спецификации и проверены на базе. ProtocolVersion не повышать. Версия 0.8.1 в трех согласованных местах.
- Не изменять/терять старые payload, balances, request IDs и сейвы при миграции. Legacy и пустой новый receipt set различаются.
- Все Verse/RimWorld и применение SharedState — главный поток. Network/disk — cancellable; результаты старой сессии не применяются к новой.
- Сохранить reset/SaveGeneration, ordinary reconnect, intentional AdminBypass, receipt privacy, ограничения на размер, проверки владельца и запреты под угрозой.
- Не читать секреты, .env, auth.json и пользовательские аккаунтные профили. Не останавливать чужие процессы/игры. Сборка мода без InstallTo не устанавливает его пользователю.
- Не трогать чужие правки. Write ownership Astra ограничена двумя документами этой задачи; исходники/тесты меняет Sol.
- UTF-8. Никаких git index/refs/commit действий со стороны Astra. Merge/push/tag/release/production — только по новой команде пользователя.
- Узкие RED→GREEN по задачам; полный suite только итоговым проходом, затем повторять лишь после новых изменений/ошибок. Один итоговый обзор всей правки по отдельному запросу.

## Review Focus

1. Остановка на любой границе journal → изменение игры → save → upload → server commit → ack → очистка оставляет ровно одно имущество и пригодное восстановление.
2. Receipt относится к байтам конкретного снимка; заголовок или текущее состояние игры не заменяют metadata pending-файла. Одинаковые тики и пустой новый формат.
3. Callback/late upload через rollback, reset, замену сессии и reconnect; только trusted download позволяет принять новый write epoch.
4. Legacy replay после миграции: другой владелец, другой вид операции, измененный payload, закрытый trade/visit, неизвестное историческое намерение.
5. Wallet freeze/ошибка не мешает доставке и урегулированию live ends; повтор восстановления действительно выполняет действия после загрузки карты.
6. Полнота изменения всех destructive callers, отсутствие возврата на временный отказ, сохранность pawn relations/IDs.
7. Никакого расширения AdminBypass или автоматического включения password auth; старые reset/G/Ctrl+V не объявлять новыми незакрытыми дефектами.

## Порядок и состояние при первоначальной передаче

Зависимости: 1 независимо; 2 → 3 → 4 → 5 → 7; 2 → 6 → 7; затем 8 → 9.
Задача 6 может выполняться раньше 3–5 той же основной сессией; параллельных исполнителей нет.
Со слов Sol к моменту передачи: #9 уже RED→23 GREEN; #3 RED→21 GREEN; #8 и #4 воспроизведены RED. Это сведения исполнителя, а не повторный прогон/ревью Astra.

### 1. Завершить небольшие серверные исправления #3/#9

**Файлы:** `src/Rimlink.Server/Application/Auth/LoginGate.cs`, `src/Rimlink.Server/Application/Live/LiveSessionService.cs`; связанные helpers `src/Rimlink.Server/Application/Economy/MailboxService.cs`, `src/Rimlink.Server/Application/Raids/RaidService.cs` менять только если необходимы для общей транзакции; тесты `tests/Rimlink.Server.Tests/Modpack/ModpackJoinLoginTests.cs`, `tests/Rimlink.Server.Tests/Auth/LoginGateTests.cs`, `tests/Rimlink.Server.Tests/Live/LiveSessionTests.cs`, `tests/Rimlink.Server.Tests/Live/LiveEndTests.cs`.

**RED уже получен Sol:** WrongServerPasswordNeverReturnsSharedSettings; VisitReturnFailureKeepsTheSessionRecoverable(false/true) через SQLite BEFORE INSERT trigger.
**GREEN уже сообщен:** gate Ban→ServerPassword→Modpack с прежним AdminBypass; возврат визита сохраняется атомарно, Announce после commit.
**Осталось в этой задаче:** тем же fault injection проверить CloseEnding и ReportVisit; если эти ветви еще не входят в общий атомарный переход, включить их. Проверить отсутствие вложенных SQLite-транзакций и регрессий raid/coop. Это целевые проверки исполнителя, не отдельное ревью.
**Expected:** после исключения прежняя сессия восстановима, нет половинного возврата; retry создает одну посылку. Неправильный пароль не возвращает configs, в том числе через Register.

### 2. Зафиксировать wire/XML контракт и одну миграцию истории

**Файлы:** `src/Rimlink.Protocol/Protos/saves.proto`, `src/Rimlink.Protocol/Protos/economy.proto`, `src/Rimlink.Protocol/Protos/auth.proto`, `src/Rimlink.Protocol/Protos/common.proto`; узкая общая модель/reader recovery metadata при необходимости — новый `src/Rimlink.Protocol/SaveRecoveryData.cs`; `src/Rimlink.Server/Storage/Entities/Entities.cs`, `src/Rimlink.Server/Storage/RimlinkDbContext.cs`, `src/Rimlink.Server/Application/Saves/SaveArchive.cs`, `src/Rimlink.Server/Application/World/ColonyArchive.cs`; EF migration `RecoveryCorrectness` и генерируемые designer/model snapshot; новые `tests/Rimlink.Server.Tests/Storage/RecoveryMigrationTests.cs`, `tests/Rimlink.Client.Tests/Net/SaveRecoveryDataTests.cs`.

**RED:** descriptor assertions на новые номера; старый XML и новый пустой набор; metadata вне собственного компонента, дубли/поврежденный формат; upgrade БД 0.8.0 со всеми четырьмя глобальными request-ID индексами и legacy archive manifest. В исходном состоянии новые контракты отсутствуют.
**GREEN:** применить только добавления из spec. Сохранить в SaveVersion идентичность/формат/epoch/receipts; добавить Player write epoch и признак перехода на новый recovery contract; parcel delivery generation/receipt mode и scope старых request IDs. Nullable hashes не заполнять выдуманными значениями. Unique indexes должны включать владельца и вид, сохраняя исторические строки.
**Команда (PowerShell в worktree):** `rtk proxy dotnet ef migrations add RecoveryCorrectness --project src/Rimlink.Server`. Имя timestamp/designer генерирует EF; итоговые пути и migration ID записать в результаты реализации.
**Expected:** upgrade сохраняет число и содержимое старых строк/файлов, wallet ledger, все IDs; новая схема допускает одинаковый request ID у разных actor/kind; прежний scoped replay остается доступен. Старые сейвы/архивы читаются без переписывания.
**Проверка:** `rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~RecoveryMigrationTests"` и соответствующий SaveRecoveryDataTests в Client.Tests.

### 3. Сделать сохранение и rollback устойчивыми (#4/#5/#6)

**Файлы:** `src/Rimlink.Client.Core/Net/SaveTransfer.cs` (включая PendingUpload/SaveMeta), `src/Rimlink.Client.Core/Net/SaveMetaReader.cs`, `src/Rimlink.Client.Core/Net/LoginPlan.cs`, `src/Rimlink.Client.Core/Net/RimlinkConnection.cs`; `src/Rimlink.Client/Game/SaveSync.cs`, `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `src/Rimlink.Client/Game/RimlinkSession.cs`, `src/Rimlink.Client/Game/Patches/Patch_GameDataSaveLoader_SaveGame.cs`, `src/Rimlink.Client/Game/Patches/Patch_GenScene_GoToMainMenu.cs`; `src/Rimlink.Server/Application/Saves/SaveStore.cs`, `src/Rimlink.Server/Application/Saves/SaveValidator.cs`, `src/Rimlink.Server/Grpc/SavesGrpcService.cs`, `src/Rimlink.Server/Grpc/AuthGrpcService.cs`, `src/Rimlink.Server/Application/Auth/AuthService.cs`, `src/Rimlink.Server/Application/Admin/AdminOperations.cs`; `src/Rimlink.Client/UI/SteamAuth.cs` (GameCredentials); tests `tests/Rimlink.Server.Tests/Saves/SaveUploadTests.cs`, `tests/Rimlink.Server.Tests/Saves/SaveGenerationTests.cs`, новые `tests/Rimlink.Server.Tests/Saves/SaveRecoveryTests.cs`, `tests/Rimlink.Client.Tests/Net/PendingSnapshotTests.cs`, существующие `tests/Rimlink.Client.Tests/Net/SaveTransferTests.cs`, `tests/Rimlink.Client.Tests/Net/SaveMetaReaderTests.cs`, `tests/Rimlink.Client.Tests/Net/CompactedRollbackLoginTests.cs`.

**RED:** использовать уже полученный Sol rollback RED; дополнить A/B uploads с принудительной перестановкой, поздний успех A после ошибки B, crash после записи .rws до postfix, save swallowed exception, повтор одного snapshot ID с теми же/другими байтами, epoch mismatch после rollback, reset через летящий upload, обычный reconnect. Проверять обе очередности commit/rollback без sleeps.
**GREEN:** долговечное writing intent → неизменяемый файл → pending manifest → последовательный upload; snapshot-specific результат вместо ожидания произвольного глобального task. Metadata читать из этого файла, не из Find/current login. Сервер проверяет snapshot/metadata/epoch под PerStoreLock; полный bounded XML parse. Для Download проверять размер/hash до замены локального файла и передавать доверенный контекст загрузки. Rollback увеличивает epoch атомарно; выход без save; late callbacks отсекаются.
**Expected:** более поздний снимок не затирается ранним; после crash доступен последний записанный pending; rollback остается выбранным после выхода/повторного входа; reset по-прежнему запрещает старую колонию; сеть не превращается в постоянный отказ. Тест legacy-клиента после epoch>0 получает понятное требование обновления до загрузки карты.
**Проверка:** узкие SaveRecovery/PendingSnapshot/SaveGeneration/SaveTransfer tests и сборка мода; прямой UploadLocal и обычный SaveSync обязаны пройти один snapshot contract.

### 4. Закрыть все окна исходящего outbox и возвратов (#2)

**Файлы:** `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`; `src/Rimlink.Client/Game/Economy/Outgoing.cs`, `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `src/Rimlink.Client/Game/SaveSync.cs`, `src/Rimlink.Client/Game/Raids/RaidOutgoing.cs`, `src/Rimlink.Client/Game/Raids/RaidResolver.cs`, `src/Rimlink.Client/Game/Shop/RimlinkShop.cs`; проверить callers в `src/Rimlink.Client/Game/Live/LiveGuest.cs` и `src/Rimlink.Client/Game/Live/LiveHost.cs`; `tests/Rimlink.Client.Tests/Economy/TransferOutboxTests.cs`, новый `tests/Rimlink.Client.Tests/Economy/TransferRecoveryTests.cs`, существующие AuctionOutbox/TradeOutbox/ProcurementOutbox tests.

**RED:** restart из каждого состояния, включая: до снятия; после снятия до save; после local save до upload; после server commit до Ready; после принятого RPC до удаления entry; отказ до возврата; возврат до save; сохраненный Returned до очистки journal. Проверить неизвестный результат и поврежденный файл journal.
**GREEN:** Prepared/Ready/Returning и XML Removed/Returned по spec. Сначала journal, затем destructive mutation. Все пути карта/caravan/pods и wallet-only различаются явно. У RaidResolver/ShopAck не разрешать premature replay до снимка. Не удалять entry на definitive отказ до долговечного возврата; не восстанавливать второй раз при Returned. Временный отказ сохраняет запрос.
**Expected:** после любого fault/restart ровно один экземпляр имущества, прежний client_request_id и возврат пешек с прежними правилами RelationLedger. Legacy без доказательств остается сохраненным/неразрешенным, а не превращается в пустой outbox.
**Проверка:** table-driven tests переходов на реальных временных файлах + существующие outbox suites; игровой адаптер проверяется в задаче 8.

### 5. Ввести доказуемые parcel receipts (#7, основа #1)

**Файлы:** `src/Rimlink.Server/Application/Economy/MailboxService.cs`, `src/Rimlink.Server/Grpc/EconomyGrpcServices.cs`, `src/Rimlink.Server/Application/Saves/SaveStore.cs`; `src/Rimlink.Client.Core/Economy/EconomyClient.cs`, `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`; `src/Rimlink.Client/Game/Economy/Incoming.cs`, `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `src/Rimlink.Client/Game/SaveSync.cs`; `tests/Rimlink.Server.Tests/Economy/MailboxTests.cs`, новый `tests/Rimlink.Server.Tests/Economy/ParcelReceiptTests.cs`, `tests/Rimlink.Client.Tests/Economy/TransferRecoveryTests.cs`.

**RED:** два снимка на одном T, до и после pod; snapshot без нужного receipt; чужой snapshot/parcel; новый пустой receipt set; повтор Deliver при потерянном ответе; crash после pod+receipt save до Deliver; receipt уже в local pending; reset; rollback на снимок без receipt; retention старого proof.
**GREEN:** сначала долговечный snapshot с pod и parcel ID, затем Deliver с ID снимка/generation/epoch. Server проверяет владельца и доказательство. Pending нового формата определяется включением receipt в текущий снимок; загрузка карты с receipt повторяет только ack. Доступные доказательства legacy не повышать до нового формата автоматически.
**Expected:** ни дублирования, ни исчезновения груза при равных тиках; wallet credit/storage charge ровно один раз; reset не возвращает вещи прежней колонии. Повторный ack не требует повторного spawn.
**Проверка:** ParcelReceipt/Mailbox tests + scenario snapshot store → deliver → rollback → pending на реальном test server.

### 6. Исправить scope и replay всех request IDs (#8)

**Файлы:** `src/Rimlink.Server/Application/Economy/TransferService.cs`, `src/Rimlink.Server/Application/Economy/WalletService.cs`, `src/Rimlink.Server/Application/Economy/MarketService.cs`, `src/Rimlink.Server/Application/Economy/MailboxService.cs`; `src/Rimlink.Server/Application/Trades/TradeService.cs`, `src/Rimlink.Server/Application/Visits/ShopService.cs`; необходимые DTO mapping в `src/Rimlink.Server/Grpc/EconomyGrpcServices.cs`, `src/Rimlink.Server/Grpc/MarketGrpcService.cs`, `src/Rimlink.Server/Grpc/TradesGrpcService.cs`, `src/Rimlink.Server/Grpc/VisitsGrpcServices.cs`; `tests/Rimlink.Server.Tests/Economy/TransferTests.cs`, `tests/Rimlink.Server.Tests/Economy/WalletTests.cs`, `tests/Rimlink.Server.Tests/Economy/MarketTests.cs`, `tests/Rimlink.Server.Tests/Trades/TradeTests.cs`, `tests/Rimlink.Server.Tests/Visits/ShopTests.cs`, новый `tests/Rimlink.Server.Tests/Economy/RequestIdentityTests.cs`.

**RED уже получен Sol:** DifferentPlayersMayUseTheSameRequestIdWithoutSharingTheResult. Дополнить одинаковым ключом send/withdraw/shop-buy и deposit/trade-side; тем же owner/kind с измененным намерением; мигрированным старым ключом; повтором после block/freeze/закрытия trade или visit.
**GREEN:** composite lookup и уникальность из задачи 2; хранить intent hash новых операций и возвращать исходный результат legacy только при доказанной связи. Для Shop.Buy replay принятой операции должен предшествовать проверке истекшего visit. Не восстанавливать чужой parcel через другой endpoint; не возвращать запрошенный произвольный TradeId как подтверждение legacy trade-side.
**Expected:** два actor с одним ключом независимы, разные operation kinds независимы, настоящий повтор не меняет деньги/escrow. Неизвестная историческая связь не вызывает повторную мутацию или автоматический refund.
**Проверка:** RequestIdentity и связанные узкие suites, включая fresh/upgrade schema.

### 7. Перестроить login/reconnect recovery независимо от Wallet (#1/#10)

**Файлы:** `src/Rimlink.Client/UI/LoginFlow.cs`, `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `src/Rimlink.Client/Game/RimlinkSession.cs`, `src/Rimlink.Client/Game/Economy/Incoming.cs`; `src/Rimlink.Client.Core/Net/LoginPlan.cs`, `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`, `src/Rimlink.Client.Core/Economy/EconomyState.cs`, `src/Rimlink.Client.Core/Live/LiveState.cs`; при необходимости маленький тестируемый coordinator `src/Rimlink.Client.Core/Net/RecoveryCoordinator.cs` без Verse и без нового framework; новый `tests/Rimlink.Client.Tests/Net/LoginRecoveryTests.cs`, существующий `tests/Rimlink.Client.Tests/Incidents/IncidentReconnectTests.cs`.

**RED:** доставленный груз есть только в pending-local; начальная mailbox snapshot устарела после upload; outbox содержит Prepared/Returning; frozen Wallet; transient ошибка отдельно mailbox/raids/live; reconnect до загрузки карты; смена сессии при летящем fetch; PendingLiveEnds приходит после FinalizeInit.
**GREEN:** соблюдать пять стадий spec. Replay не живет внутри LoadEconomy и не запускается до согласования выбранного сейва. После recovery перечитать mailbox. Независимые fetch с флагами незавершенной работы, ограниченной задержкой и cancellation; не гонять успешные мутации заново. MainThread применяет данные и вызывает необходимые settle/report после фактической загрузки.
**Expected:** после восстановленного pending нет второго pod; Wallet freeze не скрывает готовую доставку/рейд/инцидент/live end; временная ошибка исправляется без ручного relogin. Старый callback не меняет новую сессию.
**Проверка:** LoginRecoveryTests на существующем test server с injected failures и явными barriers; native сценарии задачи 8.

### 8. Согласовать документацию, версии и проверяемые игровые сценарии

**Файлы:** `Directory.Build.props`, `src/Rimlink.Client.Core/ClientInfo.cs`, `mod/About/About.xml`; `mod/Languages/English/Keyed/Rimlink.xml` и `mod/Languages/Russian/Keyed/Rimlink.xml`; `README.md`, `docs/player-guide.md`, `docs/server-setup.md`, `docs/manual-checklist.md`, новый `docs/verification/2026-11-03-recovery-correctness.md` — ownership Sol, не Astra.

**RED/проверка до изменения:** три версии еще 0.8.0; новые отказы не локализованы; пользователь не получает понятного объяснения сохраненного, но не разрешенного legacy recovery.
**GREEN:** все три версии 0.8.1; пояснить совместимость/обновление и сохранение recovery-артефактов, без новых настроек безопасности. Документировать только фактически выполненные проверки.
**Native acceptance (изолированный стенд, не пользовательские игры):** send и refusal с перезапуском в отмеченных окнах; paused same-tick delivery; восстановление pending после недоступности upload; онлайн rollback и повторный вход; reconnect при economy freeze с live end. Для pawn-передачи проверить количество, идентичность и relations. Игры/стенд запускает только Sol в разрешенной области; если изоляция недоступна, явно оставить native acceptance невыполненным.
**Expected:** пользовательские сценарии из #1–7/#10 воспроизведены без потерь/дублей; нет утверждения о native-проверке на основании core unit tests. Installer/guild influence не расширять: новые найденные вне этого пакета дефекты сообщать отдельно.

### 9. Итоговая проверка и передача на единственное ревью

**Файлы проверки:** все изменения ветки, результаты задачи 8; `src/Rimlink.Protocol/ProtocolVersion.cs` только проверяется на отсутствие ненужного bump.
**Команды PowerShell, cwd worktree:**
- `rtk proxy dotnet build Rimlink.sln -nologo -v q`
- `rtk proxy dotnet test Rimlink.sln -nologo -v q`
- `rtk proxy powershell -NoProfile -File scripts/Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld' -OutputDir 'D:\Build\rimlink-recovery-fixes\build\recovery-mod\Rimlink'` — без InstallTo, существующий output перед повтором должен оставаться внутри worktree.
- Для changed timing/reconnect — Linux full suite на двух CPU по установленному `C:/Users/Admin/.agents/skills/dotnet-linux-tests-wsl/SKILL.md`; использовать его проверенную процедуру, не перезапускать Docker/WSL/чужие процессы.
- `rtk proxy git diff --check`, проверка UTF-8/локализации, состояния чужих файлов и scope diff.

**Expected:** полный Windows suite и требуемый Linux прогон зелены; мод собирается; migration тестирует сохранение legacy данных; имеется перечень фактически выполненных native сценариев и оставшихся ограничений. Повторный полный suite только если после этого изменена реализация или выявлен сбой.
**Передать Astra отдельным запросом:** base/head или полный worktree diff, список тестов с результатами, migration ID, доказательства native scenarios, принятые отклонения от spec. Astra выполняет один целостный финальный обзор; исправления делает Sol.
**Остановка:** готовый проверенный patch 0.8.1. Коммит/merge/push/tag/release/deploy этот план автоматически не выполняет.

## Документальное дополнение по завершенному пакету

Единственное итоговое ревью выполнено: [шесть замечаний](../../verification/2026-11-03-recovery-correctness.md#итоговое-ревью-и-исправления). Их исправления выполнены Sol; это дополнение не назначает повторный обзор или новые прогоны. Исходные девять задач выше сохраняются как план исполнения.

Уточнения к задачам 1–4, 6–7 и 9: `RegisterRequest.server_password=7` передается из login-запроса в регистрацию и далее в `LoginContext.ServerPassword`; selected snapshot без Prepared removal proof означает `RecoveryRequired` с сохранением payload без угаданного discard/return. Download и authority читаются вместе под store lock; rollback увеличивает epoch после tracked reload. Все пути изменения имущества проверяют сессию/epoch в своей транзакции; callbacks/replay связаны с авторизацией и контекстом игры, а queued/in-flight файлы не удаляются очисткой.

По [отчету реализации и приемки](../../verification/2026-11-03-recovery-correctness.md): RED→GREEN для исправлений, итоговые Windows **1644/1644**, WSL на двух CPU **1644/1644**, native-сценарии завершены успешно. Здесь зафиксированы результаты основной сессии, без их повторного запуска или повторной оценки. Merge/push/release/production этим дополнением не разрешаются.
