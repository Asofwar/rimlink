# Rimlink — платные инциденты

Дата: 2026-10-30. Статус: дизайн утверждён пользователем по разделам; спек на проверке.
Основа: `2026-10-28-rimlink-raids-visits-design.md` (очередь асинхронных рейдов, правила запуска, мирный режим, заморозка),
`2026-10-30-rimlink-economy-hardening-design.md` (серверное богатство, `EconomyGuard`, долг, банк). Источник идеи — отчёт
`reports/Фичи мультиплеерных модов RimWorld.md`, «Рейды и PvP» (заказные инциденты OnlineCity; взяты идея и формула, не код).
Вторая часть ветки `asofwar/economy-hardening`.

## 0. Цель и границы

Игрок за серебро заказывает событие у колонии другого игрока: враждебное (PvP без боя), стихийное или доброе (подарок).
Событие срабатывает в игре цели, когда она играет, как асинхронный рейд. Серебро уходит в банк (сток).

Сделано, когда:
1. В окне Rimlink есть вкладка «События»: цель, событие из каталога, уровень, способ высадки, цена от сервера, заказ, мои заказы.
2. Цена зависит от богатства цели (сейв + серверное), уровня и типа; очередь на цель — не больше двух заказов.
3. Враждебные заказы подчиняются тем же защитам, что рейды игроков; крупные объявляются за полдня.
4. Событие срабатывает в игре цели, письмо называет заказчика; не сработавшее, истёкшее или отменённое возвращает серебро.

Не входит: анонимные заказы, заказ с карты мира (ПКМ по поселению), рассылка событий админом, события для живой сессии.

## 1. Как сейчас (основа для плана)

- Рейд игрока: `RaidService.Launch` (правила `RaidRules.CheckLaunch`, `RaidGate.RequireOpen`, эскроу пешек), `Pending` (на меня,
  не живые), `Announce` (`LandAtTick = gameTick + (Warned ? WarnTicks : 0)`), `Land`, `Report`, `ExpireDue` раз в час, `Cancel`.
- Клиент: `RaidIncoming.Tick` (раз в 5 с, только домашняя карта) объявляет `Queued` и высаживает `Announced`, когда
  `TicksGame >= LandAtTick`; состояние в `RaidsState` (`Incoming`, `Mine`), события `RaidQueued`/`RaidExpired`/…
- Деньги: `WalletService.Apply` (списание с проверкой), банк — игрок 0; `EconomyGuard.RequireNoDebt` (`IN_DEBT`);
  `ServerWealth.Of(player)` (первая часть).
- Ванильные def в 1.7: инциденты `RaidEnemy`, `Infestation`, `ToxicFallout`, `ColdSnap`, `HeatWave`, `Flashstorm`
  (карта), `SolarFlare` (мир), `TraderCaravanArrival`, `ResourcePodCrash`, `WandererJoin`; фракции `Pirate`,
  `OutlanderRough`, `TribeRough`, `Mechanoid`; высадка `EdgeWalkIn`, `EdgeDrop`.

## 2. Сервер: каталог, цена, правила

Каталог — настройка `Incidents:Catalog`, список записей `{Id, IncidentDef, FactionDef?, Kind (Hostile|Weather|Kind),
PriceMultiplier, PodsMultiplier?}` (`PodsMultiplier` есть — значит доступна высадка капсулами). По умолчанию:

| Id | IncidentDef | FactionDef | Kind | Множитель | Капсулы |
|---|---|---|---|---|---|
| `pirates` | RaidEnemy | Pirate | Hostile | 1 | 1.4 |
| `outlanders` | RaidEnemy | OutlanderRough | Hostile | 1 | 1.4 |
| `tribals` | RaidEnemy | TribeRough | Hostile | 1 | — |
| `mechanoids` | RaidEnemy | Mechanoid | Hostile | 5 | 1.5 |
| `infestation` | Infestation | — | Hostile | 3 | — |
| `toxic-fallout` | ToxicFallout | — | Weather | 2 | — |
| `cold-snap` | ColdSnap | — | Weather | 1 | — |
| `heat-wave` | HeatWave | — | Weather | 1 | — |
| `flashstorm` | Flashstorm | — | Weather | 1 | — |
| `solar-flare` | SolarFlare | — | Weather | 1 | — |
| `trader` | TraderCaravanArrival | — | Kind | 1 | — |
| `resource-pod` | ResourcePodCrash | — | Kind | 1 | — |
| `wanderer` | WandererJoin | — | Kind | 2 | — |

Цена (`IncidentPrice.For`, чистая функция): `W = target.WealthTotal + ServerWealth.Of(target)`;
- враждебные: `round(BasePrice × (W/100000)^(2/3) × L^(3/2) × множитель × (капсулы ? PodsMultiplier : 1))`, где `L` — уровень
  1–4 → 0,5 / 1 / 1,5 / 2;
- стихийные и добрые: `round(BasePrice × (W/100000)^(2/3) × множитель)` (уровня нет);
- не меньше `MinPrice`. `Quote` отдаёт цену и признак «объявляется заранее» без заказа.

Заказ (`IncidentService.Order`): сначала повтор по `ClientRequestId` (вернуть прежний заказ, не платить), затем:
`Incidents:Enabled`, заказчик ≠ цель, запись каталога и уровень (1–4 для враждебных), капсулы только где разрешены,
`RequireNoDebt(заказчик)`; для враждебных и стихийных (вредящих) — `RaidGate.RequireOpen` (`/raids freeze`), мирный режим
заказчика и цели, защита новичков и `MinColonyWealth` (как `RaidRules.CheckLaunch`, с серверным богатством), общий кулдаун
на цель: рейд игрока или вредящий заказ на цель за `Raids:TargetCooldownHours` (отменённые, истёкшие и не сработавшие заказы
не считаются); для добрых — только свой кулдаун `KindCooldownHours` на пару заказчик→цель; очередь — на цель не больше `MaxQueuedPerTarget`
незавершённых заказов любого вида (`INCIDENT_QUEUE_FULL`). Деньги: `Apply(заказчик, −цена, "incident", id)` и
`Apply(банк, +цена, "incident", id)` в одной транзакции.

Жизненный цикл: `Queued → Announced → Fired`; `Failed` (игра цели не смогла запустить), `Expired` (цель не объявила за
`ExpireRealDays`, обход при старте и раз в час, как у рейдов), `Cancelled` (админ). `Failed`/`Expired`/`Cancelled` возвращают
цену заказчику (`"incident-refund"`), банк отдаёт. События: цели `IncidentQueued`, заказчику `IncidentResolved`
(состояние, причина, возврат).

Блок вывода (первая часть): враждебный заказ (`Kind = Hostile`) на игрока в `Queued`/`Announced` — тоже «угроза» в
`EconomyGuard.UnderThreat`, и в клиентском `Outgoing.UnderThreat` по входящим заказам; стихийные и добрые — нет.

## 3. Исполнение в игре цели

`IncidentIncoming.Tick` (раз в 5 с, рядом с `RaidIncoming.Tick`; только домашняя карта, не в визите и не гостем живой сессии):
- `Queued` → `Announce {id, game_tick}`; сервер ставит `FireTick = game_tick + (враждебный и уровень ≥ WarnFromLevel ?
  WarnTicks : 0)` и `Announced`. Если тик в будущем — письмо «{заказчик} заказал(а) на вас {событие} (уровень N): через
  полдня».
- `Announced` и `TicksGame >= FireTick` → `IncidentParms`: `target` — карта (мировой инцидент `SolarFlare` — `Find.World`),
  `forced = true`; у враждебных `points = StorytellerUtility.DefaultThreatPointsNow(map) × множитель уровня`; у рейдов
  `faction = FirstFactionOfDef(def)` (нет в мире — `FactionUtility.DefaultFactionFrom`/случайная враждебная) и
  `raidArrivalMode` `EdgeWalkIn` или `EdgeDrop` (капсулы). `def.Worker.TryExecute(parms)`.
- Сработало → сообщение «заказано: {заказчик}» (добрые: «подарок от {заказчик}»), `Report {fired: true}`; `false` или
  исключение → `Report {fired: false, reason}` (возврат заказчику).
- Сработавший id записывается в `RimlinkGameComponent.FiredIncidents` (сохраняется с игрой) до ответа сервера: перезагрузка
  до отчёта не запускает событие второй раз; `Report` идёт через outbox и идемпотентен на сервере.
- Только главный поток; строки keyed EN/RU.

## 4. Протокол, окно, админ, настройки

Протокол только растёт; номера предварительные (перенумерация при перебазировании, как в первой части):
- новый `incidents.proto`: `service Incidents { Catalog(Empty) → IncidentCatalog; Quote(QuoteRequest) → IncidentQuote;
  Order(OrderIncidentRequest) → Incident; Pending(Empty) → IncidentList; Mine(Empty) → IncidentList; Announce(AnnounceIncident)
  → Incident; Report(IncidentReport) → Empty }`; `Incident {id, buyer, target, entry_id, label, kind, level, pods, price,
  state, fire_tick, created_at, reason}`; `IncidentState` (`QUEUED`, `ANNOUNCED`, `FIRED`, `FAILED`, `EXPIRED`, `CANCELLED`).
- `events.proto` (импортирует `incidents.proto`): `incident_queued = 80`, `incident_resolved = 81`.
- `ErrorCode.INCIDENT_QUEUE_FULL = 35`.

Хранилище: таблица `incidents` (миграция `PaidIncidents`), индекс `(TargetPlayerId, State)`, тест схемы.

Окно: вкладка «События» в `Dialog_Economy` — игрок, событие (каталог с сервера), уровень 1–4 у враждебных, «капсулами» где можно,
цена (`Quote`), «Заказать»; список моих заказов с состоянием и причиной. Состояние на клиенте — `IncidentsState` в ядре
(`Incoming`, `Mine`, `Catalog`), загрузка при входе, события.

Админ (методы в конце файлов): `AdminOperations.ListIncidents`/`CancelIncident` (с возвратом и аудитом), REST
`GET /api/admin/incidents`, `POST /api/admin/incidents/{id}/cancel`, чат `/incidents list [player] | cancel <id>`,
CLI `rimlink-tools admin incidents list|cancel`.

Настройки `Incidents` (класс `IncidentOptions`, `deploy/appsettings.example.json`, `docs/server-setup.md`): `Enabled` true,
`MaxQueuedPerTarget` 2, `BasePrice` 100, `MinPrice` 50, `WarnTicks` 30000, `WarnFromLevel` 3, `KindCooldownHours` 1,
`ExpireRealDays` 7, `Catalog` — таблица выше.

BridgeTools (dev): `rimlink/incident_order {player, entry, level, pods}`, `rimlink/incidents` — очередь и мои заказы.

Версия мода не поднимается.

## 5. Решения (rulings)

1. Серебро заказа уходит в банк; цель ничего не получает (сток), возврат только заказчику.
2. Стихийные события — вредящие: те же защиты, что у враждебных (мирный режим, новички, `MinColonyWealth`, общий кулдаун с
   рейдами, заморозка), но без уровня и очков, без предупреждения заранее. Цена ошибки: нет.
3. Добрые события не блокируются мирным режимом цели: это подарок. Цена ошибки: торговец «в подарок» как спам — ограничен
   очередью 2 и кулдауном пары.
4. Сила враждебного события — доля обычных очков угрозы цели, посчитанных её игрой в момент срабатывания (с серверным
   богатством): заказ всегда по размеру колонии, а не абсолютный. Цена ошибки: заказчик не знает точных очков заранее.
5. Не сработавший заказ (`TryExecute == false`, исключение) — полный возврат: условия цели (биом без гор для заражения) не вина
   заказчика.

## 6. Проверка и документация

xUnit (сервер): формула цены (уровни, типы, капсулы, `MinPrice`, богатство с серверной частью); каждое правило заказа (долг,
заморозка, мирный режим обеих сторон, новички, `MinColonyWealth` — для враждебных и стихийных; кулдаун общий с рейдами в обе
стороны; у добрых — только кулдаун пары и очередь, мирный режим цели их не блокирует;
очередь 2, неизвестная запись, уровень, капсулы); повтор `Order` не платит дважды; `Announce` ставит `FireTick` по уровню;
`Report` fired/failed (возврат) и повтор отчёта; `ExpireDue` с возвратом; админская отмена с возвратом и аудитом; события;
`EconomyGuard` считает враждебный заказ угрозой, добрый — нет; тест схемы. Ядро клиента: `IncidentsState`.

В игре на стенде s2 (пункты в `docs/manual-checklist.md`): заказ рейда уровня 1 — срабатывает при входе цели, письмо с именем;
уровень 3 — письмо-предупреждение, событие через полдня, вывод у цели закрыт до срабатывания; заражение на карте без гор —
`Failed` и возврат; токсичные осадки; торговый караван в подарок; очередь 2; истечение с возвратом (сервер с `ExpireRealDays=0`).

Документация: `docs/player-guide.md`, `docs/server-setup.md`, `deploy/appsettings.example.json`, `README.md`,
`docs/manual-checklist.md`.
