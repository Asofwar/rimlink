# Materialization и owner controls: проверки 2026-11-04

## Область

Task12, собственный стенд RimWorld 1.7.4995 rev309 / Harmony 3.0.1.0 / RimBridgeServer 2.3.0. Production, общий Steam-профиль и чужие процессы не менялись. Секрет документированного loopback API не сохранялся и не выводился.

## Автоматические RED → GREEN

- Allowlist WoodLog/Steel, точное pinned значение и штатные размеры стеков: 151 → 75+75+1; 1000 → 13×75+25. Невалидный элемент отказывает всему batch до создания вещей.
- Полный scope, безопасная home map, текущая game/session, точный canonical snapshot, immutable Begin intent и causal reply. Read-only fetch сам по себе не разрешает spawn.
- Durable branch/SpawnStarted fence до первой вещи; отсутствие receipt после cold load не доказывает отсутствие spawn. Известный точный receipt — ACK-only, не повторный импорт.
- Потерянный transient Begin reply до создания вещей допускает тот же durable intent; частичный spawn, повреждённое доказательство и изменённая ветвь — recovery, не local refund/respawn.
- Server reply содержит оригинальные transition ID/digest/predecessor. Старый confirmed manifest читается только по принадлежавшему игроку parcel, сохраняя исходную valuation после замены активного manifest.
- Tagged ordinary gift сохраняет однократные wallet credit, прежнюю storage fee и deliver audit в той же receipt transaction. Собственные outpost returns остаются бесплатными; повторный snapshot/ACK не повторяет credit.
- Отдельные RED показали, что изменение Parcel.pawns/items/silver не обнаруживалось typed payload digest. Полные content commitments добавлены в payload и проверяются до materialization и при authority reconciliation.
- Owner state — detached copy, committed roster/rates/capacity/pinned geography. EN/RU key sets и format arguments совпадают.

## Настоящий game process

- Собственная cold-loaded offline colony, native owner dialog: EN Outposts/Refresh и RU Аванпосты/Обновить, пустое состояние читаемо, без наложения контролов; открытие окна поставило игру на паузу.
- EN layout: [captured layout](<../../build/outpost-task12-controls-en.json>). RU после смены языка потребовал повторную загрузку game; старый scoped ref устарел. RU текст подтверждён screenshot, а не неуспешным scoped semantic assertion. [Последний RU layout](<../../build/outpost-task12-controls-ru-layout.json>).
- EN screenshot SHA-256 f79c966a841a9cea7b7d5c74d1ffc6ad7f1f3bbd31776ab8fbb9f09072d95fa6; RU 8e221bab73b1650e43a47d2195d5c7986d02c3f647f4ac195ff42b4302222e5c.
- Только своё окно закрыто штатным Alt+F4, отсутствие подтверждено отдельным CUA list_windows. Shell/process kill не использовались.

## Интеграционные проверки перед локальным коммитом

- Solution build: exit0, 0 errors, 2 прежних nullable warnings.
- Первый полный диагностический прогон: Bridge 5/5, Discord 122/122, Client 674/674, Server 1667/1668 — 2468/2469, exit1. Единственный сбой — существующий process-crash test после AfterExternalCommit: Ready → Maintenance; исходный assertion не печатал причину. Ранее подсчёт 2346/2347 не включал Discord.
- Assertion дополнен точным startup result, без изменения protection кода. 44 изолированных и 16 параллельных crash checks прошли. Повторный [полный прогон](<../../build/outpost-task12-full-reopen-diagnostics.log>) exit0: Bridge 5/5, Server 1668/1668, Discord 122/122, Client 674/674 — **2469/2469**. Первоначальный перемежающийся отказ пока не воспроизведён и не объявляется исправленным.
- Финальные [private GAME](<../../build/outpost-task12-game-final.log>) и [BRIDGE](<../../build/outpost-task12-bridge-final2.log>) builds после full gate — exit0, 0 errors, соответственно 2/4 прежних MSB3277 warnings. InstallTo bridge исправлен с companion subdir на BridgeTools root; ошибочно созданные две собственные generated копии удалены после hash-проверки. Client/Core/Protocol/Bridge DLL в окончательных собственных install paths побайтно соответствуют build artifacts. Это build/install proof, не gameplay acceptance.

## Не пройденные здесь acceptance gates

- Полные G3/G4/G7 — фактические resource/worker returns, inventory/health/relations, live owner roster/Q/F/paused/full/recovery UI и повторные multiplayer operations — требуют lifecycle/restore/admin graph Tasks13–15. Capability остаётся закрытой. Эти сценарии обязательны в Task16, текущие unit/RPC/empty-UI проверки их не заменяют.
- Обычный causal return реализован по утверждённому уточнению Astra: неизменяемые actual request/Removed(localID), query-first retained source, постоянный negative fence, один ReturnStarted, durable SpawnStarted, явный UUID bind и точный Returned receipt. Автоматически проверены server/RPC/journal/late-ingress пути; общий игровой producer и отдельный report producer собраны в собственный GAME stand. Фактический roundtrip обычной отклонённой передачи в игре ещё обязателен в Task16; build и unit/RPC его не заменяют.
- Финальные solution/GAME/BRIDGE commands и их результаты фиксируются в execution ledger после завершения; этот документ не выдаёт незапущенные проверки за выполненные.
