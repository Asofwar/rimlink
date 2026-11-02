# Steam retry: результат проверки в настоящем клиенте

Дата: 2026-11-02. Исходники: `D:/Build/rimlink-next`, HEAD `25f6c8a7cff217782c07fa0d2a349efdaf77f116`.

Статус: **PASS — настоящий игровой Steam-путь и авторетрай после typed refusal; серверный verifier подменён**. Main запустил harness и выполнил вход настоящим guest через обычный `Dialog_ServerList`. Три отказа сменились успешным входом под тестовой identity; наблюдался SteamWait, парольное окно не появлялось, итог — `Connected` и `Page_SelectScenario`. Ни Steam Web API, ни действительность билета на стороне Steam этой проверкой не подтверждаются.

## Что именно проверяется

Настоящий RimWorld с обычным `GameCredentials` отправляет запрос по сети настоящему приложению `Rimlink.Server`. Тестовая реализация `ISteamTicketVerifier` имеет `Configured=true`, отказывает первым трём непустым билетам, затем возвращает заведомо тестовую identity: `SteamId=42`, `OwnerSteamId=42`, `VacBanned=false`, имя `SteamRetryTest`.

Отказ формирует production `AuthService.LoginWithSteam`: gRPC `Unauthenticated`, typed code `SteamTicketRejected`, ключ `AuthSteamTicketInvalid`. Production `LoginFlow` фактически показал `Rimlink.Flow.SteamWait` и повторил вход с задержками 2, 4 и 8 секунд, сохранив Steam-путь без `Dialog_Login`. Это настоящий сетевой ответ с типизированным отказом, а не разрыв транспорта. Связь с typed code дополнительно сверена по исходникам `AuthService` и условию retry в `LoginFlow`; сырые gRPC payload/trailers не сохранялись.

По исходникам обычный `GameCredentials` получает билет через штатный `SteamUser.GetAuthSessionTicket`, когда доступен Steam SDK. В прогоне main использован именно обычный игровой вход, без парольного preset; непустой Steam credential дошёл до verifier. Происхождение билета следует из этого штатного пути; отдельного снимка `SteamManager.Initialized` в перечисленных JSON нет. Содержимое билета не исследовалось и не сохранялось. **Серверная проверка через Steam Web API не выполнялась:** harness возвращает тестовую identity независимо от подлинности билета, а `realSteamApiVerified` в итоговом доказательстве равен `false`.

## Фактический игровой прогон main

Стенд: PID `35824`, `https://127.0.0.1:25784`. Собственный runRoot:

`D:/Build/rimlink-next/.superpowers/sdd/next-features/steam-retry-harness/runs/20261102-091311-5bf5d9c80c6241908e02b6716c265701/`

`ready.json` фиксирует готовность в `2026-11-02T09:13:12.9122212Z`, этот PID, единственный адрес и три настроенных отказа. Это свидетельство выполненного запуска, а не утверждение о текущем состоянии процесса.

Клиент версии `0.7.0` загрузил `Rimlink.Client.dll` из отдельной игровой копии:

`D:/Build/rimlink-next-wt/verification/build/verification/game/Mods/Rimlink/Assemblies/Rimlink.Client.dll`

Последовательность по сверенным артефактам:

1. `steam-initial.json`: верхнее окно `Dialog_PinPrompt`, надписи `New server certificate` и `Trust and continue`. Записанный SHA-256 совпадает с fingerprint, показанным в UI. Main подтвердил доверие через Trust UI; сертификат или его закрытый ключ при обновлении отчёта не читались.
2. `verifications.jsonl`: ровно четыре записи; они полностью совпадают с массивом `attempts` в `steam-retry-completed.json`. Все помечены `FAKE-NOT-STEAM-API`.

   | Попытка | Время UTC, 2026-11-02 | Вердикт | Интервал после предыдущей |
   |---|---|---|---|
   | 1 | 09:15:11.0821499 | refused | — |
   | 2 | 09:15:13.4758542 | refused | 2.394 с |
   | 3 | 09:15:17.8280804 | refused | 4.352 с |
   | 4 | 09:15:26.1678284 | test-identity | 8.340 с |

   Интервалы включают сеть и обработку, соответствуют backoff 2/4/8 секунд; от первого отказа до принятия прошло около 15.086 секунды.
3. `steam-retry-ui.json`: `waitObserved=true`, `passwordDialogObserved=false`. В 83 наблюдениях за интервал `elapsed=0.046…15.544` верхнее окно — `Dialog_Wait`; в 80 снимках labels есть `Waiting for Steam to accept the login, this can take a couple of minutes...`. `Dialog_Login` в наблюдениях отсутствует; main также сообщил, что его не было за весь вход. Скриншот SteamWait визуально подтверждает эту строку.
4. `steam-retry-completed.json`: соединение `Connected`, endpoint `SteamRetryHarness (127.0.0.1:25784)`, игрок `SteamRetryTest`, роль `Admin`, верхнее окно `Page_SelectScenario`, `hasCurrentGame=false`. В `testPlayers` единственная строка `[42, "SteamRetryTest"]`. Итоговый скриншот показывает `Choose scenario`. Цель retry достигнута до создания колонии; запуск карты этим результатом не проверялся.

Проверенные исходные доказательства:

- [steam-initial.json](D:/Build/rimlink-next/build/game-acceptance/evidence/round2/round7/steam-initial.json)
- [steam-retry-ui.json](D:/Build/rimlink-next/build/game-acceptance/evidence/round2/round7/steam-retry-ui.json)
- [steam-retry-completed.json](D:/Build/rimlink-next/build/game-acceptance/evidence/round2/round7/steam-retry-completed.json)
- [verifications.jsonl](D:/Build/rimlink-next/.superpowers/sdd/next-features/steam-retry-harness/runs/20261102-091311-5bf5d9c80c6241908e02b6716c265701/verifications.jsonl)
- [ready.json](D:/Build/rimlink-next/.superpowers/sdd/next-features/steam-retry-harness/runs/20261102-091311-5bf5d9c80c6241908e02b6716c265701/ready.json)
- [SteamWait — фактическое имя round7_steam-wait.png.png](D:/Build/rimlink-next/build/game-acceptance/evidence/round2/round7_steam-wait.png.png)
- [Выбор сценария — фактическое имя round7_steam-new-colony.png.png](D:/Build/rimlink-next/build/game-acceptance/evidence/round2/round7_steam-new-colony.png.png)

При обновлении отчёта повторно прочитаны перечисленные JSON и журнал verifier, просмотрены оба скриншота, проверены совпадение записей попыток, SHA-256 в UI и evidence, флаги ожидания/пароля, финальное окно и тестовая identity. Live-процессы, GUI и GABS не управлялись; список слушающих сокетов ОС повторно не запрашивался. По сообщению main guest уже возвращён на `25783`. Завершение harness по лимиту 15 минут или вручную остаётся под управлением main; его актуальный lifecycle этим отчётом не подтверждается.

## Реализация и изоляция

Временные исходники находятся только в игнорируемом каталоге:

`D:/Build/rimlink-next/.superpowers/sdd/next-features/steam-retry-harness/`

- `SteamRetryHarness.csproj` ссылается на настоящий проект `src/Rimlink.Server`; не включён в solution или release packaging.
- `RetryFactory.cs` вызывает его существующий `Program` через `WebApplicationFactory<Program>.UseKestrel()`; сервер, RPC и auth flow не скопированы.
- Использован подход существующего `ServerFixture`: DI override через `ConfigureTestServices`, отдельный DataDir, минимальная тестовая строка мира. Сам `ServerFixture` не подключён: его `CreateChannel` рассчитан на `TestServer`; нужен реальный TLS listener. Существующий `FakeSteamVerifier` принимает синтетический UTF-8 `steam:...`, поэтому неприменим к билету обычного игрового клиента.
- У production entry point есть `ListenAnyIP`; harness удаляет регистрации настройки `KestrelServerOptions` и задаёт единственный `Listen(IPAddress.Loopback, 25784)` с HTTPS и HTTP/1.1+HTTP/2. После старта адреса дополнительно сверяются с единственным `https://127.0.0.1:25784`. Используются публичные DI/options API, без reflection и изменений production.
- Локальный self-signed TLS-сертификат создаёт штатный `ServerCertificate` в собственном DataDir. Пользовательские сертификаты и профили не используются.
- Launcher очищает окружение дочернего процесса и передаёт только служебные Windows-пути и тестовые настройки. Environment — `Production`, content root — новая пустая папка собственного запуска; user secrets и launch profiles не загружаются. `SteamWebApiKey` не задаётся. Локальные конфиги исключены из build items.
- Парольные аккаунты отключены. Тестовый id `42` назначен админом только этого одноразового сервера. При успешном входе штатный auth создал тестовую запись в его SQLite; настоящий Steam-аккаунт не создавался.
- Каждый запуск получает новый `runs/<UTC>-<guid>/data`. Счётчик отказов общий на этот процесс: один клиент, без предварительных пробных `Auth.Login`. Повтор сценария — новый запуск и новый DataDir.
- Стенд сам завершится через 15 минут после успешного старта. Fake принимает любые непустые байты после трёх отказов; допустим исключительно на loopback в этой временной проверке.
- Сборка, промежуточные файлы и зависимости проектов размещаются в `steam-retry-harness/artifacts/`. Общие `bin/obj` не очищаются и не используются как место вывода.

Порт `25783`, сервер main, игры, GUI, GABS, общий конфиг GABS и установленные моды при подготовке не затрагивались. Production-файлы и существующие тестовые фикстуры не изменялись.

## Автономная проверка при подготовке

PowerShell, рабочий каталог `D:/Build/rimlink-next`:

```powershell
rtk proxy powershell -NoProfile -File 'D:\Build\rimlink-next\.superpowers\sdd\next-features\steam-retry-harness\Harness.ps1' -Action Check
```

Сборка `net10.0`, SDK `10.0.402`, `Microsoft.AspNetCore.Mvc.Testing 10.0.14`: успешно. В исходниках сервера есть два существующих предупреждения CS9113/CS9124; код harness собран без ошибок.

Самопроверка проверяет `Configured`, пустой билет без расходования попытки, отмену, три отказа, тестовую identity, повторный успешный вызов, тестовое имя и доступность `UseKestrel` без обращения к `StartServer`/`Services`/`CreateClient`.

```text
SELF-CHECK PASS: fake verifier; UseKestrel API available; NO HOST STARTED; NO NETWORK
```

Этот PASS относится только к перечисленной автономной проверке. Полный `dotnet test Rimlink.sln` не запускался: production не менялся, запуск приложения и игровые процессы оставлены main.

## Повторный прогон: инструкция main

1. Сначала завершить подготовку и наблюдение своего клиента на `25783`. Harness не требует остановки или изменения того сервера. Проверить, что используемый клиент собран из нужного HEAD и доступен Steam SDK; сохранить только факт `SteamManager.Initialized`, без билетов, handles или содержимого credentials.
2. После handoff запустить отдельный harness в PowerShell:

   ```powershell
   rtk proxy powershell -NoProfile -File 'D:\Build\rimlink-next\.superpowers\sdd\next-features\steam-retry-harness\Harness.ps1' -Action Start
   ```

   Команда не пересобирает приложение и не останавливает процессы. Если `25784` занят, она откажет. Вывод содержит `runRoot` и `serverPid` отдельного процесса `dotnet`. **Готовность подтверждает только `ready.json`** с тем же PID, созданный после старта настоящего приложения.

3. В выданном `runRoot` дождаться `ready.json`; при `failed.json` не считать стенд поднятым. `ready.json` содержит только UTC, PID, адрес, лимит времени, число отказов и тестовый id. Проверить реальный listener по PID из этого файла. PowerShell, полный путь `runRoot` взять из вывода команды выше:

   ```powershell
   rtk proxy powershell -NoProfile -Command 'Get-NetTCPConnection -LocalPort 25784 -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess'
   ```

   Ожидается исключительно `127.0.0.1:25784`, PID совпадает с `ready.json`. Это проверка перед игровым входом, а не заранее заявленный результат. Не доверять `ready.json` от завершившегося предыдущего процесса.

4. Main обычным интерактивным входом в меню Rimlink выбирает отдельный адрес `127.0.0.1:25784`. Доверяет только локальному тестовому сертификату этого адреса при запросе pin. Сохраняет прежний endpoint своего клиента для возврата. GUI и браузер управляются только CUA Driver.
5. **Не использовать `rimlink/connect` для этого сценария:** `GameCredentials(PresetLogin)` устанавливает `PreferPassword=true` и обходит Steam. Не подменять credentials или выдачу билета в клиенте. Входить без парольного preset и без предварительного ввода логина/пароля.
6. Наблюдать окно ожидания: русская строка `Ждём, пока Steam подтвердит вход, это может занять пару минут...` соответствует `Rimlink.Flow.SteamWait`. Дождаться трёх повторов; ожидаемые паузы 2/4/8 секунд, суммарно около 14 секунд плюс SDK/сеть/загрузка. Зафиксировать отсутствие `Dialog_Login` на протяжении попытки.
7. В `verifications.jsonl` ожидаются попытки `1/2/3: refused`, `4: test-identity`. В журнале клиента допустимы только выбранные строки `[Rimlink] Steam refused the ticket, trying again in ... s`; не выгружать весь Player.log или сетевые payload. Дальше ожидается штатный успешный вход и создание новой колонии без парольного окна. До создания настоящей колонии цель retry уже достигнута; саму колонию создавать не требуется.
8. Если SDK недоступен и открылся `Dialog_Login` до первого вызова verifier, результат — **BLOCKED: Steam SDK unavailable**, не PASS retry. Если открылась форма пароля после typed refusals или нет автоматических повторов, записать фактический результат как FAIL/INCOMPLETE. Не вводить реальные credentials.
9. Записать итог в таблицу ниже и завершить только собственный harness. Предпочтительно дождаться его автоматического завершения; при необходимости main завершает именно PID сервера из `ready.json`, затем проверяет освобождение `25784`. Возврат клиентского endpoint и игровой сессии выполняет main. Другие процессы и порт `25783` не затрагивать. Тестовые артефакты остаются в игнорируемом каталоге до решения main об удалении.

## Что сохранять как доказательство

Счётчики verifier содержат только UTC, номер попытки и вердикт. Ни билет, ни его hex/base64, ни хеш, ни длина, ни сессионный токен в них не записываются. Fake не удерживает входной `byte[]`. Не добавлять HTTP/gRPC body tracing, дампы памяти и экспорт конфигурации. `.env`, реальные secret profiles и SteamWebApiKey не читать.

Для доказательства нужны вместе: SDK доступен; обычный клиентский Steam-путь; окно SteamWait; три повтора; последовательность отказов и успешный логин; отсутствие парольного окна. Одного счётчика fake или одного скриншота недостаточно. Серверное принятие неподлинного тестового билета само по себе ничего не доказывает о Steam.

| Проверка | Подтверждённый результат |
|---|---|
| Изолированная сборка настоящего `Rimlink.Server` и harness | PASS |
| Автономная проверка тестового verifier | PASS |
| `WebApplicationFactory.UseKestrel` в используемом пакете | Настоящее приложение запущено main, PID 35824 |
| Loopback HTTPS и сетевой gRPC | ready.json: 127.0.0.1:25784; Trust UI и SHA-256 сверены; игровой вход выполнен; сокеты ОС при обновлении отчёта не перепроверялись |
| Steam credential в настоящем клиенте | Штатный игровой Steam-путь пройден; вызов GetAuthSessionTicket следует из исходников, отдельного снимка SDK-флага нет; билет не исследовался |
| SteamWait, три авторетрая, отсутствие `Dialog_Login` | PASS: три отказа, 83 наблюдения UI, SteamWait на скриншоте, парольное окно не наблюдалось |
| Вход под тестовой identity после отказов | PASS: попытка 4 принята, SteamId 42 / SteamRetryTest, Connected, Page_SelectScenario |
| Настоящий Steam Web API / действительность билета | НЕ ПРОВЕРЯЕТСЯ, verifier подменён |

Официальные основания: [UseKestrel в ASP.NET Core 10](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0), [исходник WebApplicationFactory v10.0.14](https://github.com/dotnet/aspnetcore/blob/v10.0.14/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs). Поиск в Context7 выполнен; запрос текста документации не прошёл из-за transport error, API дополнительно сверены с установленным XML пакета и официальным исходником.
