# План: следующий набор функций Rimlink

Goal: реализовать десять функций взаимодействия из исследования форков поверх 0.6.2.

Architecture: существующие gRPC/REST, EF SQLite, кошелёк/эскроу/почта, outbox и host-authoritative live. Спека: `docs/superpowers/specs/2026-11-01-rimlink-next-features-design.md`.

## Global Constraints

- Отдельное рабочее дерево на направление; чужие изменения не откатывать. Без push/deploy/release.
- Новые сущности и конфигурации отдельными файлами; итоговую миграцию создаёт интегратор. Временная миграция допустима для тестирования рабочей ветки.
- Протокол только растёт; резервы номеров заданы спекой. Версию меняет интегратор.
- Деньги/предметы/идемпотентность атомарны; существующие ограничения и лимиты обязательны.
- Новые mutating calls переживают потерю ответа через outbox и request id, привязанный к намерению.
- Все команды через rtk, файлы UTF-8. GUI только CUA Driver. Рабочий сервер и чужие процессы не трогать.
- Перед изменением читать затронутые существующие спеки; отклонение записывать как ruling.

## Review Focus

Сохранность средств и вещей, повторы/гонки/рестарт, scope и участники, приватность, один автор сейва, изоляция coop от raid outcome, старый клиент/старые рейды, миграция существующей БД. Один финальный обзор всего изменения.

## Task 1: основа и контракт

Files: эта спека/план, `RimlinkDbContext.cs`, отдельные конфигурации сущностей.
RED: схема существующей БД/сервера остаётся загружаемой. GREEN: добавить стандартное применение EF-конфигураций сборки без изменения старых моделей. Expected: исходный suite проходит.

## Task 2: закупки, поставки, общие цели

Owner: procurement worktree. Files: `procurement.proto`, Application/Procurement, соответствующие endpoint/core/UI/tests/docs; регистрация через отдельный extension.
RED: эскроу/частичное закрытие/отмена/истечение/повторы/блокировки. GREEN: полный путь до почты и UI. Expected: никакой утечки денег при дубле и конкурентном выполнении.

## Task 3: союз, казна, друзья и профиль

Owner: social worktree. Files: отдельные social extension proto/entities/services/UI, существующие Chat/Raid/Live/Incident gates.
RED: чужие права, отсутствие согласия, блокировка, дневной предел, союзная агрессия, приватность истории. GREEN: полные пользовательские пути. Expected: проверенные приглашение→согласие, членство/выход, группа чата и движение серебра.

## Task 4: аукционы

Owner: auctions worktree. Files: `auctions.proto`, Application/Auctions, endpoint/core/UI/tests/docs.
RED: перебитая ставка, гонка ставок/выкупа/истечения, просрочка, самоставки, неуспешная выдача. GREEN: фоновые завершения и почта. Expected: единственный победитель и точные возвраты.

## Task 5: диагностика и приглашения

Owner: diagnostics worktree. Files: client core diagnostics/invitation parser and game UI, targeted tests/docs.
RED: секреты/личные данные, oversized log, malformed endpoint/pin, existing different pin. GREEN: локальный просматриваемый пакет и подтверждаемый импорт приглашения. Expected: ни сети для экспорта, ни молчаливой смены доверия.

## Task 6: постоянная совместная колония

Owner: интегратор. Files: `colonies.proto`, server Application/Colonies + live, core/state + game live/UI/tests.
RED: доступ без членства, отзыв прав, посторонняя пешка, offline host, занятый host, повтор join, регрессия raid/visit. GREEN: сохраняемое членство, назначенные пешки хозяина, один сейв, coop end без выдачи/удаления пешек. Expected: общая колония после переподключения и сохранения не дублируется.

## Task 7: остаточные сценарии

Owner: verification worktree. Воспроизвести EventsTab request-intent issue; RED→GREEN корня без потери идемпотентности. Подготовить/прогнать доступные residual game checks в отдельном стенде; реальные блокеры записать, не выдавать тесты за игровую проверку. Production и Discord credentials не трогать.

## Task 8: интеграция

Интегрировать source changes, разрешить регистрации/UI/протокол, создать единую EF migration поверх 0.6.2; schema test старой БД. Проверить все пути wealth/freeze/reset/archive/admin. Обновить документы и версию 0.7.0.

## Task 9: проверка и итоговый обзор

Commands (PowerShell через rtk): `dotnet test Rimlink.sln -nologo -v q`, build mod и companion существующими scripts, targeted regression suite, подходящие Linux/two-CPU прогоны для stream/timeouts. Expected: зелёные результаты с сохранённым выводом; честный список непрогнанного в игре.
Один независимый reviewer всей разницы против 9e70b25, один fix pass с воспроизведением каждой существенной ошибки. После повторной проверки локальный коммит по git-safe-commit; main/push/release только по команде пользователя.
