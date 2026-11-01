# Закупки, адресные поставки и общие цели

Игровой вход: консоль связи → экономика → «Закупки». В dev mode доступен существующий пункт Rimlink → Open economy window; Toggle pick items anywhere временно расширяет источник выбора для ручной проверки. Новые режимы используют тот же выбор предметов под торговыми маяками, сохранение перед отправкой, почту и outbox.

## Три режима

- **Закупка:** организатор задаёт defName, количество, цену единицы и срок. Полная стоимость резервируется из кошелька. Несколько поставщиков могут поставлять частями в пределах остатка; каждый получает оплату сразу за вычетом комиссии рынка. Принятые предметы приходят организатору почтой. Отмена или истечение возвращает только неиспользованный резерв; оплаченные поставки остаются завершёнными.
- **Контракт:** организатор выбирает конкретного поставщика, ресурс, полное количество, общую награду и срок. Карточка видна только сторонам и администратору. Только указанный поставщик может передать полное точное количество до срока. Частичная поставка и посторонние ресурсы отклоняются без списания. Награда резервируется заранее и выплачивается после принятия; отмена/просрочка возвращает её организатору.
- **Общая PvE-цель:** открытая кооперативная поставка ресурса. Вклады и фонд удерживаются до выполнения всей квоты. Затем организатор получает ресурсы почтой, поставщики делят фонд пропорционально количеству принятых единиц. Для каждого поставщика берётся целая часть доли; остаток по одному серебру распределяется по возрастанию ID поставщика. После этого применяется рыночная комиссия. При отмене/истечении незавершённой цели каждый получает свои предметы обратно почтой, организатор — весь фонд. Частичный вклад не превращается в бесплатный подарок организатору. Убийства NPC и другие клиентские заявления не учитываются.

Качество, материал и состояние ресурса не ограничиваются: контракт касается только defName и количества. Пешки не принимаются. Сервер проверяет реальные переданные **описания** и их суммы, а не отдельное поле заявленного прогресса. Как и остальная экономика, он не вскрывает непрозрачный Scribe XML и не доказывает подлинность блобов изменённого клиента.

## Ограничения и экономика

Общий предел: 1–1 000 000 единиц, 1–720 реальных часов, фонд до 1 000 000 000 серебра, до `min(20, Economy:MaxLotsPerPlayer)` открытых закупок на организатора, 100 принятых поставок на одну закупку. Заявленная стоимость одной поставки ограничена 1 000 000 000 серебра, чтобы не переполнять существующий учёт импорта. Размер запроса и количество стаков ограничиваются существующими настройками посылок. Сверхквота отклоняется целиком: автоматического разделения непрозрачного блоба на сервере нет. Срок проверяется при приёме, даже если фоновая очистка ещё не сработала. Очистка выполняется при старте и раз в минуту.

`Economy:MarketFeePercent` применяется к оплатам; `Economy:StorageFeePercentPerDay` и `Economy:StorageFreeDays` — к портящимся ресурсам. Для общей цели за время удержания платит поставщик при завершении/обычной отмене/истечении; плата может создать существующий storage-долг. Административная отмена и отмена при reset/archive возвращают вклады без платы за удержание. Дальнейшее хранение полученных предметов в почте оплачивается существующим правилом посылок `Trade` (с ограничением доступным кошельком получателя); возвраты `TradeReturn` освобождены.

Последняя поставка цели также проверяет заморозку всех ранних поставщиков: их товары не уходят в обход freeze. Сброс уже оплаченного поставщика закупки не отменяет чужой оставшийся заказ.

Новые расходы и поставки запрещены при выключенной/замороженной экономике, заморозке участника и долге. Сервер запрещает экспорт под угрозой Rimlink; клиент дополнительно проверяет ванильную угрозу и запрет экспорта карты визита. Резерв фонда входит в серверное богатство организатора, товары незавершённой цели — в богатство поставщика. Стоимости, объявленные поставщиком, не повышают богатство получателя через посылку: используется существующая семантика `Trade`. Автоархивация учитывает серверное богатство; reset/archive и пересоздание мира закрывают незавершённый эскроу.

В UI есть список участия, история вкладов и чистые выплаты до платы за хранение, карточка поставки, создание с показом резерва и подтверждение отмены. Для публичных изменений других игроков есть «Обновить». Событие участникам инвалидирует локальный список; повторное открытие/переподключение загружает актуальное состояние. Незавершённый запрос блокирует новую мутацию закупок; «Повторить запрос» переигрывает сохранённое намерение. Rate-limit (`ResourceExhausted`) сохраняет запись как неотвеченную: иначе потерянный ответ и быстрый повтор могли бы ошибочно вернуть уже принятые предметы. Сервер хранит `(игрок, request_id, SHA-256 намерения)` вместе с денежным/товарным результатом в транзакции. Повтор уже принятой поставки возвращает текущее состояние даже после закрытия, изменения правил или заморозки, без второго возврата предметов.

## Администрирование

REST:

- `GET /api/admin/procurement?page=0` — scope `moderate`, страницы по 50, все состояния, описания без блобов.
- `POST /api/admin/procurement/{id}/cancel` с JSON `{"reason":"ошибка организатора"}` — scope `admin`, причина до 200 символов; переход терминален и повтор не возвращает средства ещё раз. Успешная отмена журналируется как `procurement-cancel`.

PowerShell, из корня сборки (endpoint и токен передаются уже существующими опциями CLI):

```powershell
rtk proxy dotnet run --project src/Rimlink.Tools -- admin procurement list --server $env:RIMLINK_SERVER_URL --token $env:RIMLINK_ADMIN_TOKEN
rtk proxy dotnet run --project src/Rimlink.Tools -- admin procurement cancel 1 'ошибка организатора' --server $env:RIMLINK_SERVER_URL --token $env:RIMLINK_ADMIN_TOKEN
```

Для команд администратор заранее задаёт переменные окружения `RIMLINK_SERVER_URL` и `RIMLINK_ADMIN_TOKEN` своими значениями. Реальные значения не сохраняются в документации. Для локальной автоматизации предпочтителен уже настроенный клиент REST.

## Хранение и интеграция

Таблицы `procurement_orders`, `procurement_deliveries`, `procurement_requests`; отдельные сущности и `IEntityTypeConfiguration`. DI и маршруты — `ProcurementFeature`, вызовы `AddProcurement` и `MapProcurement` в Program. Новые `ErrorCode` 44–49; `ServerEvent.procurement_changed = 90`. Версии не менялись.

`20261101204304_ProcurementTemporary` и snapshot в этом worktree нужны только его тестам. Интегратор исключает оба файла временной миграции и изменение snapshot, затем генерирует общую миграцию. Существующая правка `RimlinkDbContext.ApplyConfigurationsFromAssembly` принадлежит координатору и сохранена.

Решения по минимальному объёму: общий ресурсный механизм вместо трёх отдельных сервисов, без заданий об убийствах, без произвольных условий качества, без новых пакетов и настроек. Константы отказов — `ProcurementRefusalKeys`; строки — отдельные EN/RU `Procurement.xml`.

## Ручная проверка — пока не выполнена

1. На отдельном двухклиентном стенде открыть вкладку в EN/RU; проверить ширину вкладок, создание всех трёх режимов, выбор офлайн/онлайн поставщика, карточку и список участия.
2. Закупка 10 Steel: два игрока поставляют 2 и 3; покупатель получает 5 почтой, поставщики — оплату; отмена возвращает оставшиеся 5 цен единицы. Забрать почту и перезайти: второго зачисления/предметов нет.
3. Адресный контракт: другой игрок его не видит, 9 вместо 10 отклоняется и возвращает предметы; 10 принимается; проверка поздней поставки до фоновой очистки.
4. Общая цель 3 единицы с фондом 10: вклад 1 и 2, награды 3 и 7 (при меньшем ID второго), затем другая цель с отменой после вклада: ресурс возвращается поставщику, фонд — организатору.
5. Оборвать соединение после принятия поставки и до ответа, восстановить, повторить из outbox; затем повторить сценарий с окончательным отказом: возврат на карту ровно один раз. Проверить сохранение/перезагрузку, угрозу, freeze и storage-долг.
6. Мод только собран; установка в пользовательскую игру, запуск игровых экземпляров и production в рамках этой ветки не выполнялись. Один общий обзор изменений остаётся у координатора.

## Итоговая автоматическая проверка

PowerShell, worktree `D:/Build/rimlink-next-wt/procurement`, ветка `asofwar/next-procurement`, HEAD `9e70b2557fb4d7a46428c32874af263752140afd` (без коммитов):

```powershell
rtk proxy dotnet test Rimlink.sln -nologo -v q --logger 'trx;LogFileName=procurement-final.trx'
```

Успех, exit 0: Server 951/951, Client 225/225, BridgeTools 5/5, Discord 120/120; всего 1301. В них 28 новых серверных и 4 новых клиентских теста закупок; все 11 Hosting.RefusalKeysTests проходят. TRX каждого проекта: `tests/<проект>/TestResults/procurement-final.trx` (git-ignored).

RED зафиксирован перед реализацией API/core и отдельными поведенческими исправлениями: административная плата за удержание, скрытое богатство для автоархивации, завершение с замороженным поставщиком, переполнение импорта, сброс уже оплаченного поставщика, rate-limit повторной принятой поставки. После исправлений полный набор зелёный. Проверены частичные оплаты нескольким продавцам, точный адресный контракт, приватность, детерминированная награда цели, возвраты, повтор/намерение/другой актор, гонка последних поставок и отмены, рестарт, запреты threat/freeze/debt/forbidden, пределы размеров и денег, комиссии и долг, reset/archive, REST/CLI/scopes/audit, обновление базовой схемы.

Сборка мода через штатный скрипт:

```powershell
rtk proxy powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-ClientMod.ps1 -OutputDir 'D:/Build/rimlink-next-wt/procurement/build/procurement-verified-mod'
```

Успех, exit 0. Каталог вывода заранее проверен на принадлежность worktree; `InstallTo` не задан. В сборке остаются предупреждения MSB3277 о версиях Microsoft.Extensions и CS8601 в неизменённом патче royalty tracker. Собранные EN/RU-ресурсы сверены; 41 ключ, одинаковые множества и параметры строк. `rtk git diff --check` проходит, все изменённые файлы декодируются как UTF-8.

Общий независимый обзор, игровая проверка двумя экземплярами, проверка раскладки UI и Linux-прогон не выполнялись. Исходники реализуют полную игровую цепочку, но автоматические тесты и сборка не заменяют её запуск в игре. Production, пользовательская установка игры, чужие процессы и worktree не менялись.

## Изменённые пути для интеграции

44 пути в статусе worktree. `src/Rimlink.Server/Storage/RimlinkDbContext.cs` уже был изменён координатором на старте; его не редактировал. Исключить из переноса обе временные миграции `20261101204304_ProcurementTemporary*` и feature-изменение snapshot; итоговую миграцию генерирует координатор. В `Hosting/RefusalKeysTests.cs` добавлена только запись о передаче структурированного отказа ParcelValidator; центральный загрузчик Keyed() не менялся. Новые строки отказов находятся в отдельном ProcurementRefusalKeys и проверяются отдельным тестом EN/RU.

```text
docs/procurement.md
mod/Languages/English/Keyed/Procurement.xml
mod/Languages/Russian/Keyed/Procurement.xml
src/Rimlink.Client.Core/Economy/TransferOutbox.cs
src/Rimlink.Client.Core/Net/RimlinkConnection.cs
src/Rimlink.Client.Core/Procurement/ProcurementClient.cs
src/Rimlink.Client.Core/Procurement/ProcurementState.cs
src/Rimlink.Client.Core/State/SharedState.cs
src/Rimlink.Client/Game/Economy/Outgoing.cs
src/Rimlink.Client/Game/RimlinkSession.cs
src/Rimlink.Client/UI/Economy/Dialog_Economy.cs
src/Rimlink.Client/UI/Economy/ProcurementTab.cs
src/Rimlink.Client/UI/Economy/ThingPicker.cs
src/Rimlink.Client/UI/LoginFlow.cs
src/Rimlink.Protocol/Protos/common.proto
src/Rimlink.Protocol/Protos/events.proto
src/Rimlink.Protocol/Protos/procurement.proto
src/Rimlink.Server/Application/Admin/AdminOperations.cs
src/Rimlink.Server/Application/Economy/ServerWealth.cs
src/Rimlink.Server/Application/Procurement/ProcurementDelivery.cs
src/Rimlink.Server/Application/Procurement/ProcurementDeliveryConfiguration.cs
src/Rimlink.Server/Application/Procurement/ProcurementFeature.cs
src/Rimlink.Server/Application/Procurement/ProcurementOrder.cs
src/Rimlink.Server/Application/Procurement/ProcurementOrderConfiguration.cs
src/Rimlink.Server/Application/Procurement/ProcurementRefusalKeys.cs
src/Rimlink.Server/Application/Procurement/ProcurementRequest.cs
src/Rimlink.Server/Application/Procurement/ProcurementRequestConfiguration.cs
src/Rimlink.Server/Application/Procurement/ProcurementService.cs
src/Rimlink.Server/Application/World/ColonyArchive.cs
src/Rimlink.Server/Application/World/ColonyHygiene.cs
src/Rimlink.Server/Grpc/ProcurementGrpcService.cs
src/Rimlink.Server/Grpc/RateLimitInterceptor.cs
src/Rimlink.Server/Grpc/RateLimitPolicy.cs
src/Rimlink.Server/Program.cs
src/Rimlink.Server/Storage/Migrations/20261101204304_ProcurementTemporary.Designer.cs
src/Rimlink.Server/Storage/Migrations/20261101204304_ProcurementTemporary.cs
src/Rimlink.Server/Storage/Migrations/RimlinkDbContextModelSnapshot.cs
src/Rimlink.Server/Storage/RimlinkDbContext.cs
src/Rimlink.Tools/Commands/AdminCommand.cs
tests/Rimlink.Client.Tests/Procurement/ProcurementOutboxTests.cs
tests/Rimlink.Server.Tests/Hosting/RefusalKeysTests.cs
tests/Rimlink.Server.Tests/Procurement/ProcurementHardeningTests.cs
tests/Rimlink.Server.Tests/Procurement/ProcurementSchemaTests.cs
tests/Rimlink.Server.Tests/Procurement/ProcurementTests.cs
```
