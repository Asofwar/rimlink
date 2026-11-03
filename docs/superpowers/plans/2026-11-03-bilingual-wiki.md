# Rimlink RU+EN Wiki Implementation Plan

> Для исполнителя: использовать executing-plans; реализация в основной сессии GPT-6.1 Sol. Astra пишет документы и делает один итоговый review. Не запускать per-task reviewers. Шаги ниже не выполнены этим документом.

**Goal:** полноценная статическая wiki из 45 пар RU+EN статей с локальным поиском, контролем drift и безопасной ручной публикацией.

**Architecture:** изолированный источник wiki/, VitePress locales, единый manifest структуры/ownership и статический output. Документационные проверки отдельно от игрового CI; deploy manual-only.

**Tech Stack:** VitePress, Node LTS, npm lockfile, Markdown, Node test runner для проверок контента; GitHub Pages как выбранная цель. Точные стабильные версии закрепить при реализации, не использовать latest без pin.

**Spec:** [bilingual wiki design](<../specs/2026-11-03-bilingual-wiki-design.md>).

## Global Constraints

- Только основная Sol пишет код/скрипты/конфигурацию; этот план не содержит готовую реализацию.
- Base /rimlink/, полные локали /ru/ и /en/, нейтральный корень. 45 одинаковых page IDs, 90 локализованных страниц; никаких переводов-ссылок.
- Никаких paid services, runtime backend, читательских аккаунтов, аналитики или внешнего поиска.
- Не трогать чужие изменения, включая [AGENTS](<../../../AGENTS.md>) и незакоммиченные passive-outposts/modpack-check документы. Перед работой заново проверить status/diff: основная сессия уже добавила wiki DoD в AGENTS.
- Существующие docs/URLs/anchors сохранить, исторические plans/verification не переписывать. Никаких runtime/game изменений ради wiki.
- Факты по исходникам; 0.8.1 в трёх version sources, protocol 1. README Status 0.7.0 не источник истины. GOG runtime не проверен, coop — один партнёр/owner online, influence — overlay, outposts не реализованная обещанная функция.
- Push, включение Pages, публикация и deploy только отдельной явной командой; этот план их не запускает. Коммиты не выполнять в текущем поручении. На каждом этапе оставить проверяемый diff вместо commit step; будущий коммит — отдельно по действующим правилам проекта.
- Штатные docs checks не заменяют .NET/game tests; неизвестное и незапущенное отражать в отчёте.

## Review Focus

1. Полноценный перевод против формального наличия двух файлов: смысл, числа, warnings, команды, применимость (Task 3).
2. Project base, deep-link reload и language switch с anchor: не отправлять читателя в корень или другую статью (Tasks 1, 4).
3. Legacy docs с handoff/temporary migrations/датированными результатами: не превращать их в актуальные обещания (Task 3).
4. False-green parity/drift: изменение source без docs либо одинаковый revision при устаревшем переводе (Tasks 2, 3).
5. Неожиданный deploy/публикация лишних файлов/замена существующего Pages (Task 5).

## Контракт файлов и интерфейсов

Новые пути:

- wiki/package.json, wiki/package-lock.json, wiki/.node-version — точный toolchain и команды.
- wiki/.vitepress/config.ts, wiki/.vitepress/navigation.ts, wiki/.vitepress/locales.ts — base/theme, manifest-driven nav и полный UI RU/EN.
- wiki/.vitepress/theme/index.ts — только если стандартного locale switch недостаточно для парной статьи/anchor; без собственного дизайн-фреймворка.
- wiki/content-manifest.json — все 45 IDs из спецификации, role/order, ru/en path, requiredSections, sourcePaths, owner, contentRevision, verifiedRevision, productVersion; обе статьи содержат matching pageId/contentRevision metadata.
- wiki/index.md и wiki/{ru,en}/<pageId>.md — точный каталог спецификации; для index используется wiki/{ru,en}/index.md.
- wiki/public/assets/{shared,ru,en}/ — только разрешённые изображения; каждый asset имеет provenance/alt в контенте либо реестре.
- wiki/scripts/check-content.mjs, check-links.mjs, check-drift.mjs, check-reference.mjs — ограниченные локальные валидаторы.
- wiki/tests/content.test.mjs, links.test.mjs, drift.test.mjs, reference.test.mjs — fixtures и негативные сценарии.
- wiki/reference-inventory.json — version/config/CLI/API public coverage: source symbol/route/key → page ID, либо explicit exclusion с причиной.
- wiki/search-cases.json — минимум 10 запросов на локаль с expected page ID.
- .github/workflows/wiki-check.yml, .github/workflows/wiki-deploy.yml — проверки отдельно, публикация manual-only.

Потенциальные целевые изменения: [README](<../../../README.md>) — исправить Status по version sources и добавить RU/EN ссылки без заявления о публикации; [gitignore](<../../../.gitignore>) — только generated wiki output/cache/node_modules; актуальные legacy guides — только согласованные дополнения/исправления с сохранением anchors. Не менять существующий [CI](<../../../.github/workflows/ci.yml>).

Публичный интерфейс проверок (создать, затем использовать одинаково в CI/локально):

- npm --prefix wiki run test:checks — Node tests валидаторов.
- npm --prefix wiki run check:content — manifest/page/section/UI parity, непустое содержание и отсутствие заглушек.
- npm --prefix wiki run check:reference — config/CLI/API/version inventory.
- npm --prefix wiki run check:drift -- --base <commit> — diff от явной базы, source ownership и документационные изменения/обоснования.
- npm --prefix wiki run build — статическая сборка, ошибки ссылок не подавлены.
- npm --prefix wiki run check:links — output HTML/routes/anchors/assets/base, без внешних сетевых запросов.
- npm --prefix wiki run preview -- --host 127.0.0.1 — локальный просмотр; порт и точный URL записать по фактическому выводу, процесс после проверки завершить.

Ошибки проверок: exit 1 и path/page ID/reason; успешная проверка: exit 0 и количество проверенных/пропущенных объектов. Пропуск не PASS. Валидация не изменяет исходники. Drift-обоснования хранятся в рассматриваемом изменении с source paths, причиной и reviewer; source SHA и последний timestamp сами по себе не разрешают drift.

## Task 1: Двуязычный каркас и маршрутизация

Files: toolchain, .vitepress/*, index и пара overview из контракта; необходимые .gitignore additions.

- [ ] Проверить cwd/status/diff и инструкции; зафиксировать исходные чужие изменения, не индексировать их. Сверить стабильную VitePress/Node совместимость по официальным docs, закрепить версии и lockfile.
- [ ] Сначала описать failing route checks: root language chooser, /rimlink/ru/overview и /rimlink/en/overview, одинаковая статья после switch, asset под base. Зафиксировать отсутствие/ошибку до реализации, не выдавать несуществующую команду за тест продукта.
- [ ] Создать минимальный каркас, полный locale UI и локальный поиск. Привязать nav к manifest; никаких external fonts/analytics.
- [ ] Выполнить build и проверку прямого открытия/reload обеих страниц под /rimlink/. Expected: exit 0, assets без 404, switch сохраняет page/общий anchor. До Task 3 контент помечен неполным, не готов к публикации.
- [ ] Проверить diff, без коммита.

## Task 2: Проверяемые parity, links, inventory и drift

Files: manifest, reference inventory, четыре scripts, четыре tests из контракта.

- [ ] Написать негативные fixtures: отсутствует EN; duplicate page ID; разные content revisions; нет required section; битый anchor/asset; ссылка уходит мимо /rimlink/; source изменён без пары/обоснования; новый публичный config/API/CLI без mapping; расходятся version sources.
- [ ] Запустить test:checks до реализации нужных проверок. Expected: указанные сценарии не обнаруживаются и тесты FAIL; сохранить точные причины.
- [ ] Реализовать локальные валидаторы без сети и переписывания файлов. Разрешать обоснованные exclusions, но показывать их явно; не генерировать false proof семантического перевода.
- [ ] Запустить test:checks. Expected: все негативные/позитивные fixtures PASS, damaged fixtures дают exit 1 с конкретным путём; check:content реального неполного корпуса всё ещё FAIL до Task 3.
- [ ] Проверить diff, без коммита.

## Task 3: Все 45 пар статей и legacy совместимость

Files: wiki/ru/<pageId>.md и wiki/en/<pageId>.md для каждой строки каталога спецификации; manifest/inventory; при необходимости локальные assets; README и точечные legacy links.

- [ ] Заполнить manifest полным каталогом: общие 5, players 18, admins 14, developers 8. Expected: content check перечисляет отсутствующие/неполные страницы.
- [ ] Для каждого page ID сверить source и записать обе полные статьи по шаблону спецификации. Разделить обучение/процедуры/reference от истории разработки; документировать ограничения GOG/coop/influence/recovery и переносы без потерь фактов.
- [ ] Сопоставить весь публичный config/CLI/API контракт с reference inventory. Проверить defaults, validators, units, env/restart/secret semantics, scope/CSRF/errors и local/network commands по актуальному source, не только старому примеру.
- [ ] Выполнить содержательный двуязычный self-check: каждая процедура, предупреждение, число и пример имеют эквивалент. Glossary единый, identifiers неизменны. Не считать автоматическую parity доказательством качества перевода.
- [ ] Сохранить старые URLs/anchors; добавить mapping legacy section → wiki page. Исправить README Status по версии без утверждения «сайт опубликован». Ссылки на целевой Pages до deploy обозначить как планируемые либо дать repo-local ссылки.
- [ ] Запустить check:content, check:reference, check:drift с фактической base, build, check:links. Expected: 45 пар, 0 missing/duplicates/broken links/unexplained drift. Проверить по тексту три полных маршрута из спецификации.
- [ ] Проверить diff, без коммита.

## Task 4: Поиск, accessibility и browser acceptance

Files: search-cases.json, при необходимости locales/nav/theme и статьи; результаты в итоговом отчёте, без публикации внутренних logs.

- [ ] Заполнить 10+ запросов на язык из спецификации. До исправлений зафиксировать любые неверные expected results, Cyrillic/ё-е проблемы или чужую локаль.
- [ ] Запустить контролируемый локальный preview, записать job ID/точный URL. Проверить все запросы: expected page ID в top 5; no-results, arrows/Enter/Escape; после загрузки индекса запросы не отправляются внешнему сервису.
- [ ] Проверить все 45 language switches в обе стороны, общие anchors, direct deep links/reload/root/404. При нехватке стандартного switch добавить только минимальную корректировку, не большой custom UI.
- [ ] Проверить RU/EN light/dark при 360/1280 px и 200% zoom, keyboard/focus/aria, контраст ≥4.5:1; без JS доступен основной текст. Исправить конкретные отказы, повторить затронутые сценарии.
- [ ] Повторить build/check:links. Expected: все критерии поиска/UX PASS либо честный список блокирующих непроверенных пунктов. Собрать output и остановить только собственный preview job.
- [ ] Проверить diff, без коммита.

## Task 5: CI без auto deploy и единый итоговый обзор

Files: wiki-check.yml и wiki-deploy.yml, окончательные docs/toolchain corrections.

- [ ] Проверить отрицательные условия workflow: docs-check не может deploy, нет pages:write/OIDC; deploy не срабатывает по push/PR. Добавить проверяемый контроль этих условий до принятия workflow.
- [ ] Создать docs-check на PR/push: pinned toolchain/lockfile, tests/checks/build/artifact; полный history/base для drift. PR не получает публикационные secrets. Не менять игровой CI.
- [ ] Создать отдельный workflow_dispatch deploy: только проверенный revision/artifact, минимальные права deploy job, serialized deployment. Не запускать workflow, не включать Pages и не проверять авторизационные файлы.
- [ ] Повторить полный документационный набор команд, git diff --check и secrets/output allowlist inspection. Expected: exit 0, только wiki artifact, нет data/log/save/DLL/diagnostics. Linux CI до push фактически не запущен — отметить это, не приписывать успех локальному прогону.
- [ ] Выполнить **один** итоговый review всей правки на Astra по Review Focus и спецификации; один fix pass Sol. Не организовывать обзоры по каждому аспекту/задаче.
- [ ] Итог: статьи/версии, команды и результаты, browser coverage, незапущенный CI/deploy/GOG/игровые проверки, сохранённые чужие изменения. Коммитов и публикации нет.

## За границей текущего выполнения

Перед отдельно разрешённой публикацией проверить repository visibility/условия бесплатности, существующий Pages/domain/source/content и права; при занятом сайте согласовать замену. Разрешение реализации не разрешает push/Pages/deploy. После разрешённого deploy отдельно проверить live URL, deep links, assets и поиск. Не объявлять локальную wiki опубликованной.
