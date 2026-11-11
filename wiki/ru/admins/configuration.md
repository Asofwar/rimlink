---
pageId: admins/configuration
contentRevision: 14
---

# Справочник конфигурации

Применимость: Rimlink 0.10.2 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Настройки привязываются к корневым секциям JSON. Переменная окружения заменяет двоеточия двойными подчёркиваниями и добавляет префикс Rimlink__: например Rimlink__Server__Port=25560, Rimlink__Auth__AdminLogins__0=admin. Для массивов используются индексы, не строка через запятую.

Настрой окружение службы/контейнера и перезапусти сервер после изменения: руководство не обещает hot reload. Значения ниже — инициализаторы Options, а не пример с заполненными секретами. Пример deploy/appsettings.example.json полезен как шаблон, но не является исчерпывающим контрактом (например, SelfSignedPath отсутствует).

Полный реестр ключей приведён ниже. Суффикс не заменяет описание: Hygiene:ArchiveAfterDays — реальные дни отсутствия, Hygiene:ArchiveMaxColonyDays — игровые дни колонии, NewbieProtectionDays — реальные дни с первого сохранения. Ticks — игровые тики, Bytes — байты. Сохраняй единицы при переносе настроек.

## Как изменить параметр без угадывания единиц? {#config-method}

Найди точный ключ в таблице и прочти пояснение его группы. JSON использует Section/Key, переменные — Rimlink__Section__Key; элементы массива задаются индексами. Сохрани рабочую конфигурацию и меняй один связанный набор параметров на тестовом сервере. При старте Options validation может остановить процесс: исправляй указанное значение, не удаляй базу.

Не считай пример исчерпывающим набором: таблица сверяется с Options-классами, а константы механик отдельно. Значение 0 не универсальное «выключить»: Backup:IntervalHours=0 отключает расписание, другие поля валидируются иначе. Не обещай эффект от Raids:TimeoutDays — ключ объявлен, но текущий клиент использует константу.

После изменения проверь фактическое поведение и безопасный /api/admin/config. Значения секретов там скрыты; это не повод выгружать полное окружение в тикет.

## Ограничения и безопасность {#limits}

Admin:ApiToken, Admin:Tokens:*:Token, Auth:SteamWebApiKey и Server:ServerPassword — секреты. FeedBufferSize 100–100000, FeedPingSeconds 1–300; gift/trade сроки и MaxOpenTrades >=1. Integrity пороги неотрицательны, Freeze не ниже Warn кроме 0. Hygiene: sweep 1–168 ч, неотрицательные возраст/пороги и строго возрастающие 4 уровня. Incidents каталог требует уникальных id, допустимого Kind и положительных множителей; непустой список заменяет весь встроенный каталог.


GuildCapture.BeginDelivery и GuildCapture.Manifest закрыты проверкой готовности: включённая настройка, активированный V3 и состояние запуска Ready. Новый отдельный executor handshake настраивается секцией GuildCapture ниже; он не открывает имущественные операции. Без квалификации ресурсные RPC возвращают GuildUnavailable.

## См. также {#related}

- [TLS, вход и права](./security-auth.md)
- [Игровая политика сервера](./gameplay-policy.md)

## Все привязываемые ключи {#reference}

Таблица получена из объявлений Options, а не из примерного JSON. `null` означает отсутствие значения; строки `""` пусты; `[]` — пустой массив. Выражение `Protocol.Limits.*` задаёт константу протокола: MaxParcelBytes = 921600, MaxItemsPerParcel = 200, MaxPawnsPerParcel = 10. Ключи с `*` индексируются начиная с 0.

| Ключ | Тип | Значение по умолчанию |
|---|---|---|
| `Panel:Enabled` | `bool` | `true` |
| `Panel:Public` | `bool` | `true` |
| `Panel:PublicPlayerNames` | `bool` | `true` |
| `Panel:SessionHours` | `int` | `12` |
| `Panel:ServerName` | `string` | `""` |
| `Panel:Description` | `string` | `""` |
| `Panel:PublicAddress` | `string` | `""` |
| `Server:Port` | `int` | `25560` |
| `Server:DataDir` | `string` | `"./data"` |
| `Server:MaxPlayers` | `int` | `50` |
| `Server:ServerPassword` | `string?` | `null` |
| `Server:MaxSettlementsPerPlayer` | `int` | `1` |
| `Tls:Required` | `bool` | `true` |
| `Tls:CertificatePath` | `string?` | `null` |
| `Tls:KeyPath` | `string?` | `null` |
| `Tls:SelfSignedPath` | `string?` | `null` |
| `Auth:AllowPassword` | `bool` | `false` |
| `Auth:SteamWebApiKey` | `string?` | `null` |
| `Auth:AdminSteamIds` | `long[]` | `[]` |
| `Auth:AdminLogins` | `string[]` | `[]` |
| `Economy:Enabled` | `bool` | `true` |
| `Economy:MaxParcelBytes` | `int` | `Protocol.Limits.EconomyMaxParcelBytes` |
| `Economy:MaxItemsPerParcel` | `int` | `Protocol.Limits.EconomyMaxItemsPerParcel` |
| `Economy:MaxPawnsPerParcel` | `int` | `Protocol.Limits.EconomyMaxPawnsPerParcel` |
| `Economy:MaxLotsPerPlayer` | `int` | `20` |
| `Economy:MaxPendingParcels` | `int` | `50` |
| `Economy:LotLifetimeDays` | `int` | `30` |
| `Economy:MarketFeePercent` | `int` | `0` |
| `Economy:SendCooldownSeconds` | `int` | `5` |
| `Economy:GiftDecisionDays` | `int` | `7` |
| `Economy:TradeLifetimeDays` | `int` | `7` |
| `Economy:MaxOpenTrades` | `int` | `5` |
| `Economy:ServerWealthPercent` | `int` | `100` |
| `Economy:BlockOutflowUnderThreat` | `bool` | `true` |
| `Economy:StorageFeePercentPerDay` | `double` | `0.5` |
| `Economy:StorageFreeDays` | `double` | `3` |
| `Social:MaxBlocks` | `int` | `200` |
| `Raids:Enabled` | `bool` | `true` |
| `Raids:MaxRaiders` | `int` | `10` |
| `Raids:MaxActiveRaidsPerAttacker` | `int` | `1` |
| `Raids:TargetCooldownHours` | `int` | `8` |
| `Raids:PairCooldownHours` | `int` | `24` |
| `Raids:NewbieProtectionDays` | `int` | `3` |
| `Raids:MinColonyWealth` | `long` | `20000` |
| `Raids:LootCapPercent` | `int` | `10` |
| `Raids:LootCapSilver` | `long` | `5000` |
| `Raids:AllowKidnap` | `bool` | `false` |
| `Raids:WarnTicks` | `int` | `60000` |
| `Raids:TimeoutDays` | `int` | `3` |
| `Raids:ExpireRealDays` | `int` | `7` |
| `Raids:PeacefulOffDelayHours` | `int` | `24` |
| `Raids:VisitHours` | `int` | `12` |
| `Live:Enabled` | `bool` | `true` |
| `Live:InviteSeconds` | `int` | `60` |
| `Live:ReconnectWaitMinutes` | `int` | `5` |
| `Live:GuestPauses` | `int` | `10` |
| `Live:GuestPauseSeconds` | `int` | `60` |
| `Live:MaxFrameBytes` | `int` | `262144` |
| `Live:DeltasPerSecond` | `int` | `20` |
| `Live:OrdersPerSecond` | `int` | `30` |
| `Live:HeartbeatTimeoutSeconds` | `int` | `10` |
| `Live:SweepSeconds` | `int` | `5` |
| `Admin:ApiToken` | `string?` | `null` |
| `Admin:Tokens` | `List<ApiTokenOptions>` | `[]` |
| `Admin:FeedBufferSize` | `int` | `1000` |
| `Admin:FeedPingSeconds` | `int` | `15` |
| `Admin:Tokens:*:Name` | `string` | `""` |
| `Admin:Tokens:*:Token` | `string` | `""` |
| `Admin:Tokens:*:Scopes` | `string[]` | `[]` |
| `Integrity:DevMode` | `DevModePolicy` | `DevModePolicy.AdminsOnly` |
| `Integrity:WealthWarnPerDay` | `long` | `20000` |
| `Integrity:WealthFreezePerDay` | `long` | `100000` |
| `Integrity:StorytellerPolicy` | `StorytellerPolicy` | `StorytellerPolicy.Locked` |
| `Hygiene:ArchiveAfterDays` | `int` | `14` |
| `Hygiene:ArchiveMaxWealth` | `long` | `100000` |
| `Hygiene:ArchiveMaxColonyDays` | `int` | `60` |
| `Hygiene:SweepHours` | `int` | `6` |
| `Hygiene:WealthTiers` | `long[]?` | `null` |
| `Incidents:Enabled` | `bool` | `true` |
| `Incidents:MaxQueuedPerTarget` | `int` | `2` |
| `Incidents:BasePrice` | `double` | `100` |
| `Incidents:MinPrice` | `long` | `50` |
| `Incidents:WarnTicks` | `long` | `30000` |
| `Incidents:WarnFromLevel` | `int` | `3` |
| `Incidents:KindCooldownHours` | `double` | `1` |
| `Incidents:ExpireRealDays` | `double` | `7` |
| `Incidents:Catalog` | `List<IncidentCatalogEntry>` | `[]` |
| `Incidents:Catalog:*:Id` | `string` | `""` |
| `Incidents:Catalog:*:IncidentDef` | `string` | `""` |
| `Incidents:Catalog:*:FactionDef` | `string?` | `null` |
| `Incidents:Catalog:*:Kind` | `string` | `"Hostile"` |
| `Incidents:Catalog:*:PriceMultiplier` | `double` | `1` |
| `Incidents:Catalog:*:PodsMultiplier` | `double?` | `null` |
| `Saves:CompactOldVersions` | `bool` | `true` |

`Tls:SelfSignedPath=null` выбирает `<DataDir>/tls/server.pfx`. `Hygiene:WealthTiers=null` выбирает `[50000,150000,400000,1000000]`. Panel:ServerName пустой показывает Rimlink, SessionHours ограничивается 1–168, Description обрезается до 1000 символов. `Saves:CompactOldVersions=false` прекращает создание новых дельт, но не удаляет старые. `Server:ProtocolVersion` вычисляется, это не конфигурация.

### Backup

| Ключ | Тип | По умолчанию |
|---|---|---|
| `Backup:IntervalHours` | `int` | `24` |
| `Backup:Keep` | `int` | `7` |

IntervalHours <= 0 отключает расписание, Keep сохраняет хотя бы одну новейшую копию. Ручной backup не заменяет проверку восстановления.

### Что регулируют секции

Server задаёт порт, каталог, вместимость и пароль общего доступа, не пароль аккаунта. Tls выбирает пару PEM certificate/key или сохранённый самоподписанный сертификат. Auth разрешает парольный вход и назначает администраторов по Steam ID/login; SteamWebApiKey проверяет билеты Steam.

Economy включает обмен и ограничивает посылки (суммарные bytes/items/pawns), лоты/ожидающие доставки, сроки хранения, комиссию рынка и частоту отправки. GiftDecisionDays и TradeLifetimeDays возвращают escrow после истечения решения/сделки. ServerWealthPercent учитывает серверные активы в богатстве, BlockOutflowUnderThreat блокирует вывод под угрозой; StorageFeePercentPerDay после StorageFreeDays — плата за пешек и скоропортящееся. Social:MaxBlocks ограничивает личный список блокировки.

Raids регулирует доступность, число атакующих/активных рейдов, кулдауны цели/пары, защиту новичка и минимальное богатство. LootCapPercent и LootCapSilver ограничивают добычу; AllowKidnap — похищение; WarnTicks — игровое предупреждение; TimeoutDays объявлен, но не подключён к поведению: текущий клиент использует константу 3 × 60000 игровых тиков; ExpireRealDays — реальный срок, PeacefulOffDelayHours — задержка выхода из мирного режима, VisitHours — срок визита.

Live задаёт срок приглашения/ожидания восстановления, бюджет пауз гостя, bytes кадра, частоты delta/order и контроль молчания канала. Admin задаёт legacy token панели и именованные token/scopes, размер повтора SSE и интервал ping. Integrity ограничивает dev mode, скачки богатства и storyteller; 0 отключает соответствующий порог. Hygiene архивирует только подходящие по отсутствию, возрасту и богатству колонии, 0 снимает соответствующий фильтр (ArchiveAfterDays=0 выключает sweep).

Incidents включает платные события, очередь на цель, BasePrice/MinPrice, предупреждение от WarnFromLevel, кулдаун по Kind и срок исполнения. Catalog:* задаёт Id, IncidentDef, необязательный FactionDef, Kind (Hostile/Weather/Kind), PriceMultiplier и необязательный PodsMultiplier. Не обещай поддержку def из отсутствующего мода.

### Outposts

Enabled по умолчанию false и не заменяет acceptance qualification. AnchorDirectory должен находиться вне заменяемых data trees; sqlite-local-v1 допускает только проверенный локальный backend. Linux требует фактически квалифицированный ext4/xfs/btrfs, Windows — fixed NTFS. Секреты и runtime-флаги не могут включить IsolatedAcceptance или Qualified. [Подробности](./passive-outposts.md).

| Key | Type | Default |
|---|---|---|
| `Outposts:Enabled` | `bool` | `false` |
| `Outposts:AnchorDirectory` | `string` | `""` |
| `Outposts:StorageContract` | `string` | `""` |
| `Outposts:QualifiedLinuxFileSystem` | `string` | `""` |


### GuildCapture

Enabled по умолчанию false. Для работы Product нужны явное включение, отдельные записи общей и гильдейской квалификации точных исходников и проверенная authority V3 с активным миром без происхождения из изолированного тестового стенда. Штатный сервер отклоняет активацию V3 до появления обеих записей и явного включения. Печать квалификации сборки снята в 0.10.0: возможность включается настройкой сервера и активацией формата данных, а проверки состояния запуска, формата, схемы и хранилища действуют как прежде. ExecutorId — стабильное имя одного слота. ExecutorTokenSha256 — SHA-256 отдельного секрета арбитра в UTF-8. ApprovedProfileDigest — доменный хеш утверждённого профиля арбитра. При Enabled=true нужны непустой ASCII-идентификатор (буквы, цифры, точка, дефис, подчёркивание) и два хеша по 64 шестнадцатеричных символа. Некорректная конфигурация останавливает запуск.

Офлайн-команда `scripts/Seal-OutpostQualification.ps1 -GuildCapture` осталась инструментом разработки: она сверяет файлы доказательств с идентификатором исходников сборки, но сервер её результат больше не требует. Нужны успешные HISTORY, HOME, SLICE, RESTORE, проверки отказов и производительности, паритета операторских команд, упаковки и ревью. Режим Candidate записывает Pending в свой выходной файл. Запись квалификации не меняет работающий сервер и не активирует V3.

| Key | Type | Default |
|---|---|---|
| `GuildCapture:Enabled` | `bool` | `false` |
| `GuildCapture:ExecutorId` | `string` | `""` |
| `GuildCapture:ExecutorTokenSha256` | `string` | `""` |
| `GuildCapture:ApprovedProfileDigest` | `string` | `""` |

При `GuildCapture:Enabled=false` обычный сервер отказывает executor transport. Только отдельный evidence-backed dev-host с реальным common V3 readiness и scoped guild admission может открыть его при OFF; обязательная dedicated конфигурация проверяется и в этом случае. Это разрешение HOME/SLICE не является Product qualification.
