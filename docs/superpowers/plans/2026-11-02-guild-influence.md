# План реализации влияния союзов

Автор плана: Astra. Реализация: основная сессия, GPT-6.1 Sol.

Дата: 2 ноября 2026 года. Статус: план составлен по утверждённой спеке и принятому fix pass; реализация не начата. Все команды ниже предназначены для будущего этапа реализации и при подготовке плана не выполнялись.

## Goal

Добавить информационный слой влияния колоний существующих союзов на корневой поверхности общей планеты: согласованную окраску, спорные клетки, подписи ближайших источников и легенду. Публикация принадлежности колонии требует согласия её владельца. Войны, форпосты, владение землёй и новая экономика в эту поставку не входят.

## Architecture

Сервер формирует полный публичный снимок из существующих поселений и Alliance, хранит согласие с версией/контекстом, подтверждение поверхности и цвет союза. Отдельной таблицы собственников или независимого каталога источников нет. Новый feature-контракт `influence.proto` дополняет протокол. Мутации используют существующий TransferOutbox и отдельные квитанции влияния.

Client.Core валидирует снимок, защищает кеш от гонок и рассчитывает распространение по небольшому неизменяемому графу. Игровой мод на главном потоке подтверждает исходный PlanetTile, снимает соседей/вершины, порционно строит следующий слой и атомарно заменяет текущую геометрию. UI не меняет игровые права и владельцев колоний.

Все новые классы, методы и сообщения, названные ниже, являются **проектируемыми интерфейсами**, а не утверждением об их наличии. Существующие точки интеграции проверены по текущим исходникам. Детали реализации, не меняющие контракт, выбирает основная сессия внутри соответствующей задачи.

## Spec

[Утверждённая спецификация](../specs/2026-11-02-guild-influence-design.md), включая Important1–5 и Minor6 из принятого единственного fix pass. [Записка исправления](../../verification/2026-11-02-guild-influence-spec-review.md) отделяет прежнее ревью от документальной проверки. Эти документы и research при составлении плана не изменяются; нового ревью спеки нет.

## Global Constraints

- Весь код, тесты, proto, миграции и исправления пишет только основная сессия GPT-6.1 Sol. Astra составляет документы и проводит предусмотренный итоговый review. Дополнительных исполнителей кода и субагентов нет.
- Существующие Alliance, членство, казна, сейвы и правила поселений сохраняются. По умолчанию публикация `false`, подтверждение поверхности `null`, а не `true` по подходящему int.
- Установка конкретного boolean использует ожидаемую версию и контекст мира/поселения/SaveGeneration/союза. Успешное новое `false → false` повышает версию; устаревшее enable окончательно конфликтует. Replay возвращает квитанцию без повторного эффекта и аудита.
- Leave/disband аннулируют также архивное согласие. Rejoin в тот же союз не восстанавливает его. Административный force-disable имеет тот же эффект; административного enable нет.
- Только корневая поверхность, проверенная до потери слоя. Слой влияния не исправляет и не запрещает общую регистрацию поселений на других слоях.
- Полный снимок не усекается и не разбивается на страницы. Офлайн-источники занимают ёмкость; некорректный/переполненный снимок не превращается в частичную заливку.
- High-water событий, счётчик всех инвалидаций и контекст подключения/world/process epoch проверяются и при ответе, и непосредственно перед swap геометрии.
- Verse, WorldGrid и ресурсы рендера доступны только на главном потоке. Алгоритм Client.Core не ссылается на игру. Снятая геометрия не сохраняется в репозиторий.
- UTF-8, никаких секретов, игровых файлов, данных сервера или декомпилированного игрового кода в коммитах. Только clean-room реализация по установленным интерфейсам.
- Не затрагивать чужие изменения, процессы и стенды. Проверку модпака основной сессии не дублировать и не включать в эту поставку.
- Один общий code review после всей реализации и приёмки, затем один fix pass основной сессии. Нет обзоров по задачам и второго ревью спеки. Команды commit ниже — будущие сообщения, не разрешение на commit/push/deploy в текущем задании.

## Review Focus

1. Important1: версия и контекст, два порядка enable/disable, квитанции успеха/окончательного отказа, архивы, rejoin и гонки lifecycle.
2. Important2: реальный слой до PlanetTile → int; nullable legacy; проверка владельца и контекста сервером; никакого общего изменения поведения поселений.
3. Important3: атомарная ёмкость, все офлайн-источники, restore при заполнении, полный снимок, размер и клиентский бюджет на cap.
4. Important4: событие новее ответа, инвалидация без ревизии, инвалидация при построении меша, смена процесса/мира и очистка ресурсов.
5. Important5: раздельные полномочия цвета и force-disable, запрет административного enable, отсутствие приватных данных в публичной проекции, аудит после commit.
6. Minor6: одинаковый сухопутный граф для силы и расстояния; стабильный ID влияет только на подпись. Спорный цвет не получает победителя по ID.

## Параметры первой поставки

Числа фиксируются как общие константы реализации, без нового набора пользовательских настроек. Путь для них — новый `src/Rimlink.Protocol/InfluenceLimits.cs`; клиент и сервер используют одни значения.

| Параметр | Значение и политика |
|---|---|
| Версия алгоритма | 1 |
| Радиус поселения | 3 перехода; другие радиусы и типы источников в первой поставке отклоняются |
| Общая ёмкость C | 128 активных опубликованных источников на мир, независимо от онлайна |
| Союзы в снимке | Не более 128, только представленные источниками |
| Полный protobuf-снимок | Не более 65 536 байт по `CalculateSize()`; проверять до отправки и до расчёта |
| REST-представление снимка | Не более 131 072 байт UTF-8; явный DTO того же полного набора данных |
| Запрос мутации | Не более 2 048 байт; request ID 1–64 ASCII букв/цифр/дефисов |
| Идентификаторы epoch | UUID в формате N, ровно 32 ASCII символа; world постоянный, process новый на каждый старт |
| Имя союза в публичном снимке | Не более 128 байт UTF-8; существующий лимит создания — 32 символа; длинное повреждённое значение отклонять, не обрезать |
| Палитра | 16 непрозрачных RGB-цветов; индекс 0–15, альфа рендера фиксирована клиентом; до выбора — ID союза modulo 16 |
| Чтение gRPC | `Get` и `GetSetting`: по 5 запросов за 10 секунд на аккаунт |
| Мутации gRPC | 5 запросов за 5 секунд на аккаунт; временный rate limit не равен окончательному отказу по ёмкости |
| Обновление клиента | Один запрос и один кандидат геометрии; объединение событий, повтор не чаще одного раза за 2 секунды после завершения предыдущего |
| Работа с графом | До 23 936 посещений «источник–тайл» и до 143 616 проверок соседства на снимок при степени не выше 6 |
| Кадровый бюджет | Цель 2 мс на порцию снятия/построения, максимум 256 тайлов за кадр; отсечка по Stopwatch между операциями |
| Ресурсы слоя | Не более 128 МиБ суммарно для графа, старой/новой CPU-геометрии и оцениваемых GPU-буферов; превышение отменяет кандидата целиком |
| Критерии замеров | Полное перестроение при стабильном снимке на cap ≤5 секунд; прирост p95 времени кадра ≤5 мс на описанном в отчёте стенде |

Обоснование payload: публичный источник несёт только ID, ID союза, int tile и радиус — закладываем 64 байта с framing. Союз с именем до 128 байт, ID и индексом — 160 байт. `128 × (64 + 160) + 512 = 29 184` байта, запас до 64 КиБ больше двукратного. В эти сообщения не включать PlayerRef, имена аккаунтов, имена поселений, роли, казну, версии настроек или произвольные payload. Для подписи источника использовать его ID/тайл и уже доступное описание маркера; отсутствие маркера собственного поселения не мешает заливке. JSON проверять отдельно: экранирование имён и числа имеют другую стоимость.

Обоснование обхода: для графа степени ≤6 даже без использования свойств гексагональной сетки число узлов до глубины 3 ограничено `1 + 6 + 6×5 + 6×5² = 187`; для 128 источников — 23 936. Проверка всех шести соседей каждого достигнутого узла даёт 143 616. Это консервативнее обычных 37 клеток на радиусе 3. Степень, диапазон, совпадение слоя и размеры полигонов проверяются при снятии; неподдерживаемая геометрия отключает новый слой с диагностикой, а не обрезается. Вода входит в бюджет осмотра, но не в распространение.

128 МиБ — общий защитный предел, а не обещание произвольной сложности материалов: компактные числовые массивы, без копии всей планеты и без покадровой аллокации. При приёмке измерить реальные вершины/индексы, память обеих версий и дорогую финализацию мешей. Если цель не выполняется, оптимизировать эту реализацию в заданном cap; не уменьшать полноту снимка и не молча ослаблять критерии.

## Подтверждённые номера и интерфейсы

Проверено по текущим `common.proto`, `events.proto`, `world.proto`, `auth.proto`, `alliances.proto`. В `ErrorCode` последний используемый номер — `SAVE_GENERATION_MISMATCH = 60`; в ServerEvent заняты, в частности, CommunityChanged=100 и AuctionUpdated=110. Следующие номера сейчас свободны. Перед изменением повторно проверить их в основной сессии: параллельная работа может изменить протокол.

| Существующий контракт | Добавление |
|---|---|
| `events.proto`, ServerEvent.body | `InfluenceChanged influence_changed = 111`; импорт `influence.proto` |
| `common.proto`, ErrorCode | 61 `INFLUENCE_CONFLICT`; 62 `INFLUENCE_INELIGIBLE`; 63 `INFLUENCE_CAPACITY`; 64 `INFLUENCE_INVALID`; 65 `INFLUENCE_INTENT`; 66 `INFLUENCE_UNAVAILABLE` |
| `world.proto`, WorldInfo | 6 `world_instance_id` string; 7 `process_instance_id` string |
| `auth.proto`, ColonyRestore | 6 `influence_notice_key` string: причина восстановления колонии без публикации; пусто при отсутствии уведомления |

Номера enum ErrorCode и полей ServerEvent принадлежат разным пространствам; существующие события 61–66 не конфликтуют с новыми ErrorCode. `SettleRequest.tile` и `WorldObject.tile` не менять. `ProtocolVersion.Current` сохраняется. `Rimlink.Protocol.csproj` уже включает `Protos/**/*.proto` с генерацией Both.

Новый `influence.proto` импортирует только `common.proto`, не `events.proto`. Новые сообщения имеют следующие поля; типы ID/версий/revision — int64, tile/radius/palette/algorithm — int32, epoch/request ID — string, флаги — bool. Порядок ниже задаёт конкретные номера полей:

| Новое сообщение | Поля с номерами |
|---|---|
| InfluenceContext | world_instance_id=1; settlement_id=2; save_generation=3; alliance_id=4 |
| InfluenceTarget | settlement_id=1; archive_id=2; ровно один положительный ID |
| InfluenceAlliance | id=1; name=2; palette_index=3 |
| InfluenceSource | id=1; alliance_id=2; tile=3; radius=4 |
| InfluenceSnapshot | world_instance_id=1; process_instance_id=2; revision=3; algorithm_version=4; alliances=5 repeated InfluenceAlliance; sources=6 repeated InfluenceSource |
| InfluenceSetting | context=1 InfluenceContext; version=2; enabled=3; root_surface=4 **optional bool**; tile=5; archive_id=6 |
| InfluencePublication | context=1; expected_version=2; enabled=3 **optional bool**, отсутствие — ошибка |
| InfluenceSurface | context=1; expected_version=2; tile=3; root_surface=4 **optional bool**, отсутствие — ошибка |
| InfluenceColor | world_instance_id=1; alliance_id=2; palette_index=3 |
| InfluenceMutation | client_request_id=1; oneof action: publication=10 InfluencePublication, surface=11 InfluenceSurface, color=12 InfluenceColor |
| InfluenceReceipt | applied_version=1; public_revision=2; error=3 ErrorDetail; отсутствие error — успех операции, не текущая настройка |
| InfluenceChanged | world_instance_id=1; process_instance_id=2; revision=3 **optional int64** |

Новый сервис `Influence`: `Get(Empty) → InfluenceSnapshot`, `GetSetting(InfluenceTarget) → InfluenceSetting`, `Mutate(InfluenceMutation) → InfluenceReceipt`. Все три требуют аутентификации. Get — публичная внутри сервера проекция, не анонимная HTTP-раздача. GetSetting доступен только владельцу целевого действующего/архивного поселения. Игровой клиент подтверждает surface только действующего собственного поселения. Архивное подтверждение не переписывается клиентом.

Окончательные бизнес-отказы валидной мутации сохраняются в квитанции и возвращаются одинаково при replay. Клиент переводит `receipt.error` в окончательный результат outbox; существующее поведение для транспортных RpcException сохраняется. На malformed/чужой запрос допустим обычный typed RpcException. Capacity не возвращать как ResourceExhausted: `TransferOutbox.IsDefinitive` считает этот статус временным. Новый тип outbox добавляется в конец enum без перенумерации старых значений.

WorldInfo устанавливает доверенный контекст процесса через существующий аутентифицированный World.Get/Create. Снимок сам по себе не переключает epoch. При несовпадении process ID — новое чтение World.Get с локальным номером поколения запроса; старый ответ этого чтения также не применяется. После установки нового epoch допускается меньшая revision; старые события, ответы и меши отбрасываются.

## Фактический API игры и выбранное подключение

Read-only декомпиляцией `D:/SteamLibrary/steamapps/common/RimWorld/Assembly-CSharp.dll` проверена установленная **RimWorld 1.7.4995 rev309**. Код игры не сохранялся и не копируется в реализацию.

- `RimWorld.Planet.PlanetTile`: `tileId`, `Layer`, конструктор `(int, PlanetLayer)`; неявное преобразование в int сохраняет только tileId. Одноаргументный конструктор устанавливает слой 0 и для этой фичи не подходит.
- `Find.WorldGrid.Surface`, `PlanetLayer.IsRootSurface`, `PlanetLayer.TilesCount`, `PlanetLayer.WorldDrawLayers` доступны. Проверка источника: исходный tile принадлежит именно Surface и находится в диапазоне, затем берётся tileId. Для снятия геометрии каждый ID оборачивается в `new PlanetTile(tileId, Find.WorldGrid.Surface)`.
- `WorldGrid.GetTileNeighbors(PlanetTile, List<PlanetTile>)`, `GetTileVertices(PlanetTile, List<Vector3>)`, индексатор по PlanetTile и `Tile.WaterCovered` доступны.
- `WorldDrawLayer.Initialize(PlanetLayer)`, `WorldDrawLayerBase.Render()`, `Regenerate()`, `Dispose()`, защищённый список `subMeshes`, `LayerSubMesh.FinalizeMesh(MeshParts)` доступны. Базовый Regenerate сразу очищает старые subMeshes; нельзя запускать его как способ построить кандидат с сохранением старого изображения.
- Регистрация: собственный экземпляр в `Find.WorldGrid.Surface.WorldDrawLayers`, инициализированный через Initialize. Не создавать выдуманный WorldDrawLayerDef. Отключить видимость на других слоях и в фоне через проверенные виртуальные свойства; не регистрировать коллайдеры.
- Кандидат строится вне опубликованного списка subMeshes, порциями из игрового Update; после повторной проверки токена кеша старый список заменяется и освобождается. Regenerate собственного слоя только сообщает о необходимости обновления, не синхронно обходит карту и не очищает опубликованную версию. Удалять и Dispose только принадлежащий фиче экземпляр; материалы освобождать отдельно, поскольку базовый Dispose уничтожает меши.
- `RimWorld.WorldInterface.WorldInterfaceOnGUI()` существует; `RimWorld.Planet.WorldInspectPane` имеет `PaneTopY`, `DoPaneContents(Rect)` и private getter `TileInspectString`. Легенду и дополнительную карточку выбранного тайла разместить через собственный UI рядом со штатной панелью, без замены её текста о владельце и без перехвата обычных кликов. Точную раскладку проверить в задаче 7 на UI 100%/150%.

## Выполнение и общие проверки

Задачи выполняет последовательно основная сессия. Все команды — **PowerShell из `E:/Projects/rimlink`**; в изолированном checkout использовать его корень. Не выполнять команды этого плана в ходе его написания. Разрешение на реализацию/её коммиты не выводить из существования плана.

RED означает наблюдаемый провал нового целевого теста до реализации либо зафиксированное отсутствие игрового сценария. GREEN — прохождение этого теста и регрессионной проверки после изменения. Компиляция или тест графа не заменяют игровую проверку. Перед каждым будущим коммитом: git-safe-commit, эффективные AUTHOR/COMMITTER, чужие правки, затем общие ворота:

```powershell
rtk proxy dotnet test Rimlink.sln -nologo -v q
rtk proxy powershell -NoProfile -File .\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld' -OutputDir 'D:\Build\rimlink-influence-20261102\build\Rimlink'
```

Expected: вся suite зелёная и мод собран; второй вызов **без InstallTo**, ничего не ставит в обычную игру. До использования OutputDir проверить, что каталог принадлежит этому заданию: скрипт пересоздаёт его. Если он занят чужой работой, выбрать отдельный пустой корень и зафиксировать его в ledger. Сборка не доказывает приёмку игры.

### Задача 1. Протокол, лимиты и EF-схема

Зависимости: нет.

Существующие файлы: `src/Rimlink.Protocol/Protos/common.proto`, `events.proto`, `world.proto`, `auth.proto`; `src/Rimlink.Server/Storage/Entities/Entities.cs`, `AllianceGroup.cs`; `src/Rimlink.Server/Storage/RimlinkDbContext.cs`; `src/Rimlink.Server/Storage/Migrations/RimlinkDbContextModelSnapshot.cs`.

Новые файлы: `src/Rimlink.Protocol/Protos/influence.proto`, `src/Rimlink.Protocol/InfluenceLimits.cs`; `src/Rimlink.Server/Storage/Entities/InfluenceOperation.cs`, `src/Rimlink.Server/Storage/Configurations/InfluenceOperationConfiguration.cs`; `tests/Rimlink.Server.Tests/Influence/InfluenceProtocolTests.cs`, `tests/Rimlink.Server.Tests/Storage/InfluenceSchemaTests.cs`. Новая EF-миграция `GuildInfluence` и её Designer в `src/Rimlink.Server/Storage/Migrations/`: временной префикс генерирует EF при выполнении, не выдумывать его заранее.

Контракт хранения: World получает постоянный InfluenceInstanceId и InfluenceRevision; AllianceGroup — nullable PaletteIndex. WorldObject и ArchivedColony — Enabled=false, SettingVersion, ConsentAllianceId, nullable RootSurface, контекст подтверждения world/generation/tile. В архиве сохранить исходный ID WorldObject для проверки старых намерений; restore создаёт новый ID и повышает версию. Настройка и metadata живут рядом с существующим поселением, не в новом реестре собственников. Квитанция хранит ключ caller/request ID, исходное намерение и результат, включая окончательный отказ; actor namespace отличает player от административного вызова.

RED: descriptor-тест фиксирует старые номера и ожидает новые; schema-тест начинает с фактической миграции `20261102104417_SaveGeneration`, создаёт данные союза/поселения/архива, проверяет миграцию и nullable default. GREEN: добавить контракты/поля, сгенерировать миграцию; старые деньги, членство, поколения, сейвы сохраняются, публикация выключена, слой не угадан. UUID существующего мира инициализировать один раз, не общей константой между установками.

```powershell
rtk proxy dotnet ef migrations add GuildInfluence --project src/Rimlink.Server
rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceProtocolTests|FullyQualifiedName~InfluenceSchemaTests'
```

Expected: номера уникальны и совместимы; миграция с 0.7.0 без потери данных, zero active sources. До коммита — общие ворота. Commit message: `Добавить контракты и хранение влияния союзов`.

### Задача 2. Серверное согласие, квитанции и полный снимок

Зависимости: 1.

Существующие файлы: `src/Rimlink.Server/Program.cs`, `src/Rimlink.Server/Grpc/RateLimitPolicy.cs`, `src/Rimlink.Server/Grpc/WorldGrpcService.cs`, `src/Rimlink.Server/Application/World/WorldService.cs`. Образцы: `Application/Social/AllianceService.cs`, `CommunityFeature.cs`, `Grpc/CommunityGrpcService.cs`, `Grpc/SessionInterceptor.cs`.

Новые файлы: `src/Rimlink.Server/Application/Influence/InfluenceService.cs`, `InfluenceFeature.cs`, `InfluenceProcessEpoch.cs`, `InfluenceRefusalKeys.cs`; `src/Rimlink.Server/Grpc/InfluenceGrpcService.cs`; `tests/Rimlink.Server.Tests/Support/InfluenceFixtures.cs`; `tests/Rimlink.Server.Tests/Influence/InfluenceConsentTests.cs`, `InfluenceSnapshotTests.cs`, `InfluenceCapacityTests.cs`.

Интерфейсы: InfluenceService.GetSnapshot, GetSetting, Mutate; InfluenceFeature.AddInfluence/MapInfluence по текущему CommunityFeature. ProcessEpoch — singleton UUID одного процесса; WorldGrpcService.WithPolicy дополняет ответ процессом, WorldService.ToInfo — ID мира. В тестовом fixture существующий seedWorld также получает валидный ID через обычную инициализацию.

Контракт обработчика: одна согласованная транзакция для проверки свежих строк, контекста, версии, членства, metadata, capacity и записи настройки/квитанции/аудита/revision. Уже записанная квитанция проверяется раньше повторного эффекта. Даже новая успешная установка false при false повышает SettingVersion. Сохранённый отказ не становится успехом после освобождения места. Событие транслируется только после commit. Гонка за последнее место должна сериализоваться БД; отдельный нетранзакционный Count не подходит.

GetSnapshot читает одну версию данных. Сначала проверяет количество активных источников без ограничения по online, затем строит полный набор; при >128 — typed INFLUENCE_UNAVAILABLE, без Take/пагинации/частичного результата. Отключения доступны и при переполнении. Геометрию сервер не проверяет самостоятельно: он сверяет владельца, тип, tile, мир, поколение, союз и явное подтверждение от клиента. Колония с неизвестной поверхностью не проходит enable.

RED: оба порядка enable/disable, false→false, payload mismatch, чужой объект, каждый устаревший компонент контекста, nullable surface, положительные/отрицательные квитанции; два конкурентных включения при 127; 128 офлайн-источников; повреждённый снимок и превышение размера. GREEN: минимальный обработчик и неизменяемая публичная проекция, без приватных полей и повторного аудита.

```powershell
rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceConsentTests|FullyQualifiedName~InfluenceSnapshotTests|FullyQualifiedName~InfluenceCapacityTests'
```

Expected: ровно один победитель последнего места; старое enable не оживает; cap и malformed дают определённый исход; rate limit остаётся временным. Общие ворота. Commit message: `Реализовать согласие и снимок влияния`.

### Задача 3. Lifecycle союзов, поселений и архивов

Зависимости: 2.

Существующие файлы: `src/Rimlink.Server/Application/Social/AllianceService.cs`, `src/Rimlink.Server/Application/World/WorldService.cs`, `src/Rimlink.Server/Application/World/ColonyArchive.cs`, `src/Rimlink.Server/Application/Admin/AdminOperations.cs`, `src/Rimlink.Server/Application/Saves/SaveStore.cs`; `tests/Rimlink.Server.Tests/World/ColonyArchiveTests.cs`.

Новый файл: `tests/Rimlink.Server.Tests/Influence/InfluenceLifecycleTests.cs`. Дополняются новые InfluenceService/InfluenceFixtures из задачи 2.

Контракт интеграции: компактные методы InfluenceService для аннулирования согласия и обновления revision вызываются внутри транзакции соответствующего перехода, не открывают вложенные транзакции. AllianceLeave/AllianceDisband сбрасывают также настройки архивов и повышают версии. Abandon удаляет источник и инвалидирует снимок. Archive сохраняет только серверный контекст, снимает активность и повышает версию. Restore переносит metadata только для той же колонии/позиции/поколения, перечитывает согласие и членство внутри транзакции; не доверяет ранее tracked entity.

При restore и свободном месте неаннулированное согласие может вернуться; при заполнении колония всё равно восстанавливается, публикация становится false с новой версией и `influence_notice_key`. Rejoin не создаёт согласие. При ResetPlayer/RecreateWorld аннулирование активных и архивных настроек согласовать с фактическим повышением SaveGeneration в SaveStore.Archive: между повышением поколения и удалением объектов не должно оставаться подходящего для enable старого источника. Rollback сейва не меняет серверное согласие. Новый мир получает новый UUID.

RED: archive→leave→rejoin→restore; disband с архивами; force-disable против restore; enable против leave/archive/reset; новый ID restore; полный cap при восстановлении; backup с меньшей revision/превышенным cap; rollback не возвращает согласие. GREEN: объединить только необходимые записи lifecycle в транзакции, сохранить текущую дисциплину ColonyLocks/SaveStorageLock и порядок работы с файлами. Не удерживать новый общий lock через файловое копирование и не перестраивать архивный механизм.

```powershell
rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceLifecycleTests|FullyQualifiedName~ColonyArchiveTests'
```

Expected: колонии/сейвы не теряются; старые контексты и архивное отозванное согласие не оживают; restore при cap сохраняет обычный вход. Общие ворота. Commit message: `Связать влияние с жизненным циклом колоний`.

### Задача 4. REST, CLI и административные права

Зависимости: 3.

Существующие файлы: `src/Rimlink.Tools/Commands/AdminCommand.cs`, `src/Rimlink.Server/Application/Admin/AdminAudit.cs`; образцы `src/Rimlink.Server/Rest/AdminEndpoints.cs`, `AdminAuth.cs`, `ApiTokens.cs`, `src/Rimlink.Server/Application/Admin/Actor.cs`, `tests/Rimlink.Server.Tests/Tools/AdminCommandTests.cs`.

Новые файлы: `src/Rimlink.Server/Rest/InfluenceEndpoints.cs`, `src/Rimlink.Server/Application/Influence/InfluenceAdminService.cs`, `tests/Rimlink.Server.Tests/Influence/InfluenceAdminTests.cs`. Подключение через InfluenceFeature из задачи 2.

Контракты: `GET /api/admin/influence` с read — только публичный снимок; `GET /api/admin/influence/settings/{settlementId}` и `GET /api/admin/influence/archive/{archiveId}/setting` с admin — текущий контекст для выключения. `POST /api/admin/influence/settings/{settlementId}/disable` и `POST /api/admin/influence/archive/{archiveId}/disable` принимают requestId, ожидаемую версию и полный контекст; boolean во входе не принимается. `POST /api/admin/influence/alliances/{allianceId}/color` принимает world ID, requestId и paletteIndex, требует admin. Цвет не изменяет публикацию.

CLI в существующем admin: `influence`, `influence-setting <settlementId>`, `influence-archive-setting <archiveId>`, `influence-disable <settlementId> <requestId> <worldId> <generation> <allianceId> <version>`, `influence-archive-disable <archiveId> <settlementId> <requestId> <worldId> <generation> <allianceId> <version>`, `influence-color <allianceId> <worldId> <paletteIndex> <requestId>`. Ошибка аргументов не должна давать IndexOutOfRange. Никакого enable. API-ошибки: 403 для прав, 400 для malformed, 409 для конфликта/ёмкости, 503 для непригодного полного снимка, 429 для временного rate limit.

Actor/CSRF/scoped tokens сохраняются. Ключ квитанции включает серверно установленный actor namespace, не присланную строку вместо авторизации. AdminAudit.Write сейчас публикует feed немедленно: для атомарного результата отделить запись строки аудита от публикации после commit минимальным дополнением существующего сервиса, сохранив поведение других вызовов. Дубликат квитанции не создаёт второй аудит/feed.

RED: read не может менять; moderate не получает admin-операции; admin не может установить true; цвет не включает; архивный force-disable необратим через rejoin/replay; проверка CSRF и CLI-маршрутов. GREEN: тонкие DTO/маршруты над теми же транзакционными правилами. Для REST задать конечный body limit 4 КиБ и read/mutate rate limits по тем же числам, не менять глобальные лимиты сервера.

```powershell
rtk proxy dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceAdminTests|FullyQualifiedName~AdminCommandTests'
```

Expected: read-проекция совпадает с gRPC по составу, административный обход согласия невозможен, CLI и аудит проверены. Общие ворота. Commit message: `Добавить управление влиянием в REST и CLI`.

### Задача 5. Транспорт клиента, outbox и кеш

Зависимости: 2, 3.

Существующие файлы: `src/Rimlink.Client.Core/Net/RimlinkConnection.cs`, `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`, `src/Rimlink.Client.Core/State/SharedState.cs`, `src/Rimlink.Client/UI/LoginFlow.cs`, `src/Rimlink.Client/Game/RimlinkSession.cs`; образцы `src/Rimlink.Client.Core/Social/CommunityClient.cs`, `CommunityCache.cs`, `src/Rimlink.Client/Game/CommunitySync.cs`.

Новые файлы: `src/Rimlink.Client.Core/Influence/InfluenceClient.cs`, `InfluenceCache.cs`; `tests/Rimlink.Client.Tests/Influence/InfluenceClientTests.cs`, `InfluenceCacheTests.cs`. Дополнить `tests/Rimlink.Client.Tests/Economy/TransferOutboxTests.cs`.

Интерфейсы: InfluenceClient.Get/GetSetting/Submit/Mutate; InfluenceCache.Invalidate, BeginFetch, TryAcceptCandidate, TryPublish, Reset. Точный вид токена — небольшой неизменяемый объект с подключением/local epoch, world/process, invalidation counter и candidate revision. В SharedState применить новое событие и Reset. В RimlinkConnection добавить typed stub, в OutboxClients — необязательный InfluenceClient, в TransferOutbox — Add/Decode/CanReplay/Attempt/NothingToReturn для нового вида. Обновить вызов Replay в LoginFlow.LoadEconomy; отсутствие API не блокирует обычный login.

High-water отдельно от Current.Revision. Любая инвалидация увеличивает счётчик. Pending fetch завершается, но не очищает возникшую потребность нового запроса; повтор после задержки, без параллельных запросов. Проверять token при ответе и при публикации готовой геометрии. При смене server/world/process/context отменять кандидат; reconnect включает полный refresh. Unimplemented показывает недоступность слоя. Квитанция старого enable не выставляет enabled: настройку перечитать. Не блокировать законное отключение только из-за незавершённого старого включения.

RED: r9→fetch r10→event r11→response r10; без revision; инвалидация после ответа до TryPublish; coalescing; новая сессия/epoch; отказ/успех replay, пустые операции ничего не возвращают в колонию; неизвестный RPC. GREEN: повторить принцип CommunityCache/Sync с двумя дополнительными барьерами, без нового общего механизма синхронизации проекта.

```powershell
rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceClientTests|FullyQualifiedName~InfluenceCacheTests|FullyQualifiedName~TransferOutboxTests'
```

Expected: stale-ответ/меш никогда не становится актуальным, старые намерения завершаются корректно, остальные виды outbox не меняют поведение. Общие ворота. Commit message: `Добавить клиентский кеш и outbox влияния`.

### Задача 6. Детерминированный расчёт влияния

Зависимости: 1, 5.

Новые файлы: `src/Rimlink.Client.Core/Influence/InfluenceGraph.cs`, `InfluenceCalculator.cs`; `tests/Rimlink.Client.Tests/Influence/InfluenceCalculatorTests.cs`, `InfluenceBudgetTests.cs`.

Интерфейс: Calculate получает валидированный полный снимок и неизменяемые данные небольшого графа: ID, соседей и water flag. Выход содержит силу/претендентов/собственный источник и данные подписи; цвет назначается по союзу либо состоянию спорности. На входе ограничить размер, количество источников/союзов, уникальность ID/центров, ссылки, радиус, версию алгоритма, степень графа. Ошибка делает весь кандидат непригодным.

RED: вода/перешеек/край, максимум вместо суммы, собственный тайл, 2–3 равных претендента, перестановки входов, удаление, пустой снимок, cap, некорректный источник и граф. Для каждого претендента — минимальная сухопутная дистанция среди достигающих источников; при равенстве минимальный стабильный ID только в подписи. GREEN: ограниченный BFS по каждому источнику с объединением максимумов; не добавлять универсальную географическую библиотеку.

```powershell
rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~InfluenceCalculatorTests|FullyQualifiedName~InfluenceBudgetTests'
```

Expected: одинаковый результат при любом порядке источников/соседей, корректная спорность, обход в рассчитанном бюджете. Это не игровой performance benchmark. Общие ворота. Commit message: `Добавить расчёт зон влияния союзов`.

### Задача 7. Игровая metadata, карта, UI и локализация

Зависимости: 3, 5, 6.

Существующие файлы: `src/Rimlink.Client/Game/Patches/Patch_WorldObjectsHolder_Add.cs`, `src/Rimlink.Client/Game/RimlinkGameComponent.cs`, `src/Rimlink.Client/Game/RimlinkSession.cs`, `src/Rimlink.Client/RimlinkMod.cs`, `src/Rimlink.Client/UI/Dialog_Community.cs`, `src/Rimlink.Client/UI/LoginFlow.cs`. `ForeignSettlements.cs` использовать как источник текущих маркеров, не менять владельцев и не создавать иностранный маркер собственной колонии.

Новые файлы: `src/Rimlink.Client/Game/Influence/InfluenceSync.cs`, `InfluenceSurfaceCapture.cs`, `InfluenceLayerController.cs`, `WorldDrawLayer_Influence.cs`, `InfluenceDebugActions.cs`; `src/Rimlink.Client/Game/Patches/Patch_WorldInterface_WorldInterfaceOnGUI_Influence.cs`; `src/Rimlink.Client/UI/InfluenceOverlay.cs`; `mod/Languages/English/Keyed/Influence.xml`, `mod/Languages/Russian/Keyed/Influence.xml`.

Metadata: в патче регистрации снять исходный PlanetTile и подтверждение Surface **до** существующего int-преобразования. После успешной регистрации и получения ID отправить отдельную surface-мутацию с актуальными контекстом/версией. Ошибка этого второго шага оставляет поселение зарегистрированным, но неподтверждённым. Для загруженной legacy-колонии Add не вызывается: на главном потоке при явном действии владельца найти реальное собственное Settlement загруженной колонии, проверить Surface до int и сверить серверный контекст. Ни WorldObject.Tile, ни int из сейва сами не подтверждают поверхность. При неоднозначности не подтверждать. Surface=false аннулирует прежнее согласие; новая валидная metadata сама enable не делает.

UI: настройка публикации и объяснение раскрываемой информации в Dialog_Community, цвет доступен владельцу союза; отдельно слой/легенда на карте. В RimlinkSettings сохранить локальную видимость по ServerEndpoint.Id. Легенда, спорность, ближайшие источники, «Ваш союз», loading/stale/unavailable/empty, конфликт и причина restore — EN/RU. Отключение не считается успешным при конфликте. Надписи и контуры дополняют цвет, обычные клики не перехватываются.

Lifecycle: Sync.Tick из существующего GameComponentUpdate, подключение слоя только к Surface; снятие графа и мешей порциями из предыдущего раздела. Проверить актуальность токена непосредственно перед заменой списка. На отключении слоя отменять лишнюю работу; на End/новом мире/одиночной игре удалить свой слой, освободить кандидат, меши и материалы, очистить ссылки. Изменение цвета может переиспользовать согласованную геометрию. Смена поверхности/орбиты не публикует меши чужого слоя.

RED: отметить отсутствие слоя и metadata; подготовить управляемый через dev actions случай инвалидации между готовым кандидатом и swap. GREEN: сборка плюс настоящие сценарии задачи 8. Обработку race/barriers проверить через тестируемый InfluenceCache из задачи 5; не выдавать эти тесты за проверку игрового рендера.

```powershell
rtk proxy powershell -NoProfile -File .\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld' -OutputDir 'D:\Build\rimlink-influence-20261102\build\Rimlink'
rtk proxy dotnet test tests/Rimlink.Client.Tests -nologo -v q --filter 'FullyQualifiedName~Influence'
```

Expected: мод собирается, core-тесты зелёные; игровая часть остаётся непроверенной до задачи 8. Общие ворота. Commit message: `Добавить слой влияния и настройки публикации`.

### Задача 8. Изолированная игровая приёмка и замеры

Зависимости: 4, 7.

Существующие файлы: `tools/Rimlink.BridgeTools/StatusTools.cs`, `docs/manual-checklist.md`; использовать `scripts/Build-ClientMod.ps1`, `scripts/Build-BridgeTools.ps1` и образцы `tools/Rimlink.BridgeTools/Support/Bridge.cs`, `WorldTools.cs`.

Новые файлы будущей реализации: `tools/Rimlink.BridgeTools/InfluenceTools.cs`, `docs/verification/2026-11-02-guild-influence-acceptance.md`. Только при необходимости для cap — `tests/Rimlink.Server.Tests/Influence/InfluenceStandSeedTests.cs`: явный opt-in тестовый наполнитель отдельного DataDir, не production-эндпоинт и не часть обычного запуска.

Новые dev tools: состояние слоя/epoch/high-water/counter/candidate, применённая revision, число источников/клеток/мешей и замеры; публикация/цвет через те же клиентские методы; остановка кандидата перед swap и продолжение для воспроизводимой гонки. Подтверждать результат у второго клиента и по серверному снимку, не только `success` инструмента. Компаньон — dev-only, в релиз не попадает.

Безопасный стенд: отдельный корень `D:/Build/rimlink-influence-20261102`, копия игры в `game`, собственные `host-data`, `guest-data`, `server-data`, `gabs`. Предлагаемый порт 25630 проверяется на свободу **при выполнении**, сейчас его доступность не заявлена. Не менять общий GABS config; использовать отдельный configDir, если он поддерживается установленной версией, либо заранее выделенный свободный acceptance-стенд с доказанным владельцем и отсутствием активного прогона. Не останавливать чужие процессы и не переиспользовать занятый стенд. Перед стартом записать PID/порты/пути DLL и резервировать стенд; Steam/Workshop в рамках этого плана не настраивать заново.

Команды сборки для будущего стенда, только после подготовки копии и при закрытых **своих** игровых экземплярах:

```powershell
rtk proxy powershell -NoProfile -File .\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-influence-20261102\game' -OutputDir 'D:\Build\rimlink-influence-20261102\build\Rimlink' -InstallTo 'D:\Build\rimlink-influence-20261102\game\Mods'
rtk proxy powershell -NoProfile -File .\scripts\Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-influence-20261102\game'
```

RED→GREEN игровой проверки:

1. Два клиента, реальные колонии A/B, два разных союза. Оба владельца явно включают публикацию. Проверить совпадающие снимки/цвета, собственный источник, спорный тайл, выбор ближайших источников, легенду и отсутствие влияния на игровые права.
2. Третью настоящую колонию C без союза создать последовательно на одном из тех же двух клиентов, сохранив A офлайн. Разместить личную C внутри зоны A: её владелец, название и доступ не меняются, A остаётся источником офлайн. Затем вернуться к A/B.
3. Enable/disable и replay, archive/restore, leave/rejoin, disband, admin force-disable, cap restore и смена цвета: сверить изменения на втором клиенте. Инвалидация r11 при r10 в полёте и отдельно перед swap — старый кандидат не применяется.
4. Реальная поверхность/орбита с одинаковым tileId: орбитальный объект не создаёт поверхностный источник. Если нужный слой недоступен в составе игры стенда, записать конкретный непроверенный сценарий, не заменять его core-тестом и не объявлять всю игровую приёмку полной.
5. UI 100%/150%, EN/RU, приближение/отдаление: дороги, реки, пинги, выделение и клики доступны; спорность читается без различения цветов. Disconnect/reconnect, новый process epoch с меньшей revision, другой мир, одиночный сейв и повторная загрузка не оставляют чужую геометрию.
6. На 50 и 128 источниках измерить protobuf/JSON bytes, время полного пересчёта, время порций и финализации, p50/p95/max кадра, CPU/GPU-оценку памяти при двух версиях. Использовать одинаковую камеру и 30 секунд наблюдения до/после, затем серию из 10 обновлений. Для cap допустимы явно обозначенные синтетические офлайн-источники только в изолированной БД с координатами, снятыми из настоящего Surface; это проверка реального рендера полного снимка, не заявление о 128 одновременно сыгранных колониях. Семантику согласия отдельно доказывают реальные A/B/C и серверные тесты.

Expected: сценарии и численные критерии выполнены, доказательства сохранены с версиями и настройками стенда; непроверенные условия помечены отдельно. `rimlink/status.clientAssembly` указывает на Mods/Rimlink выбранной копии. Копии launch.log сохранить до повторного старта; учитывать общий Player.log. Завершить только собственные процессы стенда. Общие ворота. Commit message: `Добавить приёмку влияния на отдельном стенде`.

### Задача 9. Документация, версия, общий review и один fix pass

Зависимости: 1–8, включая результаты настоящей приёмки.

Существующие файлы: `README.md`, `docs/player-guide.md`, `docs/server-setup.md`, `docs/manual-checklist.md`, `deploy/appsettings.example.json`, `Directory.Build.props`, `src/Rimlink.Client.Core/ClientInfo.cs`, `mod/About/About.xml`. Дополнить созданный в задаче 8 отчёт приёмки. Утверждённую спеку, записку её fix pass и research не переписывать результатами реализации.

Документировать публикацию/отзыв, offline/cap, архивы/rejoin, Surface-only, спорность, CLI/REST scopes, ошибки и порядок восстановления после превышения. Пределы фиксированные: не добавлять фиктивные ключи в appsettings.example.json; менять его только если реализация действительно добавила согласованный конфигурационный ключ. Версию повысить один раз с проверенной 0.7.0 до 0.8.0 во всех трёх источниках; если основная сессия уже повысила версию параллельно, согласовать единый следующий номер в ledger без отката её изменений. ProtocolVersion не повышать при сохранении совместимости.

RED→GREEN: сначала отметить отсутствующие инструкции и несоответствия документированного поведения фактическим тестам; затем обновить документы и проверить единообразие версии/EN-RU/ссылок. После готовности всей ветки — **одно** общее review Astra по Review Focus и ledger всей реализации, без делегации. Основная сессия GPT-6.1 Sol делает **один** fix pass: каждый подтверждённый дефект воспроизвести целевым тестом/игровым сценарием, исправить и перепроверить затронутое. Повторного независимого обзора не запускать. Незакрытые существенные замечания блокируют заявление о готовности; minor явно перечислить.

```powershell
rtk proxy dotnet build Rimlink.sln
rtk proxy dotnet test Rimlink.sln -nologo -v q
rtk proxy powershell -NoProfile -File .\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-influence-20261102\game' -OutputDir 'D:\Build\rimlink-influence-20261102\build\Rimlink'
```

Expected: solution и мод собираются, suite зелёная, игровая приёмка и замеры представлены, документы соответствуют поведению, единственный review/fix pass отражён в ledger. При timing-fix применить существующий workflow dotnet-linux-tests-wsl с полным прогоном на двух CPU; не считать обычную Windows suite доказательством устранения Linux timing. Commit message документации: `Описать влияние союзов и обновить версию`. Для исправлений review — отдельные короткие русские сообщения по фактической причине, без attribution и без выдуманного номера задачи.

Финальный отчёт основной сессии: выполненные задачи, конкретные команды/результаты, игровой стенд и измерения, rulings/непроверенные сценарии/оставшиеся minor. Merge, push, релиз, установка в обычную игру и обновление рабочего сервера — только по отдельной команде пользователя.

## Готовность плана и границы текущей работы

План содержит девять задач и готов к исполнению основной сессией. Блокирующих продуктовых вопросов нет. Проверка текущего состояния стенда, точного расположения UI и внутренних деталей методов оставлена соответствующим задачам реализации; это не новый этап исследования спеки.

При подготовке плана читались исходники и API установленной игры; игры, серверы, сборки, автоматические тесты и production не запускались. Исходники, тесты, proto, SQL/миграции, настройки, ветки и коммиты автор плана не меняла. Изменён только этот Markdown; новая спека GOG в данный этап не входит.
