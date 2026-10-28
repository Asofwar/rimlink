# Rimlink — асинхронные рейды и визиты (подпроект 3a): дизайн

Дата: 2026-10-28. Статус: утверждён в обсуждении, ждёт план.
Основа: `2026-10-28-rimlink-core-design.md` (ядро), `2026-10-28-rimlink-economy-design.md` (экономика:
блобы, посылки, кошелёк, outbox, антидюп по тику сейва). Всё ниже — поверх них; протокол расширяется
только добавлением (новые `.proto`, новые виды посылок, новые типы событий в `oneof`).

## 1. Цель и границы

Игроки нападают на чужие колонии и гостят в них без того, чтобы вторая сторона была онлайн:
рейд ждёт на сервере, пока защитник не окажется в своей колонии; визит — временная загрузка копии
чужой колонии из её сейва. Сервер по-прежнему ничего не симулирует: бой считает клиент защитника.

Решения, принятые в обсуждении:

| Вопрос | Решение |
|---|---|
| Что такое подпроект 3 | Две спеки: **3a** асинхронные рейды и визиты (эта), **3b** живой визит/живой рейд (оба онлайн, поток команд через сервер); 3b переиспользует «бухгалтерию» рейда отсюда |
| Что отправляет атакующий | На выбор: **Real** — пешки уходят из его игры (эскроу на сервере), выжившие возвращаются с ранами; **Snapshot** — копии, оригиналы дома, обратно только лут |
| Что теряет защитник | Реальный лут (рейдеры крадут и уходят) в пределах лимита; плюс кулдауны, защита новичков, мирный режим |
| Когда прилетает рейд | На выбор атакующего: **Instant** (без предупреждения, следующий вход защитника на карту) или **Warned** (письмо и 1 игровой день *игры защитника*) |
| Как добраться | Караваном (появление у края карты) или транспортными капсулами (дроп-поды у колонии) |
| Визит | Турист на копии карты хозяина + магазин (хозяин помечает вещи ценой, гость покупает из кошелька) + подарок (обычный `Send`) |
| Кто водит рейдеров | В 3a — ИИ (`controller = Ai`); поле `controller` и состояние `Landed` заложены так, чтобы 3b добавил `Live` |

Вне рамок: живые визиты и живые рейды (3b), осады, захват поселений, рейды НПС-фракций через сервер,
союзы и дипломатия, серверная симуляция боя, покупки в магазине без визита.

## 2. Рейд

### Параметры

`pawn_mode` ∈ {Real, Snapshot}, `timing` ∈ {Instant, Warned}, `arrival` ∈ {Edge, Pods},
`controller` ∈ {Ai} (3b добавит Live), пешки — `PawnList` блобов с одеждой, снаряжением и инвентарём
(до `MaxRaiders`). Real: пешки снимаются с каравана/капсул и уходят в эскроу (как `Send`); Snapshot:
блобы снимаются с живых пешек, из игры никто не уходит.

### Состояния (сервер)

```
Queued ──Announce──► Announced ──Land──► Landed ──Report──► Resolved
  │                     │                   │
  └──── 7 реальных дней без высадки ────────┴──► Expired   (Real: пешки домой посылкой RaidReturn)
  └──────────────── админ ──────────────────────► Cancelled (Real: пешки домой; лут не считается)
```

- `Queued`: принят (кулдауны, защита, мирный режим, лимит активных рейдов проверены), эскроу записан.
- `Announced`: клиент защитника забрал рейд из `Pending`. Warned: письмо «Рейд игрока X» и
  `land_at_tick = announced_tick + WarnTicks (60000)` — отсчёт идёт в тиках игры защитника, пауза и
  выход из игры его не продвигают. Instant: `land_at_tick = announced_tick`.
- `Landed`: клиент защитника заспавнил рейд и подтвердил с тиком (идемпотентно, как `Deliver`).
- `Resolved`: клиент защитника прислал отчёт (ниже). `Expired`/`Cancelled`: см. схему.

### У защитника

Пешки десериализуются с новыми ID (как входящие посылки). Фракция — `RimlinkRaiders`: скрытая,
`permanentEnemy`, определена модом (`mod/Defs/FactionDefs`), создаётся лениво в мире защитника при
первом рейде. Спавн: Edge — у края карты (`RCellFinder.TryFindRandomPawnEntryCell`), Pods — дроп-поды
у колонии (`DropCellFinder.TradeDropSpot`/`FindRaidDropCenterDistant`). Лорд:
`LordJob_AssaultColony(faction, canKidnap: Raids:AllowKidnap, canTimeoutOrFlee: true, sappers: false,
useAvoidGridSmart: true, canSteal: true)` — ванильное поведение: атака, при потерях бегство с добычей.
Письмо высадки — как ванильное о рейде, с именем атакующего.

Связка `raid_id → lord` хранится в `GameComponent` (`ExposeData`): после перезагрузки защитника отчёт
всё равно состоится.

### Исход (`RaidResolver`, клиент защитника)

Срабатывает, когда лорд завершён (все рейдеры мертвы, сбежали с карты или пленены) или через
`TimeoutDays` (3) игровых дня после высадки. По каждой пешке рейда:

| Исход | Real | Snapshot |
|---|---|---|
| `DEAD` | потеряна (труп и вещи у защитника) | то же |
| `CAPTURED` (пленник защитника) | остаётся у защитника | остаётся у защитника |
| `FLED` (ушла с карты) / `ALIVE` (жива по таймауту) | возвращается атакующему посылкой `RaidReturn` с ранами и снаряжением (блоб после боя); `ALIVE` деспавнится | удаляется |

Лут = вещи в инвентаре и в руках сбежавших/живых рейдеров, в пределах лимита (раздел 3); лишнее
бросается у края карты. Идёт атакующему посылкой `Loot`. Похищенные колонисты защитника (если
`AllowKidnap`) — посылкой `Captive` как пленники, не более 1 за рейд.

Антидюп: перед отчётом клиент защитника сохраняется (лут и выжившие ушли, пленники есть) и выгружает
сейв; отчёт несёт `game_tick` этого сейва, сервер штампует `LastOutgoingTick` защитника. Отчёт идёт
через outbox (вид `Report`), как любая передача без ответа. Real-рейд при запуске проходит конвейер
`Outgoing.Run`: снять с каравана/подов → сейв → выгрузка → `Launch` → outbox.

Множители лута: Instant ×0.5, Snapshot ×0.5 (оба — ×0.25).

## 3. Защита и лимиты (конфиг `Raids:*`)

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `Enabled` | true | рейды выключены целиком или заморожены админом (`raids freeze`): `Launch`/`Announce` отвечают `ECONOMY_FROZEN` с причиной; визиты и магазин не затрагиваются |
| `MaxRaiders` | 10 | пешек в рейде |
| `MaxActiveRaidsPerAttacker` | 1 | рейдов не в `Resolved/Expired/Cancelled` у одного атакующего |
| `TargetCooldownHours` | 8 | одна цель — не чаще раза в 8 реальных часов кем угодно |
| `PairCooldownHours` | 24 | пара атакующий→цель — не чаще раза в сутки |
| `NewbieProtectionDays` | 3 | цель с `first_save_at` моложе 3 реальных дней не рейдится |
| `MinColonyWealth` | 20000 | цель с `wealth_total` ниже порога не рейдится |
| `LootCapPercent` | 10 | лимит лута = min(`wealth_items` × 10 %, `LootCapSilver`) по рыночной стоимости |
| `LootCapSilver` | 5000 | абсолютный потолок лута |
| `AllowKidnap` | false | похищение колонистов защитника |
| `WarnTicks` | 60000 | отсчёт Warned в тиках защитника |
| `TimeoutDays` | 3 | игровых дней у защитника до принудительного исхода |
| `ExpireRealDays` | 7 | реальных дней без высадки → `Expired` |
| `PeacefulOffDelayHours` | 24 | задержка выключения мирного режима |

`wealth_total` и `wealth_items` — новые поля `SaveHeader` (клиент берёт `WealthWatcher`); сервер хранит
последние значения на игроке. `first_save_at` — время первой выгрузки сейва.

Мирный режим: `players.peaceful` — включается мгновенно из окна Rimlink; мирный не может рейдить и
быть целью. Выключение записывает `peaceful_off_at = now + PeacefulOffDelayHours`, до этого момента
игрок считается мирным. Визиты и магазин мирный режим не трогает.

Доверие: клиент защитника авторитарен в бою. Сервер валидирует отчёт: пешки в отчёте ⊆ отправленным
(по индексу), лут ≤ лимита по `MarketValueX100` блобов (иначе `LOOT_OVER_CAP`, клиент бросает лут
обратно на карту), дефы лута в модпаке (если дефы загружены), `game_tick` отчёта ≥ `landed_tick`,
`CAPTIVE` ≤ 1 и только при `AllowKidnap`.

## 4. Визиты и магазин

### Визит = временное переключение игры

1. Караван (или капсулы) на поселении цели → «Визит». Сервер (`Visits/Begin`): тайл совпадает,
   хозяин не закрыл визиты (`players.visits_closed`), у хозяина есть сейв → `VisitTicket`.
2. Клиент сериализует *копии* участников (блобы; из своей игры никто не уходит, караван ждёт на тайле),
   сохраняет и выгружает свой сейв как обычно, скачивает сейв хозяина (`Saves/DownloadOf`, только по
   действующему тикету) во временный файл и загружает его в режиме визита.
3. При загрузке: пешки, постройки и поселение хозяина перефракционируются в сгенерированную
   дружественную фракцию с именем хозяина (как ванильное дружественное поселение); копии гостей
   спавнятся у края карты фракцией игрока; флаг визита в `GameComponent`.
4. Режим визита: сохранение, автосейв и выгрузка отключены (расширение патча
   `Patch_GameDataSaveLoader_SaveGame`); `Incoming` не сыплет посылки (карта не `IsPlayerHome`);
   чат, окно Rimlink и `Send` работают; кнопка «Закончить визит» → `Visits/End`, загрузка своего сейва,
   временный файл удалён. Из копии домой ничего не уносится.

Хозяину — событие `VisitStarted` («X гостит у вас»); таблица `visits` для статистики и админа.

### Магазин

- Хозяин помечает вещь гизмо «Продаётся: цена» (`MapComponent_RimlinkShop`: `thingID → цена`; цена 0
  снимает отметку). При каждом сохранении (`SaveSync.OnSaved`) клиент публикует список
  (`Shop/Publish`), он заменяет прежний: `thing_id, def, stuff, count, quality, hp, price, save_tick`.
- Гость в визите видит ценники над помеченными вещами, «Купить N» → клиент сериализует вещь из копии,
  `Shop/Buy(host, thing_id, count, blob)`. Сервер в одной транзакции (serializable): у покупателя есть
  действующий визит к этому хозяину (`visits` без `ended_at`), запись есть и не
  продана, дескриптор блоба совпадает с записью (def, stuff, quality, count ≤ остатка), кошелька
  хватает → списать покупателю, зачислить хозяину минус `Economy:MarketFeePercent`, пометить `sold_at`
  (частичная покупка уменьшает `count`), создать покупателю посылку `Purchase` с блобом, хозяину —
  `pending_removals(thing_id, count)`; события `ItemSold` хозяину и `WalletChanged` обоим после
  коммита.
- Клиент хозяина при входе и при загрузке карты забирает `Shop/PendingRemovals`, удаляет вещи с карты
  по `thingID` (нет вещи или меньше — удаляет сколько есть), сохраняется, `Shop/AckRemoval` через
  outbox. Запись без подтверждения показывается админу.
- Без визита магазин игрока виден в окне Rimlink («Магазины») только для просмотра; покупка — только
  из визита (блоб берётся из загруженной копии).

## 5. Протокол (`raids.proto`, `visits.proto`, пакет `rimlink.v1`)

```
service Raids {
  rpc Launch (LaunchRequest) returns (Raid);        // client_request_id, target_player_id, pawn_mode, timing, arrival, pawns, game_tick
  rpc Pending (Empty) returns (RaidList);           // защитник: Queued|Announced к нему, без блобов
  rpc Get (RaidRef) returns (Raid);                 // с блобами (защитник или атакующий)
  rpc Announce (RaidTick) returns (Raid);           // raid_id, game_tick → Announced, land_at_tick
  rpc Land (RaidTick) returns (Empty);              // Landed, идемпотентно
  rpc Report (RaidReport) returns (Empty);          // raid_id, game_tick, outcomes[{index, outcome, blob?}], loot: ItemList, captives: PawnList
  rpc Mine (Empty) returns (RaidList);              // атакующий: свои рейды с состоянием
  rpc SetPeaceful (PeacefulRequest) returns (PlayerFlags);
  rpc SetVisitsClosed (VisitsClosedRequest) returns (PlayerFlags);
}
service Visits {
  rpc Begin (VisitRequest) returns (VisitTicket);   // target_player_id, game_tick
  rpc End (VisitRef) returns (Empty);
}
// Saves: rpc DownloadOf (PlayerRef) returns (stream SaveChunk);  — только по действующему тикету
service Shop {
  rpc Publish (ShopList) returns (Empty);
  rpc Of (PlayerRef) returns (ShopList);
  rpc Buy (BuyRequest) returns (Parcel);            // client_request_id, host_player_id, thing_id, count, blob
  rpc PendingRemovals (Empty) returns (RemovalList);
  rpc AckRemoval (RemovalAck) returns (Empty);      // ids, game_tick
}
```

`ParcelKind` += `LOOT`, `RAID_RETURN`, `CAPTIVE` (`PURCHASE` уже есть). События (`oneof` 60–66):
`RaidQueued` (защитнику), `RaidLanded`, `RaidResolved` (атакующему, сводка), `RaidExpired`, `ItemSold`,
`VisitStarted`, `PlayerFlagsChanged`. Ошибки `ErrorCode` 20–27: `RAID_COOLDOWN`, `TARGET_PROTECTED`,
`PEACEFUL_MODE`, `RAID_ACTIVE`, `RAID_GONE`, `LOOT_OVER_CAP`, `VISITS_CLOSED`, `ITEM_SOLD`.
`SaveHeader` += `wealth_total`, `wealth_items`. Лимиты: `Launch`/`Report`/`Buy` ≤ 1 МБ как посылка;
`Launch` и `Buy` под кулдаун-интерцептором (5 с).

## 6. Хранение (миграция `Raids`)

- `raids`: id, attacker_player_id, target_player_id, pawn_mode, timing, arrival, controller, state,
  pawns (blob `PawnList`), loot_cap, created_at, announced_tick, land_at_tick, landed_tick, resolved_at,
  expires_at, summary (json), client_request_id (уникален на атакующего).
- `raid_outcomes`: raid_id, index, outcome.
- `visits`: id, visitor_player_id, host_player_id, started_at, ended_at.
- `shop_items`: host_player_id, thing_id, def, stuff, count, quality, hp, price, save_tick, sold_at
  (уникально host+thing_id).
- `pending_removals`: id, host_player_id, thing_id, count, created_at, acked_at.
- `players` += peaceful, peaceful_off_at, visits_closed, first_save_at, wealth_total, wealth_items.
- Все переносы вещей и пешек между играми — через `parcels` (новые kind); эскроу рейда — `raids.pawns`.
- `RaidExpiryService` (раз в час): `Expired` для просроченных, возврат Real-пешек посылкой `RaidReturn`.

## 7. Клиент

Клиент-ядро: `RaidClient`, `VisitClient`, `ShopClient` (дедлайн 30 с), `RaidState` в `SharedState`
(входящие/свои рейды, события 60–66), outbox получает виды `Launch`, `Land`, `Report`, `Buy`,
`AckRemoval`.

Игровой слой (`Game/Raids`, `Game/Visits`, `Game/Shop`):
- `RaidOutgoing`: пункты «Рейд» и «Визит» у `PlayerSettlement` (караван — `GetFloatMenuOptions`;
  капсулы — `TransportPodsArrivalAction_RimlinkRaid/Visit` в меню запуска на объект игрока); диалог
  выбора пешек, режима и тайминга; Real — конвейер `Outgoing.Run`; Snapshot — блобы без снятия.
- `RaidIncoming` (тик 5 с на домашней карте): `Pending` → `Announce` → письмо и отсчёт по `TicksGame`
  → `Get` → спавн → `Land` (через outbox) → сейв.
- `RaidResolver`: завершение лорда/таймаут → исходы, лут по лимиту, пленники → сейв → выгрузка →
  `Report` через outbox; `LOOT_OVER_CAP` → лут обратно на карту.
- `VisitSession`: копии → свой сейв → `DownloadOf` → загрузка с пост-обработкой → UI «Закончить визит».
- `Shop`: гизмо «Продаётся», публикация в `OnSaved`, ценники в визите, «Купить», `pending_removals`
  при входе/загрузке карты.
- Окно Rimlink: вкладки «Рейды» (свои и входящие, состояния), «Магазины» (просмотр), переключатели
  «Мирный режим» и «Закрыть визиты».
- `FactionDef` `RimlinkRaiders` в `mod/Defs`; дружественная фракция визита генерируется на лету.

## 8. Администрирование

Чат/REST/CLI: `raids list [player]`, `raids cancel <id>` (возврат пешек), `raids freeze|unfreeze`,
`raids stats`, `shop <player>`, `removals <player>` (неподтверждённые), `player peaceful <id> on|off`
(без задержки). Метрики: `rimlink_raids_total{outcome}`, `rimlink_raids_active`, `rimlink_visits_total`,
`rimlink_shop_sales_total`. Аудит: запуск, высадка, отчёт, отмена, истечение.

## 9. Обработка ошибок (клиент)

| Код | Клиент |
|---|---|
| `RAID_COOLDOWN`, `TARGET_PROTECTED`, `PEACEFUL_MODE`, `RAID_ACTIVE` | отказ до снятия пешек: сообщение, ничего не ушло |
| `RAID_GONE` (цель сбросила колонию / рейд отменён) при `Announce`/`Land` | защитник: забыть; атакующий получает пешки обратно (`RaidReturn`) |
| `LOOT_OVER_CAP` | лут обратно на карту, отчёт повторяется без лишнего |
| `VISITS_CLOSED`, нет сейва | визит не начинается, караван остаётся |
| `ITEM_SOLD`, `WALLET_INSUFFICIENT` | покупка не прошла, вещь остаётся в копии |
| нет ответа (сеть, заморозка) | запись в outbox, как в экономике |

## 10. Тестирование

- Сервер: `RaidTests` (жизненный цикл, идемпотентность `Launch`/`Land`/`Report`, кулдауны, защита
  новичков и богатства, мирный режим и задержка выключения, лимит активных, лимит лута и `LOOT_OVER_CAP`,
  истечение с возвратом, отмена, `RAID_GONE` после `reset`, антидюп-штамп защитника), `VisitTests`
  (`Begin` по тайлу и `visits_closed`, `DownloadOf` только по тикету), `ShopTests` (publish заменяет,
  buy атомарно с комиссией и событиями после коммита, частичная покупка, `ITEM_SOLD`, removals и ack),
  `SchemaTests`, админ-операции, метрики.
- Клиент-ядро: `RaidClientTests`, `ShopClientTests` против реального сервера, outbox с новыми видами.
- Игровой слой: ручной чеклист (`docs/manual-checklist.md`): Real/Snapshot × Warned/Instant × Edge/Pods,
  отчёт с лутом/выжившими/пленниками, перезагрузка защитника во время рейда, визит с покупкой и
  подарком, истечение, мирный режим.
- Review Focus плана: отчёт после крэша защитника (лорд в `ExposeData`), рейд на сброшенную колонию,
  покупка уже проданной вещи, лут ровно на лимите, визит при смене модпака хозяина.

## 11. Порядок работ

1. Протокол, хранение, флаги игрока, `SaveHeader.wealth_*`.
2. Сервер рейдов: создание с проверками, состояния, эскроу, отчёт с валидацией, истечение, админ.
3. Сервер визитов и магазина.
4. Клиент-ядро: клиенты, `RaidState`, outbox.
5. Игра: фракция, `RaidOutgoing`/`RaidIncoming`/`RaidResolver`.
6. Игра: `VisitSession`, магазин.
7. UI, строки, документация, чеклист, проверка в игре.

## 12. Вне рамок (3b и дальше)

Живой визит и живой рейд (`controller = Live`, принятие боя защитником, поток команд), осады и захват
поселений, союзы, покупки без визита, НПС-рейды через сервер.
