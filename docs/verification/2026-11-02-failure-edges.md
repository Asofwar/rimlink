# Аварийные экономические сценарии Rimlink 0.7.0 — 2026-11-02

В выполненной матрице **343/343 проверки прошли: 285 существующих серверных, 42 клиентских и 16 дополнительных случаев harness**. Падений и пропусков нет. Воспроизводимых ошибок runtime этими проверками не обнаружено. Уровень результата — **SERVER/CORE only**; новых **GAME PASS нет**.

Проверено дерево `D:/Build/rimlink-next`, HEAD `25f6c8a7cff217782c07fa0d2a349efdaf77f116`, версия `Directory.Build.props` — `0.7.0`. Перед запуском рабочее дерево было чистым; после запусков HEAD, исходники runtime и существующие тесты не изменены. Созданы только этот отчёт и собственные игнорируемые файлы в `.superpowers/sdd/next-features/failure-edges/`; сборка направлена в `build/failure-edges-tests`.

Игры, общий private stand, сервер `25783`, чужие процессы и `.env` не использовались. Коммитов, push, установки мода и deploy нет. `docs/manual-checklist.md` не изменялся.

## Контракт и способ проверки

Прочитаны [действующая спецификация](../superpowers/specs/2026-11-01-rimlink-next-features-design.md), [план](../superpowers/plans/2026-11-01-rimlink-next-features.md), экономические спецификации [исходной экономики](../superpowers/specs/2026-10-28-rimlink-economy-design.md) и [hardening](../superpowers/specs/2026-10-30-rimlink-economy-hardening-design.md), [уточнения аукционов](../auctions-implementation.md), [чеклист](../manual-checklist.md) и границы [предыдущего игрового прогона](2026-11-02-game-mcp-acceptance.md).

Контрольные инварианты: повтор привязан к игроку и исходному намерению; резерв/оплата/товар изменяются атомарно; повторное завершение не создаёт выплату или возврат; офлайн-игроки получают расчёт через кошелёк и почту; freeze откладывает автоматический расчёт; административный возврат разрешён при freeze; полный mailbox не уничтожает уже принятый escrow.

Существующие интеграционные тесты запускают настоящий `Program` через `ServerFixture : WebApplicationFactory<Program>` / ASP.NET TestServer с отдельной временной SQLite и каталогом данных. Это не mock-сервис. Часть выбранных тестов — чистые проверки ядра/арифметики; доказательство серверных сценариев опирается на конкретные интеграционные случаи ниже, а не на общий счётчик.

Дополнительный xUnit-проект использует те же fixtures и настоящий `EconomyClient` / `RimlinkConnection` / file-backed `TransferOutbox`. Для сетевых сценариев поднят только транспортный HTTP/2 relay к собственному TestServer: Kestrel слушает `127.0.0.1:0`, ОС назначает отдельный порт. Relay пересылает запросы, тела и gRPC trailers; экономической логики и заготовленных ответов в нём нет. В финальном прогоне использованы порты **8459, 48051, 46562, 15364, 15368**; все relay завершены через собственный `DisposeAsync`. Сам TestServer TCP-порт не занимает.

Harness выполняется последовательно. На каждый случай задан timeout 60 секунд, ожидание фонового результата ограничено 15 секундами и использует `Stopwatch`; остановка собственного relay ограничена 10 секундами. VSTest дополнительно ограничивает зависание через `--blame-hang-timeout 3m`.

## Команды и результаты

Все команды выполнялись в PowerShell из `D:/Build/rimlink-next`, через `rtk`. Фактический первоначальный запуск существующего набора:

```powershell
rtk proxy powershell -NoProfile -File .superpowers/sdd/next-features/failure-edges/run-existing.ps1
```

Внутри wrapper последовательно выполнены следующие команды; путь `--results-directory` здесь записан с эквивалентными прямыми разделителями:

```powershell
rtk proxy dotnet test tests/Rimlink.Server.Tests --artifacts-path build/failure-edges-tests --nologo -v minimal --filter 'FullyQualifiedName~Auctions|FullyQualifiedName~Economy|FullyQualifiedName~Procurement|FullyQualifiedName~Trades|FullyQualifiedName~PlayerFreezeAdminTests|FullyQualifiedName~AdminOperationsTests|FullyQualifiedName~ColonyArchiveTests|FullyQualifiedName~ArchiveAdminTests' --logger 'trx;LogFileName=server.trx' --results-directory 'D:/Build/rimlink-next/.superpowers/sdd/next-features/failure-edges/results' --blame-hang-timeout 3m
rtk proxy dotnet test tests/Rimlink.Client.Tests --artifacts-path build/failure-edges-tests --nologo -v minimal --filter 'FullyQualifiedName~Economy|FullyQualifiedName~Procurement|FullyQualifiedName~TradeOutboxTests' --logger 'trx;LogFileName=client.trx' --results-directory 'D:/Build/rimlink-next/.superpowers/sdd/next-features/failure-edges/results' --blame-hang-timeout 3m
```

Запуск собственного harness и проверка сохранённых доказательств:

```powershell
rtk proxy pwsh -NoProfile -File .superpowers/sdd/next-features/failure-edges/run-harness.ps1
rtk proxy pwsh -NoProfile -File .superpowers/sdd/next-features/failure-edges/verify-evidence.ps1
```

`run-harness.ps1` выполняет:

```powershell
rtk proxy dotnet test 'D:/Build/rimlink-next/.superpowers/sdd/next-features/failure-edges/FailureEdgesHarness.csproj' --artifacts-path build/failure-edges-tests --nologo -v minimal --logger 'trx;LogFileName=harness.trx' --results-directory 'D:/Build/rimlink-next/.superpowers/sdd/next-features/failure-edges/results' --blame-hang-timeout 3m
```

| Запуск | Passed / total | Failed / skipped | Длительность из вывода | Exit code |
|---|---:|---:|---:|---:|
| Существующие server tests | 285 / 285 | 0 / 0 | 49 s | 0 |
| Существующие client tests | 42 / 42 | 0 / 0 | 7 s | 0 |
| Финальный harness | 16 / 16 | 0 / 0 | 42 s | 0 |

Первый вариант harness прошёл 15/15 за 36 s. Затем добавлен отдельный вариант срока при работающем сервере с офлайн-игроками; финальный вариант прошёл 16/16. Первый TRX сохранён как `results/harness-first.trx`. Это расширение матрицы, падения продукта между запусками не было. Сборка существующих проектов показала прежние compiler/xUnit warnings; финальная компиляция harness новых предупреждений не выдала.

`verify-evidence.ps1` разобрал каждый TRX, проверил число и статус всех 343 случаев, 16 строк результатов harness, пять разных loopback-портов, неизменность HEAD/runtime/tests и синтаксис трёх PowerShell-скриптов. Итог:

```text
PASS: 343 distinct cases, 16 harness assertions, five isolated loopback ports, unchanged HEAD/runtime/tests, valid PowerShell syntax
```

## Выполненная матрица

| Сценарий | Фактически выполненная проверка и результат | Граница |
|---|---|---|
| Офлайн-расчёт при работающем сервере | Два лота: Steel ×5 со ставкой 100 и WoodLog ×7 без ставок. Обе сессии завершены; `SessionRegistry.ByPlayer` пуст до/после наступления срока. Сдвинут только fixture `ManualTimeProvider`. Настоящий фоновый `AuctionSweep` создал две посылки. Затем два рестарта host с той же SQLite сохранили ровно эти результаты. | SERVER PASS; не час реального ожидания и не две игры. |
| Срок во время остановки сервера | До остановки посылок 0; fixture host корректно остановлен с сохранением DataDir. Время fixture продвинуто за deadline, новый host открыт поверх той же базы без повторного seed мира. Фоновый startup sweep, без ручного `ExpireDue` до ожидания результата, закрыл лоты. Второй рестарт и контрольный sweep не создали дублей. | SERVER PASS; graceful host restart, не убийство ОС-процесса посреди транзакции. |
| Сохранность офлайн-расчёта | В обоих вариантах ровно один `AuctionSold`, один `AuctionExpired`, по одной посылке покупателю/продавцу. `ItemList` посылок побайтово равны исходным. Кошельки: продавец 90, покупатель 400, банк 10; сумма 500 равна начальному капиталу. `DeliveredAt` не выставлен. | SERVER PASS; проверены байты escrow, физические предметы не загружались. |
| Потеря Post/Bid/Cancel после commit | Три отдельных случая. До RPC намерение записано на диск. `DropReply` получает настоящий успешный HTTP/2 ответ с `grpc-status=0`, полностью читает его, затем уничтожает ответ и бросает `HttpRequestException` до получения клиентом. Клиент получает `RpcException`; в SQLite уже есть durable operation. Запись outbox после чтения с диска имеет исходный payload и не имеет refusal. | SERVER/CORE PASS; реальная потеря полученного серверного ответа в транспортном handler, не авария игрового процесса. |
| Replay после нового входа | Первая `RimlinkConnection` закрыта; создаётся другая, выполняется новый login. При глобальной заморозке исходный outbox завершается с `ReplayReport(1,0,0)`; следующий replay — `(0,0,0)`. Post оставляет один лот и одну запись `auction_post`; Bid — баланс 400 вместо повторного списания; Cancel — одну посылку с исходными байтами. Число durable operations не растёт от replay. | CORE PASS; новый объект соединения и чтение файла, не запуск нового процесса игры. Кнопка UI не нажималась. |
| Rate limit после принятого Post с потерянным ответом | Реальный limiter включён. После обрыва ответа две внутренние попытки replay получают ограничение; итог `(0,0,1)`, refusal отсутствует, возвратной посылки нет. После ограниченного ожидания окна limiter, закрытия соединения и нового login replay успешен даже при freeze. Один лот, один исходный payload, без возврата уже принятого escrow. | SERVER/CORE PASS. |
| Reset/archive продавца и лидирующего участника | Четыре случая: reset seller/bidder, archive seller/bidder. У обоих есть fixture-колонии и сейвы; после Post/Bid включены глобальный freeze и freeze обоих игроков. Административная операция возвращает ставку: покупатель снова 500; продавец получает одну посылку с точными исходными байтами; продавец/банк имеют 0. Статус Cancelled, ровно один `auction_return` и один соответствующий audit. Повторная `AdminCancel` возвращает false и ничего не добавляет. | SERVER PASS; создание новых игровых колоний и падение капсул не запускались. |
| REST/CLI cancel при freeze после ставки | Два случая проходят реальный admin REST endpoint, второй — через существующий CLI `AdminCommand.Run` / `RestClient`. Глобально и индивидуально заморожены обе стороны. Выполнены отмена и повтор: оба escrow возвращены, один `auction_cancel` audit, один возврат товара. | SERVER/CLI-library PASS; отдельный процесс CLI и браузерная панель не запускались. |
| Frozen settlement и разморозка | Три случая: global/seller/buyer freeze. После срока контрольный sweep возвращает 0; посылок и выручки нет, ставка 100 остаётся в escrow, баланс покупателя 400. После снятия freeze настоящий hosted worker выдаёт одну посылку, продавцу 100; повторный контрольный sweep возвращает 0, запись `auction_sale` одна, байты товара совпадают. | SERVER PASS. |
| Полный mailbox и сбой записи | Существующий `DeliveryFailureRollsBackSaleAndNextSweepCompletesExactlyOnceEvenWithFullMailbox`: при лимите 1 почта заполнена, SQLite trigger отклоняет INSERT посылки. Первая попытка не платит продавцу и не закрывает лот. После снятия сбоя следующий sweep завершает один раз, повтор — 0, в почте 2 посылки при лимите 1, деньги 100/400. | SERVER PASS; реальная транзакция SQLite с инъекцией ошибки INSERT. |
| Полный mailbox и повреждённый Scribe blob | Собственный случай: лимит 1, одна посылка уже есть; выкуп добавляет вторую. В товаре намеренно содержится недопустимый Zstd `[1,2,3]` при корректной protobuf-оболочке. Core дважды входит, List/Get доступны, возвращаются исходные байты, реальный Zstd decompressor бросает исключение. Без `Mailbox.Deliver` посылка остаётся pending, `DeliveredAt`/`DeliveredTick` null, записей deliver 0; кошельки 100/400. | SERVER/CORE preservation PASS. Это **не доказательство вызова игрового `Incoming.Drop`**: harness намеренно не посылает ack, Scribe/карта не запускались. |
| Серверное богатство escrow | Существующий `StorageFeeAndBothEscrowsAreCountedInWealth` прошёл: вещевой резерв продавца 500 и wallet+bid участника 500 учитываются без двойного счёта; после двух дней хранения комиссия переводится банку. Дополнительно прошли guards и boundary-проверки. | SERVER PASS; значение рассказчика на настоящей карте здесь не проверялось. |

Ключевые существующие проверки, подтверждённые именно этим запуском: `AuctionLifecycleTests.EscrowAndOperationIdentitySurviveServerRestartAndOfflineExpiry`, `DeliveryFailureRollsBackSaleAndNextSweepCompletesExactlyOnceEvenWithFullMailbox`, `SellerCreditOverflowLeavesEscrowAndCanBeRetriedAfterBalanceHasRoom`, `RefundOverflowRejectsNewBidWithoutLosingEitherReserve`, `ConcurrentBuyoutsAndCloseProduceOneWinnerAndConserveMoney`, `FrozenOrFailingAuctionsDoNotStarveLaterDeadlines`; `AuctionGuardTests.ReplaysSucceedDuringFreezeAndSweepWaitsForThaw`, `ResetCancelsAuctionWhenPlayerIsSellerOrHighestBidder` с обоими параметрами, `ArchivingColonyReturnsBothEscrowsAndBlocksNewAuctionsUntilRestored`; все четыре `AuctionOutboxTests`.

Выбранный серверный набор также исполнил существующие procurement/trades regression cases: рестарт/истечение, гонки поставок и отмены, переполнение возврата с повтором, reset/archive, запреты при freeze и сохранность принятого вклада. Они не заменяют перечисленные аукционные случаи.

## Что остаётся открытым

Серверные и core-части запрошенных аварийных сценариев подтверждены в указанной матрице. Составные игровые пункты `manual-checklist.md` около строк 948–954 **остаются без GAME PASS**:

1. В настоящих играх выйти обоими игроками до срока, дождаться офлайн-расчёта/перезапустить выделенный сервер, затем войти и подтвердить по сейвам, что покупатель получил одну физическую поставку, а лот без ставок вернулся один раз. Harness доказывает ledger/mailbox, а не спавн и сохранение карты.
2. Потерять ответы Post/Bid/Cancel из игрового UI, проверить кнопку повтора и автоматический replay после входа. Проверить, что изъятие стака/сейв до RPC и окончательный отказ возвращают физические предметы один раз; rate limit не возвращает уже принятый лот. Core-повторы проверены, игровая часть `Outgoing` не исполнялась.
3. После reset/archive каждой стороны и admin cancel при freeze подтвердить появление единственной возвратной капсулы и сохранность её содержимого после игрового сейва/загрузки. Здесь проверены серверный адресат, точные байты и однократность возврата.
4. Полный mailbox: получить оплаченный товар в настоящую карту. Повреждённый blob: исполнить настоящий `Incoming.Drop`/Scribe, увидеть отказ распаковки, отсутствие ack/капсулы и сохранение посылки после нового входа. В harness проверен только отказ Zstd и хранение без ack. Повреждение всей protobuf-оболочки строки `Parcels.Items`, отказ Scribe на валидном Zstd и сбой создания drop pod здесь не моделировались.
5. На настоящей карте проверить вклад накопленного auction escrow в богатство/угрозу рассказчика, включая freeze/unfreeze. Серверный `ServerWealth` проверен; Harmony-путь рассказчика не выполнялся.

Это конкретные границы текущего поручения: игры и общий стенд находятся под управлением пользователя. EN/RU layout, Steam/Discord, физический ввод и браузерная панель не входили в эту проверку. Полная `Rimlink.sln`, сборка игрового мода, Linux/2-CPU timing и crash recovery посреди commit не запускались. Перезапуск fixture — корректная остановка/новый host с той же SQLite; клиентский relogin — новое соединение с чтением outbox, без падения ОС-процесса.

Fixture `EconomyFixtures.Items` создаёт тестовые blobs, а не валидные RimWorld-вещи. Побайтовая сохранность в этих проверках доказывает сохранность серверного payload и суммы денег; она не доказывает успешную загрузку предмета в игре. Статический просмотр `src/Rimlink.Client/Game/Economy/Incoming.cs` подтверждает ветку catch до добавления `DeliverRequest` при ошибке `Unpack`, но такая проверка исходника не повышает результат до GAME PASS.

## Артефакты

Все локальные доказательства находятся в `.superpowers/sdd/next-features/failure-edges/` и игнорируются Git:

- `FailureEdgesHarness.csproj`, `FailureEdges.cs` — ограниченный assert harness, 16 случаев;
- `run-existing.ps1`, `run-harness.ps1`, `verify-evidence.ps1` — воспроизводимые команды и проверка результатов;
- `server.log`, `client.log`, `harness.log`, `existing-results.json`, `harness-result.json` — выводы и коды завершения;
- `results/server.trx`, `results/client.trx`, `results/harness.trx` — первичные результаты с именами, длительностями и stdout;
- `harness-first.log`, `harness-first-result.json`, `results/harness-first.trx` — первоначальный вариант 15/15;
- `evidence-index.json` — статусы всех 343 случаев, реальные порты, SHA-256 TRX, скриптов и собранных server/core/test/harness DLL.

| Файл | SHA-256 |
|---|---|
| `results/server.trx` | `465946650E01C779C1C51918A6E62E1E8BEB91C098B9349570BDAFC48AA2370B` |
| `results/client.trx` | `19DD2B7BF7C0CCA1C2C1923C223DCDE5157AF74D5E947F32EFA7B3461C42318C` |
| `results/harness.trx` | `76E7D49EA09AC4438FA2C0EC4A7DDA1C2823828689AC0AB92127350C7412D213` |

Логи и отчёт сохранены в UTF-8. Первоначальные server/client логи, записанные Windows PowerShell через Tee-Object, преобразованы в UTF-8 без изменения текста; wrapper затем исправлен на явный UTF-8 writer. Исходные TRX не преобразовывались. Проверки не требуют исправления/отката runtime: изменений в нём нет. Игнорируемые доказательства сохранены для повторного разбора; автоматически ничего не удалялось.
