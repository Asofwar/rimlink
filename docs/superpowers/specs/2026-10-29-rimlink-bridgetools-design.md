# Rimlink — инструменты для агента через RimBridgeServer (BridgeTools): дизайн

Дата: 2026-10-29. Статус: утверждён в обсуждении, ждёт план.
Основа: `2026-10-28-rimlink-core-design.md` (вход, мир, поселение), `2026-10-28-rimlink-economy-design.md`,
`2026-10-28-rimlink-raids-visits-design.md`, `2026-10-29-rimlink-live-design.md`. Протокол не меняется.

## 1. Цель и границы

Агент (Claude/Codex) ведёт игровые потоки Rimlink на двух экземплярах RimWorld через GABS + RimBridgeServer без
кликов по пикселям: вход на сервер, новая колония, рейды, визиты, живые сессии, экономика, чат. Основной потребитель —
прогоны `docs/manual-checklist.md`.

Что мешало в живом прогоне (RimBridgeServer 2.3.0 на RimWorld 1.7, GABS 1.1.2):

- `rimworld/get_ui_layout` не видит список пунктов главного меню (`OptionListingUtility`), поэтому пункт «Мультиплеер»
  недостижим; не видит нижние кнопки `Page_SelectStartingSite` (Назад / Случайное место / Далее) — их рисует
  `DoCustomBottomButtons` поверх мирового экрана.
- `rimworld/set_text_field` в режиме `typed` уходит в таймаут на окнах Rimlink; `instant` работает.
- Вход и старт колонии — около 15 шагов UI на клиента.

Решения, принятые в обсуждении:

| Вопрос | Решение |
|---|---|
| Носитель | Компаньон-DLL на `RimBridgeServer.Sdk`, отдельный проект, загружается из папки `BridgeTools` игры |
| Как пройти диалоги входа | Небольшой seam в `Rimlink.Client`: только необязательные параметры, без типов RimBridgeServer |
| Действия первой версии | Рейды и визиты, живые сессии, экономика, чат |
| Форма | Один инструмент на действие (`rimlink/raid`, …); список — префикс `rimlink/` в `games_tool_names` |
| SDK | DLL из установленного RimBridgeServer по HintPath, не NuGet: на nuget.org только 2.0.0, в игре 2.3.0, хост всё равно подменяет любую загрузку SDK своей копией |
| Версия мода | `modVersion` не меняется: seam не меняет поведение для игрока |

Вне рамок: апстрим-исправления RimBridgeServer (только заметка), караваны и транспортные капсулы как способ отправки,
магазин визита, приказы гостя в живой сессии, выбор пешек на странице пешек, Steam-вход из инструмента.

## 2. Проект и деплой

`tools/Rimlink.BridgeTools/Rimlink.BridgeTools.csproj`, `net10.0`, вне `Rimlink.sln` (как `Rimlink.Client`: нужна игра).
Ссылки, все без копирования (`Private=false` / `ExcludeAssets=runtime`):

- `ProjectReference` на `src/Rimlink.Client` (транзитивно `Rimlink.Client.Core`, `Rimlink.Protocol`);
- `Assembly-CSharp`, `UnityStubs` из `$(RimWorldDir)`;
- `Lib.Harmony` (центральная версия);
- `RimBridgeServer.Sdk` — HintPath `$(RimWorldDir)\Mods\RimBridgeServer\1.6\Assemblies\RimBridgeServer.Sdk.dll`;
  отсутствие файла — понятная ошибка сборки, как `CheckRimWorld` у клиента.

Скрипт `scripts/Build-BridgeTools.ps1 [-RimWorldDir <game>] [-InstallTo <RimWorld>\BridgeTools]` собирает Release и
кладёт **ровно один файл** `Rimlink.BridgeTools.dll` в `<BridgeTools>\Rimlink\`. Если в этой папке есть другие файлы,
скрипт останавливается с ошибкой. Причина: резолвер компаньонов RimBridgeServer сначала ищет зависимости в папке
компаньона (`BundleDirectory`), и копия `Rimlink.Client.dll` там загрузилась бы второй раз — со своим пустым
`RimlinkSession` (`Active` всегда false, без ошибки). Без копий зависимость разрешается по полному имени к уже
загруженной сборке мода, поэтому мод и BridgeTools собираются из одного дерева (одинаковая версия сборки).

`Package-Release.ps1` и `Build-ClientMod.ps1` не меняются: BridgeTools никогда не попадает в `dist/` и в папку мода.
В `src/Rimlink.Client` нет ссылок на RimBridgeServer.

Компаньоны загружаются при старте моста: после деплоя игру нужно перезапустить. Диагностика загрузки —
`rimbridge/get_bridge_status` (записи discovery, ошибки load/register).

## 3. Seam в Rimlink.Client

1. `LoginFlow.Run(ServerEndpoint endpoint, GameCredentials? credentials = null, IPinPrompt? pins = null)` возвращает
   `Task<LoginOutcome>`. Значения по умолчанию — сегодняшнее поведение (`new GameCredentials()`, `GamePinPrompt`);
   UI-вызовы игнорируют результат.
2. `LoginOutcome` (в `Rimlink.Client.Core/Net`, рядом с `LoginPlan`): `Kind` = `Loading` (сейв загружается),
   `NewColony` (открыта `Page_SelectScenario`), `ModpackDiff`, `Failed`, `Cancelled`; `Message` — текст ошибки сервера.
   В предустановленном режиме отмена из-за исчерпанных попыток входа даёт `Failed` с последней ошибкой
   (`LastError`), а не `Cancelled`.
3. `PresetLogin` (в `Rimlink.Client.Core`, чистый класс, покрыт xUnit): логин, пароль, имя игрока,
   `registerIfRefused`, пароль сервера. Последовательность попыток: вход → (если вход отклонён и разрешено) регистрация →
   стоп (`null`). Сервер отвечает одинаково на неизвестный логин и неверный пароль («login details are invalid»), поэтому
   регистрация — только после отказа входа; отказ регистрации «login already taken» значит неверный пароль → стоп.
4. `GameCredentials(PresetLogin preset)`: не открывает `Dialog_Login` и `Dialog_ServerPassword`, не использует Steam
   (`PreferPassword = true` сразу), берёт попытки из `PresetLogin`. Отказ `WRONG_PASSWORD` сервера в этом режиме —
   `Failed`, без диалога. Пароль не печатается (`PasswordLogin.ToString` уже скрывает его).
5. `Dialog_LiveInvite.Answer(bool)` становится `public`.

Пин-сертификата компаньон отвечает своей реализацией `IPinPrompt` (интерфейс уже публичный в `Rimlink.Client.Core`).

## 4. Инструменты

Все вызываются через `games_call_tool`. Игрок — имя или id; пешки — id из `rimworld/list_colonists` или имена; действия —
на текущей карте. Ответ всегда `{success, error?, ...}`.

| Инструмент | Поведение |
|---|---|
| `rimlink/status` | `ProgramState`, тип верхнего окна; сессия: адрес, состояние соединения, игрок (id, имя, роль), мир (есть, seed); игроки (онлайн, тайлы поселений); экономика (кошелёк, посылки, свои лоты); рейды (входящие, свои, мирный режим); живая сессия (приглашение, роль, состояние); визит; размер outbox |
| `rimlink/connect {host=127.0.0.1, port=25560, name?, login, password, playerName?, register=auto\|never, serverPassword?, trustChangedPin=false, timeoutSec=120}` | Только `ProgramState.Entry` и без активной сессии. Добавляет сервер в список настроек, если его нет; запускает `LoginFlow.Run` с `PresetLogin` и пин-ответом «новому отпечатку доверять, смене — только при `trustChangedPin`». Возвращает исход; при `Loading` ждёт `Playing` с картой |
| `rimlink/start_colony {scenario?, storyteller?, difficulty?, seed?, tile?, timeoutSec=300}` | Проход страниц новой колонии (раздел 5) до `Playing`, карты и своего поселения в `RimlinkSession.State.Objects`. Возвращает тайл, имя колонии, пройденные шаги |
| `rimlink/menu_options` | Пункты главного меню, запомненные Harmony-постфиксом на `OptionListingUtility.DrawOptionListing` (последний отрисованный список в `ProgramState.Entry`) |
| `rimlink/click_menu_option {label}` | Вызов действия пункта на главном потоке; работает для любых пунктов («Мультиплеер», «Новая колония», «Загрузить») |
| `rimlink/page_state` | Верхняя `Page`: тип, заголовок, `CanDoBack`, выбранный тайл (для страницы места) |
| `rimlink/page_action {action=next\|back\|random_site}` | `next`: `CanDoNext()` → `DoNext()`; `back`: `CanDoBack()` → `DoBack()`; `random_site`: то же, что кнопка «Случайное место» |
| `rimlink/raid {player, pawns, mode=snapshot\|real, timing=instant\|warned}` | `RaidOutgoing.LaunchFromCaravanless` |
| `rimlink/visit {player, pawns}` | `VisitSession.Begin` |
| `rimlink/live_propose {player, pawns, kind=raid\|visit}` | `LiveGuest.ProposeFromMap`, `TimingInstant` |
| `rimlink/live_answer {accept}` | `Dialog_LiveInvite.Answer` открытого приглашения; нет окна — ошибка |
| `rimlink/live_end` | Гость: `LiveGuest.EndByPlayer`; хозяин: `LiveHost.Abort`; без окна подтверждения |
| `rimlink/send {player, items?: [{def, count}], pawns?, silver=0, anywhere=false}` | `Outgoing.SendItems`; вещи — из-под маяков (`TradeUtility.AllLaunchableThingsForTrade`), с `anywhere` — любые заспавненные |
| `rimlink/post_lot {items? \| pawn?, price, anywhere=false}` | `Outgoing.PostLot` (ровно одно из `items`/`pawn`) |
| `rimlink/market {seller?}` | Лоты рынка (id, продавец, содержимое, цена) |
| `rimlink/buy {lotId}` | `EconomyClient.Buy` и обновление состояния, как в окне экономики |
| `rimlink/deposit {silver, anywhere=false}` | `Outgoing.Deposit` |
| `rimlink/chat {count=20}` | Последние сообщения из `RimlinkSession.State.Chat` |
| `rimlink/chat_send {text, to?}` | `Chat.SendAsync` (публично или игроку) |

Действия возвращают `{success, started, message}` сразу после запуска; исход — через `rimlink/status` и строки
`[Rimlink]` в `rimbridge/list_logs`. Выход в меню — существующий `rimworld/go_to_main_menu` (патч Rimlink сохраняет и
выгружает сейв; проверяется в прогоне).

## 5. Новая колония за один вызов

`start_colony` работает, когда открыта `Page_SelectScenario` (после `connect` с исходом `NewColony`) или когда сессия
активна в `ProgramState.Entry` (тогда открывает её сам). Цикл по верхней `Page`, на каждой странице — подготовка, затем
`CanDoNext()` → `DoNext()` через `AccessTools` (методы `protected`). Если страница открыла `Dialog_MessageBox` с
подтверждением, компаньон нажимает подтверждение.

| Страница | Подготовка |
|---|---|
| `Page_SelectScenario` | `curScen` = сценарий по `scenario` (имя или defName), по умолчанию Crashlanded |
| `Page_SelectStoryteller` | рассказчик (по умолчанию Cassandra), сложность (по умолчанию Rough), `permadeathChosen = true`, `permadeath = false` |
| `Page_ChooseIdeoPreset` (Ideology) | классическая идеология (как пункт «Classic» страницы) |
| `Page_CreateWorldParams` | мир есть на сервере: параметры уже навязаны `Patch_Page_CreateWorldParams`; админ без мира: дефолты или `seed`. `CanDoNext` ставит генерацию в long event и возвращает false: ждать простоя long events и следующую страницу. Админ: дополнительно ждать `RimlinkSession.World.Exists` (регистрация мира идёт фоном) |
| `Page_SelectStartingSite` | `tile` или `TileFinder.RandomStartingTile()`; пока `TileFinder.IsValidTileForNewSettlement` ложно (тайл занят чужим поселением) — новая попытка, не более 20; подтверждение «рядом поселение» — принять |
| `Page_ConfigureStartingPawns` | пешки по умолчанию; подтверждение об отключённых работах — принять |
| неизвестная `Page` | только `CanDoNext()` → `DoNext()`; шаг записывается в ответ |

Ожидание конца: `Playing`, `Find.CurrentMap != null`, своё поселение в `State.Objects`. Таймаут — `success=false`, тип
верхнего окна и последний шаг.

## 6. Исполнение и ошибки

- Игровые объекты — только на главном потоке: `ctx.MainThread.InvokeAsync`. Ожидания — `ctx.Game.RunUntilAsync`;
  первая (spike) задача проверяет, что предикат там выполняется на главном потоке; если нет — ожидание
  `NextFrameAsync` + `InvokeAsync` в цикле.
- Сетевые вызовы (`Buy`, `Chat.SendAsync`) — фоном; состояние обновляется через `MainThread.Post`, как в моде.
- Ответы — только анонимные объекты и простые DTO. Protobuf-сообщения и объекты `Verse` наружу не уходят.
- Нарушенное предусловие (нет сессии, не та `ProgramState`, неизвестный игрок/пешка/def, не та страница, нет
  приглашения) — `success=false` с причиной, без исключения. Исключение внутри — `success=false` и его сообщение.
- `connect` и `start_colony` не запускаются, пока идёт такой же поток (статический флаг).
- Пароль не попадает в ответ и в лог.

## 7. Тестирование и проверка

- xUnit (TDD): `PresetLogin` в `tests/Rimlink.Client.Tests` — порядок попыток, остановка, регистрация только при
  разрешении, пароль сервера. Игровой код автотестов не имеет (AGENTS.md).
- Бар коммита: `dotnet test Rimlink.sln -nologo -v q` зелёный, мод (`Build-ClientMod.ps1`) и BridgeTools собираются.
- Spike (первая задача плана): компаньон с одним `rimlink/status`, деплой, рестарт игры, `rimbridge/get_bridge_status`
  и `rimlink/status` через GABS — доказательство, что связка `Assembly.LoadFile` + резолвер находит уже загруженный
  `Rimlink.Client` на .NET 10 рантайме.
- Живой прогон через GABS (окно согласуется с сессией чек-листа, которая владеет играми, папкой `Mods` и сервером на
  25560; деплой — при закрытой игре): хост `connect` admin (register auto) → `start_colony`; гость `connect` Nova →
  `start_colony` на залоченном мире; `chat_send`/`chat` в обе стороны; `send` silver; `raid` snapshot;
  `live_propose` → `live_answer` → `live_end`; отдельно `menu_options`/`click_menu_option`, `page_action random_site`,
  `go_to_main_menu` с выгрузкой сейва. Результат — строкой в `docs/manual-checklist.md`.

## 8. Документация

- AGENTS.md: раздел «Управление игрой из агента (GABS + Rimlink.BridgeTools)» — сборка и деплой, правило «одна DLL»,
  рестарт после деплоя, список инструментов, типовой сценарий, ловушки (Steam должен быть запущен, `set_text_field`
  только `instant`, `get_bridge_status` при пропавших инструментах). Строка `tools/` в таблице раскладки.
- Заметка для апстрима RimBridgeServer (без issue/PR) — в папку общих заметок сессий: `get_ui_layout` не видит
  `OptionListingUtility` и `DoCustomBottomButtons` страницы места, `typed` в `set_text_field` уходит в таймаут.

## 9. Порядок работ

1. Spike: проект, скрипт деплоя, `rimlink/status`, проверка загрузки в игре.
2. Seam: `PresetLogin` (TDD), `LoginOutcome`, `LoginFlow.Run` с параметрами, `GameCredentials(PresetLogin)`,
   `Dialog_LiveInvite.Answer` public.
3. `rimlink/connect`.
4. `rimlink/menu_options`, `click_menu_option`, `page_state`, `page_action`.
5. `rimlink/start_colony`.
6. Действия: рейды и визиты, живые сессии, экономика, чат.
7. Живой прогон на двух экземплярах, AGENTS.md, чек-лист, заметка для апстрима.
