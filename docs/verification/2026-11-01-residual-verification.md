# Остаточные проверки и передача игрового стенда — 2026-11-01

Обновление 2026-11-02: совместная колония и основные новые окна проверены двумя реальными клиентами через
игровой GABS / RimBridge MCP. Это следующий прогон после описанного ниже ограничения CUA; подробные
действия и границы доказательства — в [актуальном протоколе](2026-11-02-game-mcp-acceptance.md).
Остальные непроверенные остаточные сценарии ниже сохраняют свой статус.

Рабочее дерево: `D:/Build/rimlink-next-wt/verification`, ветка `asofwar/next-verification`, база `9e70b25`.
Спека и план прочитаны по абсолютным путям в `D:/Build/rimlink-next/docs/superpowers/`.
Изменение `src/Rimlink.Server/Storage/RimlinkDbContext.cs` уже присутствовало до начала работы и оставлено как есть.
Миграции, версии, коммиты, публикация и рабочий сервер не затрагивались. Итоговый обзор общей интеграции остаётся координатору.

## EventsTab: воспроизведение и исправление

Старый путь сохранял только `requestId`, но после сетевой ошибки собирал остальные поля из нового выбора.
`LegacyReusedKeyReturnsTheOldTargetAndTypeDespiteChangedSelection` воспроизводит это на настоящем тестовом сервере:
заказ `trader` для Bob, затем `resource-pod` для Cara с прежним ID возвращает первый заказ для Bob.
Сервер правильно не списывает второй платёж; старое окно могло сообщить об успехе для нового выбора.

Теперь `IncidentOrderRetry.Begin` сохраняет сериализованный запрос в существующий `TransferOutbox` **до RPC**.
Пока он не разрешён, любое новое намерение возвращает сохранённую запись, не новый ID и не изменённый payload.
Вкладка показывает исходные цель, событие, уровень/капсулы и кнопку повтора, скрывая изменение намерения.
Этот экран доступен даже без каталога и свежей котировки и при долге: проверяется результат уже отправленного запроса.
Успешное сообщение строится из ответа сервера. Окончательный отказ использует существующий `IsDefinitive`;
неопределённые ошибки сохраняют запись. После подтверждения следующий заказ получает новый ID.
Запись переживает закрытие окна и повторный вход; `LoginFlow.LoadEconomy` повторяет её при входе/реконнекте.
У отказанного заказа нет локальных вещей, поэтому outbox не создаёт пустую возвратную посылку.

Затронутые вызывающие пути проверены: EventsTab, login/reconnect replay, outbox decode/refusal, возврат отказанных
вещей через `Outgoing.ReturnRefused`. Прямой dev-вызов `IncidentTools` не является UI-повтором и не менялся.
Строки добавлены одновременно в EN/RU; новых настроек/зависимостей нет.

RED: до проверки сохранённого намерения целевой набор дал **2 failed / 3 passed**:
`ChangedSelectionCannotReplaceTheUnansweredIntentEvenAfterReload` и
`LostReplyThenChangedSelectionReplaysOriginalAndChargesOnlyOnce`.
GREEN после привязки: **5/5**, после добавления воспроизведения прежнего поведения и транспортной инъекции — **7/7**.
Транспортный тест действительно буферизует ответ Order до конца, затем теряет его через `HttpMessageHandler`:
клиент получает `RpcException`, сервер уже хранит заказ; повтор после загрузки outbox оставляет один заказ и одну оплату.
Это локальная fault injection через официальный HttpMessageHandler/TestServer, не управление игрой или сетью компьютера.

## Фактические проверки

Все команды выполнялись из назначенного дерева в PowerShell через `rtk`.

```powershell
rtk proxy dotnet test tests/Rimlink.Client.Tests --filter FullyQualifiedName~IncidentOrderRetryTests -nologo -v q --logger trx --results-directory build/verification/incident-retry
rtk proxy dotnet test Rimlink.sln -nologo -v q --logger trx --results-directory build/verification/full
rtk proxy powershell -NoProfile -File scripts/Build-ClientMod.ps1 -RimWorldDir D:/Build/rimlink-bt/RimWorld -OutputDir D:/Build/rimlink-next-wt/verification/build/verification/mod/Rimlink
rtk proxy dotnet test tests/Rimlink.Client.Tests --filter 'FullyQualifiedName~IncidentReconnectTests|FullyQualifiedName~CompactedRollbackLoginTests' -nologo -v q --logger trx --results-directory build/verification/residual-fixed
```

Первый полный suite: **923 server + 228 client + 120 Discord + 5 BridgeTools = 1276 passed**, без пропусков.
Сборка игрового мода успешна; вывод содержал существующие MSB3277 (Logging.Abstractions 8/10) и CS8601
в `Patch_Pawn_RoyaltyTracker_ExposeData`. Сборка сама по себе не подтверждает поведение новой UI-ветки в игре.
Три добавленные residual-проверки прошли отдельно. Их первый запуск выявил двойной Dispose в самом новом тесте;
исправлена область `await using`, код подключения не менялся.
TRX хранятся в указанных каталогах `build/verification/`; каталог игнорируется Git.
Из первого полного TRX отдельно извлечены успешные группы: SteamLoginWaitTests **5**, SteamTicketReuseTests **4**,
ServerWealthTests **8**, UnderThreatTests **16**, IncidentLifecycleTests **13**, SaveCompactionTests **31**; падений в них нет.

Финальный повтор после всех изменений тестов:

```powershell
rtk proxy dotnet test Rimlink.sln -nologo -v q --logger trx --results-directory build/verification/final
```

**923 server + 231 client + 120 Discord + 5 BridgeTools = 1279 passed, 0 failed, 0 skipped; exit code 0.**
Этот прогон включает точное воспроизведение legacy-повтора после настоящей потери HTTP-ответа, все 7 retry-тестов
и 3 новые residual-проверки. Финальная сборка мода тоже успешна, лог UTF-8: `build/verification/mod-build.log`.
`git diff --check` и строгая проверка UTF-8 всех 12 изменённых/добавленных файлов успешны; все 3 новых ключа есть в EN/RU.

| Сценарий | Что подтверждено | Что остаётся в игре |
|---|---|---|
| Steam refusal/autowait | Существующие SteamLoginWaitTests/SteamTicketReuseTests; LoginFlow различает SteamTicketRejected и обычный отказ | Настоящий отказ Steam и автоматическое подтверждение без пароля; ключи не запрашивались |
| `g` в чате | PingSender проверяет keyboardControl и WindowsPreventCameraMotion; это только чтение исходника | Обычная клавиатурная печать, отсутствие пинга у второго игрока |
| Богатство визита | Patch_Map_PlayerWealthForStoryteller исключает визит/live guest; ServerWealthTests зелёные | Значение и storyteller points в копии хозяина при большом кошельке гостя |
| Враг / караван | UnderThreatTests, включая NothingBlocksTheAttackerOthersOrACaravanHandOver; SendFromCaravan не вызывает карту угрозы, но проверяет долг | Настоящий враг на карте, отказ отправки с карты, разрешённая передача из каравана |
| Infestation без места | Серверный ReportFailedRefunds и повтор отчёта зелёные | Отказ именно ванильного Infestation worker на неподходящей карте |
| Pending / Report | IncidentReconnectTests (fired и failed): настоящий reconnect, старый Pending не возвращает handled ID; повтор отчёта не повторяет оплату/возврат | Срабатывание worker и обрыв без выхода в меню |
| Откат дельты / вход | CompactedRollbackLoginTests: проверен SaveForm.Delta, REST панели, новый login, DownloadThenLoad, побайтовый XML старой версии; SaveCompactionTests зелёные | Нажатие кнопки в панели и полноценная загрузка карты RimWorld |
| Discord live | Не запускался | Отложен пользователем; токен не требуется для этой работы |

## Что произошло при попытке игрового запуска

GABS-инструментов нет в `ALL_TOOLS`. CUA Driver 0.38.0 доступен: health_report подтвердил session_active,
UIAutomation и D3D11. Изначально `list_windows(app: RimWorld)` вернул 0 окон.

Создана собственная копия игры в `build/verification/game` из существующей тестовой копии
`D:/Build/rimlink-bt/RimWorld`, без изменения источника. `Mods` и `BridgeTools` источника исключены;
установлен только собранный в этом дереве мод и локальная dev-копия установленного Harmony из NuGet.
Профиль host создан с нуля, без чужих аккаунтов/сейвов/пинов. Профиль guest подготовлен отдельно.

CUA Driver `launch_app` успешно запустил эту копию с
`-savedatafolder=D:/Build/rimlink-next-wt/verification/build/verification/host`:
PID **31440**, HWND **7868650** (`0x7810ea`), окно 1280×830, RimWorld **1.7.4990**,
в меню видна кнопка Multiplayer. Снимки: `build/verification/first-window.png` и `multiplayer.png`.
Сервер собственного дерева запущен с новыми данными `build/verification/server`, TLS **25783**;
подтвердил `Now listening on: https://[::]:25783` и `TLS endpoint check passed`.

Первый клик Multiplayer (886,348) через Driver, background:
`UIA pixel click timeout ... No fallback input was sent`. Свежий снимок подтвердил прежнее меню.
Предложенный самим Driver повтор foreground:
`foreground_unavailable: exact target HWND 0x7810ea ... was not foreground after the click
(actual foreground HWND 0x5e01404)`.
Ни один из этих вызовов не подтвердил открытие Multiplayer. GUI-обходов, GABS HTTP, Lua и недокументированных endpoint не было.

Игра закрыта **через Driver кликом по системному X**; повторный `list_windows(pid:31440)` вернул 0 окон.
Собственный сервер остановлен через его exec-сессию, затем `Get-NetTCPConnection -LocalPort 25783 -State Listen`
подтвердил отсутствие слушателя. Неизвестные процессы не останавливались. Игровые пункты не отмечены PASS.

## Подготовленный двухсторонний coop-стенд координатора

Прочитан текущий `D:/Build/rimlink-next/docs/shared-colony.md`. На момент чтения в BridgeTools нет отдельного
coop invite/accept/assign/join tool; имеющийся `rimlink/live_propose` — raid/visit с привозимыми пешками.
Его, а также старые loan/pawn-return пути **не использовать** для проверки совместной колонии.
Точки будущих GUI-действий брать только из свежих CUA-снимков. Эндпоинты RimBridge не угадывать.

Подготовлены:

- игра: `D:/Build/rimlink-next-wt/verification/build/verification/game`;
- host savedata: `D:/Build/rimlink-next-wt/verification/build/verification/host`;
- guest savedata: `D:/Build/rimlink-next-wt/verification/build/verification/guest`;
- отдельные данные сервера: `D:/Build/rimlink-next-wt/verification/build/verification/server`;
- порт: **25783**, перед следующим запуском проверить, что свободен;
- мод в стенде пока **ветки verification 0.6.2**, не coop-интеграции.

До явного handoff координатора **не выполнять** следующий build/redeploy и не запускать интеграционный сервер:
исходники и итоговая миграция интеграции ещё могут меняться.

После handoff — PowerShell, сначала подготовка сборки без пользовательской установки:

```powershell
rtk proxy powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; $stand = "D:/Build/rimlink-next-wt/verification/build/verification"; $modTarget = [IO.Path]::GetFullPath("$stand/game/Mods/Rimlink"); if (-not $modTarget.StartsWith([IO.Path]::GetFullPath($stand) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {throw "Mod target escaped isolated stand"}; & "D:/Build/rimlink-next/scripts/Build-ClientMod.ps1" -RimWorldDir "$stand/game" -OutputDir $modTarget -BundleHarmony'
```

Отдельная PowerShell/exec-сессия сервера; только новые тестовые учётные записи и локальный тестовый токен:

```powershell
rtk proxy powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; if (Get-NetTCPConnection -LocalPort 25783 -State Listen -ErrorAction SilentlyContinue) {throw "Port 25783 is already in use; do not stop its owner"}; $env:Rimlink__Server__DataDir = "D:/Build/rimlink-next-wt/verification/build/verification/server"; $env:Rimlink__Server__Port = "25783"; $env:Rimlink__Auth__AllowPassword = "true"; $env:Rimlink__Auth__AdminLogins__0 = "verification-admin"; $env:Rimlink__Admin__ApiToken = "verification-local-only"; $env:Rimlink__Integrity__DevMode = "Everyone"; dotnet run --no-launch-profile --project D:/Build/rimlink-next/src/Rimlink.Server'
```

Два запуска выполняются только CUA Driver `launch_app`, а не shell/скриптом автоматизации:

```json
{"path":"D:/Build/rimlink-next-wt/verification/build/verification/game/RimWorld.exe","additional_arguments":["-savedatafolder=D:/Build/rimlink-next-wt/verification/build/verification/host"],"session":"rimlink-coop-host"}
```

```json
{"path":"D:/Build/rimlink-next-wt/verification/build/verification/game/RimWorld.exe","additional_arguments":["-savedatafolder=D:/Build/rimlink-next-wt/verification/build/verification/guest"],"session":"rimlink-coop-guest"}
```

У каждой игры записать возвращённые PID/HWND. Первый checkpoint: Driver действительно открывает Multiplayer
и делает подтверждённое изменение UI в обоих окнах. Если сохраняется foreground_unavailable — остановить ввод,
сохранить снимки/ошибку и оставить сценарий NOT RUN. Не подменять его тестами или старым live-визитом.

### Приёмочный сценарий coop (пока NOT RUN)

1. В обоих клиентах добавить только `127.0.0.1:25783`, подтвердить его новый тестовый pin. Зарегистрировать отдельные
   локальные аккаунты verification-admin/verification-guest; создать собственные колонии на одном мире и сохранить обе.
   Зафиксировать ID и число пешек хозяина, собственных пешек партнёра, почту обоих и версии обоих сейвов.
2. Через **Совместная колония** под чатом хозяин приглашает партнёра. До принятия нет доступа; у партнёра обновить
   список и принять. Хозяин назначает ровно одну свою свободную пешку, оставляя другую неназначенной.
3. Партнёр жмёт **Войти в колонию** из своей домашней колонии. Проверить сохранение его дома перед загрузкой зеркала.
   В зеркале именно назначенная пешка хозяина, со старым ID; собственные пешки партнёра не перевозятся и не исчезают дома.
4. Партнёр двигает/драфтит назначенную пешку и меняет одну клетку Work/Schedule; хозяин видит исполнение.
   Команда неназначенной пешке не меняет её приказ у хозяина. Снять снимки обеих сторон с одним игровым тиком/ID.
5. Выход партнёра через coop/live UI: партнёр возвращается к своему дому, обе колонии сохраняют исходные ID/число пешек;
   в почте нет пешек хозяина. Сравнить версию канонического сейва: автор — хозяин, не партнёр.
6. Сохранить/перезайти обоим: членство и назначение остались; повторный вход работает без приглашения; расписание и
   изменения из пункта 4 сохранились. При хозяине вне домашней игры join отказан, нет второй автономной симуляции.
7. Во время нового coop хозяин отзывает доступ или меняет назначение. Сессия заканчивается; старые команды больше не
   исполняются, пешки остаются у хозяина, посылки/добыча с ними не создаются. Проверить `GET /api/admin/colonies?playerId=…`
   на этом тестовом сервере и повторный отказ join без действующего разрешения.
8. Повторить обрыв по очереди каждой стороны штатным модовым механизмом только при наличии проверенного semantic tool
   или доступного UI. Проверить PeerLost/PeerBack, сохранность назначений, отсутствие raid outcome/pawn-return и второго
   автора сейва. Смерть/плен назначенной пешки — отдельный checkpoint после сохранения, без удаления чужих данных.
9. Ограничение «один активный партнёр»: для одновременного второго join нужен дополнительный изолированный профиль;
   если доступны только два клиента, это ограничение остаётся серверным тестом и отдельным NOT RUN в игре.

### Остальные игровые рецепты (пока NOT RUN)

1. EventsTab: открыть через консоль связи или dev **Rimlink → Open economy window**, выбрать цель/тип/уровень/капсулы и
   дождаться цены. На собственном сервере устроить потерю связи без изменения сети компьютера. После неопределённого
   результата убедиться, что виден исходный заказ и нельзя сменить намерение. Закрыть/открыть окно, восстановить сервер,
   повторить, проверить один заказ/платёж и новый заказ другой цели после подтверждения. Потеря ответа именно после commit
   уже проверена автоматической транспортной инъекцией; без управляемой инъекции в игре не заявлять тот же точный момент.
2. `g`: с двумя подключёнными игроками щёлкнуть поле чата и обычным keyboard input набрать `g g`, отправить. Получатель
   должен увидеть текст, но ни пинг, ни меню адресатов появляться не должны. Без фокуса поля отдельно проверить штатный пинг.
3. Экономика: записать server wealth и угрозу дома, затем войти в копию визита — кошелёк гостя не увеличивает угрозу хозяина.
   Создать настоящего врага на домашней карте: депозит/лот/вывод с карты отказаны, вещи сохранены; отдельно отправить вещи
   уже сформированного каравана, оставив владельца/носителя в нём. Этот обычный visit-тест отделён от coop и не служит его заменой.
4. Infestation: отдельная пригодная для отказа карта без доступного места для ульев; заказ должен стать Failed с причиной
   worker и ровно одним возвратом. Нельзя считать серверный искусственный Report(false) доказательством ванильного отказа.
5. Pending/Report: на собственном стенде поймать Announced, оборвать только свой сервер перед отчётом, дать событию пройти,
   восстановить соединение без выхода в меню. Один запуск worker, один отчёт/возврат и отсутствие повторного срабатывания
   после resync. Если момент разрыва не зафиксирован — этот подпункт не подтверждён.
6. Дельта: на собственном сервере сохранить две заметно разные игровые версии, убедиться в наличии старой `*.d*.zst`,
   в панели откатить на неё и войти снова. Проверить конкретный старый объект/клетку в мире, а не только размер файла/HTTP 200.
   Затем сделать бэкап и восстановить в ещё один собственный каталог, проверить старт; не использовать production.
7. Настоящий Steam refusal/autowait требует управляемого отказа настоящего билета без новых секретов пользователя.
   Пока такого способа нет, оставить NOT RUN; тесты интервалов/повторного использования билета дают только core evidence.

Итоговые критерии — screenshot + состояние обоих клиентов + изменения сейвов/почты тестового сервера.
Неизвестные результаты не превращаются в PASS по наличию HTTP 200, логина или зелёных unit-тестов.

## Пути для интеграции

Изменены/добавлены только эти 12 путей; существующая правка RimlinkDbContext в список авторских изменений не входит:

```text
src/Rimlink.Client.Core/Economy/TransferOutbox.cs
src/Rimlink.Client.Core/Incidents/IncidentOrderRetry.cs
src/Rimlink.Client/UI/Economy/EventsTab.cs
src/Rimlink.Client/UI/LoginFlow.cs
mod/Languages/English/Keyed/Rimlink.xml
mod/Languages/Russian/Keyed/Rimlink.xml
tests/Rimlink.Client.Tests/Incidents/IncidentOrderRetryTests.cs
tests/Rimlink.Client.Tests/Incidents/IncidentReconnectTests.cs
tests/Rimlink.Client.Tests/Net/CompactedRollbackLoginTests.cs
docs/player-guide.md
docs/manual-checklist.md
docs/verification/2026-11-01-residual-verification.md
```

При интеграции согласовать добавленный в конец OutboxKind `IncidentOrder` и необязательный параметр
`OutboxClients.Incidents` с другими направлениями. `LoginFlow` передаёт этот клиент до загрузки состояния.
Внешних регистраций, серверных сущностей и новых API в этой правке нет.
