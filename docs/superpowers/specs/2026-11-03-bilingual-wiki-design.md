# Rimlink: полноценная RU+EN wiki

Дата: 2026-11-03. Статус: дизайн для согласования; VitePress + GitHub Pages подтверждены пользователем командой «делай» через основную сессию. Это не разрешение на push, включение Pages или deploy.

## Цель и границы

Бесплатная полноценная документация для игроков, администраторов и разработчиков. Обе локали содержат полные статьи, примеры, ограничения и предупреждения, а не ссылки вместо перевода. Сайт статический, без runtime backend, читательских аккаунтов, интерактивного редактирования, комментариев, аналитики или внешнего поиска. Wiki не заменяет игровую админ-панель и публичную страницу отдельного сервера. Бесплатность wiki не означает бесплатности игры, DLC или hosting игрового сервера.

## Выбор платформы

| Вариант | Tradeoff |
|---|---|
| **VitePress + GitHub Pages — выбран** | MIT, Markdown, встроенные locales и local search, review рядом с кодом; отдельный Node build-toolchain и обязательный контроль перевода |
| MkDocs Material + GitHub Pages | Хорошая документационная тема/поиск; отдельный Python-toolchain и схема/плагин двуязычности, без явного выигрыша здесь |
| GitHub Wiki | Меньше инфраструктуры, но отдельный репозиторий и слабее контроль локализации, навигации и совместного review с кодом |

Целевой адрес https://asofwar.github.io/rimlink/, base /rimlink/, языки /ru/ и /en/. Pages первоначально был рекомендованным default, не следствием ответа только о языках; затем пользователь подтвердил формат. Бесплатность предполагает подходящий public repository и соблюдение лимитов Pages/Actions. Настройки аккаунта, visibility, существующий Pages/domain/content **не проверены**; перезапись существующего сайта не разрешена. Платные runners, custom domain и платный поиск не требуются. Версии инструментов закрепляются при реализации в lockfile, не объявляются уже установленными.

## Основания и достоверность

Версия **0.8.1** совпадает в [Directory.Build.props](<../../../Directory.Build.props>), [About.xml](<../../../mod/About/About.xml>) и [ClientInfo.cs](<../../../src/Rimlink.Client.Core/ClientInfo.cs>); [ProtocolVersion](<../../../src/Rimlink.Protocol/ProtocolVersion.cs>) равен 1. [README](<../../../README.md>) содержит устаревший Status 0.7.0 рядом с разделом 0.8.1: не переносить противоречие в wiki. Исходная версия не доказывает наличие опубликованного release.

Основы: [player guide](<../../player-guide.md>), [server setup](<../../server-setup.md>), [modpack delivery](<../../modpack-delivery.md>), [community](<../../community.md>), [coop](<../../shared-colony.md>), [influence](<../../guild-influence.md>), [procurement](<../../procurement.md>), [auctions](<../../auctions-implementation.md>), [diagnostics/invites](<../../client-diagnostics-invites.md>), [AGENTS](<../../../AGENTS.md>). Смешанные handoff-документы не копировать wholesale: временные миграции, worktrees и старые проверки относятся к своим датам.

- Колония заморожена офлайн. Coop: один активный партнёр, хозяин онлайн, один авторитетный писатель сейва.
- Influence — добровольный overlay, не ownership/война/захват/права атаки. Passive outposts из чужого плана не представляются доступной функцией.
- Modpack delivery не выдаёт игру/Core/DLC или новый auth. GOG требует разрешённого password login и подготовленного разрешённого к распространению пакета. Реальная GOG runtime-приёмка остаётся неподтверждённой; Steam-копия не является GOG-проверкой.
- Config сверяется по [примеру](<../../../deploy/appsettings.example.json>), [Options](<../../../src/Rimlink.Server/Hosting/ServerOptions.cs>), [binding/validation](<../../../src/Rimlink.Server/Program.cs>) и месту использования. TlsOptions.SelfSignedPath существует, хотя отсутствует в примере. Runtime default, демонстрационное значение, настраиваемый предел и константа различаются.
- Save generation и save-write epoch после rollback — разные понятия. Не обещать абсолютное отсутствие потерь; описать неопределённые операции и ручное вмешательство. Не советовать очищать outbox/journals или удалять lock-файлы.
- [Manual checklist](<../../manual-checklist.md>) и датированные verification — ограниченные свидетельства конкретных сценариев/окружений, не универсальные гарантии. Dev-only приёмы не выдаются за обычный gameplay.

## Структура сайта

Отдельный источник wiki/; исторический docs/ не становится сайтом автоматически. Будущие файлы:

- wiki/package.json и lockfile: изолированный build-toolchain.
- wiki/.vitepress/config.ts: общая тема, base, locales и search UI.
- wiki/content-manifest.json: page IDs, порядок/роль, source paths, owner, content revision, verified source revision/version, обязательные section IDs.
- wiki/index.md: нейтральный выбор «Русский / English», без принудительного определения языка.
- wiki/ru/ и wiki/en/: одинаковые относительные пути статей ниже, расширение .md.
- wiki/public/assets/shared/, ru/, en/: локальные изображения с правами и alt.

Каждая строка ниже — обязательная содержательная статья в обеих локалях. Page ID равен пути без языка и расширения; ASCII slugs одинаковы. Всего 45 пар статей (90 локализованных страниц), плюс нейтральный корень.

| Page ID | Обязательная область |
|---|---|
| index | Три роли, быстрые действия, поиск, версия и ограничения |
| overview | Модель multiplayer, асинхронность/live и границы доверия |
| compatibility | RimWorld 1.7, Harmony, client/server/protocol, DLC/modpacks, Steam/GOG |
| faq | Офлайн, сервер, совместимость, бесплатность и recovery |
| glossary | Согласованные RU/EN термины escrow/outbox/snapshot/generation/epoch/alliance |
| players/getting-started | От требований до первого сохранения |
| players/install | Steam/ручная/GOG, Harmony/load order и обновление |
| players/connect | Адрес/порт, TLS fingerprint, Steam/account/server password, приглашения и отказы |
| players/modpacks | Mismatch, Fix and restart, delivery/settings/redistribution, unmanaged conflicts/recovery |
| players/world | Мир/колония/storyteller, маркеры, offline/archive |
| players/saves-recovery | Autosave/выход/reconnect, pending save/reset/rollback, неопределённость |
| players/chat-pings | Public/direct/alliance chat, карточки мест, G, блокировки/лимиты, Discord |
| players/economy | Маяки/кошелёк/рынок/почта, комиссии/хранение/долг/freeze/wealth |
| players/gifts-trades | Согласие/отказ/возвраты и подтверждение revision обмена |
| players/auctions | Лоты/резерв ставки/выкуп/продление/отмена/результат |
| players/procurement | Закупки/контракты/общие PvE-цели, точность поставки/резерв/возвраты |
| players/raids-incidents | Snapshot/real raids, защита/ограничения/платные события и исходы |
| players/visits | Копия колонии/магазин, вход/выход/экспорт/возврат пешек |
| players/live | Live raid/visit, authority, pause/speed/letters, reconnect/resync/end |
| players/coop | Согласие/пешки/отзыв/возврат, owner online, последствия rollback |
| players/community | Профили/друзья/альянсы/приглашения/блокировки/казна/защита |
| players/guild-influence | Consent/легенда/цвета/спорные зоны/offline sources/приватность |
| players/troubleshooting | Симптом → диагностика → решение → когда остановиться; diagnostic ZIP |
| admins/getting-started | Hosting/первый админ/мир/modpack/готовность |
| admins/hosting | Docker Linux, bare Windows/Linux, runtime/TCP/HTTPS/firewall/data volume |
| admins/configuration | Все ключи: тип/default/единицы/границы/env alias/secret/restart |
| admins/security-auth | TLS/pinning/ротация/auth/roles/scoped tokens/cookies/CSRF/public data |
| admins/modpacks | Import/order/version/defs/settings/package/publish/unpublish/права |
| admins/gameplay-policy | Economy/raids/incidents/live/integrity/hygiene и встроенные пределы |
| admins/administration | Panel/moderation/freeze/archive/restore/reset/coop/influence/роли |
| admins/cli | Все пользовательские команды, аргументы/network-local/права/опасные эффекты |
| admins/api | REST admin/public/events: route/method/scope/auth/CSRF/request/response/errors/pages |
| admins/backup-recovery | Согласованный backup, stopped restore, locks/delta/base/epoch/проверка |
| admins/updating | Backup/client-server/migrations/modpack/downgrade |
| admins/monitoring | Health/status/metrics/logs/disk/pending/recovery и доступность metrics |
| admins/discord | Bot/config/permissions/intents/roles/scopes/bridge/границы проверок |
| admins/troubleshooting | Startup/TLS/auth/modpack/storage, доказательства и ручное recovery |
| developers/architecture | Protocol/server/core/mod/tools/Discord, ответственность/потоки |
| developers/build | SDK/dependencies/solution/mod/scripts/build без install/packaging |
| developers/protocol | gRPC/protobuf/events/errors/additive evolution/request ID/replay/limits |
| developers/storage | EF/SQLite/migrations/saves/locks/delta/receipts/generation/epoch/escrow |
| developers/client | Game-free core/async/outbox/SharedState/main thread/Harmony/mirror |
| developers/localization | Keyed RU/EN/refusal args и отдельная локализация wiki |
| developers/testing | Unit/integration/mod build/два клиента/isolated stand/dev BridgeTools |
| developers/contributing | Clean-room/design/review/роли моделей/секреты/wiki DoD |

Практическая статья: задача/применимость → prerequisites → процедура → ожидаемый результат → отказы/ограничения → безопасное восстановление → связи/источники. Справочник — полный контракт и пояснённые примеры. Заголовок или ссылка на legacy guide не считаются статьёй.

## Локализация и UX

Меню: Игрокам / Администраторам / Разработчикам, язык, поиск, репозиторий/релизы. Sidebar/previous-next не перескакивают между языками. Переключатель языка открывает ту же статью по page ID. Общие стабильные ASCII section IDs сохраняют anchor; иначе — начало той же статьи. Нет скрытого fallback вместо отсутствующего перевода: это ошибка сборки.

Переводятся title/sidebar/оглавление/search/no-results/keyboard hints/aria/404/подписи/предупреждения. API keys/commands/package IDs/ErrorCode/placeholders не переводятся. Screenshot на нужном языке либо с переводом всех необходимых действий; иллюстрации не выдаются за реальный UI. Без секретов и личных данных.

VitePress local search (MiniSearch), статический индекс, browser-side запросы без внешнего сервиса. Индексы и выдача разделены по локалям. Русская морфология не обещается автоматически: синонимы в тексте/glossary/metadata и тесты кириллицы/регистра/ё-е. История/планы/verification исключены. JS нужен поиску; основной текст и ссылки читаемы без JS.

Документационная тема light/dark, callouts, keyboard focus и mobile sidebar; без сложного custom frontend, внешних шрифтов/трекеров/embed. Base /rimlink/ учитывается в assets/routes/search/canonical/hreflang. Глубокие URL открываются напрямую без SPA fallback.

## Обязательное сопровождение и drift

[Правило AGENTS](<../../../AGENTS.md>) делает wiki частью DoD. Добавление/изменение/удаление фичи, исправление поведения, изменение config/CLI/API/установки/эксплуатации/recovery требует обновления соответствующих RU+EN статей, примеров, ограничений и навигации **в той же задаче до объявления завершения**. Для новой возможности добавляется статья/раздел. Исполнитель проверяет parity/links/build и перечисляет статьи/результаты в отчёте; незапущенные проверки отмечает. Отсутствие влияния обосновывает явно.

Пока wiki не создана — актуализировать существующие руководства и назвать затронутые будущие page IDs; не утверждать, что несуществующие статьи обновлены. После появления — использовать фактическую структуру. Обновление исходников не разрешает публикацию.

Владелец репозитория отвечает за wiki в целом, автор изменения — за контент, технический reviewer — за факты, bilingual reviewer — за смысл перевода. Это обязанности, не новые аккаунты/сервисы. Manifest связывает пару с одной content revision и применимостью. Изменённый source path требует обновления пары либо рассмотренного обоснования отсутствия влияния. Новые Options/API/CLI обнаруживаются inventory/review; исключения явно обоснованы. Автоматическое равенство revisions не заменяет чтения переводов.

Старые docs/URLs/anchors сохраняются. Позже допустим указатель на wiki без удаления legacy текста/якорей; пока старые guides обещают актуальное поведение, поддерживаются вместе с wiki. Mapping legacy section → page ID фиксирует ownership. Исторические handoff/plans/verification не переписываются; перевод архива не обязателен, но все необходимые инструкции полностью доступны в обеих локалях.

Wiki описывает один явно указанный source revision/version. Не смешивать будущую реализацию с опубликованным release; дата проверки не равна дате генерации. Полное многоверсионное дерево не требуется в первом выпуске.

## Проверки и публикация

Отдельный docs-check на PR/push: parity, внутренние ссылки/anchors/assets/base, reference inventories и build; без deploy и pages:write/OIDC. [Существующий CI](<../../../.github/workflows/ci.yml>) .NET/Docker и его release-триггеры не меняются.

Отдельный manual-only deploy workflow: workflow_dispatch, без push/PR triggers. Даже его наличие не разрешает агенту запускать публикацию. Включение Pages, push и deploy — по отдельной явной команде пользователя. Только проверенный revision/артефакт, сериализованные deployments, Pages/OIDC-права только deploy job. Rollback сайта — ручная публикация проверенного старого артефакта, не force-push.

Перед публикацией проверить visibility/условия/лимиты, существующий Pages source/domain/content и права. Если сайт уже существует — согласовать замену. Публикуется только build output wiki/, не БД/logs/saves/dumps/diagnostic ZIP/игровые DLL/modpacks/worktrees/внутренние отчёты. Примеры — фиктивные адреса/placeholders; --insecure не универсальный безопасный совет. Wiki не исполняет admin API и не хранит tokens.

## Измеримая приёмка будущего сайта

1. Все 45 page IDs полностью представлены в RU+EN; нет TODO, заглушек, переводов-ссылок. Install→connect→save, setup→backup→restore и build→targeted test описаны без поиска обязательных шагов в legacy docs.
2. Page IDs, обязательные section IDs, content revision/применимость совпадают; весь UI переведён. Bilingual review проверил смысл, числа и предупреждения всех статей.
3. Версии из трёх источников согласованы; product/protocol/save version различены. Config/API/CLI inventories покрыты полностью или с обоснованными исключениями. GOG/coop/influence/recovery имеют явные ограничения; outposts не представлены реализованными.
4. Ноль broken internal links/anchors/images/base escapes. Проверены все language switches, deep-link reload, root/404. Legacy paths/anchors не удалены. Внешние timeout/rate limit — unknown, не PASS.
5. Минимум 10 запросов на язык, ожидаемая статья в top 5: установка/install, сертификат/certificate, сейв/save, подарок/gift, аукцион/auction, закупки/procurement, совместная колония/coop, влияние/influence, резервная копия/backup, GOG. No-results/keyboard/Escape работают, нет чужой локали; после загрузки индекса запросы не создают внешнего трафика.
6. Обе темы/локали при 360/1280 px и zoom 200%: без потери текста/контролов, локальный scroll таблиц/кода. Клавиатура/focus доступны, контраст обычного текста ≥4.5:1.
7. Linux CI и локальная сборка воспроизводимы с pinned toolchain/lockfile. Preview работает под /rimlink/, не только /. Parity/link/inventory errors и неразрешённый drift блокируют готовность артефакта.
8. Изменение поведения включает RU+EN и отчёт проверок либо обоснованное отсутствие влияния. До создания wiki обновлены legacy guides и названы будущие статьи.
9. Docs-check не публикует. После отдельно разрешённого deploy проверены live URL/assets/deep links/search; локальная сборка не считается публикацией.

Documentation checks не доказывают gameplay. .NET tests, real two-client run, Steam Web API, Discord и GOG — отдельные проверки с датой/окружением/результатом. Для текущего документа выполняется только чтение/проверка текста и diff; сайт и игровые проверки здесь не запускались.

## Открытые границы

До публикации: public repository и условия бесплатности, существующий Pages/site/domain, выбранный release revision. До реализации: pin toolchain и проверка русского поиска. Неизвестные не означают готовый или опубликованный сайт. Никаких автоматических установок, коммитов или публикации при принятии этого текста.

## Внешние источники решения

- [VitePress locales](https://github.com/vuejs/vitepress/blob/main/docs/en/guide/i18n.md).
- [Local search и UI translations](https://github.com/vuejs/vitepress/blob/main/docs/en/reference/default-theme-search.md).
- [Project base/Pages](https://github.com/vuejs/vitepress/blob/main/docs/en/guide/deploy.md); upstream auto-deploy по push намеренно не принят.
- [VitePress MIT](https://raw.githubusercontent.com/vuejs/vitepress/main/LICENSE).
- [GitHub Pages](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages).
