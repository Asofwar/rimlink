# План серверной доставки модпака для GOG

Автор плана: Astra. Реализация: основная сессия, GPT-6.1 Sol.
Дата: 2 ноября 2026 года. Выполнение: самостоятельно основной сессией, в текущем worktree `D:/Build/rimlink-gog-modpack`, ветка `asofwar/gog-modpack-download`, исходная база `45a188b`.
Основание продолжения — команда пользователя «Дальше со скачиванием модпака». Этот план не переписывает статус спеки и не утверждает, что реализация уже проверена. Все новые DTO, методы, пути и команды ниже — контракт будущих изменений, не существующие API.

## Goal

Дать игроку GOG один управляемый путь: узнать возможности сервера до login → подтвердить полный пакет → скачать с pinned origin → проверить и установить → один restart/rejoin. Steam API, SteamCMD, зеркала и магазин модов не требуются. Существующий Steam-путь сохраняется отдельно.

## Architecture

CLI пакует подготовленные разрешённые моды, сервер независимо проверяет архив и публикует неизменяемый пакет. Bootstrap token открывает пакет и только связанный снимок согласованных ModConfig, без игровой сессии. Client.Core ведёт download/install/recovery; игровой adapter передаёт пути и загруженные корни. ModContent уже находится в Rimlink.Protocol: сервер переиспользует его без зависимости Server→Client.Core. Pins, comparer, ModpackDiff и JoinIntentStore сохраняются.

## Spec

[Спецификация GOG](../specs/2026-11-02-gog-modpack-download-design.md). Общие новые методы конкретизируются в задачах без готовых тел реализации. Спека, план гильдий и другие worktree не изменяются.

## Global Constraints

- Код, тесты, JSON DTO, EF-миграции и исправления пишет только основная сессия GPT-6.1 Sol. Нет новых агентов или исполнителей; Astra составляет план и проводит один предусмотренный финальный review.
- Новых proto/oneof/ErrorCode нет. Не занимать guild ErrorCode61–66, events111, WorldInfo6/7 и ColonyRestore6. Общая версия обеих фич — **0.8.0**, не 0.9.0; ProtocolVersion без изменения.
- Production Steam-only, `allowPassword=false`, рабочий сервер и Steam не изменять/не останавливать. Состав методов аутентификации показывается честно; download token не заменяет пароль аккаунта или Steam ticket.
- Сервер никогда не читает путь/URL, присланный клиентом. Локальный `--source` существует только в CLI. Запрещены игра/Core/DLC, Config, сейвы, ключи, секреты и исполняемые install-hooks.
- Публикация только полной обязательной коллекции с зависимостями. Любой конфликт/превышение отклоняет целый пакет; без усечения, неявного выбора копии или «успеха» при отсутствии файлов.
- Конечные каталоги — отдельные верхнеуровневые `Mods/rimlink-managed-<safe-id>`. Staging/backup/journal находятся **вне Mods**, на том же томе. Не создавать невидимый игре `Mods/Rimlink/packages/...`.
- Неуправляемая точная копия может использоваться; отличающаяся/дублирующая не перемещается и не перезаписывается. Факт владения каталогом — локальный журнал, не совпадение packageId.
- До restart проверяется подготовленный диск и будущий ModsConfig. Старые загруженные сборки не объявляются исправленными. После restart — новая фактическая инвентаризация и обычный comparer/login.
- AdminBypass сохраняется до и после создания мира: администратор вправе входить с отличающимся модпаком. Один общий review/fix pass; чужие правки и выполняемую основной сессией отмену её изменений LoginGate не трогать.
- UTF-8; команды ниже только для будущей реализации. План не разрешает публикацию в production, commit/push/deploy. Пользовательские пароли/token не писать в логи, журналы, join-intent и примеры.

## Review Focus

Полнота/права раздачи; доступ до login; pin до секрета; отзыв stream; Range/ETag; SafeZip/квоты; duplicate packageId; publication; crash recovery; ModsConfig-last; чужие файлы; loaded/prepared; полный ModConfig до одного restart; сохранение AdminBypass после создания мира; неизменность outbox/Steam-only.

## Конкретные контракты

### DTO и идентичность

Новый каталог `src/Rimlink.Protocol/ModpackDelivery/` содержит обычные C# JSON DTO, не protobuf: `DeliveryContracts.cs`, `DeliveryLimits.cs`, `ModMetadata.cs`, `SafeModArchive.cs`. Формат `formatVersion=1`; неизвестная версия отклоняется. JSON ограничен по байтам до разбора, глубина ≤32; никаких `$type` или полиморфного создания типов.
`CollectionDescriptor`: revision, gameVersion, ordered `entries[{packageId,version,filesHash,kind,position}]`, catalogHash. Revision — SHA256 канонических entries/gameVersion. CatalogHash связывает revision с отсортированным полным списком согласованных ModConfig `{packageId,handle,hash}`; hash каждого content вычисляется штатным ModContent.SettingsHash. Оба значения проверяются при upload/publish и перед commit; изменение списка или settings требует новой публикации.
`PackageManifest`: formatVersion, packageId (UUID N), collectionRevision, catalogHash, gameVersion, archiveSha256, archiveBytes, expandedBytes, fileCount, mods, prerequisites. `mods`/DeliveryMod: packageId, displayName, version, filesHash, **обязательный contentHash (SHA256, 64 hex)**, folderId, position, supportedVersions, dependencies, incompatibilities, loadBefore/loadAfter, expandedBytes, fileCount. JSON format1 ещё не опубликован: поле добавляется без новой версии; отсутствие/неверный формат отклоняется. Prerequisites — Core/DLC без файлов; Settings не в ZIP.
`DeliveryMod.ContentHash` — SHA256 канонически упорядоченных case-sensitive ordinal относительных имён ВСЕХ файлов и SHA256 полных байтов каждого. Packer, ZIP-validator, дисковый metadata reader и installer вычисляют независимо по одному контракту; manifest не принимается на веру. Пустые каталоги не влияют на ContentHash, но входят в отдельный локальный TreeHash для защиты удаления/rollback. Gameplay FilesHash (Defs/Patches/Assemblies) и protobuf неизменны. CollectionDescriptor сохраняет filesHash; точность reuse дополнительно проверяется по contentHash опубликованного manifest.
`folderId` — 32 lowercase hex, назначается локальным упаковщиком и сверяется на уникальность; архив содержит только `mods/<folderId>/...`. `safe-id` конечной папки составляется из UUID операции установки и folderId, а не из packageId/имени архива. Пути строятся только после проверки состава/длины.
`DeliveryError`: code, messageKey, безопасные args, retryable; HTTP 400 malformed, 401 invalid/expired access, 403 forbidden, 409 conflict/revision, 410 withdrawn, 413 limits, 416 bad range, 429 busy/rate, 507 disk budget. Это string-коды JSON, не новые ErrorCode. Ошибки не раскрывают serverPassword, token или серверные абсолютные пути.

### Административная публикация и локальный CLI

Все admin endpoints под `/api/admin/modpack/delivery`, существующие RequireAdmin/RequireScope/Actor и cookie-CSRF сохраняются. Чтение requirements/status — read; upload/publish/revoke — admin. DTO проверяются независимо от CLI.
`GET /requirements` → CollectionDescriptor; `GET /publication` → статус без секретов. `POST /packages` принимает `UploadOffer{manifestWithoutPackageId,redistributionConfirmed,publicDistributionConfirmed}` → `{packageId,uploadExpiresAt}`; upload intent живёт 15 минут, одновременно один незавершённый upload сервера.
`PUT /packages/{id}/archive` — streaming ZIP, фиксированный серверный root. Проверить длину/SHA256/пределы, писать `.part`; затем SafeZip/metadata и независимо FilesHash/ContentHash каждого мода. Повтор готового архива идемпотентен; оборванный upload начинается заново. JSON/base64/multipart buffering и upload resume не нужны.
`POST /packages/{id}/publish {expectedCollectionRevision,expectedCatalogHash,sharedSettingsConfirmed}` сверяет набор/порядок, оба hash и согласия; непустой ModConfig требует отдельного sharedSettingsConfirmed. Снимок берётся только из валидированного ModpackService.GetConfigs и сохраняется с пакетом, не из пути/URL. Нужны redistributionConfirmed и при пустом serverPassword publicDistributionConfirmed. Повтор текущей публикации без эффекта; `DELETE /publication` отзывает grants/streams.
CLI `modpack package`: `requirements --server ... --token ... --output ...`; `pack --source <набор> --requirements <requirements.json> --output <новый-каталог>` → archive.zip/manifest.json; `publish --from <пакет> --server ... --token ... --confirm-redistribution [--confirm-public] [--confirm-shared-settings]` → offer/upload/publish. `status`/`unpublish` читают/снимают публикацию. При settings флаг обязателен. Локальные пути не отправляются серверу, согласия не угадываются.
CLI использует System.IO.Compression/XML и существующий ModContent; набор сначала проверяется целиком. Required mods + обязательные зависимости должны совпасть с подготовленной серверной коллекцией; dependency, числящаяся только Allowed, требует явного исправления админом списка до упаковки. Optional Allowed не добавляются автоматически. Core/DLC лишь prerequisites; About/ресурсы обычных модов сохраняются. DTD/внешние XML entities запрещены.

### Bootstrap, download и отзыв

`GET /api/modpack-delivery/capabilities` без login отдаёт только formatVersion, deliveryAvailable, requiresServerPassword, authMethods/passwordLoginAvailable/steamLoginAvailable и limits; приватные названия модов, package ID и манифест не раскрываются. Значения auth-method metadata выводятся из фактической конфигурации/доступного механизма, не из возможности bootstrap.
`POST /api/modpack-delivery/access {serverPassword,expectedPackageId?}` после pin/пароля → `AccessGrant{token,expiresAt,packageId,collectionRevision,catalogHash}`. Без serverPassword нужна подтверждённая публичность. Token 256 бит, memory-only (на сервере hash), 15 минут: только manifest/archive/settings одного package ID/catalogHash; никаких игровых/admin прав. `GET /packages/{id}/settings` под тем же префиксом возвращает `SettingsSnapshot{packageId,catalogHash,configs:[{packageId,handle,hash,contentBase64}]}` — полный опубликованный ModConfig без фильтра по известным клиенту ModHandles, в действующих лимитах Protocol.Limits для настроек. Не произвольный serverConfig/Config и не чтение файлов по параметрам.
`GET /api/modpack-delivery/packages/{id}/{manifest|archive|settings}` требует Bearer download-token и тот же pinned HTTPS-origin. В bootstrap handler **явно AllowAutoRedirect=false**: общий ConnectionTransport.Pinned оставляет redirect включённым по умолчанию. URL в manifest отсутствует; capabilities устанавливает pin через CertificatePinStore/IPinPrompt до отправки секрета. Changed pin требует решения пользователя.
Archive: identity encoding без автоматической декомпрессии HTTP, strong ETag по archiveSha256, Accept-Ranges: bytes; только один диапазон. Resume отправляет Range+If-Range, принимает 206 только с точным Content-Range/ETag/полным размером. Ответ 200 означает новый файл с нуля, не append; 416 при уже полном фрагменте ведёт к финальному hash-check, иначе к новому скачиванию. Истечение token требует повторного access, пароли не сохраняются в resume state.
Новая публикация, unpublish, смена применённого serverPassword, списка или согласованных settings отзывают grants/streams через access epoch; expiry/отзыв проверяются между блоками. После рестарта tokens недействительны. Private→public без согласия блокирует delivery. SetConfigs меняет catalogHash так же, как Set меняет collectionRevision; смешать архив одной публикации и settings другой нельзя. Hot reload конфигурации не добавляется.

### Пределы и хранение

Пределы: manifest ≤1 МиБ, mods ≤512, ZIP ≤2 ГиБ, expanded ≤8 ГиБ, entries ≤100000, file ≤512 МиБ, ratio ≤100:1 per entry/total, depth ≤16, relative/full path ≤180/240. Проверять фактические потоки, не только заголовки. На томе Mods нужны archive+expanded+backup+512 МиБ; на томе ModsConfig — свой staging/temp/backup и запас. При Mods на D и Config на C проверки места раздельны, журнал операции общий; межтомных rename нет, вся операция не атомарна.
Bootstrap/access лимитируются по нормализованному адресу: 5 попыток/мин; grants ≤64; downloads ≤4 на сервер и ≤1 на token. Временные отказы имеют Retry-After. Admin: один upload/validation одновременно; server JSON offer ≤1 МиБ+16 КиБ, простые POST ≤8 КиБ, streaming body ≤2 ГиБ. Повысить request limit только у archive upload, не глобально у сервера.
Файлы сервера: `DataDirectory.Root/modpack-delivery/{staging,packages}`, каталоги по UUID. EF хранит manifest, immutable опубликованный ModConfig-снимок/catalogHash, creator/согласия и publication/access epoch. Только согласованные settings допустимых модов, без персонального Config/ключей. Архив flush+rename до транзакции; потеря файлов или hash mismatch выключает delivery, не login. Незавершённое не публикуется после crash.
Квота файлов: текущий и один предыдущий архив плюс один upload и одна validation-распаковка — максимум 14 ГиБ + ограниченные metadata/запас. Новый upload при недостатке места отклоняется. Уборка только собственных staging/неиспользуемых архивов без streams/публикации; `.env`, game DataDir и пользовательские пути не затрагиваются. Пакеты воспроизводимы из локального export: восстановленная БД без архивов требует повторной загрузки, не создания ссылки на чужой путь.

## Порядок выполнения

12 задач, выполняет GPT-6.1 Sol. Команды ниже — PowerShell из `D:/Build/rimlink-gog-modpack`. RED наблюдается до реализации, GREEN после; исходники игры повторно не исследовать без конкретного препятствия реализации. Существующие пути перечислены явно; новые помечены «Новые». Тесты используют временные каталоги, ServerFixture.WithSetting/WithServices/WithoutWorld и AdminRestExtensions.Rest/WithToken; реальные секреты и моды в fixture не нужны.
Общие ворота перед будущим коммитом: `rtk proxy dotnet test Rimlink.sln -nologo -v q` и сборка мода скриптом Build-ClientMod без InstallTo в собственный OutputDir. Проверить владельца OutputDir: скрипт его пересоздаёт. Git-safe-commit/identity и чужие правки обязательны; сообщения ниже — будущие короткие русские сообщения, не команда сейчас коммитить.

### 1. DTO, общие лимиты и одинаковое сравнение

Зависимости: нет. Существующие: `src/Rimlink.Protocol/ModContent.cs`, `src/Rimlink.Server/Application/Modpack/ModpackComparer.cs`, `tests/Rimlink.Server.Tests/Modpack/ModpackComparerTests.cs`, `tests/Rimlink.Client.Tests/Mods/ModContentTests.cs`.
Новые: `src/Rimlink.Protocol/ModpackDelivery/DeliveryContracts.cs`, `DeliveryLimits.cs`, `ModMetadata.cs`; `src/Rimlink.Protocol/ModpackComparison.cs`; `tests/Rimlink.Client.Tests/Mods/DeliveryContractTests.cs`. SettingsSnapshot также обычный JSON DTO в Protocol; зависимости Server→Client.Core нет.
Интерфейсы: каноническая CollectionRevision, чтение About без DTD, Normalize, ValidateManifest. Чистую существующую логику Compare/Normalize перенести в ModpackComparison, сохранив серверный ModpackComparer как совместимый делегат; не заводить вторую расходящуюся реализацию. Дубли проверяются до comparer, который сейчас сводит client к dictionary. ModContent.Scan при необходимости переводится на bounded streaming **без изменения формата хеша**.
RED: unknown version, отсутствующий/неверный contentHash, overflow/duplicate, revision, прежние comparer/FilesHash. Sol уже подтвердил RED для отличающихся Textures при одинаковом FilesHash: включить в покрытие, не заявлять собственный прогон. GREEN: обязательный DeliveryMod.ContentHash без изменения gameplay hash/protobuf.
Команда: `rtk proxy dotnet test Rimlink.sln -nologo -v q --filter 'FullyQualifiedName~ModpackComparerTests|FullyQualifiedName~ModContentTests|FullyQualifiedName~DeliveryContractTests'`.
Expected: прежние правила сохранены, новые данные имеют конечные пределы; proto и версии не изменены. Commit: `Добавить контракты серверного пакета модов`.

### 2. SafeZip и локальный упаковщик CLI

Зависимости: 1. Существующие: `src/Rimlink.Tools/Commands/ModpackCommand.cs`, `src/Rimlink.Tools/Commands/RestClient.cs`; образец импорта не использовать для молчаливого выбора последнего duplicate About.
Новые: `src/Rimlink.Protocol/ModpackDelivery/SafeModArchive.cs`; `src/Rimlink.Tools/Commands/ModpackPackageCommand.cs`; `tests/Rimlink.Server.Tests/Tools/ModpackPackageCommandTests.cs`; `tests/Rimlink.Client.Tests/Mods/SafeModArchiveTests.cs`.
Интерфейсы: PackPreparedCollection, ValidateArchive, ExtractValidated. SafeZip общий для CLI/server/client: разрешённые ZIP store/deflate, только обычные файлы/каталоги, запрет ссылок/reparse/ADS/UNC/drive/traversal/device/trailing-dot-space/case-normalization collisions, проверка root и компонентов перед записью, счёт фактических потоков. Не использовать безусловный ExtractToDirectory. Отказы не оставляют файлов вне временного корня.
Источник только непосредственные каталоги подготовленного набора с About; не обход всей установки игры. Запрещённые категории Config/Saves/Logs/ключи/`.env` и игра/DLC отклоняют пакет. Перед pack CLI показывает полный инвентарь; администратор обязан подготовить разрешённый набор, сканер имён не выдаётся за доказательство отсутствия любого секрета внутри DLL/произвольного файла.
RED: неполный required/dependencies/DLC, order/duplicates, malicious ZIP/пределы, подмена Textures/About при прежнем FilesHash. GREEN: SHA256 архива, независимые FilesHash/ContentHash packer и ZIP-validator совпадают; порядок обхода не влияет, регистр имён влияет, пустые каталоги ContentHash не меняют. Только свои fixture.
Команда: `rtk proxy dotnet test Rimlink.sln -nologo -v q --filter 'FullyQualifiedName~SafeModArchiveTests|FullyQualifiedName~ModpackPackageCommandTests'`.
Expected: разрешённый тестовый набор воспроизводимо пакуется, недопустимый целиком отклоняется, исходные файлы не изменяются. Commit: `Добавить безопасную упаковку модпака`.

### 3. Хранилище, upload и публикация

Зависимости: 1, 2. Существующие: `src/Rimlink.Server/Program.cs`, `src/Rimlink.Server/Application/Modpack/ModpackService.cs`, `src/Rimlink.Server/Storage/RimlinkDbContext.cs`, `src/Rimlink.Server/Storage/DataDirectory.cs`, `src/Rimlink.Server/Storage/Migrations/RimlinkDbContextModelSnapshot.cs`, `src/Rimlink.Tools/Commands/RestClient.cs`.
Новые: `src/Rimlink.Server/Application/Modpack/ModpackPackageStore.cs`, `ModpackDeliveryService.cs`; `src/Rimlink.Server/Rest/ModpackDeliveryAdminEndpoints.cs`; `src/Rimlink.Server/Storage/Entities/ModpackPackage.cs`, `ModpackPublication.cs`; соответствующие `Storage/Configurations/ModpackPackageConfiguration.cs`, `ModpackPublicationConfiguration.cs`; `tests/Rimlink.Server.Tests/Storage/ModpackDeliverySchemaTests.cs`, `tests/Rimlink.Server.Tests/Modpack/ModpackPublicationTests.cs`. EF-миграция `ModpackDelivery` с автоматически сгенерированным временным префиксом, не заимствовать номер миграции гильдий.
Реализовать admin contracts, streamed Upload, CLI publish/status/unpublish. На publish сверить согласия и зафиксировать полный валидированный ModConfig-снимок/catalogHash в той же транзакции, что publication+audit, после готовности архива. Set и SetConfigs инвалидируют публикацию/grants; после рестарта проверить оба digest. Отсутствующие bool не являются согласием. Лог успеха только после commit.
RED: права/CSRF, interrupted/hash mismatch, чужой path/URL, Set/SetConfigs при validate/publish, публикация settings без согласия, crash file rename/DB commit, повтор publish, DB без архива. GREEN: immutable согласованный архив+settings, прежние данные сохранны, согласия с actor.
Команды: `rtk proxy dotnet ef migrations add ModpackDelivery --project src/Rimlink.Server`; `rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackDeliverySchemaTests|FullyQualifiedName~ModpackPublicationTests|FullyQualifiedName~ModpackPackageCommandTests'`.
Expected: миграция сохраняет прежние данные, начальная публикация отсутствует, CLI передаёт байты, а не серверный filesystem path. Commit: `Добавить публикацию серверного пакета модов`.

### 4. Bootstrap до login, токены и Range

Зависимости: 3. Существующие: `src/Rimlink.Server/Hosting/ClientAddress.cs`, `src/Rimlink.Server/Hosting/ServerOptions.cs`, `src/Rimlink.Server/Program.cs`; настройки читать, новые разрешения не включать.
Новые: `src/Rimlink.Server/Rest/ModpackDeliveryEndpoints.cs`; `src/Rimlink.Server/Application/Modpack/ModpackDownloadAccess.cs`; `tests/Rimlink.Server.Tests/Modpack/ModpackDownloadAccessTests.cs`, `ModpackDownloadRangeTests.cs`.
Методы: GetCapabilities, GrantAccess, Revoke, StreamArchive; фиксированные маршруты/JSON из контрактов. Memory-only grant store с TimeProvider/сроком, семафорами передачи, cancellation epoch; архив отдаётся bounded потоком без загрузки в память. Не полагаться только на проверку token в начале долгого stream.
RED: private manifest/settings без пароля, чужой token/catalogHash, expiry/смена политики в stream, private→public, лимиты 5/64/4/1, Range/200/206/416, restart. Отдельно новый неизвестный ModHandle получает полный согласованный снимок, но произвольный Config недоступен. GREEN: scope строго package/catalogHash, без игровой сессии.
Команда: `rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackDownloadAccessTests|FullyQualifiedName~ModpackDownloadRangeTests'`.
Expected: приватные данные недоступны до access; отозванный stream прекращается; authMethods соответствуют серверу, Steam-only остаётся Steam-only. Commit: `Добавить защищённую загрузку пакета до входа`.

### 5. Сохранить AdminBypass и обычную проверку игроков

Зависимости: до интеграции задачи 9. Существующие: `src/Rimlink.Server/Application/Auth/LoginGate.cs`, `tests/Rimlink.Server.Tests/Modpack/ModpackLoginTests.cs`, `tests/Rimlink.Server.Tests/Auth/LoginGateTests.cs`; ServerFixture.WithoutWorld для bootstrap.
Уточнение пользователя: AdminBypass — желаемое поведение, не дефект. Администратор может войти с несовпадающими модами и после world.Exists; условие обязательного comparer для него не вводить. Основная сессия сама убирает только собственную отменённую правку; эта задача её не откатывает и не повторяет.
Сохранить AdminBypassesModpackCheck; проверить admin до/после создания мира, обычный mismatch без session token, пустой список и REST-импорт. Для этого существующего инварианта достаточно регрессионной проверки, искусственный RED не нужен. Успешный admin login сам по себе не доказывает установку: download-приёмка использует обычного игрока и свежую инвентаризацию файлов/settings.
Команда: `rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~Modpack|FullyQualifiedName~LoginGateTests'`.
Expected: AdminBypass и bootstrap/import сохранены, обычные игроки проходят comparer. Commit только при добавлении покрытия: `Проверить сохранение административного обхода модпака`; без изменений отдельный коммит не нужен.

### 6. Клиентский pinned HTTP, resume и отмена загрузки

Зависимости: 1, 4. Существующие: `src/Rimlink.Client.Core/Net/ConnectionTransport.cs`, `CertificatePinStore.cs`, `ServerEndpoint.cs`; использовать текущий Pinned и IPinPrompt, а не новый trust store.
Новые: `src/Rimlink.Client.Core/Mods/Delivery/ModpackDownloadClient.cs`, `ModpackDownloadState.cs`; `tests/Rimlink.Client.Tests/Mods/ModpackDownloadClientTests.cs`.
Методы: Capabilities, Access, Manifest, Settings, Download/Resume, Cancel. Pin до секрета; явно AllowAutoRedirect=false и decompression off. Все пути из endpoint/ID; Settings проверяет package/catalogHash, размер и hash каждого content. Resume хранит endpoint/pin/package/hash/size/ETag/offset без token/пароля, только собственный `.part`.
RED: changed pin, redirect, truncated/oversized body, bad ETag/Content-Range, 200 вместо206, expired grant, corrupted partial/full hash, cancel и продолжение после process restart. GREEN: одна операция с ограниченными буферами, stream cancellation, понятный terminal/pending результат. Никакой зависимости от SteamManager/Steam API.
Команда: `rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackDownloadClientTests|FullyQualifiedName~CertificatePinStoreTests'`.
Expected: секрет не уходит до pin; частичный файл не принимается за установленный модпак, новое согласие нужно для другого manifest. Commit: `Добавить загрузку модпака без Steam`.

### 7. План установки по диску, зависимостям и будущему ModsConfig

Зависимости: 1, 2, 6. Новые: `src/Rimlink.Client.Core/Mods/Delivery/ModpackInstallPlan.cs`, `PreparedModInventory.cs`; `tests/Rimlink.Client.Tests/Mods/ModpackInstallPlanTests.cs`.
Вход: manifest/staging, все установленные копии, игра/DLC, активный список, ownership journal и `runningRootDirs` от игрового adapter — корни модов с реально загруженными Assemblies DLL. Выход: reuse/install/retire-owned, будущий порядок, место по томам, блокеры; записи на диск нет.
Reuse единственной копии требует packageId/версии/FilesHash и заново вычисленного дисковым metadata reader ContentHash. Отличие Textures при прежнем FilesHash — конфликт unmanaged без изменения файлов. Retire/replace runningRootDirs запрещён **даже при разрешённом ОС rename**. Dependencies/order/Core/DLC проверяются целиком, extra files не удалять.
RED: Textures/About/прочие файлы и регистр имён при неизменном FilesHash, точный reuse, duplicate, nested target, dependency/DLC/order, loaded-root. GREEN: ModpackComparison проверяет ModRef/порядок, отдельная delivery-проверка — ContentHash всех файлов; полный settings-снимок до restart.
Команда: `rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackInstallPlanTests'`.
Expected: каждый новый мод находится в отдельном верхнем Mods/rimlink-managed-...; чужие каталоги не изменяются, план полностью объясняет действия пользователю. Commit: `Добавить проверку подготовленного модпака`.

### 8. Журналируемая установка, ModsConfig-last и recovery

Зависимости: 7. Новые: `src/Rimlink.Client.Core/Mods/Delivery/ModpackInstaller.cs`, `ModpackInstallJournal.cs`, `ModsConfigTransaction.cs`; `tests/Rimlink.Client.Tests/Mods/ModpackInstallerTests.cs`, `ModpackRecoveryTests.cs`.
Состояния: Downloading → Verified → Prepared → Committing → AwaitingRestart → VerifiedAfterRestart; Cancelled/RecoveryRequired. Один lock install-root, общий журнал intent/completion обоих томов. Installer независимо сверяет ContentHash; ledger хранит TreeHash файлов **и пустых каталогов** для защиты удаления/rollback, не token. Reparse проверяется повторно.
До commit сверить publication/catalogHash, disk fingerprints, runningRootDirs, место на каждом томе. Mods staging/rollback на D, ModsConfig temp/backup на C при таком размещении; rename только внутри тома. Сохранить исходный config/факт отсутствия. Retire лишь owned незагруженные корни; установить моды, подготовить отдельно согласованный ServerSettingsStore, **ModsConfig последним**. Старый ApplyServerOrder с First/немедленной записью не использовать.
Cancel при commit завершает шаг и откатывает по общему журналу; crash до join восстанавливается по нему. SharedSettings activation также фиксируется с предыдущим указателем для rollback, не копированием персонального Config. Заменять loaded-root нельзя независимо от файловой блокировки. Не удалять/не затирать externally modified ownfiles, config или backup: оставить обе версии и RecoveryRequired с путями. Нет общей атомарности между C/D.
RED: fault injection C/D/config/settings, disk full/readonly, loaded-root, отсутствие config/concurrent/cancel; внешние Textures/ownfiles/backup и добавленный пустой каталог. GREEN: ContentHash ловит файлы, TreeHash также пустые каталоги; перед удалением/rollback проверяется TreeHash, при внешнем изменении сохраняются обе версии.
Команда: `rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackInstallerTests|FullyQualifiedName~ModpackRecoveryTests'`.
Expected: исходные данные доступны после каждого crash point; никакой записи вне проверенных roots; невозможно продолжить join с незавершённым журналом. Commit: `Добавить установку модов с журналом и откатом`.

### 9. UI GOG, credentials, настройки и один restart/rejoin

Зависимости: 5, 6, 8. Существующие: `src/Rimlink.Client/UI/ModpackActions.cs`, `Dialog_ModpackDiff.cs`, `LoginFlow.cs`, `SteamAuth.cs` (GameCredentials), `MainMenuPatch.cs`, `ServerSettings.cs`; `src/Rimlink.Client.Core/Mods/JoinIntentStore.cs`, `ModpackDiffView.cs`; `tests/Rimlink.Client.Tests/Mods/JoinStoresTests.cs`.
Новые: `src/Rimlink.Client/UI/ModpackDownloadFlow.cs`, `Dialog_ModpackDownload.cs`; `src/Rimlink.Client.Core/Mods/Delivery/ModpackJoinReadiness.cs`; `tests/Rimlink.Client.Tests/Mods/ModpackJoinReadinessTests.cs`; `mod/Languages/English/Keyed/ModpackDelivery.xml` и `mod/Languages/Russian/Keyed/ModpackDelivery.xml`.
Workflow из Dialog_ModpackDiff/LoginFlow доступен до login: capabilities, список/объём/действия, pin/access, явное согласие. ServerPassword только в памяти, отдельно от account password. Adapter передаёт installer актуальные runningRootDirs; Steam-only показывается без изменения auth. GOG не вызывает SubscribeMissing/PendingDownloads.
Login.ModpackDiff.Configs недостаточен: AddConfigs фильтрует неизвестные ModHandles. **До единственного restart** получить полный bootstrap SettingsSnapshot того же package/catalogHash, проверить его хеши/лимиты и отдельное согласие на настройки; отказ/неполнота блокируют restart. Подготовить весь набор штатным ServerSettings.Store.Activate с прежними правилами порядка/redirect. Никаких Config в ZIP и второго Settings-restart как нормального пути. Guard FixAndRestart требует commit, подготовленное сравнение и готовые settings; pending=0 не доказательство.
JoinIntentStore одноразовый/15мин/без пароля. MainMenuPatch: recovery → Take → одна LoginFlow попытка. VerifiedAfterRestart требует свежих RunningMods/ModContent/settings, ContentHash дисковых корней по manifest и успешного login; gameplay comparer не проверяет все ресурсы, AdminBypass не доказательство установки. Отдельный admin-вход сохраняется; auth/network отказ не стирает файлы, TransferOutbox только после login.
RED: новый скачанный мод с прежде неизвестным ModHandle и sharedsettings → полный согласованный snapshot → **один** restart → matching login без Settings diff; также отказ согласия, смена catalogHash, missing при pending=0, prepared≠loaded, double click, expiry/auth/recovery. GREEN: EN/RU и readiness без restart-loop.
Команда: `rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~ModpackJoinReadinessTests|FullyQualifiedName~JoinStoresTests'`; затем существующий `scripts/Build-ClientMod.ps1` без InstallTo, OutputDir собственного стенда.
Expected: новый мод получает согласованные настройки при первом restart; до него UI не объявляет старые сборки исправленными. Commit: `Добавить интерфейс установки модпака для GOG`.

### 10. Сквозная приёмка API–CLI–клиент и отказов

Зависимости: 3–9. Существующие fixture: `tests/Rimlink.Server.Tests/Support/ServerFixture.cs`, `AdminRestExtensions.cs`. Новые: `tests/Rimlink.Server.Tests/Support/ModpackDeliveryFixtures.cs`, `tests/Rimlink.Server.Tests/Modpack/ModpackDeliveryFlowTests.cs`.
Один тестовый цикл на собственных маленьких модах: CLI requirements/pack/publish → private access → Range download → SafeZip/план/commit → подготовленный comparer; отдельно имитация post-restart inventory/login. Использовать реально исполняемые библиотечные методы CLI/client и тестовый сервер, без mocks для критических файловых переходов. Это не игровой прогон.
RED→GREEN: pack/ZIP/disk/installer одинаково считают ContentHash; Textures mismatch запрещает чужой reuse, отсутствующее поле отклоняется, empty-dir меняет только TreeHash. Также revision/settings/revoke/restart, Config/outbox, права/DLC, новый handle до одного restart, no-redirect, recovery C/D, loaded-root и неизменный AdminBypass.
Команда: `rtk proxy dotnet test Rimlink.sln -nologo -v q --filter 'FullyQualifiedName~Modpack|FullyQualifiedName~JoinStores|FullyQualifiedName~CertificatePinStore'`.
Expected: полный внутренний путь и терминальные отказы подтверждены, не объявлены игровым успехом. Commit: `Проверить доставку модпака и восстановление после сбоев`.

### 11. Настоящая игровая приёмка в отдельном стенде

Зависимости: 10. Будущие документы: `docs/verification/2026-11-02-gog-modpack-download-acceptance.md`, существующий `docs/manual-checklist.md`. Никаких изменений рабочего сервера/Steam. Выбрать свободный собственный root вне worktree и guild-стенда, отдельные DataDir/Mods/savedata/порт; доступность проверить при выполнении, не считать любой предложенный порт свободным заранее.
Пользователь подтвердил: **GOG-копии нет**, настоящая GOG acceptance заблокирована и не заявляется. Разрешённый игровой прогон на доступной копии описать с этим ограничением; он не заменяет GOG. Не придумывать `-noSteam` и не завершать Steam. Password только на изолированном тестовом сервере. GOG-сценарий остаётся невыполненным до появления законной копии.
Свои моды: missing/hash/order, reuse/duplicate, DLC/extras; новый неизвестный ModHandle со settings. Отдельное согласие, package/catalogHash, ServerSettingsStore, **один** restart и обычный игрок без Settings diff. Реальные UI/pin/resume/каталоги; отдельно admin с несовпадающим модпаком входит по AdminBypass и после создания мира. Steam-only не меняется.
Crash/rollback проверить с Mods на D и ModsConfig на C, внешним изменением ownfiles и восстановлением без старта игры. Loaded owned DLL не заменяется даже при успешном пробном OS rename; точный Rimlink reuse допускается. Несовпадающая загруженная копия — отказ/ручное исправление, не горячая замена.
Команда сборки (после определения своего безопасного пути): `rtk proxy powershell -NoProfile -File .\scripts\Build-ClientMod.ps1 -RimWorldDir $gogStandGame -OutputDir $gogStandBuild -InstallTo $gogStandMods`; переменные задаются только путями проверенного собственного стенда, не production и не каталогом гильдий. Игровые действия — установленный семантический MCP; ввод/рабочий стол только CUA Driver.
Expected: записаны build, пути, package/hash, число restart, UI/логи/файлы до и после; явно различены API, файловая и GOG-проверка. Неустановленные DLC не приобретать и не заменять. Commit: `Зафиксировать игровую приёмку доставки GOG`.

### 12. Документация, 0.8.0, один review и fix pass

Зависимости: 1–11. Существующие: `README.md`, `docs/player-guide.md`, `docs/server-setup.md`, `docs/manual-checklist.md`, `Directory.Build.props`, `src/Rimlink.Client.Core/ClientInfo.cs`, `mod/About/About.xml`; обновить отчёт задачи 11. Спеку не менять и не переписывать её согласование.
Документировать pack/publish, права/публичность/settings-согласие, полный snapshot до restart, Core/DLC/лимиты, auth, конфликты копий, loaded/prepared, recovery C/D и восстановление публикации. Новых runtime-ключей нет. Версия во всех источниках 0.8.0; bump гильдий сохранить без повторного повышения.
Единственный общий code review всей реализации проводится отдельно **после игрового прогона**, с явной отметкой отсутствующей GOG acceptance. Затем один fix pass GPT-6.1 Sol и затронутые проверки. Эта правка документов — не code review; обзоров задач/нового обзора спеки/агентов нет. Незакрытые Critical/Important и отсутствие настоящей GOG-приёмки не скрывать заявлением о полной готовности.
Команды: `rtk proxy dotnet build Rimlink.sln`; `rtk proxy dotnet test Rimlink.sln -nologo -v q`; безопасная сборка мода на стенде. Проверить EN/RU, ссылки, общий номер версии, отсутствие архивов игры/модов/Config/секретов в Git и конфликтов с зарезервированными guild-полями.
Expected: общий review/fix pass и реально выполненная приёмка отражены в ledger; непроверенное перечислено. Commit: `Описать серверную доставку модпака для GOG`. Merge/push/релиз/production только по отдельной команде.

## Границы подготовки плана

ContentHash уточняет уже одобренную точную копию, не новую фичу. Astra меняет только разрешённые Markdown, без кода/прогонов/code review/коммитов. AdminBypass сохранён, upcoming 0.8.0 общая с guilds. GOG-копии нет: реальная GOG acceptance не выполнена; единственный общий review будет отдельно после игрового прогона.
