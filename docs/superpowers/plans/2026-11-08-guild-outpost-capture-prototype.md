# C: изолированный прототип игрового арбитра

Дата: 8 ноября 2026 года. Исполнитель: основная `gpt-6.1-sol`.

**Goal:** доказать на настоящей RimWorld 1.7 возможность одного независимого арбитра, двух внешних контроллеров, фиксированного боя 4×4, независимого objective result и однократного применения результата в изолированном тестовом ledger. Не реализовывать Product-захват или ресурсную экономику.

**Spec:** [утверждённая пользователем 8 ноября спецификация C](../specs/2026-11-07-guild-outpost-capture-design.md). Все четыре продуктовых решения уже приняты; технические gate не являются повторным запросом согласия на них.

**Architecture:** dev-only расширение существующего `Rimlink.BridgeTools` + отдельный локальный `.NET` coordinator под `tools/`. Три собственных игровых процесса: арбитр с настоящей картой, два контроллера в отдельных профилях без личной колонии. Для первой стадии контроллеры получают ограниченный тактический mirror в native debug-окне (авторитетная сетка, позиции восьми бойцов, состояние здоровья/боеспособности, зона, tick/sequence/result) и посылают команды из своих процессов. Это не готовый обычный `LiveGuest` и не доказательство полной синхронизации произвольной карты. Полноценный production mirror, рынок, гильдейские объекты и маршруты выдачи остаются следующей стадией.

**Baseline:** историческая база чтения API — `6b26142d002be85f3de5ec2511427d58fd1c31e7`; готовая кодовая база после завершения residual fixes — **`820749314a4305d9566830b34de99c3a0c4e8b37`**. Для неё подтверждены сборка, полные Windows и Linux/two-CPU suites по 3832/3832 и native local-pending guard с успешным последующим recovery. Это проверки residual fixes, не прототипа. Отдельная feature-ветка ещё не создана, Task 0 и `ENV-PASS` не выполнены. Перед реализацией создать ветку от этой кодовой базы с утверждёнными документами проектирования и записать её фактический base commit; не выдавать дату/commit плана за выполненный игровой прогон.

## Global Constraints

1. Только новые dev-файлы `tools/`, отдельные tests и разрешённые изменения dev `.csproj`/документации. Никаких изменений `src/`, production `.proto`, EF schema, `mod/`, Product gates, `Directory.Build.props`, release packaging, конфигурации рабочего сервера. Если это становится необходимо для feasibility — STOP с конкретной причиной, не расширять scope молча.
2. Участники не контролируют outcome/runtime state. Одноразовые side capabilities отделены от executor capability; `ConfirmDefeat` отсутствует. `.NET` coordinator не симулирует игру, доверяет только закреплённому игровому арбитру.
3. `RimlinkSession.Active == false` во всех трёх процессах; никакого login на рабочий сервер, вызова `LiveHost.Begin`, `LiveGuest`, `SaveSync.TriggerSave`, `RaidService.Report`, почты или pawn admission. Тестовый owner ledger нельзя соединить с production API.
4. Не использовать личные сейвы, замороженных работников, их экипировку и custody. Нейтральный шаблон создаётся новой vanilla-игрой в новом собственном профиле; извне принимаются только проверенные шаблон/правила, никогда пользовательский blob/код.
5. Game objects — main thread. Команды исполняются на границе simulation ticks; факт победы вычисляется в конце **каждого** `DoSingleTick`, а не раз за визуальный frame, где может пройти несколько тиков. Во время snapshot/result capture следующий тик не запускается.
6. Отдельный каталог стенда, DataDir coordinator, три savedata, независимый GABS configDir и свои process IDs. Не перезаписывать общий GABS с 14 профилями, не использовать старые `rimworld-host/guest`, не останавливать чужие игры. Зафиксировать владельца процесса до любого crash test.
7. GUI — только CUA Driver. GABS/Bridge — профильный игровой API. Никаких Playwright/CDP/OS-keyboard обходов. Фоновый coordinator запускается скрыто; видимые игровые окна — только необходимые изолированные процессы прототипа.
8. Никаких invented headless/arbiter launch flags. Команды и параметры GABS проверяются установленным `--help`/tool schema в Task 0. Нельзя считать наличие файлов доказательством лицензионного права; не покупать/скачивать игру или DLC без отдельного запроса.
9. Не копировать game binaries/decompiled source в Git, не переносить чужой multiplayer. API signatures допустимы как evidence; реализация адаптера — своя. Игровые snapshots/artifacts содержатся только в игнорируемом каталоге стенда.
10. Поддерживаемая здесь долговечность — проверенные process crash на конкретной файловой системе; не объявлять power-loss или Product restore квалифицированными по JSON/SQLite fixture. Нет deterministic game replay: неизвестный исход → TechnicalAbort/RecoveryRequired.
11. Один итоговый обзор **этого будущего прототипа**, после всех задач, без поэтапных code reviews. Ранее завершённый итоговый обзор residual-исправлений не повторять и не применять к новой реализации. Коммиты/push/release/deploy этим планом автоматически не выполняются; перед отдельным разрешённым commit — `git-safe-commit` и необходимые зелёные проверки.
12. Все файлы с русским текстом UTF-8. Не изменять чужие WIP. Ledger выполнения `.superpowers/sdd/guild-outpost-capture-prototype/progress.md` — локальный, с задачами, source/build boundaries, PASS/STOP и rulings, без секретов.

## Review Focus

- Две реальные независимые стороны, запрет чужих команд и forged result; никаких заглушек, которые объявляют победителя вместо игрового state.
- Не пересекаются ID/credentials/snapshot paths battle и домашней колонии; бойцы не экспортируются.
- Один Start, monotonic fencing token, записанный final artifact до ResultAccepted, owner fixture transition ровно один раз; crash после любого commit не создаёт второй исход.
- Tick-level objective, ничья в одном тике, defensive AI и reconnect; визуальный frame и клиентский timestamp не определяют победителя.
- Отсутствие auto-fallback на host, replay неопределённого боя и переносов через fixture restore boundary.
- Честная граница прототипа: debug tactical mirror не равен производственному full-map mirror; resource fixture не доказывает настоящее escrow/выдачу; Product Pending/OFF сохраняется.

## Проверенные prerequisites и непроверенные части

8 ноября чтением установленной копии подтверждены `Assembly-CSharp.dll`, `Waylith.dll`, `UnityStubs.dll` и `Mods/RimBridgeServer/1.6/Assemblies/RimBridgeServer.Sdk.dll` под `D:/SteamLibrary/steamapps/common/RimWorld`. `1.6` здесь — фактический путь SDK, не заявление о версии игры. SHA-256 `Assembly-CSharp.dll`: `D55AA8E47FA3C5463CA85B0ECCD51F195B4BBCD04F56DA3AF21C2F6688A81254`; текущая установка по readback основной сессии — 1.7.5000. Проверить заново на копии стенда.

| Проверенная точка | Что можно использовать | Что нельзя из неё заключить |
|---|---|---|
| `Verse.TickManager`: `DoSingleTick()`, `TickManagerUpdate()`, `TicksGame`, `CurTimeSpeed` — прочитаны из установленной сборки | Scoped Harmony prefix/postfix, остановка и наблюдение фактических тиков | Нет готового authoritative battle scheduler |
| `Verse.PawnGenerator.GeneratePawn(PawnGenerationRequest)` и overload с `PawnKindDef`, `Faction`, `PlanetTile?` — прочитаны из сборки | Создать новые матчевые пешки после проверки параметров запроса и defs | Не доказаны равенство характеристик/отсутствие случайных отношений без post-validation |
| `Verse.GameDataSaveLoader.SaveGame(string)`, `LoadGame(string)`, `LoadGame(FileInfo)` — прочитаны из сборки | Только отдельные battle snapshots в своём профиле; ожидание завершения и hash readback | Возврат метода сам не доказывает durable snapshot или отсутствие loader callbacks |
| `tools/Rimlink.BridgeTools/Support/Bridge.cs` | `ctx.MainThread.InvokeAsync`, `ctx.Game.NextFrameAsync`, bounded waits | Не использовать `RunUntilAsync` как предположительно свободную shared control lease |
| `OrderExecutor.Apply(Order, long, LiveKind, Map)`, `MapComponent_LiveOwners.Mark/OwnerOf` | Узкая адаптация move/attack/draft для side IDs в изолированной карте | Нельзя дать контроллеру общий `TakeJob`, speed, inventory и production `LiveSession` |
| `LordJob_RimlinkGuests` | Idle lord удерживает не-player бойцов на карте до приказа | Это не готовый defensive combat AI; его надо написать и проверить в прототипе |
| `LiveHost` / `DeltaCollector` / `MirrorMode` / `MirrorApplier` | Паттерны main-thread reading, snapshot baseline и delta sequencing | Статики связаны с `LiveTempo`, `RimlinkSession`, `VisitLoader`; прямой вызов не даёт третьего neutral host |
| `AcceptanceControlPipe.cs` | Образец bounded JSON frame, CurrentUserOnly pipe, nonce binding, отсутствие guessed reply | Его outpost-семантику/credentials не переиспользовать для боевого результата |
| `Build-BridgeTools.ps1` | Сборка и установка только companion DLL; все game/client references `Private=false` | Не класть рядом вторую `Rimlink.Client.dll`: получится второй статический session state |

GABS доступен; `games_list` вернул 14 старых профилей. По проверке основной сессии GABS 1.1.2 поддерживает `server --configDir`; точная команда запуска отдельного экземпляра и его transport endpoints повторно проверяются перед использованием. Новые battle profiles пока не созданы. Два контроллера, tick hooks, генерация равных бойцов и боевой результат **ещё не испытаны**.

## Точные файлы и новые интерфейсы

Имена ниже — задания на создание, не существующие команды/API. Других файлов реализации план не разрешает без отдельного ruling и проверки scope.

| Файл | Назначение |
|---|---|
| `tools/Rimlink.BattlePrototypeHost/Rimlink.BattlePrototypeHost.csproj` | Новый dev-only net10 console coordinator; без game references, не в `Rimlink.sln`/release |
| `tools/Rimlink.BattlePrototypeHost/Contracts.cs` | Bounded DTO: binding, command, view, result; linked source в companion, без Verse |
| `tools/Rimlink.BattlePrototypeHost/BattleLedger.cs` | Изолированная SQLite authority fixture, expected state/fence/boundary, receipts и fake owner |
| `tools/Rimlink.BattlePrototypeHost/BattleTransport.cs` | Три role-bound named-pipe lanes, bounds/deadlines, command sequence, result authentication |
| `tools/Rimlink.BattlePrototypeHost/Program.cs` | Локальные команды `prepare`, `serve`, `inspect`, `fault`, `rotate-boundary`; аргумент `--stand` обязателен, production URL не поддерживается |
| `tools/Rimlink.BridgeTools/BattlePrototypeTools.cs` | Только dev tools bind/status/prepare/start/open-controller-view/stop; owner stand checks |
| `tools/Rimlink.BridgeTools/BattlePrototype/BattleRuntime.cs` | Coordinator channel, lifecycle, main-thread pump, роль arbiter/controller, scoped Harmony registration |
| `tools/Rimlink.BridgeTools/BattlePrototype/BattleScenario.cs` | Проверка нейтрального шаблона, фиксированные стороны/8 бойцов/геометрия, snapshot capture |
| `tools/Rimlink.BridgeTools/BattlePrototype/BattleRules.cs` | Чистая objective/sequence/state logic без Verse, linked в tests |
| `tools/Rimlink.BridgeTools/BattlePrototype/BattleOrders.cs` | Allowlist и адаптация проверенного `OrderExecutor.Apply`, fallback AI, control handoff |
| `tools/Rimlink.BridgeTools/BattlePrototype/BattleView.cs` | Native debug tactical mirror двух контроллеров; authoritative positions/status, отправка команд своей стороны |
| `tools/Rimlink.BridgeTools/Rimlink.BridgeTools.csproj` | Только явный link `Contracts.cs`; существующие private=false зависимости сохраняются |
| `tests/Rimlink.BattlePrototype.Tests/Rimlink.BattlePrototype.Tests.csproj` | Отдельный game-free xUnit проект, reference coordinator + link чистых правил; existing central package versions |
| `tests/Rimlink.BattlePrototype.Tests/BattleRulesTests.cs` | Objective, boundary ticks, identity, duplicate command |
| `tests/Rimlink.BattlePrototype.Tests/BattleLedgerTests.cs` | Result once, fence, restore, crash после/до commit; настоящая временная SQLite, не mock |
| `tests/Rimlink.BattlePrototype.Tests/BattleTransportTests.cs` | Две стороны, wrong role/key, lengths, partial frames/cancellation, disconnect/resync |
| `docs/verification/2026-11-08-guild-outpost-capture-prototype.md` | Публично безопасный отчёт exact source/build/runtime, gate outcomes и ограничения |
| `wiki/ru/developers/testing.md`, `wiki/en/developers/testing.md`, `wiki/content-manifest.json` | Только dev-процедура/статус прототипа, не заявление о выпущенном capture |
| `wiki/docs-impact.json`, `wiki/reference-inventory.json` | Менять лишь при фактическом drift/reference impact, с точными обоснованиями |

Профили, `stand.json`, secrets, battle snapshots, process logs, результатные артефакты, SQLite и исходный template — вне Git в новом `D:/Build/rimlink-capture-prototype-20261108/`. Не переиспользовать прошлый `rimlink-residual-guard-20261107` как боевой stand. Этот путь — назначение новой изолированной среды, не утверждение, что она уже создана.

### Контракты взаимодействия

- `Binding`: stand/run ID, role (`arbiter`, `side-a`, `side-b`), свой pipe name/nonce, expected source/build/game/mod hashes, absolute owned game/savedata roots. Никаких секретов другой роли в controller binding.
- `BattleIdentity`: campaign ID, battle UUID, restore boundary, attempt/fence, rules/template/roster digests, fixture owner revision. Все effectful сообщения содержат полный identity.
- `Command`: side определяется authenticated binding, а не полем JSON; monotonically increasing side sequence, fixed command kind, pawn ID и bounded target. Intake/result ack отдельно от applied-tick ack.
- `View`: battle/fence/snapshot sequence, actual tick, восемь battle-local IDs с side/position/health/боеспособностью, зона/control counter/result. Публикуется read-only с единого конца тика. Это полное небольшое состояние прототипа, не дельта произвольного мира.
- `FinalResult`: provenance всего Start, terminal tick/outcome, objective facts, initial/final artifact hashes, journal hash и executor authentication. Только arbiter lane может отправлять; receipt не требует side-b ack.
- Fixture ledger states: `Prepared → StartCommitted → ResultPending → Applied` либо `TechnicalAbort/RecoveryRequired`; `OwnerChangedCount` всегда 0/1. Отдельная транзакция result apply меняет fake owner и terminal receipt; это не вызов production authority.
- Лимиты прототипа: 16 KiB command/control frame, 256 KiB view frame, максимум 8 бойцов, максимум 64 pending commands на сторону, 20 команд/сек на сторону, 10 view updates/сек. JSON depth 16, duplicate/unknown fields отвергать. Oversize/queue overflow дают bounded refusal, не остановку боевого процесса. Таймеры gameplay — утверждённые §5–7 спеки.

## Task 0 — предохранители стенда и техническая инвентаризация

**Files:** verification report (новый), локальные `stand.json`/ledger; пока без кода.

1. Проверить `git status`, effective base и отсутствие чужих правок в будущих файлах; отделить завершённые residual fixes от новой feature-ветки. Не stash/reset и не commit автоматически.
2. Зафиксировать пути лицензированной установки/SDK, hash game DLL/модов и активную конфигурацию. Проверить, что подготовка трёх собственных игровых экземпляров допустима в имеющейся лицензированной среде; отсутствие подтверждения — STOP, без выдуманного обхода.
3. Проверить установленный GABS `--help`, `server --help`, поддержку `--configDir`, схему новой конфигурации и запуск без изменения общей. Выделить свои профили `capture-arbiter`, `capture-side-a`, `capture-side-b` в отдельном configDir; точные launch commands брать из прочитанной действующей схемы/профилей, а не придумывать флаги. Скопировать законно установленный runtime в новую собственную среду; исходную установку не изменять.
4. Записать executable path + process start time + PID + savedata каждого своего процесса. Проверить, что три процесса видят один game/mod build и раздельные savedata. Отсутствие активной `RimlinkSession` — обязательный readback. Глобальный `Player.log` может пересекаться: сохранить GABS launch logs каждого процесса до следующего запуска, не объявлять `-logFile` изоляцией.
5. Создать чистый template обычным vanilla new-game UI через CUA в профиле арбитра. Не использовать `rimlink/start_colony`, поскольку он требует server session. Зафиксировать точные локальные defs/стандартный scenario/совместимые DLC. Точный боевой состав/gear/template задаётся evidence manifests после проверки в runtime; пока не выдумывать defName.

**RED:** неверный savedata root, несовпадающий game hash, отсутствие SDK/одного клиента или наличие player session должны запрещать дальнейшую подготовку. **GREEN:** наблюдены три отдельные игры, одинаковые зависимости, ни одного production connection, чистый сохранённый template.

**Expected:** gate `ENV-PASS`, либо `STOP-ENV` с конкретным отсутствующим prerequisite. Чтение API и три запущенных процесса сами по себе не означают `FEASIBILITY-PASS`.

## Task 1 — чистые правила и coordinator с двумя независимыми сторонами

**Files:** новый coordinator (5 файлов из таблицы), новый tests project и три test-файла, `BattleRules.cs`.

1. RED tests: 2999/3000 непрерывных тиков, contest/reset zone, downed/mental, wipe одного/обоих отрядов в одном тике, tick 30000; время wall-clock не выдаёт DefenderWin при stalled game. Тесты используют state vectors, не поддельный «готовый боевой финал» как итог acceptance.
2. RED для wrong side/battle/boundary/fence, duplicate seq same/different intent, partial frame и потерянного ack. Отдельно два clients одновременно; fake arbiter не может пройти финальную игровую приёмку, но допустим для unit isolation.
3. Реализовать минимальные DTO/правила/transport и SQLite fixture. SQLite — существующая центральная зависимость, без новой abstraction над хранилищем. `prepare` создаёт только fixture world/object и три role bindings; secrets не выводит в лог. Свой root проверять canonical path и отсутствие reparse alias до записи.
4. Initial/final artifacts сначала завершённо записываются с hash; запись Start/result ссылается только на готовый artifact. Crash между file и DB оставляет orphan artifact для проверки, не owner transition. Process restart читает ledger; несовпавшие записи дают recovery.
5. Coordinator denylist не маскирует generic proxy: protocol вообще не имеет game blob upload, произвольного URL, команды shell или production admin action.

**Check, PowerShell из корня feature worktree:**

```powershell
rtk proxy dotnet test tests/Rimlink.BattlePrototype.Tests/Rimlink.BattlePrototype.Tests.csproj -nologo -v q
```

**Expected:** unit GREEN, независимые side keys/seq, fake owner не меняется от player result. Только `CONTRACT-PASS`; настоящий бой ещё не доказан.

## Task 2 — реальный game adapter и изолированная карта 4×4

**Files:** `BattlePrototypeTools.cs`, `BattleRuntime.cs`, `BattleScenario.cs`, `BattleOrders.cs`, dev `.csproj`; расширить tests лишь чистой логикой.

1. RED: bind в неверном процессе/с активной player session/чужом savedata должен отказать до Harmony/файловых изменений. Нет bind — никакой обработки кадров, tick patches и сетевого listener. После stop — снять только свои Harmony patches и закрыть свои каналы.
2. Добавить dev tools с описанными именами `rimlink/battle_bind`, `rimlink/battle_status`, `rimlink/battle_prepare`, `rimlink/battle_start`, `rimlink/battle_view`, `rimlink/battle_stop`. Это **новые** инструменты: сначала реализовать/собрать, затем убедиться в `get_bridge_status` и `games_tool_detail`; не вызывать до регистрации.
3. На свежем template создать безопасную симметричную arena внутри карты с утверждённой зоной, двумя сторонами и ровно 4 новыми бойцами каждой. Проверить конечные health/skills/traits/gear/позиции; генерация не должна молча оставлять случайный перекос. Не использовать исходные colonists template как «личные» бойцы: удалить/заменить только содержимое проверенного disposable template. Def IDs и состав зафиксировать после реального readback и связать digest.
4. Factions/idle lord выбирать по реально доступным собственным паттернам `RimlinkFactions`/`LordJob_RimlinkGuests`; не копировать декомпилированный AI. Проверить, что обе стороны враждебны и не уходят с карты самопроизвольно. Соответствие боеспособности — реальные `Pawn.Dead/Downed/InMentalState` и проверенные способности; unsupported predicates — STOP, не приравнивать к клиентским bool.
5. `OrderExecutor.Apply` разрешается вызвать лишь после собственного battle/side/ID/target allowlist: `MoveTo`, `Attack`, `Draft`; запрет `TakeJob`, inventory, speed, work/schedule, внешних targets. Battle-side IDs существуют только в этой карте, не являются player accounts. Если существующая функция вызывает неподходящий production side effect — не менять `src`; отделить минимальный собственный adapter на тех же проверенных public game calls.
6. Scoped prefix/postfix `DoSingleTick` проверяет фазу, применяет допустимую порцию команд до тика и вычисляет objective после. При exception/сломанном marker останавливает процесс матча на уровне дальнейших тиков. `Root.Update` служит только pump UI/network; не заменяет tick hook.
7. Initial snapshot записать обычным `SaveGame` в battle-specific basename, дождаться завершения loader/save, проверить существование/hash/roster; не вызывать `SaveSync` и не считать один return метода durability. Загрузить тот же snapshot обратно и сверить идентичности до Start.

**Check, PowerShell из feature worktree:**

```powershell
rtk proxy powershell -NoProfile -File scripts/Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-capture-prototype-20261108\game' -NoInstall
rtk proxy dotnet test tests/Rimlink.BattlePrototype.Tests/Rimlink.BattlePrototype.Tests.csproj -nologo -v q
```

Установка companion — существующим script с явным собственным game root только при закрытых своих трёх процессах; прочие приложения не останавливать. В companion directory только разрешённые companion DLL/PDB, не копия Client/Core/Protocol. Отдельно сверить одинаковую сборку Rimlink-мода в профилях.

**Expected:** `ENGINE-PASS`: настоящий map/8 бойцов/один tick source, две side identities, реальные move/attack разрешены, запрещённые команды не исполнились. При engine/API incompatibility — `STOP-ENGINE`, без расширения production кода.

## Task 3 — две внешние игры-контроллеры и feasibility gate

**Files:** `BattleView.cs`, `BattleRuntime.cs`, `BattleTransport.cs`, `BattleTransportTests.cs`, verification report.

1. RED: side-a пытается двигать side-b; side-b повторяет чужой nonce; оба посылают одинаковые номера seq; пересылка result через side lane; frame чужого battle ID. Ни одна проверка не исполняет неверную команду и не меняет fixture owner.
2. Два настоящих RimWorld-процесса в Entry открывают собственное debug battle window через companion. Окно отображает **полученное** полное малое состояние арбитра и через native controls шлёт свои commands. Нет локальной игровой симуляции или `MirrorMode.Enter` с поддельным `RimlinkSession`; оба view содержат одинаковые tick/objective/terminal facts.
3. View sequence монотонен. На reconnect запросить последнее полное состояние и watermark команд; устаревшая view не откатывает экран, неподтверждённая команда не повторяется под новым seq. Обновления UI — `Bridge.OnMain`, не из network thread.
4. Одновременно отправить движение и атаки обеих сторон. Подтвердить в readback арбитра принятый tick каждого эффекта, реальные перемещения/урон; оба клиента видят одинаковые ID и результат. Повторить при отключении одного controller. Fallback AI своим минимальным правилом выбирает допустимые цели/движение через тот же adapter, не получает особых state setters.
5. Измерить bytes/view, backlog и idle CPU; 8 бойцов/полное состояние должны укладываться в установленный frame cap. При переполнении не обрезать бойцов/цель для «успеха».

**Expected — обязательный GATE F:** `FEASIBILITY-PASS` только если один настоящий arbiter управляет боем и **два внешних реальных процесса** независимо командуют/наблюдают, есть настоящий damage/control-zone progress, чужое управление отвергнуто, production session отсутствует. Один script, отправляющий команды за обе стороны напрямую в game context, не считается двумя контроллерами. Если gate не пройден — STOP; Task 4–6 и экономика не начинаются. Не заменять C доверенным отчётом игрока.

## Task 4 — настоящий финал, однократный fixture owner transition и таймеры

**Files:** `BattleRuntime.cs`, `BattleRules.cs`, `BattleLedger.cs`, `BattleLedgerTests.cs`, report.

1. RED unit tests границ objective и once transition уже есть; добавить mismatch artifact/mod/rules hash и конфликт двух результатов. Runtime malformed state должен останавливать match, а не автоматически присуждать победу.
2. Провести настоящий бой с победой атакующих через удержание зоны. В конце terminal tick запретить следующий tick, долговечно сохранить final snapshot/journal, hash readback, отправить executor envelope. Защитник не отправляет подтверждение. Coordinator применяет fake owner transition и receipt один раз.
3. Повторить тот же envelope после потерянного ответа; `OwnerChangedCount == 1`, те же result/receipt bytes. Второй иной winner для battle ID — конфликт без изменения owner. Все дальнейшие side commands rejected terminal.
4. Реальные прогоны: победа защитников уничтожением, тайм-аут 30000 ticks, взаимное поражение; side disconnect → AI через 30 секунд; coordinator lost → pause на tick boundary, grace 60 секунд/TechnicalAbort. Полный игровой результат не заменять `SetWinner`; редкие simultaneous fixtures отдельно пометить как setup-проверку predicates.
5. Wall-clock 20 минут — monotonic clock; handler без tick progress не получает победу. Для быстрых unit fault tests допускается test clock, но хотя бы один smoke использует настоящие disconnect/grace интервалы. Изменение core balance timers ради ускоренного smoke не выдавать за обычный gameplay acceptance.

**Expected:** `RESULT-PASS`: независимый игровой финал, доверенный источник, фиктивный owner A/B меняется один раз и без подписи проигравшего. Реальные гильдии/форпосты всё ещё не создаются.

## Task 5 — process crash, stale executor и восстановление fixture

**Files:** `Program.cs` (test-only fault actions), `BattleLedger.cs`, `BattleRuntime.cs`, ledger/transport tests, report.

Fault hooks только в собственном прототипе; перед каждой остановкой сверить PID/exe/savedata/starttime и сохранить artifact hashes. Если управление процессом требуется через GUI — CUA Driver; профильные GABS stop/disconnect применять только к своему target. Чужой PID по имени не искать и не останавливать.

| Fault point | Expected |
|---|---|
| До initial file completion / до StartCommitted | Нет owner change, старта нет; partial file не принимается |
| После StartCommitted, до final evidence | После lease revocation — TechnicalAbort; новый бой только с новым ID, не guessed replay |
| После durable final, до доставки | Рестарт арбитра повторяет тот же envelope из artifacts, не запускает повторную симуляцию |
| После result transaction, до ack | Inspect/replay даёт прежний receipt; owner count 1 |
| Старый arbiter вернулся после fence rotation | Его команды/результат отвергнуты, даже если artifact hash настоящий |
| Missing/corrupt final file | RecoveryRequired, ни передачи, ни fabricated no-effect ack |
| Coordinator disconnect во время боя | Арбитр приостановлен на tick boundary; таймер outcome не работает как победа |
| Fixture snapshot restore + новая boundary | Старые result/capability не принимаются; объект соответствует выбранному fixture cut |
| Controlled write failure | Сбой записи/flush не признаётся успехом; отдельно обозначить, что это не настоящий power-loss/full-disk test |

Измерить запуски/abort/файловый рост. Прототип не сохраняет все игровые состояния для вечного retry: известные артефакты имеют manifest, а очистка только после terminal receipt и сохранения доказательств; неопределённый исход не удалять.

**Expected:** `FAULT-PASS` для подтверждённых process-crash границ на данном NTFS/runtime. Не называть это Product restore/проверкой hard power loss. Если crash неразличим по evidence — STOP с сохранением каталога.

## Task 6 — итоговая проверка, документация и выход из стадии

**Files:** verification report, RU/EN developer testing pages + manifest, фактически нужные docs-impact/reference updates; код по замечаниям единственного обзора при необходимости.

1. Целевой suite и сборка companion зелёные; полный `Rimlink.sln` regression и существующий BridgeTools suite, чтобы linked DTO не меняли runtime загрузку. Сборка мода штатным `Build-ClientMod.ps1` в новый output без установки в обычную игру. Результаты записать с exact final source identity, не переносить ранние PASS через изменённые файлы.
2. Честный report: game/runtime/mod hashes, три профиля/PID и время, template/rules/artifact hashes, commands/result IDs, исход всех gates, CPU/RAM/traffic, no production connection proof, Known limits. Не включать credentials, игровые бинарники или личные журналы.
3. Обновить Wiki как developer-only исследовательский прототип и его ограничения; утверждённая Product функция ещё не доступна. Сохранить полные RU/EN версии, увеличить согласованную revision и run штатных checks.
4. Один итоговый read-only обзор всей новой ветки на Astra, затем минимальный fix pass Sol и целевые повторные проверки. Не запускать второй обзор residual и не считать его заключение относящимся к прототипу. Нет automatic commit/push/merge/deploy.

**Команды проверки, PowerShell из корня feature worktree:**

```powershell
rtk proxy dotnet test tests/Rimlink.BattlePrototype.Tests/Rimlink.BattlePrototype.Tests.csproj -nologo -v q
rtk proxy dotnet test Rimlink.sln -nologo -v q
rtk proxy powershell -NoProfile -File scripts/Build-BridgeTools.ps1 -RimWorldDir 'D:\Build\rimlink-capture-prototype-20261108\game' -NoInstall
rtk proxy powershell -NoProfile -File scripts/Build-ClientMod.ps1 -RimWorldDir 'D:\Build\rimlink-capture-prototype-20261108\game' -OutputDir 'D:\Build\rimlink-capture-prototype-20261108\artifacts\client-mod'
rtk proxy npm ci --prefix wiki --ignore-scripts
rtk proxy npm run test:checks --prefix wiki
rtk proxy npm run check:content --prefix wiki
rtk proxy npm run check:reference --prefix wiki
rtk proxy npm run build --prefix wiki
rtk proxy npm run check:links --prefix wiki
```

`check:drift` запускается с фактическим записанным base commit feature-ветки из Task 0, не с HEAD. `check:browser` сначала сверить со штатным script и ограничением CUA-only; если он требует запрещённый движок, не запускать обход: выполнить допустимую CUA-проверку страниц и честно указать непрохождение именно автоматической команды. Timing pure tests дополнительно прогнать на двух CPU/Linux, если поддерживается их environment; это не замена игровому Windows runtime.

**Expected:** `PROTOTYPE-PASS` требует ENV + CONTRACT + ENGINE + FEASIBILITY + RESULT + FAULT и итоговых проверок, либо честный `STOP`/`PARTIAL` с точной границей. Итог НЕ означает готовность полного native-map mirror, scheduler массовых боёв, supply/escrow, cross-domain authority, production backup/restore, security hardening multiplayer endpoint или нового Product seal.

## Следующая стадия после PASS

Отдельный Product plan должен связать доказанный arbiter с серверными campaign/гильдиями/реальной ресурсной выдачей, получить полноценный игровой mirror без обхода save custody, реализовать shared authority/restore и все политики утверждённой спеки, обновить пользовательские RU/EN инструкции и пройти новую квалификацию. Ничего из этого не включается скрыто в текущий dev-only прототип.

**Конкретный следующий шаг Sol:** residual fixes завершены в `820749314a4305d9566830b34de99c3a0c4e8b37`; создать отдельную feature-ветку на этой кодовой базе с утверждёнными спецификацией/планом, записать фактический base commit и выполнить Task 0 до `ENV-PASS`. Затем — RED чистых правил/ledger и минимальный adapter. Feature-ветка и gate пока не созданы/не пройдены. Не начинать с изменения `LiveRelay` или включения пассивных аванпостов.
