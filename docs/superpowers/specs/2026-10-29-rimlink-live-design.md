# Rimlink — живые рейды и визиты (подпроект 3b): дизайн

Дата: 2026-10-29. Статус: утверждён в обсуждении, ждёт план.
Основа: `2026-10-28-rimlink-raids-visits-design.md` (3a: рейды, визит-копия, магазин, эскроу, лимиты, отчёт),
`2026-10-28-rimlink-economy-design.md`, `2026-10-28-rimlink-core-design.md`. Протокол расширяется только
добавлением (новый `live.proto`, новые события в `oneof`, новые поля).

## 1. Цель и границы

Когда оба игрока онлайн, гость (атакующий или визитёр) управляет своими пешками на **живой** карте
хозяина: видит зеркало его игры с задержкой сетевого тика и отдаёт ванильные приказы. Хозяин авторитарен:
бой и работа считаются только в его игре. Сервер — ретранслятор кадров между двумя клиентами.

Решения, принятые в обсуждении:

| Вопрос | Решение |
|---|---|
| Контроль гостя | «Полный ванильный» в реализуемой трактовке: всё меню ПКМ (любой `Job` с целями), призыв, движение, атака, приоритеты работ и расписание (визит), инвентарь/снаряжение. Не входит: строить, зонировать, назначать на карте хозяина |
| Симуляция | Хозяин считает всё; гость видит зеркало (снимок = сейв хозяина + дельты раз в 6 тиков). Лок-степа нет |
| Обрыв гостя | Хозяин на паузе 5 минут в ожидании; вернулся — `Resync`; нет — деградация в 3a (рейд под ИИ-лордом, визит — пешки уходят с карты) |
| Скорость и пауза | Минимум из скоростей двух сторон; пауза любого — пауза для всех; гостю не более 10 пауз по 60 с на сессию |
| Старт | Флаг хозяина «автоматически принимать живые сессии» + приглашение на 60 с, если флага нет; отказ/таймаут → откат в 3a (ИИ-рейд с теми же пешками / визит-копия) |
| Пешки гостя | Всегда Real (эскроу, возврат посылкой с состоянием после сессии) |
| Участники | 1 хозяин + 1 гость на сессию; несколько гостей — вне рамок |
| Гостю некем управлять (решение 2026-10-29, предложение P6) | Хозяин сам завершает сессию, как по `End` гостя (`END_NORMAL` → `Ending` → отчёт), когда у гостя не осталось пешек: каждая ушла с карты, мертва, в плену у колонии или пропала (`RaidResolver.NoneLeft`; живая пешка где угодно — лежит, её несут, она в капсуле или у другой фракции — держит сессию, иначе отчёт назвал бы её мёртвой). Условие проверяется раз в секунду, не раньше первого `Hello` гостя (иначе гость, ещё грузящий зеркало, пропустил бы конец), и должно выполниться дважды подряд, чтобы кадр со смертью или пленом дошёл до гостя; во время `Resync` не проверяется. Новой причины в `LiveEndReason` нет |

Вне рамок: несколько гостей на одной карте, живая торговля лицом к лицу (магазин 3a работает и в
живом визите), управление хозяйскими пешками гостем, стройка/зоны гостя, лок-степ, зрители.

## 2. Жизненный цикл сессии

Виды: `LiveRaid`, `LiveVisit`. Состояния `live_sessions`:

```
Proposed ─accept/auto─► Starting ─Hello гостя─► Running ─End/обрыв/нет пешек гостя─► Ending ─Report хозяина─► Ended
    │                                              │
    └─ decline/60 с ─► Fallback (3a)               └─ PeerLost ─► Waiting (≤ 5 мин) ─► Running | Ending
```

- **Proposed**: гость на тайле хозяина (караван или капсулы) жмёт «Живой рейд»/«Живой визит». Сервер
  проверяет: хозяин онлайн и на домашней карте (клиент хозяина держит флаг присутствия `at_home` в
  сессии), для рейда — правила 3a (`RaidRules.CheckLaunch`, мирный режим, лимит активных), пешки ≤
  `MaxRaiders`. `auto_accept_live` у хозяина → сразу `Starting`; иначе хозяину `LiveInvite` на
  `InviteSeconds` (60). Гость ждёт с кнопкой «Отмена».
- **Fallback**: отказ или таймаут → рейд создаётся в 3a (`Launch` с теми же пешками, `timing` из
  `timing_fallback` гостя), визит → гостю предлагается визит-копия 3a. Гостю событие `LiveFallback`.
- **Starting**: пешки гостя уходят в эскроу (конвейер `Outgoing.Run`: снять с каравана/подов → сейв →
  выгрузка → `Propose`; при `Fallback` они же идут в 3a). Хозяин спавнит их из блобов (у края карты
  или подами) с новыми ID: рейд — фракция `RimlinkRaiders` без лорда; визит — `Faction.OfPlayer` с
  меткой владельца. Хозяин сохраняется (`live-snapshot`), выгружает, сервер отдаёт гостю
  `snapshot_version`; гость качает (`Saves/DownloadOf` по тикету сессии) и грузит копию в режиме
  зеркала. До `Hello` гостя хозяин на паузе.
- **Running**: дельты и приказы (раздел 3).
- **Waiting**: heartbeat одной стороны пропал на 10 с → другой стороне `PeerLost`; при потере гостя
  хозяин ставится на паузу с таймером `ReconnectWaitMinutes` (5); гость вернулся (`Stream` той же
  сессии) → `Resync` (новый снимок); нет → деградация: рейд — лорд `LordJob_AssaultColony` под ИИ,
  исход как в 3a; визит — пешки гостя уходят с карты, `Ending`. Хозяин не может снять паузу ожидания;
  кнопка «Прервать сессию» = деградация сразу.
- **Ending**: по `End` гостя, «Прервать сессию» хозяина или, по решению P6, сам у хозяина, когда ни одна пешка гостя не может
  действовать (мертвы, в плену, ушли или пропали; лежащие ещё держат сессию). Хозяин сериализует гостевых пешек (на родине
  пешка из блоба приходит без работы: её работа была привязана к карте хозяина) и считает исход `RaidResolver`-ом 3a (без таймаута):
  мертва / пленена / жива (визит: все живые — `FLED`, вещи хозяина из их инвентаря сбрасываются на
  карту перед сериализацией, лут не считается) → `Report` через outbox → сервер превращает в посылки
  гостю (`RaidReturn`, `Loot`, `Captive`) → `Ended`. Затем хозяин сохраняется.
- Хозяин пропал: сервер → `Ending`, гостю `LiveEnded(HostLost)`, копия закрывается, свой сейв
  грузится обратно. Пешки гостя остаются в сейве хозяина с меткой владельца; при следующем входе
  хозяина `LoginReply.pending_live_ends` → его клиент доигрывает `Ending` (сериализует, `Report`).
- Пауза: `speed = min(host, guest)`; `Pause` любого → пауза с индикатором «кто»; гостю
  `GuestPauses` 10 × `GuestPauseSeconds` 60, дальше `OrderResult(ok=false)`.

## 3. Протокол синхронизации (`live.proto`, сервис `Live`)

```
service Live {
  rpc Propose (LiveProposal) returns (LiveSession);   // client_request_id, host_player_id, kind, pawns, timing_fallback, arrival, game_tick
  rpc Respond (LiveAnswer) returns (LiveSession);     // session_id, accept
  rpc Cancel (LiveRef) returns (Empty);               // гость до Starting; хозяин = «прервать сессию»
  rpc Current (Empty) returns (LiveSession);
  rpc Stream (stream LiveClientMsg) returns (stream LiveServerMsg);
  rpc SetAutoAccept (AutoAcceptRequest) returns (PlayerFlags);
}
```

Один `Stream` на участника; сервер ретранслирует кадры второму участнику той же сессии без
интерпретации (проверяет только размер, частоту и маршрут). Кадр ≤ `MaxFrameBytes` (256 КБ); дельты
≤ `DeltasPerSecond` (20), приказы ≤ `OrdersPerSecond` (30); лишнее отбрасывается с `Notice`.

Решение 2026-10-29 (прогон на стенде s1): дельта — разница с тем, что хозяин считает состоянием гостя, поэтому отброшенная дельта
не «заменяется следующей», её изменения (уход унесённой пешки) теряются. Хозяин шлёт не чаще 10 дельт в секунду (на высокой
скорости растёт дельта, а не их число), ставит в очередь все кадры дельты или ни одного и не фиксирует у себя дельту, которую его
поток не принял (следующая повторит изменения). Кадры дельт нумеруются (`Delta.delta_seq`, 1, 2, 3… на поток хозяина; 0 — старый
хозяин): гость, увидевший пропуск номера (сервер отбросил кадр лимитом или очередью 64, обрыв), или отбросивший дельты в своей
очереди, просит `ResyncRequest`, не чаще раза в 10 с.

**Хозяин → гость, `Delta`** (раз в 6 тиков, не чаще 10 в секунду; на паузе — раз в секунду пустая):
- `tick`, `speed`, `paused_by` ∈ {none, host, guest}, `snapshot_seq` (версия сейва-снимка).
- `pawns[]` (только изменившиеся, все пешки карты): `thing_id, cell, rot, drafted, downed, dead, job_def,
  job_target (thing_id|cell), health_pct, mental_state_def, carried_thing_id, path_next_cell`.
- `things_spawned[]` (`thing_id, cell` + полный блоб `ItemBlob`/`PawnBlob` — вещи, которых не было в
  снимке: дроп-поды, трупы, постройки), `things_despawned[]` (`thing_id`), `things_changed[]`
  (`thing_id, cell, stack_count, hp`).
- `cells[]` (`cell, kind ∈ {roof, terrain, snow}, value`), `fires[]` (`cell, size, gone`).
- `damage_events[]` (`attacker_id, victim_id, amount, body_part_def, damage_def`) — для эффектов и
  журнала гостя; `letters[]`/`messages[]` хозяйской игры, адресованные гостю (`text, look_target`).
- Раз в 10 с: `guest_pawn_blobs[]` — полные блобы гостевых пешек (здоровье переустанавливается целиком).

**Гость → хозяин, `Order`** (`seq`, `pawn_id`, один из): `Draft(bool)`, `MoveTo(cell)`,
`AttackTarget(thing_id|cell)`, `TakeJob(job_def, target_a, target_b, target_c, count, force)`,
`SetWorkPriority(work_def, prio)`, `SetSchedule(hour, assignment_def)`, `Equip(thing_id)`,
`Wear(thing_id)`, `Drop(thing_id)`, `SetSpeed(speed)`, `Pause(bool)`. Ответ `OrderResult(seq, ok,
reason)`. Приоритеты и расписание — только в визите.

**Служебные** (оба направления): `Hello(role, snapshot_seq)`, `Heartbeat` (каждые 2 с), `ResyncRequest`
(гость) → хозяин сохраняется и выгружает → `SnapshotReady(save_version)` → гость перекачивает,
`PeerLost`/`PeerBack` (от сервера), `End(reason)`.

Снимок = обычный сейв хозяина; `Saves/DownloadOf` принимает тикет живой сессии так же, как тикет визита.

## 4. Зеркало у гостя

Копия загружается `VisitLoader`-ом 3a (временная игра, перефракционирование хозяина по виду сессии,
запрет сохранений и выгрузок). `MirrorMode` — флаг в `GameComponent` и Harmony-патчи, активные только
при нём:

- **Время**: `TickManager.TickManagerUpdate` — локальные тики не идут сами; тикаем ровно до `Delta.tick`
  партиями ≤ 60 тиков за кадр; отставание > 600 тиков → `ResyncRequest`. Скорость и пауза — из дельты;
  кнопки скорости шлют `SetSpeed`/`Pause` (перехват setter `TickManager.CurTimeSpeed`).
- **Пешки**: `Pawn.Tick` — ИИ, работы, `pather`, нужды, здоровье не тикают; `Drawer` и анимации идут.
  Из дельты: позиция/поворот с интерполяцией к `path_next_cell`, `drafter.Drafted`, `Downed`/`Dead` как
  флаги отображения (`Kill`/`forceDowned` не вызываются; труп приходит блобом в `things_spawned`),
  `curJob` подменяется лёгким `Job` того же `def` и цели для инспектора.
- **Вещи**: `things_spawned` → десериализация с *теми же* ID (`newIds: false`; хозяин — источник ID) и
  `GenSpawn.Spawn`; `things_despawned` → `DeSpawn` + `Destroy(Vanish)`; `things_changed` → поля.
  Локальные источники вещей и изменений (рост растений, гниение, `Fire.Tick`, `TickLong`-механики)
  выключены патчами; огонь и клетки рисуются из `fires`/`cells`.
- **Урон**: `damage_events` → `MoteMaker`, звуки, журнал боя; здоровье — `health_pct` и периодические
  полные блобы своих пешек.
- **Ввод**: выделять можно только свои пешки (метка владельца); чужие — инспектор без гизмо. ПКМ-меню
  строится ванильно (`FloatMenuMakerMap`) на зеркале, `FloatMenuOption.action` подменён на `TakeJob`;
  гизмо призыва/атаки/снаряжения — на `Draft`/`AttackTarget`/`Equip`/`Wear`/`Drop`; назначения, зоны,
  стройка, биллы скрыты. Отказ `OrderResult` → сообщение.
- **Сеть**: `LiveClient` (клиент-ядро) держит стрим; дельты дренируются в `MainThread` раз в кадр;
  heartbeat 2 с; обрыв → автопереподключение до `ReconnectWaitMinutes` (та же `ReconnectPolicy`), затем
  «сессия потеряна»: копия закрывается, свой сейв грузится обратно; пешки вернутся посылкой.

## 5. Хозяин: авторитет, владение, приказы, исход

- **Владение**: `MapComponent_LiveOwners` (`thing_id → owner_player_id`, `ExposeData`). У хозяина чужие
  пешки нельзя выделять и командовать (патчи `Selector`/`Pawn.GetGizmos`). Рейд-гости — `RimlinkRaiders`
  без лорда: стоят, пока гость не прикажет, ванильного бегства нет. Визит-гости — `Faction.OfPlayer` с
  меткой; `WorkGiver` хозяина их не берёт, работают по приоритетам гостя.
- **Исполнение приказов** (`LiveHost.Apply(Order)`, главный поток): проверка владения; `Draft` →
  `drafter.Drafted`; `MoveTo` → `Job(JobDefOf.Goto, cell)`; `AttackTarget` → `AttackMelee`/`AttackStatic`
  по оружию; `TakeJob` → `Job` из `job_def` и целей (`LocalTargetInfo` по thingID/клетке), проверка
  `CanReserveAndReach` и `TryMakePreToilReservations`, затем `jobs.TryTakeOrderedJob(job, JobTag.Misc)`;
  `SetWorkPriority`/`SetSchedule` → `workSettings`/`timetable`; `Equip/Wear/Drop` → соответствующие
  `Job`. Отказ → `OrderResult(ok=false, reason)`. `LiveRules.ForbiddenForRaid` (стройка, зоны, мебель
  хозяина) и `LiveRules.ForbiddenForVisit` (`Deconstruct`, `Uninstall`, `Ignite`, `Slaughter`).
- **Сбор дельт** (`LiveHost.Tick`, раз в 6 тиков): диф с кешем прошлого кадра; хуки `GenSpawn.Spawn`,
  `Thing.DeSpawn`, `Thing.TakeDamage`, `Pawn.Kill`, `Fire` пишут события в очередь кадра; полный скан
  карты — раз в 10 с (самопроверка, `things_changed`).
- **Темп**: `SetSpeed` → `min(host, guest)`; `Pause` гостя → пауза с индикатором, лимит пауз; пауза
  ожидания не снимается хозяином (только «прервать сессию»).
- **Снимок**: `SaveSync.TriggerSave("live-snapshot")` на старте и при `ResyncRequest`, ждать выгрузки;
  до `Hello` гостя — пауза.
- **Исход**: `Ending` → `RaidResolver` 3a по гостевым пешкам без таймаута; визит — все живые `FLED`,
  `inventory.DropAll` перед сериализацией; `Report` через outbox; затем сейв.
- **Защита**: `Propose` применяет кулдауны/защиту/мирный режим/лимит активных из 3a; `LiveRaid`
  считается активным рейдом атакующего; лимит лута тот же.

## 6. Сервер

- `live_sessions`: id, kind, host_player_id, guest_player_id, state, raid_id (для `LiveRaid` строка
  `raids` с `controller = Live` держит эскроу и правила), pawns (эскроу `LiveVisit`), snapshot_version,
  created_at, started_at, ended_at, end_reason, client_request_id (уникален на гостя).
  `players` += `auto_accept_live`; `Session` += `at_home` (клиент сообщает `Presence/SetAtHome`).
- `LiveRelay` (singleton): на сессию два канала; очередь ≤ 64 кадров на получателя; переполнение
  выкидывает старые дельты, приказы и служебные кадры не выкидывает; повторный `Stream` того же игрока
  — переподключение (старый канал закрывается).
- Таймауты: heartbeat 10 с → `PeerLost` второму, `Waiting`; `ReconnectWaitMinutes` → `Ending`
  (`GuestLost`/`HostLost`); `InviteSeconds` → `Fallback`. `LiveTimeoutService` (раз в 5 с).
- `LoginReply` += `pending_live_ends[]` (сессии в `Ending`, где этот игрок — хозяин).
- Конфиг `Live:*`: `Enabled` true, `InviteSeconds` 60, `ReconnectWaitMinutes` 5, `GuestPauses` 10,
  `GuestPauseSeconds` 60, `MaxFrameBytes` 262144, `DeltasPerSecond` 20, `OrdersPerSecond` 30.
- Ошибки (`ErrorCode` 28–31): `HOST_OFFLINE`, `LIVE_ACTIVE` (у стороны уже есть сессия), `LIVE_GONE`,
  `LIVE_DISABLED`. События (`oneof` 70–75): `LiveInvite`, `LiveStarted`, `LiveEnded`, `LiveFallback`,
  `LivePeerLost`, `LivePeerBack`.
- Админ: `live list|end <id>`, REST `/api/admin/live/*`; метрики `rimlink_live_sessions_total{kind,end_reason}`,
  `rimlink_live_frames_total{dir}`, `rimlink_live_active`; аудит старт/конец/прерывание.

## 7. Тестирование

- Сервер: `LiveSessionTests` (propose → invite → accept → Starting/Running; auto-accept; decline и
  таймаут → `LiveFallback` + строка 3a; хозяин офлайн/не дома → `HOST_OFFLINE`; кулдауны, мирный,
  `LIVE_ACTIVE`; `Cancel`), `LiveRelayTests` (два тестовых стрима: дельта доходит только второму; лимиты
  частоты и размера → кадр отброшен + `Notice`; переполнение выкидывает старые дельты, не приказы;
  heartbeat-таймаут → `PeerLost`, переподключение тем же игроком, `Ending(GuestLost)` по истечении),
  `LiveEndTests` (`Report` из `Ending` → посылки гостю; `HostLost` → `pending_live_ends` в `LoginReply`),
  админ, метрики, схема.
- Клиент-ядро: `LiveClientTests` (стрим против реального сервера: дельты в очередь, приказы, heartbeat,
  автопереподключение с `ResyncRequest`), `DeltaCodecTests` (модель без Verse: кеш → дельта → применение
  даёт исходное состояние; большая дельта режется на кадры ≤ 256 КБ).
- Игра, ручной чеклист на двух клиентах (`-savedatafolder`): живой рейд (призыв, движение, атака, пауза
  гостя и её лимит, отказ приказа, ранение/смерть/плен, исход и посылки), живой визит (приоритеты,
  покупка, подарок), обрыв гостя с возвратом и без, обрыв хозяина, `Resync` при отставании, отказ
  приглашения → откат в 3a.
- Review Focus плана: приказ пешке, умершей в дельте в пути; `ResyncRequest` во время паузы гостя;
  хозяин сохраняется с гостевыми пешками и падает; два `Propose` подряд; дельта с вещью, которой нет в
  снимке.

## 8. Порядок работ

1. Протокол, хранение, флаги, присутствие `at_home`, конфиг.
2. Сервер: сессии (propose/respond/fallback/cancel), таймауты, `pending_live_ends`.
3. Сервер: `LiveRelay` и `Stream` с лимитами.
4. Клиент-ядро: `LiveClient`, кодек дельт, очередь, переподключение.
5. Игра, хозяин: владение, спавн гостей, сбор дельт, исполнение приказов, темп, снимок, исход.
6. Игра, гость: `MirrorMode` (время, пешки, вещи, урон), ввод и приказы, UI сессии.
7. UI приглашений и флагов, строки, документация, чеклист, проверка на двух клиентах.

## 9. Вне рамок

Несколько гостей, зрители, управление чужими пешками, стройка и зоны гостя, живая торговля лицом к лицу,
лок-степ.
