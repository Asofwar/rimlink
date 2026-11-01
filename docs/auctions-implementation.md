# Аукционы: передача интегратору

Рабочее дерево: `D:/Build/rimlink-next-wt/auctions`, ветка `asofwar/next-auctions`, основа `9e70b25`.
Коммитов, push, release, deploy и изменения версии нет. Production, credentials, чужие worktree и игровые процессы
не использовались. Сборка читает DLL установленной игры, но собранный мод остаётся только в этом рабочем дереве.

## Реализованный путь

- `auctions.proto`: Post/Bid/Cancel/List, состояние аукциона, descriptor-only проекции. `ServerEvent.auction_updated = 110`;
  `ErrorCode` 56–59. Остальные зарезервированные номера не заняты.
- Отдельные `AuctionRow`/`AuctionOperation` и EF-конфигурации. Успешный запрос хранит `(player_id, request_id)`, SHA-256
  сериализованного намерения с типом операции и точный ответ. Повтор после потери ответа/завершения/заморозки возвращает
  этот ответ; другой игрок имеет отдельное пространство ID, другое намерение с тем же ID отклоняется.
- Предметы изымаются существующим `Outgoing` после выгрузки сейва, операция записывается в обычный file-backed outbox.
  При окончательном отказе предметы восстанавливаются тем же путём, что рыночные лоты. Bid/Cancel ничего физического
  из игры не удерживают. Три новых вида outbox добавлены в конец enum.
- Ставка резервируется транзакционно; перебитая возвращается в той же транзакции. Собственный лидер повышает ставку
  только на разницу. Продавец не ставит на свой лот. Выкуп требует ровно заявленную цену и явный buyout-флаг.
- Последние 30 секунд: продление на 30 секунд от текущего срока, максимум +300 секунд от исходного. Sweep при старте
  и каждые 5 секунд повторно проверяет текущий срок/состояние в serializable-транзакции. Неуспешный или замороженный лот
  не блокирует обработку следующих. Без ставок — возврат, с победителем — одна посылка и одна выплата продавцу.
- Применяются существующие wallet/mailbox, комиссия банку, хранение perishables, общий лимит фиксированных/аукционных
  лотов, размеры, threat/debt/freeze и forbidden defs. Предметы относятся к богатству продавца, текущая ставка — участника.
  Числовые границы проверяются до wallet-арифметики; некорректные descriptor-стоимости не попадают в wealth/storage.
- Отмена продавцом только до первой ставки. Admin cancellation, reset и archive возвращают оба escrow, в том числе
  при заморозке. Существующие ограничения ролей/REST scopes/CSRF сохранены; отмены администратора журналируются.
- Новые регистрации и REST/gRPC map находятся в `AuctionFeature`, в `Program` только вызовы расширения.
- Вкладка **Аукционы**: создание вещевого лота через маяки, цены/срок, ставки/выкуп с подтверждением, отмена, страницы,
  свои лоты/текущие ставки, статусы, deadline, replay ожидающих операций. EN/RU: 33 ключа в каждой локали. Вкладки окна
  размещены в два ряда, высота caravan-режима сохранена. Существующий dev action **Open economy window** открывает
  все эти операции для ручного прогона.
- REST: `GET /api/admin/auctions/`, `POST /api/admin/auctions/{id}/cancel` с reason 1–256 символов без controls.
  CLI/admin chat: `auctions`, `auction cancel <id> [reason]`.

## Принятые уточнения

1. Угроза и forbidden defs проверяются при новом выводе: для продавца при Post, для серебра участника при Bid.
   Изменения запретов/угрозы продавца не запирают уже изъятые вещи. Это соответствует существующим market/trades
   и разделу 10 economy-hardening; новая поставка запрещённого предмета по-прежнему отклоняется.
2. Заморозка продавца/победителя откладывает автоматическое завершение; после снятия следующий sweep продолжает.
3. Выдача уже оплаченного товара допускает полный mailbox, как существующие escrow returns; предметы не пропадают.
4. При переполнении суммы возврата/выручки/банка транзакция откатывается целиком. Escrow остаётся до освобождения
   места в кошельке. Частичная выплата, перенос через незащищённый арифметический путь и потеря резерва исключены.
5. В `TransferOutbox.IsDefinitive` исправлен существующий случай: `ResourceExhausted` с типизированным `UNSPECIFIED`
   от rate limiter остаётся неопределённым результатом. Иначе replay ранее принятого лота мог вернуть его вещи повторно.
6. Динамические size/forbidden/freeze-ограничения проверяются после чтения сохранённого результата, поэтому изменение
   правил после приёма запроса не превращает replay в отказ с возвратом уже существующих на сервере вещей.

## Миграция и точки интеграции

Для локальных тестов создана **одна** временная миграция `20261101204228_AuctionsTemporary`. Не переносить её и её
Designer/изменённый ModelSnapshot в итоговую ветку: координатор создаёт объединённую миграцию. Тест схемы обновляет
реальную базу с последней миграции 0.6.2 (`20261030085555_IncidentRequestIdPerBuyer`) и проверяет сохранность wallet/player
и уникальность durable operation ID. Предварительная правка `RimlinkDbContext.ApplyConfigurationsFromAssembly` принадлежит
координатору, не изменялась и не откатывалась.

Общие точки для разрешения конфликтов: proto common/events, Program, shared economy window, EconomyClient/TransferOutbox,
connection/state, ServerWealth, shared market lot limit, AdminOperations reset, ColonyArchive, admin command tables и
`RefusalKeys`. Изменения ограничены подключением аукционов и необходимой защитой replay.

`Hosting/RefusalKeysTests.cs` оставлен без наших правок по указанию координатора. Для интеграционного прогона нужны его
централизованные изменения:

- Keyed() должен загружать все `Keyed/*.xml`, включая `Auctions.xml`.
- В существующий точечный список forwarding добавить
  `("Application/Auctions/AuctionService.cs", "RpcErrors.Failed(refusal.Code, refusal.Key, refusal.Message, refusal.Args)")`.
  Это перенос существующего ключа/аргументов `ParcelValidator`, а не новый отказ без локализации.

## Проверки

RED наблюдался до добавления server/core API. Отдельно воспроизведены RED для rate-limited replay, уменьшения лимита
размера после успешного Post, чрезмерной descriptor-стоимости, уже изъятых вещей под угрозой/изменённым blacklist и
starvation после первых 100 замороженных лотов. После исправлений все целевые аукционные проверки проходят.

Последние результаты и команды приведены ниже в итоговом протоколе проверки. Тестовые TRX находятся в игнорируемых
`tests/*/TestResults/auctions-*.trx`. Тесты используют временные каталоги/базы, без обращения к production.

Реальный двухклиентский игровой прогон и Linux/two-CPU прогон не выполнялись. Сценарии без отметок PASS добавлены
в `docs/manual-checklist.md`. Независимый итоговый review оставлен координатору; дополнительных reviewers не запускали.

## Итоговый протокол проверки (2026-11-02)

- Целевые server/schema/REST/CLI проверки аукционов: **41/41 PASS**.
- Клиентские проверки аукционов, включая disk reload, replay и rate limit: **4/4 PASS**; весь client suite **225/225 PASS**.
- Последний полный solution run: server **962 PASS / 2 FAIL / 964**, client **225/225**, Discord **120/120**,
  BridgeTools **5/5**. Всего **1312 PASS / 2 FAIL**; полный run вернул exit code 1.
- Оба FAIL находятся в централизованно обновляемом Hosting.RefusalKeysTests: сканер пока не знает forwarding
  ParcelValidator в AuctionService, а загрузчик локализации пока читает только Rimlink.xml. Точные изменения указаны
  выше. Новых/других отказов в последнем полном run нет; shared-тест здесь оставлен без изменений.
- Build-ClientMod.ps1 успешно собрал финальный мод в uild/mod/Rimlink, без InstallTo. Сборка имеет существующие
  предупреждения MSB3277 о версиях Microsoft.Extensions.Logging.Abstractions; ошибок нет.
- git diff --check: exit code 0; Git предупреждает только о нормализации CRLF в snapshot/CLI.
- Все 43 изменённых/новых текстовых файла в worktree читаются как UTF-8; один из них — исходная правка DbContext
  координатора. Наших изменённых путей — 42. EN/RU Auctions.xml: по 33 совпадающих ключа.

Команды выполнялись в PowerShell из этого worktree через rtk:

`powershell
rtk proxy dotnet test tests/Rimlink.Server.Tests --filter 'FullyQualifiedName~Auction' -nologo -v q -clp:ErrorsOnly --logger 'trx;LogFilePrefix=auctions-target'
rtk proxy dotnet test Rimlink.sln -nologo -v q -clp:ErrorsOnly --logger 'trx;LogFilePrefix=auctions-complete'
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-ClientMod.ps1 -OutputDir D:\Build\rimlink-next-wt\auctions\build\mod\Rimlink
rtk proxy git diff --check
`

## Изменённые пути

```text
README.md
docs/auctions-implementation.md
docs/manual-checklist.md
docs/player-guide.md
docs/server-setup.md
mod/Languages/English/Keyed/Auctions.xml
mod/Languages/Russian/Keyed/Auctions.xml
src/Rimlink.Client.Core/Economy/EconomyClient.cs
src/Rimlink.Client.Core/Economy/TransferOutbox.cs
src/Rimlink.Client.Core/Net/RimlinkConnection.cs
src/Rimlink.Client.Core/State/SharedState.cs
src/Rimlink.Client/Game/Economy/Outgoing.cs
src/Rimlink.Client/UI/Economy/AuctionsTab.cs
src/Rimlink.Client/UI/Economy/Dialog_Economy.cs
src/Rimlink.Protocol/Protos/auctions.proto
src/Rimlink.Protocol/Protos/common.proto
src/Rimlink.Protocol/Protos/events.proto
src/Rimlink.Server/Application/Admin/AdminCommandRunner.cs
src/Rimlink.Server/Application/Admin/AdminOperations.cs
src/Rimlink.Server/Application/Auctions/AuctionFeature.cs
src/Rimlink.Server/Application/Auctions/AuctionService.cs
src/Rimlink.Server/Application/Economy/MarketService.cs
src/Rimlink.Server/Application/Economy/ServerWealth.cs
src/Rimlink.Server/Application/World/ColonyArchive.cs
src/Rimlink.Server/Grpc/AuctionsGrpcService.cs
src/Rimlink.Server/Grpc/RateLimitPolicy.cs
src/Rimlink.Server/Grpc/RefusalKeys.cs
src/Rimlink.Server/Program.cs
src/Rimlink.Server/Storage/Configurations/AuctionOperationConfiguration.cs
src/Rimlink.Server/Storage/Configurations/AuctionRowConfiguration.cs
src/Rimlink.Server/Storage/Entities/AuctionOperation.cs
src/Rimlink.Server/Storage/Entities/AuctionRow.cs
src/Rimlink.Server/Storage/Migrations/20261101204228_AuctionsTemporary.Designer.cs
src/Rimlink.Server/Storage/Migrations/20261101204228_AuctionsTemporary.cs
src/Rimlink.Server/Storage/Migrations/RimlinkDbContextModelSnapshot.cs
src/Rimlink.Tools/Commands/AdminCommand.cs
tests/Rimlink.Client.Tests/Economy/AuctionOutboxTests.cs
tests/Rimlink.Server.Tests/Auctions/AuctionCliTests.cs
tests/Rimlink.Server.Tests/Auctions/AuctionGuardTests.cs
tests/Rimlink.Server.Tests/Auctions/AuctionLifecycleTests.cs
tests/Rimlink.Server.Tests/Auctions/AuctionTests.cs
tests/Rimlink.Server.Tests/Storage/AuctionSchemaTests.cs
```

Предварительная правка координатора src/Rimlink.Server/Storage/RimlinkDbContext.cs в список наших изменений не включена.
