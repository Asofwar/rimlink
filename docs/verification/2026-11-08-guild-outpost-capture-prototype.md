# Прототип арбитра автоматического захвата

Статус: **PARTIAL только по браузерной проверке Wiki**: ENV/CONTRACT/ENGINE/FEASIBILITY/RESULT/FAULT выполнены, единственный итоговый обзор завершён, замечание исправлено и проверено. CUA Driver блокирует браузерную проверку; полный PROTOTYPE-PASS не объявляется. Product остаётся Pending/OFF.

База: `8c3202266faf3c672e4b998f10affcf959c0deb7`. [План](../superpowers/plans/2026-11-08-guild-outpost-capture-prototype.md), [контракт C](../superpowers/specs/2026-11-07-guild-outpost-capture-design.md). Изменения ещё не закоммичены; merge/push/release/deploy не выполнялись.

## Реализованная граница

Dev-only coordinator с отдельной SQLite, три role-bound named pipe, постоянный listener каждой роли, bounded JSON, отдельные capability/sequence, очередь и неизменяемые acknowledgements. Единственный игровой арбитр вычисляет objective после каждого настоящего DoSingleTick. Контроллеры в Entry показывают восемь бойцов в native debug окнах и отправляют Move/Attack/Draft своей стороны. Ни один процесс не имеет RimlinkSession; production API, почта, escrow и настоящие гильдейские объекты не используются.

Арена 50×50: x/z 100…149; зона 6×6: 122…127. Восемь новых бойцов: возраст 25, Male, 12 навыков уровня 8, нет traits, полное здоровье, Normal Gun_BoltActionRifle, Cloth Apparel_BasicShirt и Apparel_Pants. У обеих NPC factions одинаковый idle lord. Конечные характеристики/gear/позиции проверяются до сохранения и после реальной повторной загрузки. Штатные native SaveGame-файлы завершаются, копируются с flush и хешируются до Start/result.

3000 непрерывных тиков удержания, contest сбрасывает счётчик; предел 30000 тиков; взаимное поражение имеет приоритет. Отключённая сторона получает AI через 30 секунд. Потеря coordinator останавливает следующий simulation tick, после grace 60 секунд — TechnicalAbort. Wall-clock 20 минут проверяется монотонно; победа при технической остановке не присуждается. Файлы финала, receipt, terminal view и fixture owner связаны одной проверяемой записью результата. Холодный арбитр только повторяет готовый envelope, не симулирует бой заново.

## Исходники и окружение

- Fingerprint 19 исходных/test/project/version файлов после исправления ревью: `7d30fc61c378ea29064a5141d3a356e64386d85bf0aac4c05d6bace819dfe4d7` (SHA-256 UTF-8 JSON списка отсортированных path/bytes/sha256 с LF; без этого отчёта и Wiki). Последний игровой smoke: `fee9dbf5-7d00-4cb6-805a-0d08d57cccda`. Предыдущий fingerprint `bf77b8d21629d1fa7a62246094f6a7ab147ecc10d26920d5fc7b47636527908a` сохранён отдельно; его проверки не переименованы в проверки нового build.
- RimWorld 1.7.5000, Assembly-CSharp.dll: `d55aa8e47fa3c5463ca85b0eccd51f195b4bbcd04f56da3af21c2f6688a81254`. NTFS. Harmony 3.0.1.0, Core, Rimlink 0.9.2, RimBridgeServer 2.3.0; DLC выключены. Metadata bridge заявляет 1.6 — проверены только перечисленные операции на этой 1.7.5000.
- Rimlink.Client.dll: `82c4d9bc7c38cb08fe0698e476d3af7aadbb5bd54582edccf9e9828e9c117e07`. Итоговый companion: `a601586bf70ed25634e8b0c3c469c873252c10d12767eaa720b8cbaa5e8a4242`.
- Coordinator/tests: SDK 10.0.403 и .NET 10.0.14. Игровой CoreCLR имеет технический ProductVersion 42,42,42,42424; квалификацией игрового runtime 10.0.14 это не объявляется.
- Лицензионная возможность трёх локальных процессов подтверждена пользователем. Собственная копия игры, отдельные savedata arbiter/side-a/side-b и отдельный GABS configDir; исходная установка и общий конфиг GABS не переписывались.
- Vanilla template создан обычным UI через CUA: Crashlanded, Cassandra/Peaceful, flat temperate forest с рекой, 250×250. Фактическое покрытие мира 30%, не 5%. Штатный Save после приземления трёх одноразовых колонистов: tick580, 14617394 байта, SHA-256 `3118aca36c8e563315ad4343f551272b45566b72a318b62c1e3906a9a636df88`. Личная колония не использована.
- ENV-инвентаризация: 1477 runtime/content файлов; SHA-256 `5afa996cb25e58ba9b602660f24a60323db42f6f4a772cf1e979eaa17ebed5e5`. Это начальный inventory; companion затем менялся по мере разработки, его итоговый хеш указан отдельно.

## Проверенные игровые сценарии

Матрица выполнялась по шагам разработки. Ниже указаны реальные battle ID и отдельные сборки. Поздние исправления перепроверены целевыми тестами и финальным трёхпроцессным smoke; единая непрерывная Product-квалификация одного immutable build не заявляется.

| Сценарий | Наблюдавшийся результат | Battle ID / сборка |
|---|---|---|
| Два внешних контроллера, команды и урон | Move обеих сторон tick1285, Attack tick1299; реальные изменения карты/здоровья, чужое управление и forged outcome отклонены | fafadb32-e4bf-4300-990a-e57583ab02ec / D |
| AI и возврат управления | После 30s side-b offline — AI; после reconnect новый Move b3 seq3 принят tick4133, z123→128 | fafadb32-e4bf-4300-990a-e57583ab02ec / D |
| Coordinator потерян в идущем бою | Tick4886 неизменен; при silence45.7s ещё Ongoing, при74.2s TechnicalAbort; после возврата receipt без передачи | fafadb32-e4bf-4300-990a-e57583ab02ec / D |
| Защитники вывели атакующих из боя | DefenderWin tick4998; owner=Defender, revision1, count0; cold replay в Entry без новых тиков/команд | 3cb49f5a-6975-49c5-abfb-36a5500431d5 / E |
| Полный лимит тиков | Start580 → terminal30580, ровно +30000; все бойцы здоровы, зона пуста; DefenderWin/count0 | f12cb761-2566-4fb3-b448-fb812dc69b9e / E |
| Взаимное поражение | Штатными debug Kill на паузе подготовлены восемь погибших; следующий реальный tick585 дал Draw/count0. Это setup-проверка predicates | 8978ab03-af5c-4bee-94f4-1580a79e96d5 / E |
| Partial initial artifact | Контролируемый Exit86 после 1024 байт initial; Started не записан, owner не изменён | 1e0ac7aa-3986-45c4-8319-8b0406d22f93 / B |
| Crash до StartCommitted | SQLite осталась Prepared/count0; после restart повтор того же Start успешно начал игру | c02a9998-df37-4e92-afca-7dc8c8b708eb / C |
| Revoke/stale executor | Отзыв увеличил fence, остановил живой арбитр; его новый процесс со старой привязкой получил отказ и остался Unbound | c02a9998-df37-4e92-afca-7dc8c8b708eb / C |
| Crash после StartCommitted | SQLite Started/count0; повтор Start после restart не создал второй старт, затем настоящий ZoneHeld | 553eeb52-e160-4b04-acc9-48e1a9638e4e / C |
| Два последовательных crash вокруг результата | Native ZoneHeld tick3777; Exit86 после durable envelope до доставки, затем Exit86 coordinator после owner transaction до ack. Cold arbiter в Entry доставил прежний результат; все три процесса получили count1 | 1b75c121-c059-44e7-9942-c9c9bbabc576 / B |
| Повтор/конфликт результата | Два повтора дали тот же receipt и view sequence; иной winner отклонён, count остался1 | 1b75c121-c059-44e7-9942-c9c9bbabc576 / B |
| Restore cut + boundary | Согласованный SQLite backup Started восстановлен вместо принятого owner=A/count1; после новой boundary/fence восстановлены owner=Defender/revision1/count0. Старый настоящий result, capability и game bind отклонены | 553eeb52-e160-4b04-acc9-48e1a9638e4e / C |
| Missing/corrupt final evidence | Два отдельных валидных Started cut с копиями настоящих native artifacts: отсутствие/повреждение final при Apply → RecoveryRequired/count0, без ack | 553eeb52-e160-4b04-acc9-48e1a9638e4e / C |
| Контролируемый отказ записи | Каталог вместо final artifact вызвал отказ FileStream: RecoveryRequired, причина сохраняется после фоновых poll, envelope/receipt отсутствуют, coordinator count0 | d64ca8c7-d8ca-43d9-85c5-8bd203e644b7 / A |
| Итоговый smoke после исправления Unix listener | Все три процесса: одинаковый ZoneHeld tick3776, hold3000, count1, revision2; обе стороны исполнили Move/Attack, side-b переподключилась | eb0e099d-c93a-4ebd-aedc-37ee21666e40 / A |
| Потеря ответа уже сохранённой view на паузе | Два последовательных Exit86 coordinator в after-view-commit: первый при идущем бое, второй после записи latest view при неизменном tick641. Точный повтор после restart принят, networkPaused=false, tick1306; настоящий Move внешнего контроллера затем завершил ZoneHeld tick7449/hold3000/count1, одинаковый receipt всех трёх | fee9dbf5-7d00-4cb6-805a-0d08d57cccda / F |

Companion SHA-256 сборок:

- A: `d457ce014f587a1a16b21587d5236f96a26fd6c43bdd6a5beb82b2a56baab0b8`.
- B: `451ea53c0a1a8bb89a1759f05fd83312e0811f710de9c345e8515a504d206dfd`.
- C: `7d8689ba7c3229370af26bfe1b9cd685f32f9e6560020b5416d342fa2fd32804`.
- D: `531fac7fb35794caa4458fc6f4c1cce05d1d6cdd9dc4f8d2519ee3fe759b9ee2`.
- E: `9e69a74ffd247274b1dc6f3f502fdaeed7def7416e8efb493e29ecd29d59a6ee`.
- F: `a601586bf70ed25634e8b0c3c469c873252c10d12767eaa720b8cbaa5e8a4242`.

Для критического crash B: result digest `502bb040164257c3f4a652faf0ec7788bd71c9fee0fb235030862b64a18afe24`, final snapshot 13721273 байта, journal 4112404 байта. До cold delivery coordinator оставался Started/count0; после commit-before-ack — Applied/count1. Для restore: Started cut SHA-256 `a45514a12f9f2b9828b213f7985a0e7305b47eedf7600f7b4c6dd2565ced3411`, terminal cut `d845793ab41924f5b23f9977b5a1dce32dcbe89aa7fba1509dcd0c42e5710f95`. Исходный accepted DB/WAL и artifacts сохранены перед восстановлением.

## Исправления, найденные проверками

- Асимметрия ванильной player faction устранена: две NPC factions с одинаковым idle lord. Гейзер внутри одноразовой арены удаляется DeSpawn, поскольку обычное Destroy для него запрещено.
- Ready ждёт Game.PlayerHasControl после загрузочного затемнения; Start проверяет реальную скорость. Игровые атласы/portraits очищаются перед сменой карты.
- Отказ команды не расходует cursor контроллера. Повтор ack/command не исполняет приказ повторно.
- Cold result атомарно обновляет terminal view вместе с receipt/owner; три RED-теста стали GREEN.
- Причина локального RecoveryRequired больше не стирается успешным poll; наблюдён настоящий игровой RED→GREEN отказа записи.
- Linux/two-CPU выявил сбросы при уничтожении Unix listener между запросами. Listener теперь сохраняется, соединение сбрасывается через Disconnect; исходные два FAIL сохранены, после исправления три полных прогона GREEN. Поведение listener проверено по [исходникам .NET 10.0.14](https://raw.githubusercontent.com/dotnet/runtime/v10.0.14/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Unix.cs).
- Единственный итоговый read-only обзор Astra нашёл P2: ответ на сохранение view мог потеряться, а повтор той же sequence ошибочно вызывал Freeze при уже остановленных тиках. Publish теперь идемпотентно принимает только точный повтор последнего содержимого/sequence в Started; иной payload и старая sequence отклоняются. Два новых теста RED2/59 → GREEN59/59, в том числе закрытие настоящего pipe без чтения ответа; живой двойной crash подтверждён сборкой F. Result digest финала: `66c738d733b5af34a6d18c5f566175a271bf0226dda3bc0b74ce643ae31db7a1`.

Не засчитываются как PASS: первая арена с неудалённым гейзером; player-vs-NPC пробный бой; match46b86f81 без исходного tick progress; первые evidence-clone проверки, открывавшие ledger до копирования initial (заменены валидными v2); diagnostic write-failure до исправления стирания причины. В первой проверке lost-view (451aaa9e) наблюдение прервалось, grace истёк: TechnicalAbort tick643/count0; это не проверка успешного продолжения. Она заменена полным fee9dbf5. Отрицательные исходы сохранены.

## Автоматические проверки и документация

- Полный `Rimlink.sln`: **3832/3832**, exit0: server2735, client943, Discord131, BridgeTools23. Фиксированное имя TRX перезаписывало предыдущие assembly-файлы; итог подтверждён stdout/exit, последний TRX содержит client943. Полный suite повторно не запускался после изменений только standalone prototype/game companion, которые в solution не входят; `src/` и существующие tests не изменены.
- Standalone prototype после исправления ревью: **59/59 Windows**, **59/59 Linux на CPU0,1 ×3**. Ранее сохранены 57/57 до ревью. Linux — точные скопированные байты исходников нового прототипа, не квалификация свежего CI checkout или production migrations.
- Штатные Build-BridgeTools -NoInstall и Build-ClientMod с новым отдельным OutputDir прошли. Working-game установка, версия0.9.2, протокол1 и release packaging не менялись.
- Wiki RU/EN developers/testing: revision8, одинаковый охват и manifest mapping. npm ci --ignore-scripts: 0 advisories; test:checks31, content47 пар, reference100 keys/104 HTTP/58 CLI, build и links96 HTML/0 broken — PASS. Drift: явная база8c3202266faf3c672e4b998f10affcf959c0deb7, 0 errors.
- `check:browser` **NOT RUN**: штатный script использует Playwright, запрещённый CUA-only правилом. Попытка отдельного браузера через CUA Driver 8 ноября отказала browser_endpoint_owner_mismatch. Обходов не было; именованная CUA-сессия завершена. Это не browser PASS.
- Единственный итоговый обзор: **завершён**; одно P2 исправлено и проверено RED→GREEN и настоящими тремя процессами. Повторный обзор не запускался.

## Размеры и ограничения

В финальном smoke максимальный наблюдавшийся reply с header — 3975 байт при body cap256KiB, backlog максимум3. Единичный замер трёх процессов при остановленной симуляции: 61.941s, сумма CPU≈0.64 ядра, сумма working set≈4413MiB (это не уникально занятая физическая RAM). Не является нагрузочной квалификацией scheduler. На момент подсчёта сохранены 15 fixtures: 497864788 байт battle/evidence-файлов и 603403117 байт native saves; неизвестные исходы не очищались.

Не проверены физическое power loss/full disk, полный native-map mirror, массовые бои, production restore/backup, supply/escrow/выдача, пользовательские колонисты, сторонние модпаки и GOG. Полные 20 минут wall-clock в реальной игре не выжидались: граница проверена pure tests, реальный disconnect/grace — отдельно. Debug mutual elimination не объявляется естественной перестрелкой. Ранее выполненные игровые случаи имеют свои сборки; новый Product seal, реализация полной ресурсной экономики и выпуск функции не заявляются.

Секретные role bindings, личные журналы, game binaries, snapshots и код декомпиляции в Git не добавляются. Все три собственные игры, coordinator и текущий изолированный GABS helper завершены. Readback процесса по stand-scope: 0; SHA-256 общего GABS config совпадает с исходным. Именованная CUA-сессия закрыта.
