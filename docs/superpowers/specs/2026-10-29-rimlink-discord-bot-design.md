# Rimlink — Discord-бот: дизайн

Дата: 2026-10-29. Статус: утверждён в обсуждении, ждёт план.
Основа: `2026-10-28-rimlink-core-design.md` (ядро: «после ядра отдельными подпроектами идут веб-панель
администратора и Discord-бот; оба работают через REST-API ядра»), экономика, рейды и визиты (3a), живые
сессии (3b). Протокол расширяется только добавлением (новое значение enum, новое поле).

## 1. Цель и границы

Бот связывает сервер Rimlink с Discord-сообществом. Он публикует события сервера в каналы, даёт
модераторам и админам команды из Discord, соединяет публичный чат игры с каналом Discord в обе стороны и
отвечает любому участнику на простые запросы (кто онлайн, статус, рынок). Бот — отдельный процесс; с
сервером он говорит только через REST под `/api/admin/*`.

Решения, принятые в обсуждении:

| Вопрос | Решение |
|---|---|
| Роли бота | Все четыре: уведомления о событиях, админ-команды, мост чата в обе стороны, публичные запросы |
| Привязка Discord ↔ игрок | Нет в этом подпроекте: уведомления только в каналы, строки из Discord в игре подписаны «[Discord] Ник», права — по ролям Discord |
| Группы событий | `server` (сервер и игроки), `raids` (рейды, визиты, live), `market` (рынок), `moderation` (журнал аудита, закрытый канал). Каждая группа — свой канал или выключена. Личное (кошелёк, посылки, ЛС) не публикуется никогда |
| Канал событий | Подход A: SSE-поток `GET /api/admin/events` с кольцевым буфером и догоном по `Last-Event-ID`. Без таблицы в БД |
| Права | Две настраиваемые роли Discord: Moderator и Admin (Admin включает Moderator). Необратимые команды — только Admin и только с кнопкой подтверждения |
| Стек | .NET 10, Discord.Net 3.20.x, отдельный проект `src/Rimlink.Discord` в `Rimlink.sln`, без ссылки на `Rimlink.Server` |
| Деплой | Отдельный контейнер `rimlink-discord` в том же `docker-compose.yml` |
| Анонс рейда | `raid.queued` в ленту не идёт: цель не должна узнать о рейде из Discord раньше игры. Публикуются высадка и итог |

Сервер видит каждый вызов бота как админа (`Actor.Api`): проверка прав целиком на боте. Это граница
привилегий, и спецификация её фиксирует (раздел 5).

## 2. Сервер: лента админ-событий

### Модель

`FeedEvent { id, at, type, data }`, сериализуется в JSON (camelCase). `id` — строка `"<epoch>:<seq>"`:
`epoch` — момент старта процесса сервера (Unix-секунды), `seq` — счётчик с 1 внутри процесса.
Первое событие каждого процесса — `server.started`.

`AdminFeed` (singleton, `Application/Feed/AdminFeed.cs`):
- кольцевой буфер последних `Admin:FeedBufferSize` событий (по умолчанию 1000);
- `Publish(type, data)` — не блокирует вызывающего, не бросает: присваивает `id`, кладёт в буфер,
  пишет в канал каждого подписчика;
- подписчик — ограниченный канал на `FeedBufferSize` событий; не успевает — канал закрывается,
  подписчик отключается (как отставшая игровая сессия в `EventQueue`) и догоняет из буфера по
  `Last-Event-ID`;
- метрика `rimlink_admin_feed_subscribers` (gauge).

### Эндпоинт

`GET /api/admin/events` — SSE (`text/event-stream`), тот же фильтр токена, что у всей группы.
- Точка продолжения: заголовок `Last-Event-ID` или `?after=<id>`; нет ни того, ни другого — только новые
  события.
- `epoch` в `id` не совпадает с текущим, или `seq` уже вытеснен из буфера → первым кадром идёт
  `event: gap` (`data: {"from": "<id>"}`), затем весь буфер.
- Кадр события: `id: <id>`, `event: <type>`, `data: <json>`.
- Каждые 15 с — комментарий `: ping`, чтобы прокси не рвали соединение, а клиент видел живость.

### Где публикуются события

Явные вызовы `feed.Publish(...)` в месте, где событие происходит, после коммита транзакции. Не перехват
`EventBus`: игровое событие адресовано одному игроку и несёт неполные данные (у `LotSold` нет продавца),
`SendTo` пропускает офлайн-игроков, а у части событий (выставлен лот) игрового события нет вовсе.

| Группа | `type` | `data` (игрок = `{id, name}`) | Источник |
|---|---|---|---|
| server | `server.started` | `{version}` | старт `AdminFeed` (hosted service) |
| server | `player.joined`, `player.left` | `{player, faction}` | `PresenceNotifier` |
| server | `colony.founded` | `{player, colony, tile}` | `WorldService` (новый `Settlement`) |
| server | `colony.removed` | `{player, colony}` | `WorldService`, `AdminOperations.RemoveObjectsOf` |
| server | `notice` | `{text}` | `AdminOperations.Broadcast` |
| server | `server.shutdown` | `{inSeconds, reason}` | `AdminOperations.Shutdown` |
| server | `economy.frozen`, `raids.frozen` | `{frozen, reason}` | `AdminOperations.SetEconomyFrozen` / `SetRaidsFrozen` |
| raids | `raid.landed` | `{raidId, attacker, target}` | `RaidService` (высадка) |
| raids | `raid.resolved` | `{raidId, attacker, target, dead, captured, fled, alive, lootValue, captives}` | `RaidService` (итог) |
| raids | `visit.started` | `{visitor, host}` | `VisitService` |
| raids | `live.started`, `live.ended` | `{sessionId, kind: raid\|visit, host, guest, reason?}` | `LiveSessionService` |
| market | `lot.posted` | `{lotId, seller, price, summary}` | `MarketService.Post` |
| market | `lot.sold` | `{lotId, seller, buyer, price, summary}` | `MarketService.Buy` |
| market | `shop.sold` | `{host, buyer, item, count, price}` | `ShopService` |
| moderation | `admin.action` | `{action, actor, via, target?, details}` | `AdminOperations.Audit` |
| chat | `chat.message` | `{id, kind: player\|system\|external, from?, author?, text}` | `ChatService` (только публичные строки) |

`summary` лота — строка из `ItemBlob.label` и `count` («Steel ×50, Component ×3») или ярлык пешки,
не длиннее 200 символов (дальше «…»). Собирается одной функцией, ею же пользуется `GET /economy/lots`.

ЛС чата (`ToPlayerId != null`), ответы на `/`-команды, кошельки, посылки и `raid.queued` в ленту не
попадают.

## 3. Сервер: остальные REST-дополнения

- **Атрибуция.** Необязательный заголовок `X-Rimlink-Actor` (до 64 символов, без управляющих символов;
  иначе 400). REST-вызов выполняется от `Actor.Api` с именем из заголовка (`api`, если его нет).
  `AdminOperations.Audit` дописывает к `Details` суффикс ` [via <имя>]` для REST-вызовов и пишет имя в лог.
  `ActorAccountId` остаётся 0. Поле `via` в `admin.action`: имя из заголовка, `api` или имя игрока,
  выполнившего `/`-команду. Миграции для атрибуции нет.
- **Игроки.** `GET /api/admin/players?name=<префикс>&limit=<1..25>` (по умолчанию 25) — регистронезависимый
  префиксный поиск, точное совпадение первым: `{id, accountId, name, faction, online, role, mutedUntil,
  peaceful, colonies: [name]}`.
- **Статус.** `GET /api/admin/status` → `{version, protocolVersion, startedAt, online, maxPlayers,
  worldCreated, economyFrozen, economyFreezeReason, raidsFrozen, raidsFreezeReason}`.
- **Чат.**
  - `GET /api/admin/chat?after=<id>&limit=<1..200>` — публичные строки по возрастанию `id`:
    `{id, kind, from?, author?, text, at}`. Для панели и ручной проверки; бот в обычной работе берёт чат из ленты.
  - `POST /api/admin/chat {author, text}` — строка из Discord. `author`: 1–32 символа без управляющих;
    `text`: те же правила, что у игрового чата (`Trim`, не пусто, ≤ `Limits.ChatMaxLength`, без
    управляющих символов кроме `\n`). Сохраняется `ChatMessage { FromPlayerId = null, Kind = External,
    ExternalAuthor = author }`, рассылается всем сессиям как `ChatMessageEvent { kind = EXTERNAL,
    external_author }`, публикуется в ленту. `/`-команды из Discord-чата не исполняются: строка уходит как текст.
- **Лоты.** Ответ `GET /api/admin/economy/lots` получает поля `sellerName` и `summary` (добавление полей
  совместимо с CLI).
- **Сертификат.** При старте сервер пишет в лог SHA-256 своего TLS-сертификата (нужно для пина бота).

### Протокол и хранение

- `chat.proto`: `MessageKind.EXTERNAL = 2`, `ChatMessageEvent.external_author = 7`.
- `Storage.Entities.MessageKind.External = 2`, колонка `ChatMessages.ExternalAuthor` (TEXT NULL),
  миграция `ChatExternalAuthor` и тест схемы.
- `ChatService.History` отдаёт `ExternalAuthor` вместе с остальными строками.
- `ProtocolVersion.Current` не меняется.

### Конфиг сервера

`Admin:FeedBufferSize` (int, 100–100000, по умолчанию 1000) в `AdminOptions`, с валидацией;
документируется в `deploy/appsettings.example.json` и `docs/server-setup.md`.

## 4. Бот: компоненты

Проект `src/Rimlink.Discord` (сборка `rimlink-discord`, Generic Host, worker). Зависимости: `Discord.Net`
(точная версия 3.20.x — в `Directory.Packages.props`), `Microsoft.Extensions.Hosting`. Ссылки на
`Rimlink.Server` и `Rimlink.Protocol` нет: DTO бота — свои записи, расхождение ловят тесты против
реального сервера.

| Компонент | Что делает |
|---|---|
| `BotOptions` | Конфиг с валидацией при старте (раздел 6) |
| `RimlinkApi` | Типизированный `HttpClient` к `/api/admin/*`: токен, `X-Rimlink-Actor`, пин сертификата, ошибки в `RimlinkApiException(status, detail)` |
| `FeedReader` | Читает SSE, разбирает кадры, хранит последний `id`, переподключается с backoff 1 → 30 с, отдаёт события в `Channel<FeedEvent>` |
| `FeedRouter` | Событие → группа → канал; формат через `Formatter`; `chat.message` отдаёт `ChatBridge` |
| `Formatter` | Тексты событий на языке `Discord:Language`, экранирование Markdown |
| `ChatBridge` | Мост чата (раздел 5) |
| `OutboundQueue` | Очередь исходящих сообщений на канал: сброс раз в 1 с, склейка строк до 2000 символов, предел 500 строк (старые выбрасываются со счётчиком в логе) |
| `Permissions` | Роли участника → `Caller { userId, displayName, level: Everyone\|Moderator\|Admin }` |
| `CommandHandlers` | Чистые обработчики `(Caller, аргументы) → Reply`; вызывают `RimlinkApi` |
| `Modules/*` | Тонкие модули Discord.Net Interactions: разбор опций, вызов обработчика, ответ |
| `Confirmations` | Кнопки «Подтвердить / Отмена» для необратимых команд |
| `IDiscordOut` / `DiscordOut` | Отправка в канал (`AllowedMentions.None`); фейк в тестах |
| `DiscordHost` | Логин, регистрация guild-команд, приём сообщений канала чата, проверки старта |
| `Strings` | `Strings.en.json`, `Strings.ru.json`; одна функция `T(key, args)` |

## 5. Бот: поведение

### События в каналы

`Discord:Channels:{Server,Raids,Market,Moderation,Chat}` — id каналов, 0 = группа выключена. Каждое
событие ленты — одна строка в канале своей группы, через `OutboundQueue`.

- `gap` → строка в `moderation`: «лента прервалась, часть событий пропущена».
- Лента недоступна дольше 60 с → одна строка «сервер недоступен» в `server`; при восстановлении —
  «сервер снова доступен» (порог гасит короткие обрывы).
- `server.started` после такого простоя заменяет строку восстановления (одна строка, не две).

### Мост чата

- **Игра → Discord.** `chat.message` с `kind = player` → `**Имя**: текст` в канал `Chat`. `system`
  пропускается (вход/выход и `[server]` уже в группе `server`), `external` — это эхо самого бота.
- **Discord → игра.** Сообщение не-бота в канале `Chat` → `POST /api/admin/chat { author = отображаемое
  имя, text = CleanContent }`. Вложения добавляются как `[вложение]`; текст длиннее
  `Limits.ChatMaxLength` обрезается с «…»; пустое сообщение не отправляется. Ошибка сервера → реакция ❌
  на исходное сообщение и строка в лог.
- Модерация Discord-чата — средствами Discord (slowmode, роли). Игровой `mute` к Discord-авторам не
  применяется.
- Боту нужен privileged intent **Message Content** (включается в Developer Portal; в инструкции).

### Команды и права

Guild-команды регистрируются на сервер `Discord:GuildId` при старте. Аргумент `player` — имя с
автодополнением (`GET /players?name=`); разрешение имени: точное совпадение без учёта регистра, иначе
единственное совпадение по префиксу, иначе ошибка со списком кандидатов.

| Уровень | Команды |
|---|---|
| Все | `/online`, `/status`, `/market [seller]` (открытые лоты: id, продавец, цена, состав; до 20 строк), `/player <name>` (фракция, онлайн, колонии, мирный режим) |
| Moderator | `/kick <player> [reason]`, `/mute <player> <minutes>`, `/unmute <player>`, `/broadcast <text>`, `/bans`, `/actions [limit]`, `/raids list [player]`, `/raids stats`, `/live list [player]`, `/economy stats` |
| Admin | `/ban <player> [days] [reason]`, `/pardon <banId>`, `/wallet <player> <delta> [reason]`, `/refund lot <id>`, `/refund parcel <id>`, `/economy freeze [reason]`, `/economy unfreeze`, `/raids cancel <id> [return] [reason]`, `/raids freeze [reason]`, `/raids unfreeze`, `/live end <id> [reason]`, `/peaceful <player> <on\|off>`, `/saves <player>` |
| Admin + подтверждение | `/shutdown <seconds> [reason]`, `/rollback <player> <version>`, `/reset <player>`, `/recreateworld` |

- `Discord:ModeratorRoleIds`, `Discord:AdminRoleIds` — списки id ролей. Admin включает Moderator.
- Уровень проверяется на каждую интеракцию по ролям участника; это единственная проверка.
  `DefaultMemberPermissions` (Manage Guild) скрывает модераторские и админские команды от обычных
  участников — удобство, не защита.
- Подтверждение: эфемерное сообщение с описанием действия и кнопками. Нажать может только автор
  команды (иначе эфемерный отказ); через 60 с кнопки гаснут; повторное нажатие не выполняет действие
  второй раз.
- Ответы публичных запросов — в канал; модераторских и админских — эфемерно. Само действие попадает в
  `moderation` через `admin.action` с `via = discord:<username> (<userId>)` — бот шлёт это значение в
  `X-Rimlink-Actor`.
- Ошибки: 4xx → эфемерно текст `detail` сервера; 5xx, таймаут (10 с), обрыв → «сервер не ответил».

### Язык

Тексты бота на `en` или `ru` (`Discord:Language`, по умолчанию `en`). Имена команд английские; описания
команд и опций — с локализацией Discord (`en-US`, `ru`).

## 6. Конфиг, TLS, деплой

Конфиг бота: `appsettings.json` рядом со сборкой, env с префиксом `RimlinkBot__` (`RimlinkBot__Discord__Token`).

| Ключ | Что |
|---|---|
| `Server:Url` | `https://rimlink:25560` в compose |
| `Server:ApiToken` | `Admin:ApiToken` сервера |
| `Server:CertificateSha256` | Пин сертификата (hex, из лога сервера); при совпадении проверка цепочки и имени не нужна |
| `Server:Insecure` | Принять любой сертификат; допустимо только внутри compose-сети |
| `Discord:Token` | Токен бота |
| `Discord:GuildId` | Сервер Discord |
| `Discord:Language` | `en` \| `ru` |
| `Discord:ModeratorRoleIds`, `Discord:AdminRoleIds` | Списки id ролей |
| `Discord:Channels:Server\|Raids\|Market\|Moderation\|Chat` | id каналов, 0 = выключено |

Старт: нет `Server:ApiToken` или `Discord:Token` → процесс завершается с ошибкой. Канал или роль не
найдены, или у бота нет права писать в канал → warning в лог, группа отключается, остальное работает.
Ни пина, ни `Insecure`, а сертификат не проходит обычную проверку → ошибка подключения в лог и повтор с
backoff (без падения).

Деплой: `deploy/Dockerfile.discord` (тот же двухстадийный шаблон, что у сервера, без `curl`);
сервис `rimlink-discord` в `deploy/docker-compose.yml` (`image: ghcr.io/asofwar/rimlink-discord:latest`,
закомментированный `build:`, `depends_on: rimlink`, `restart: unless-stopped`, секреты `DISCORD_TOKEN` и
`RIMLINK_API_TOKEN` из `.env`). Пример конфига — `deploy/discord.appsettings.example.json`.
`Package-Release.ps1` бот не собирает: он поставляется образом.

## 7. Мод

`Dialog_Chat.Format`: `kind = External` → `[время] <b>[Discord] Ник</b>: текст` (тот же `Escape`).
Клиент-ядро не меняется: `ChatMessageEvent` проходит как есть. Версия: одна смена `modVersion` /
`ClientInfo.Version` на подпроект (следующая минорная на момент слияния).

## 8. Тестирование

- `Rimlink.Server.Tests`:
  - `AdminFeedTests`: буфер и вытеснение; догон по `Last-Event-ID` и `?after=`; `gap` при чужом `epoch`
    и при вытесненном `seq`; отставший подписчик отключается; `ping`; токен обязателен.
  - Публикация в каждом источнике таблицы раздела 2 (вход/выход, колония, высадка и итог рейда, визит,
    live, лот выставлен и продан, магазин, broadcast, shutdown, заморозки, `admin.action` с `via` для
    REST-заголовка, без заголовка и для `/`-команды); ЛС и `raid.queued` в ленту не попадают.
  - REST: `players` (префикс, точное первым, лимит), `status`, `chat` GET/POST (валидация автора и
    текста, `EXTERNAL` доходит до тестовой сессии с автором, `/`-строка не исполняется), поля лотов,
    `X-Rimlink-Actor` (суффикс в `Details`, 400 на плохое значение).
  - Схема: колонка `ChatMessages.ExternalAuthor`.
- `Rimlink.Discord.Tests` (новый проект, в `Rimlink.sln`):
  - `FeedReader` против реального тестового сервера: события приходят, обрыв → продолжение без потерь и
    дублей, `gap`.
  - `FeedRouter` + `Formatter`: группа → канал, выключенная группа молчит, экранирование, `summary`.
  - `ChatBridge`: фильтр эха и `system`, обрезка, вложения, пакетирование `OutboundQueue` (склейка,
    2000 символов, предел очереди).
  - `Permissions`: роли → уровень, Admin включает Moderator.
  - `CommandHandlers` против реального тестового сервера с фейком `IDiscordOut`: каждая команда, отказ по
    уровню, разрешение имени игрока, подтверждение (автор, таймаут, повторное нажатие).
- Ручной чеклист (`docs/manual-checklist.md`, раздел «Discord-бот»): тестовый сервер Discord, бот из
  Developer Portal (создаёт владелец), сервер Rimlink и две игры; каждое событие в своём канале, мост чата
  в обе стороны, `[Discord]` в игре, команды по ролям, подтверждение, рестарт сервера (строки
  недоступен/доступен), рестарт бота (догон без дублей).
- Review Focus плана: гонка подписки и `Publish` (событие между снимком буфера и подпиской не теряется и
  не дублируется); `gap` при рестарте сервера; эхо чата; проверка уровня до любого вызова REST;
  двойное нажатие подтверждения; утечка приватного в ленту.

## 9. Порядок работ

1. Сервер: `X-Rimlink-Actor`, `players`, `status`, поля лотов, лог SHA-256 сертификата.
2. Сервер: `AdminFeed`, SSE-эндпоинт, публикация во всех источниках.
3. Сервер: чат REST, `EXTERNAL`, миграция, событие ленты; мод: отрисовка `[Discord]`, версия.
4. Бот: проект, `BotOptions`, `RimlinkApi`, `FeedReader`, тестовый проект.
5. Бот: `FeedRouter`, `Formatter`, `Strings`, `OutboundQueue`, `ChatBridge`.
6. Бот: `Permissions`, `CommandHandlers`, модули, подтверждения, `DiscordHost`.
7. Деплой, документация (`server-setup.md`, `player-guide.md`, `appsettings.example.json`, `README.md`),
   чеклист, ручной прогон.

## 10. Согласование с веб-панелью

Лента (раздел 2), `players`, `status`, `chat` и `X-Rimlink-Actor` — общие серверные дополнения: панель
(параллельный подпроект) пользуется ими же. Кто сливается первым, тот их вводит; второй перестраивается на
готовое и не заводит параллельных ручек. Форма ленты — общая: новые типы событий добавляются, старые не
меняются.

## 11. Вне рамок

Привязка аккаунтов Discord ↔ игрок, личные уведомления в ЛС, «мой кошелёк / мои посылки», `raid.queued`,
несколько серверов Discord на один бот, правка модпака из Discord, хранимая лента в БД, бот внутри процесса
сервера, голосовые каналы.
