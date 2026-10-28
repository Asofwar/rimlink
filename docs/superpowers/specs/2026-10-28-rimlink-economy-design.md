# Rimlink — экономика (подпроект 2): дизайн

Дата: 2026-10-28. Статус: утверждён в обсуждении, ждёт план.
Основа: `2026-10-28-rimlink-core-design.md` (ядро). Всё ниже — поверх ядра; протокол расширяется
только добавлением (новые `.proto`, новые типы событий в `oneof`).

## 1. Цель и границы

Игроки на одном сервере передают друг другу вещи, серебро и пешек, торгуют на общей доске
объявлений и водят караваны к чужим поселениям — при том, что колонии заморожены офлайн и
сервер не симулирует игру.

Решения, принятые в обсуждении:

| Вопрос | Решение |
|---|---|
| Получатель офлайн | Почтовый ящик на сервере: посылка хранится до следующего входа получателя |
| Что передаётся | Вещи (стаки с материалом/качеством/hp), серебро, пешки (люди и животные) |
| Рынок | Доска объявлений с эскроу: продавец выставляет лот по цене в серебре, сервер атомарно меняет серебро на вещи |
| Караваны | Караван-доставка: ванильный караван доходит до `PlayerSettlement`, по прибытии — окно передачи; входа на чужую карту нет |
| Вход/выход вещей | Ванильные механики: консоль связи + торговые маяки на выход, дроп-поды на вход |
| Серебро | Кошелёк на сервере (баланс на игрока); физическое серебро только по явной выдаче |
| Представление вещей | Непрозрачные блобы `Scribe` XML (zstd) + дескриптор для списков и проверок |
| Антидюп | Исходящая передача → сейв + выгрузка; сервер отвергает сейв старше последней передачи |

Вне рамок: визиты и рейды (подпроект 3), серверный магазин по ванильным ценам, аукционы,
налоги, симуляция цен, передача построек, фракция на игрока в мире (отдельный подпроект,
экономика от неё не зависит: чужое поселение остаётся `PlayerSettlement`).

## 2. Модель данных (сервер, SQLite)

- `Wallet`: `player_id` (PK), `balance` (int64, CHECK ≥ 0), `updated_at`.
- `WalletEntry`: `id`, `player_id`, `delta`, `kind` (`sale|purchase|deposit|withdraw|admin|refund|fee`),
  `ref_id` (лот/посылка/перевод), `at`. Полная история, баланс = сумма записей (проверяется тестом).
- `Parcel`: `id`, `to_player_id`, `from_player_id` (NULL = рынок/админ), `kind` (`gift|purchase|lot_return|withdraw|admin`),
  `items` (protobuf `ItemList`), `pawns` (protobuf `PawnList`), `silver`, `size_bytes`, `created_at`,
  `delivered_at` NULL, `delivered_tick` NULL.
- `Lot`: `id`, `seller_player_id`, `items|pawn` (как в посылке), `price`, `created_at`, `expires_at`,
  `status` (`open|sold|withdrawn|expired`), `buyer_player_id`, `closed_at`.
- `Transfer` (журнал): `id`, `from_player_id`, `to_player_id|NULL`, `kind` (`send|post|buy|withdraw|deposit|deliver|expire|refund`),
  `game_tick` отправителя, `summary` (текст для админа), `at`.
- `Player.last_outgoing_tick` (новая колонка): тик сейва, сопровождавшего последнюю исходящую передачу.
- Блобы вещей/пешек лежат в полях `items`/`pawns` как protobuf-байты (zstd внутри каждой записи);
  отдельного файлового хранилища нет — лимит на посылку делает это допустимым.

Миграция EF Core `Economy` — только новые таблицы и одна колонка.

### Дескриптор и блоб

```
ItemBlob   { string def_name; string stuff_def; int32 count; int32 quality; int32 hit_points; int32 max_hit_points;
             int64 market_value_x100; string label; bytes scribe_xml_zstd; }
PawnBlob   { string kind_def; string name; bool humanlike; int32 age_years; string label;
             repeated SkillBrief skills; bytes scribe_xml_zstd; }
ItemList   { repeated ItemBlob items; }     PawnList { repeated PawnBlob pawns; }
```

Сервер читает только дескриптор; `scribe_xml_zstd` — ровно то, что клиент получил от
`Scribe` для этого `Thing`/`Pawn` (см. §4). Сервер проверяет: `def_name`/`stuff_def`/`kind_def`
присутствуют в списке дефов модпака (§6), `count ≥ 1`, размеры в лимитах.

## 3. Протокол (`economy.proto`, пакет `rimlink.v1`)

Сервисы (все требуют сессию):

- `Wallet`: `Get(Empty) → WalletState{balance}`; `Deposit(DepositRequest{silver, game_tick}) → WalletState`;
  `Withdraw(WithdrawRequest{silver}) → Parcel` (посылка `withdraw` самому себе); `History(Empty) → WalletHistory`.
- `Mailbox`: `List(Empty) → ParcelList` (не доставленные); `Deliver(DeliverRequest{parcel_id, game_tick}) → Empty`
  (идемпотентно; второй вызов с тем же id — `OK`).
- `Market`: `List(MarketQuery{seller?, text?, page}) → LotList`; `Post(PostRequest{items|pawn, price, game_tick}) → Lot`;
  `Withdraw(LotRef) → Parcel` (вещи возвращаются продавцу посылкой `lot_return`); `Buy(LotRef) → Parcel`.
- `Transfer`: `Send(SendRequest{client_request_id, to_player_id, items, pawns, silver, game_tick}) → Parcel`.
  `client_request_id` (GUID клиента) есть и в `PostRequest`, `DepositRequest`, `WithdrawRequest`: повтор с тем же id
  возвращает прежний результат, не создавая вторую посылку/лот/запись.

События в `ServerEvent` (новые поля `oneof`): `ParcelArrived{parcel}` (адресату), `LotSold{lot_id, buyer, price}`
(продавцу), `LotClosed{lot_id, reason}` (продавцу: истёк/снят админом), `WalletChanged{balance, delta, kind}`.

Коды ошибок (расширение `ErrorCode`): `WALLET_INSUFFICIENT`, `LOT_GONE`, `PARCEL_TOO_BIG`, `DEF_NOT_IN_MODPACK`,
`SAVE_STALE`, `RECIPIENT_UNKNOWN`, `ECONOMY_FROZEN`, `SELF_TRADE`.

Лимиты (конфиг `Economy:*`, значения по умолчанию): `MaxParcelBytes` 900 КБ (921600; посылка должна помещаться в одно gRPC-сообщение ≤ 1 МБ), `MaxItemsPerParcel` 200,
`MaxPawnsPerParcel` 10, `MaxLotsPerPlayer` 20, `MaxPendingParcels` 50, `LotLifetimeDays` 30,
`MarketFeePercent` 0, `SendCooldownSeconds` 5, `Enabled` true.

## 4. Клиент

### Консоль связи

Новый пункт в меню консоли связи «Rimlink» (Harmony-постфикс `Building_CommsConsole.GetCommTargets`
добавляет цель «Rimlink»; выбор открывает `Dialog_Economy`). Вкладки:

- **Рынок**: лоты всех игроков (дескрипторы), фильтр по тексту/продавцу, «Купить» → `Market.Buy` → посылка
  прилетает сразу (игрок на карте колонии) или ждёт в ящике.
- **Мои лоты**: «Выставить» — выбор из вещей под торговыми маяками (ванильный `TradeUtility.AllLaunchableThingsForTrade`)
  или пешки из списка колонистов/животных карты; цена; «Снять».
- **Отправить**: получатель из списка игроков сервера (онлайн не обязателен), вещи/пешки/серебро из кошелька.
- **Кошелёк**: баланс, история, «Внести» (серебро под маяками → `Wallet.Deposit`), «Выдать n» (→ посылка дроп-подом).
- **Ящик**: входящие, «Принять сейчас»; по умолчанию посылки принимаются автоматически при входе в колонию.

Без консоли связи и маяка вкладки «Выставить/Отправить/Внести» недоступны (как ванильная торговля).

### Выход вещей и пешек

1. Вещи снимаются с карты (`Thing.SplitOff`/`DeSpawn`), пешка — `DeSpawn` после подтверждения
   («покинет колонию навсегда»; для людей — предупреждение об отношениях).
2. Каждый объект сериализуется ванильным `Scribe` в строку XML (`ScribeSaver` в `MemoryStream`, корень `<thing>`/`<pawn>`
   в режиме `LoadSaveMode.Saving`, `DeepSave`), сжимается zstd; строится дескриптор.
3. `SaveSync.TriggerSave("transfer")` → после записи файла и выгрузки клиент вызывает RPC с `game_tick` этого сейва.
4. RPC не удался → объекты возвращаются на карту тем же `Scribe` (десериализация из своего же блоба) и
   `Messages` с причиной; следующий автосейв зафиксирует возврат.

Пешки: отношения с оставшимися колонистами обрабатываются как при уходе пешки из колонии
(`PawnRelationUtility`/вызов `Pawn.SetFaction(null)` до сериализации не нужен — получатель вызывает
`SetFaction(OfPlayer)` при спавне); идеология, гены, ранения, инвентарь, экипировка — внутри блоба.

### Вход посылок

- Триггеры: вход в колонию (после загрузки), событие `ParcelArrived`, кнопка «Принять сейчас». Только когда
  текущая карта — карта поселения игрока; иначе посылка ждёт.
- Вещи: `DropPodUtility.DropThingsNear` у торгового маяка (или центра колонии). Пешки: тот же дроп-под;
  перед спавном `Pawn.SetFaction(Faction.OfPlayer)`, для людей — `PawnComponentsUtility.AddAndRemoveDynamicComponents`.
  Серебро из посылки — в кошелёк (физическим становится только по «Выдать»).
- После успешного спавна — `Mailbox.Deliver(parcel_id, game_tick)`; затем `SaveSync.TriggerSave("delivery")`.
- Посылка, принятая, но не сохранённая (крэш до сейва): при входе клиент сравнивает `delivered_tick` посылки с
  тиком загруженного сейва — если сейв старше, посылка спавнится повторно (сервер отдаёт такие посылки в `List`
  с флагом `redeliver`). Это единственный повторный спавн; второго `Deliver` сервер не ждёт.

### Караван к чужому поселению

`PlayerSettlement` получает `GetCaravanGizmos`/`GetFloatMenuOptions` «Передать игроку …». По прибытии каравана
на тайл открывается `Dialog_Economy` в режиме «Отправить» с источником — инвентарь каравана (вещи и пешки каравана).
Отправленные пешки удаляются из каравана; если ушли все — караван расформировывается ванильно. Серебро из инвентаря
каравана можно «внести» в кошелёк здесь же. Караван остаётся на тайле, игрок уводит его сам.

### Клиент-ядро

`src/Rimlink.Client.Core/Economy/EconomyClient.cs`: типизированные обёртки RPC, `MailboxState` (ожидающие
посылки, баланс, свои лоты) в `SharedState`, события `ParcelArrived/LotSold/WalletChanged` через тот же `EventInbox`.
Сериализация блобов — в игровом слое (`Game/Economy/ThingBlobs.cs`), ядро работает с байтами.

## 5. Согласованность и антидюп

- `Send`/`Post`/`Deposit` несут `game_tick`; сервер записывает `Player.last_outgoing_tick = max(...)` в той же
  транзакции. `Saves.Upload` с `header.game_tick < last_outgoing_tick` → `SAVE_STALE`: отданное уже у другого игрока.
  Клиент при `SAVE_STALE` предлагает загрузить серверную копию (его локальный файл сохраняется как `*.stale-<дата>.rws`).
- Админский rollback на версию с `game_tick < last_outgoing_tick` разрешён, пишется в журнал действий
  с пометкой «возвращает отданное»; `last_outgoing_tick` при этом сбрасывается на тик версии.
- Покупка: одна транзакция SQLite с `BEGIN IMMEDIATE` — проверка `status = open`, баланс покупателя ≥ цена,
  списание/зачисление (`WalletEntry` ×2, комиссия — третья запись серверу-«банку» `player_id = 0`), посылка
  покупателю, лот `sold`, `Transfer`. Гонка двух покупателей — второй получает `LOT_GONE`.
- Выдача серебра: `Withdraw` списывает сразу, посылка `withdraw`; если посылка не принята — серебро «в пути»,
  видно в истории; админ может `refund`.
- Истечение лотов: фоновая задача раз в час — `expired` + посылка `lot_return` продавцу + `LotClosed`.
- Посылки не удаляются; старше 90 дней без доставки — в сводке админу.
- `Economy:Enabled=false` или `/economy freeze` → все операции `ECONOMY_FROZEN`; доставка посылок (`Deliver`) разрешена.
- Нельзя отправить самому себе (`SELF_TRADE`), купить свой лот (`SELF_TRADE`).

## 6. Модпак и дефы

`rimlink-tools modpack defs --mods-dir … --server … --token …` собирает список `defName` типов `ThingDef`,
`PawnKindDef` из XML активных модов (без запуска игры) и загружает на сервер (`Modpack.SetDefs`, admin). Сервер
отвергает блобы с неизвестными дефами (`DEF_NOT_IN_MODPACK`). Пустой список = проверка выключена
(с предупреждением в логе при старте). При строгом модпаке проверка страхует от клиента с лишним модом.

## 7. Администрирование

Чат `/`, REST `/api/admin/economy/*`, CLI `rimlink-tools admin …`: `wallet <player> [+n|-n]`, `parcels <player>`,
`lots [player]`, `refund lot <id>` / `refund parcel <id>` (возврат отправителю), `economy freeze|unfreeze`,
`economy stats`. Каждая операция — в журнале действий ядра. Метрики Prometheus: `rimlink_trades_total{kind}`,
`rimlink_silver_moved_total`, `rimlink_parcels_pending`, `rimlink_economy_errors_total{code}`.

## 8. Обработка ошибок (клиент)

| Ситуация | Поведение |
|---|---|
| `WALLET_INSUFFICIENT` | Сообщение с балансом и ценой; лот остаётся |
| `LOT_GONE` | «Лот уже продан или снят», список обновляется |
| `PARCEL_TOO_BIG` / лимиты | Сообщение до отправки (клиент считает размер сам), вещи не снимаются с карты |
| `DEF_NOT_IN_MODPACK` | Вещи возвращаются на карту, сообщение с именем дефа |
| `SAVE_STALE` при выгрузке | Диалог: загрузить серверную копию (рекомендуется) или выйти; локальный файл → `*.stale-<дата>.rws` |
| Обрыв сети во время `Send` | Вещи возвращаются на карту; если сервер всё же принял — при реконнекте клиент видит `Transfer` с тем же `client_request_id` и повторно снимает вещи (идемпотентность по `client_request_id` в `SendRequest`) |
| Дроп-под не может приземлиться (нет места) | Ванильный выбор точки; `Deliver` только после успешного спавна |

## 9. Тестирование

- Сервер (`WebApplicationFactory`): покупка (баланс, посылка, лот), гонка двух покупателей, комиссия, истечение
  лота, `Withdraw`/`Deposit`, отрицательный баланс невозможен, `SAVE_STALE` после `Send`, идемпотентный
  `Deliver`, `redeliver`, лимиты, `DEF_NOT_IN_MODPACK`, `ECONOMY_FROZEN`, `SELF_TRADE`, rollback с пометкой,
  баланс = сумме `WalletEntry` после случайной серии операций.
- Клиент-ядро: `EconomyClient` против реального сервера (посылка приходит событием, баланс в `SharedState`),
  контрактные тесты блобов на фиксированных XML (round-trip байт в байт).
- Игра (ручной чек-лист): отправить/принять вещь, животное, человека; выставить/купить; караван к чужому
  поселению; откат на старый сейв → `SAVE_STALE`; приём после крэша до сейва → повторный спавн; `/economy freeze`.

## 10. Порядок работ

Сервер (миграция, кошелёк, посылки, рынок, журнал, антидюп в `SaveStore`, дефы модпака, админ) → клиент-ядро
(`EconomyClient`, состояние, события) → игровой слой (блобы, консоль связи, дроп-поды, караван) → документация
(player-guide: раздел «Торговля», server-setup: `Economy:*`, `modpack defs`) → ручной чек-лист.

## 11. Вне рамок

Серверный магазин, аукционы/ставки, налоги и симуляция цен, передача построек и чертежей, фракции игроков
как `FactionDef`, визиты/рейды, торговля с НПС-поселениями через сервер.
