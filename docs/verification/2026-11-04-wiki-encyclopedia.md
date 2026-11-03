# Wiki encyclopedia verification — 2026-11-04

## Scope and provenance

Source base `25b22d40d0eef026fcfbc6e6d83ddd3652cdf654`; branch docs/bilingual-wiki. All45 RU/EN pairs retain paths; revision2 adds source-backed scenario coverage, not a word-count target. Initial gap: generic welcome and short introductions lacked complete procedures/recovery. Matrix questions below describe those gaps and are now answered in the authored sections. Structural checks are not semantic proof; exact cited source files are in the manifest. Four HTML/CSS text diagrams explain ownership, login/modpacks, escrow/mail and recovery. Existing SVG/concept art is not gameplay capture; zero verified game captures found in docs/images and artifacts. No game starts, production restore or GOG acceptance run.

## Acceptance matrix

Each question is mirrored in English; each named anchor exists in both locales. Both bodies were authored and source-checked during implementation.

| Page pair | Questions / scenarios | Anchors |
|---|---|---|
| `index` | Как найти маршрут и любую статью? | `usage` |
| `overview` | Кто симулирует мир и владеет сохранением?; Какие связи действительно общие? | `ownership`, `boundaries` |
| `compatibility` | Как сверить сборку до подключения?; Что проверено, а что нельзя обещать? | `checklist`, `verified` |
| `faq` | Нужен ли отдельный сервер и общий график?; Почему привычный обход проблемы опасен? | `choosing`, `mistakes` |
| `glossary` | Какие термины нельзя смешивать при восстановлении? | `state-terms` |
| `players/getting-started` | Первый вечер: от адреса сервера до повторного входа; Если первый вход не завершился | `first-evening`, `first-failure` |
| `players/install` | Как понять, что установлен именно клиентский мод? | `verify-install` |
| `players/connect` | Вход по шагам: сертификат, учётная запись, колония; Различай временное ожидание и окончательный отказ | `login-flow`, `login-refusal` |
| `players/modpacks` | Выбор пути: Workshop или серверный пакет?; Безопасное восстановление после прерванной установки | `modpack-decision`, `modpack-recovery` |
| `players/world` | Как выбрать место и прочитать чужое поселение?; Что происходит после долгого отсутствия? | `settlement`, `archive` |
| `players/saves-recovery` | Как сохраняются отправки и доставки?; Дерево решений после обрыва, rollback или reset | `save-cycle`, `recover-tree` |
| `players/chat-pings` | Публичный, личный и союзный канал: куда уйдёт сообщение? | `channels` |
| `players/economy` | Как внести серебро, купить и получить товар?; Как продать и почему баланс может стать отрицательным?; Что делать, если ответ на отправку потерян? | `wallet-market`, `selling-fees`, `exchange-recovery` |
| `players/gifts-trades` | Подарок: отправка, решение и возврат; Двусторонняя сделка: что именно подтверждают обе стороны? | `gift-flow`, `trade-flow` |
| `players/auctions` | Пример: резерв ставки и возврат перебитому участнику; Где результат после окончания, заморозки или отмены? | `auction-example`, `auction-settlement` |
| `players/procurement` | Как создать заказ и поставить ресурс?; Общая цель: когда платят и как делится остаток? | `procurement-steps`, `goal-example` |
| `players/raids-incidents` | Как выбрать риск рейда до отправки?; Платные события: заказ, неизвестный ответ и возврат | `raid-choice`, `incident-order` |
| `players/visits` | Обычный визит и покупка с полки; Если карта визита не загружается | `visit-flow`, `visit-load` |
| `players/live` | Совместная сессия: действия гостя и хозяина; Паузы, отставание зеркала и разрыв | `live-flow`, `live-failure` |
| `players/coop` | Кто что контролирует после приглашения?; Отзыв, занятость и восстановление назначений | `coop-check`, `coop-recovery` |
| `players/community` | Профиль, приглашение и альянс: управление согласием; Казна: взнос не является личным депозитом | `social-consent`, `treasury` |
| `players/guild-influence` | Опубликовать, скрыть слой или отозвать согласие?; Почему источник не включается или слой недоступен? | `publish-influence`, `influence-refusal` |
| `players/troubleshooting` | Диагностика по этапу, а не переустановка всего; Когда остановиться и передать данные администратору? | `triage`, `escalate` |
| `admins/getting-started` | Приёмка до приглашения игроков; Что сообщить игрокам заранее? | `launch-proof`, `launch-policy` |
| `admins/hosting` | Сеть и постоянные данные: проверка снаружи | `host-checklist` |
| `admins/configuration` | Как изменить параметр без угадывания единиц? | `config-method` |
| `admins/security-auth` | Разделение и отзыв доступа интеграций; Плановая замена сертификата | `rotate-access`, `rotate-cert` |
| `admins/modpacks` | Версия коллекции как отдельный релиз; Приёмка новой сборки и откат публикации | `modpack-release`, `delivery-accept` |
| `admins/gameplay-policy` | Пример согласованного набора правил; Заморозка подсистемы при инциденте | `policy-example`, `freeze-runbook` |
| `admins/administration` | Разобрать обращение о пропавшей отправке; Выбор между rollback, reset и archive | `moderation-case`, `admin-danger` |
| `admins/cli` | Безопасный первый сеанс CLI | `cli-read-first` |
| `admins/api` | Интеграция: начать с чтения и правильно повторять | `api-client` |
| `admins/backup-recovery` | Проверка backup в изоляции, шаг за шагом; Что не исправляет whole-server restore? | `restore-drill`, `backup-scope` |
| `admins/updating` | Контрольная точка и решение о возврате | `update-proof` |
| `admins/monitoring` | Три уровня наблюдения; Как обрабатывать пропуск SSE | `monitor-layers`, `sse-gap` |
| `admins/discord` | Запуск бота с минимальными правами | `bot-accept` |
| `admins/troubleshooting` | Порядок расследования серверного отказа | `incident-triage` |
| `developers/architecture` | Карта компонентов и границ доверия; Как найти место для изменения? | `boundaries`, `change-trace` |
| `developers/build` | От чистого checkout до сборки мода | `build-track` |
| `developers/protocol` | Контракт мутации: не только protobuf-поле | `mutation-contract` |
| `developers/storage` | Порядок исследования повреждённого сейва | `storage-repair` |
| `developers/client` | Путь UI-намерения и граница главного потока; Пешки: id, отношения и возврат | `client-intent`, `pawn-lifecycle` |
| `developers/localization` | Проверка перевода как поведения | `translation-work` |
| `developers/testing` | Какая проверка отвечает на какой вопрос?; Как записать воспроизводимую приёмку? | `test-matrix`, `acceptance-record` |
| `developers/contributing` | Готовность задачи включает документацию | `contribution-dod` |

## Six desk walkthroughs (not new runtime acceptance)

1. **First login:** getting-started → install → connect → world → saves-recovery. Harmony/load order, client archive, server address/certificate pin, account, collection/restart, first colony/save/rejoin; refusal stops before unsafe recovery.
2. **Trade:** economy → gifts-trades → saves-recovery. Wallet differs from silver stacks; beacon goods enter escrow; same-revision confirmation, changed terms, cancellation, mail and original-request replay are distinguished.
3. **Live/coop:** live → coop → community. Host consent and assigned-pawn rights are separate; owner online/sole writer, one active partner, revoke/rollback/reset and pause/drop/end are explained.
4. **Collection recovery:** modpacks → troubleshooting → admins/modpacks. Workshop vs server delivery, redistribution consent, backup/journal preservation/restart/verification; unpublish is not client rollback; no Core/DLC redistribution or GOG guarantee.
5. **Restore:** admins/backup-recovery → updating → developers/storage. Coherent DB+saves archive, new isolated restore directory, compatible binary/closed access, verify current/old saves; preserve locks/delta bases and client evidence. Production restore explicitly destructive and not performed.
6. **Developer:** developers/build → testing → contributing. Solution excludes game mod; script with safe separate output, no implicit game installation/restart; protocol/storage/game/UI changes need corresponding tests, translations and evidence.

## Observed checks

- Node24.22.1 / VitePress1.6.4 / Vite6.4.5 / Playwright1.64.1, installed Chrome channel.
- 30 validator/prose tests PASS after RED fixtures; 45 pairs no structural errors. All prior rendered heading IDs and explicit anchors preserved.
- Build/browser PASS: 45 SSR catalog links/3 routes, search20queries+Cyrillicfolding, same-page language+hash, keyboard/Escape/noresults, deepreload, active sidebar, 360/768/1440 lightdark, noJS, 404, artwork/nooverflow/noexternalrequests/noexceptions. Contrast10.94light/12.81dark. 640CSSpx reflow is proxy200%, not literalOSzoom.
- Portal-width regression RED at720px; DefaultTheme content max784px caused it. Scoped homepage content override fixes width>=1100 assertion; desktop/mobile/tablet captures inspected.
- dotnet test Rimlink.sln -nologo -v q exit0:1113server+404client+122Discord+5BridgeTools; existing warnings.
- Build-ClientMod.ps1 exit0 using fresh .superpowers/wiki-encyclopedia-mod-check-20261104, no InstallTo/game restart; preexisting MSB3277 dependency version warnings.

## Review and publication boundary

One Astra read-only whole-change review completed. Single Sol fix pass: repaired configuration units/zero semantics/RU placeholders, added response DTO projections and status-first empty-body auth handling, completed source-build Discord launch/config/scope/run/stop instructions, fixed RU redaction wording and wrapped sidebar labels. No second general review. Final local build, 30 tests, 45 pairs, 96 keys/90 HTTP/57 CLI references, 92 HTML links and full browser checks PASS. Full workspace drift correctly refuses the independent local Preview.png marketing change; explicit-base staged-scope validation excludes marketing/foreign files and passes. Source commit/publication follow only after identity and staged-secret checks. Actual Pages source remains legacy gh-pages:/; public verification is recorded separately after deployment. Foreign AGENTS/passive-outposts/modpack verification and independent marketing README/assets are excluded. Steam upload/render and live Discord calls were not performed.
