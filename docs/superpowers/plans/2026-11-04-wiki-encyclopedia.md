# План: Rimlink Wiki как RU/EN энциклопедия

> Реализация: основная сессия GPT-6.1 Sol, inline. Astra не пишет код/статьи; один итоговый whole-change review после всей итерации.

**Goal:** заменить ощущение пустой wiki полноценными объяснениями и проверяемыми сценариями, насыщенным порталом и удобным чтением.
**Architecture:** существующие VitePress 1.6.4 / Vite 6.4.5 / Node 24, DefaultTheme, локальный поиск, статические RU/EN страницы, manifest как общий каталог.
**Spec:** [утверждённое направление и критерии](../specs/2026-11-04-wiki-encyclopedia-design.md).

## Global Constraints

- Сохранить 45 существующих пар, pageId, URL, anchors, /rimlink/, поиск, legacy docs и фактическую legacy gh-pages публикацию. Новые feature articles допустимы только парой и с навигацией; general/reference не накачивать ради длины.
- Не трогать чужой diff [AGENTS.md](../../../AGENTS.md), passive-outposts, modpack verification и параллельные marketing assets/README правки. Перед началом заново проверить status; маркетинг не включать в wiki-коммит случайно.
- Runtime игры не менять; факты сверять с исходниками. Никаких выдуманных screenshots, обещаний outposts, GOG acceptance, фоновой симуляции coop или lossless guarantees. Без новых сервисов/платных API/смены стека.
- Публикация обновления разрешена пользователем по сообщению main; выполнять только main после проверок и в существующий gh-pages, без смены Pages source/force-push. В этой итерации manual workflow лишь собирает проверенный artifact без deploy-pages/write permissions; публикация отдельным подтверждённым путём.
- Явную базу diff зафиксировать перед работой; текущий обследованный HEAD 25b22d4 не подменять HEAD после правок. Локальные коммиты только свои, после проверок автора/коммитера, staged diff и обязательного project test gate. Если gate недоступен — не коммитить, сообщить, не придумывать PASS.

## Review Focus

Смысловой coverage каждой пары; самостоятельность инструкций; source-backed цифры/права/побочные эффекты; безопасность восстановления и примеров; отсутствие непереведённых блоков; полнота reference; читаемый портал/меню/ширина; URL/anchors и поисковая выдача; structural PASS не равен semantic quality; ручной deploy без скрытой смены hosting. Не делать промежуточные per-task или multi-lens reviews.

## Task 1 — Зафиксировать содержательный контракт и проверки

**Files:** [manifest](../../../wiki/content-manifest.json), [check-lib](../../../wiki/scripts/check-lib.mjs), [check-content](../../../wiki/scripts/check-content.mjs), [check.test](../../../wiki/scripts/check.test.mjs), [review.test](../../../wiki/scripts/review.test.mjs); новый docs/verification/2026-11-04-wiki-encyclopedia.md.

1. Снять baseline проверок и карту старых routes/anchors из текущего build. Вести один ledger .superpowers/sdd/wiki-encyclopedia/progress.md; включить явную базу и параллельные чужие изменения.
2. По матрице spec задать каждому pageId тип portal/guide/explanation/reference/faq/glossary и coverage: уникальный topic id, проверяемый вопрос/сценарий, anchor, sourcePaths. Навигационные метаданные group/order/summary локализованы. Указать владельца обновления по feature, не отвязывать от source drift. Не создавать отдельную CMS/сложную систему аттестации.
3. RED: тесты на потерю исходного pageId/anchor, topic без источника/anchor, orphan page, отсутствующую локаль/перевод метаданных, повторный id. Текущие минимальные usage/limits/related и длина секции сами по себе не достаточны. Не заставлять FAQ имитировать tutorial.
4. GREEN: минимально расширить проверки структуры; semantic verdict оставить ручной сверке статьи и источника. В verification — исходные пробелы всех 45 пар и конкретные acceptance вопросы, без галочек за ещё не выполненное.

**Expected:** новые негативные fixtures красные до исправления и зелёные после; валидатор обнаруживает структурные пробелы, а не «доказывает» качество текста. Неполная production матрица остаётся явно незавершённой до следующих задач.
**Checks:** npm run test:checks --prefix wiki; npm run check:content --prefix wiki.

## Task 2 — Сделать портал, понятное меню и широкую рабочую область

**Files:** [RU home](../../../wiki/ru/index.md), [EN home](../../../wiki/en/index.md), [config](../../../wiki/.vitepress/config.mts), [theme entry](../../../wiki/.vitepress/theme/index.ts), [styles](../../../wiki/.vitepress/theme/style.css), [browser checks](../../../wiki/scripts/check-browser.mjs); при необходимости новый wiki/.vitepress/theme/WikiPortal.vue.

1. RED: браузерные проверки наличия/видимости трёх маршрутов, полноценного каталога из manifest и quick-start ссылок; пути к каждому manifest pageId; активное меню, портал без JS. Текущая главная должна не проходить новый сценарий.
2. GREEN: короткий ввод/версия → маршруты → быстрый старт → тематический каталог → помощь. Не заменять большим hero и одинаковыми пустыми карточками. Общий layout до ~1440px, читаемый текст отдельно от широких таблиц; sidebar группирует механики и открывает активную группу, outline сохраняется.
3. Проверить base-aware ссылки и assets, SSR содержимого, переводы/aria/focus, мобильную навигацию. Переписать старый тест обязательного большого art на проверку полезной композиции: это обоснованная смена требования, не удаление проверок accessibility/overflow.

**Expected:** при 1440×900 полезные маршруты и начало каталога на первом экране; из портала любая статья ≤2 переходов; без JS виден текст и каталог; 360/768px не ломают ширину. Визуально осмотреть RU/EN light/dark, а не ограничиться DOM.
**Checks:** npm run build --prefix wiki; npm run check:links --prefix wiki; npm run check:browser --prefix wiki (уже установленный browser/channel, без неявной установки).

## Task 3 — Превратить общие и игровые страницы в законченные инструкции

**Files:** wiki/{ru,en}/{overview,compatibility,faq,glossary}.md; все 18 wiki/{ru,en}/players/*.md по исходному manifest; [manifest](../../../wiki/content-manifest.json); новые wiki/public/diagrams/ и при наличии wiki/public/screenshots/ с provenance.

1. Для каждого из 22 pageId прочитать привязанные docs и реальные UI/service/options по сомнениям; отметить недостающие шаги/ошибки в coverage. Это содержательный RED, не требование к word count.
2. Написать обе локали целыми сценариями по spec: предусловия/где нажать/что ввести/результат/проверка/отказ/восстановление. Для торговли различать wallet, стопку серебра и escrow; для raids/visits/live/coop — инициатора, хозяина, копию карты, владение пешками и сохранение. Для общих страниц выбрать свой формат, не копировать шаблон механик.
3. Добавить схемы входа/modpack, escrow/почты и сохранения/recovery с текстовым эквивалентом. Новые узкие статьи выделять только при самостоятельной цели; существующие anchors оставить. Изображения не заменяют действия и текст перевода.
4. Сверить числа, названия UI, последствия и ограничения с источниками; обновить revisions обеих локалей и связи manifest. Нужные админу детали ссылкой на полноценную статью того же языка, не английский fallback.

**Expected:** игрок самостоятельно проходит первый вход и повторную загрузку; видит безопасную ветвь при изменённом pin, modpack mismatch, отказе/обрыве сделки и live; не получает обещаний будущих функций. Coverage всех 22 pageId закрыт содержательно; index закрыт task2.
**Checks:** test:checks, check:content, check:reference, build, check:links; команды через npm run <имя> --prefix wiki. Ручная смысловая сверка RU/EN каждого topic с зафиксированным результатом.

## Task 4 — Довести эксплуатацию, reference и developer track

**Files:** все 14 wiki/{ru,en}/admins/*.md и 8 wiki/{ru,en}/developers/*.md; [reference inventory](../../../wiki/reference-inventory.json), [reference checks](../../../wiki/scripts/check-reference.mjs), [reference tests](../../../wiki/scripts/reference.test.mjs), [manifest](../../../wiki/content-manifest.json); архитектурная схема в wiki/public/diagrams/.

1. Содержательный RED: по матрице отметить недостающие условия запуска, точные CLI/API аргументы и результаты, backup/restore proof, обновление/rollback, build prerequisites/outputs, protocol/storage boundaries. Проверить не только наличие имён в таблице.
2. GREEN: написать обе локали; tutorials отделить от полного reference, дать навигацию по группам и работающие anchors. Сохранить все фактические keys/routes/commands; общие auth/error schemas могут быть вынесены, но каждая операция однозначно на них ссылается. Примеры чтения безопасны; опасные операции помечены и используют отдельный тестовый data dir/placeholders.
3. Backup runbook различает согласованность DB+saves, восстановление в изоляции, generation/epoch, journals/locks, rollback игрока и whole-server restore; отдельно ограничения modpack backup. Не выполнять разрушительные команды против живых данных ради документации.
4. Architecture/client/storage связывают диаграмму с точными компонентами, потоком данных и границами доверия. Build/testing/contributing объясняют реальные проверки; docs CI не выдаётся за запуск игры. References обновлять только после сверки, не переписывать digest ради зелёного результата.

**Expected:** админ может подготовить сервер, диагностировать отказ и провести осмысленную изолированную проверку восстановления по документу; разработчик понимает что собирает solution и чего он не проверяет. Все 45 пар имеют предметный coverage, без формальной раздутости reference/general.
**Checks:** test:checks, check:content, check:reference, build, check:links, check:drift с исходной явной базой. Ручная сверка каждого topic и опасного примера.

## Task 5 — Проверить целиком, один review, ручное обновление Pages

**Files:** [browser checks](../../../wiki/scripts/check-browser.mjs), [maintenance](../../../wiki/README.md), [wiki CI](../../../.github/workflows/wiki-check.yml), [manual workflow](../../../.github/workflows/wiki-deploy.yml), docs/verification/2026-11-04-wiki-encyclopedia.md; ledger. Исходный game CI не менять.

1. Сохранить regression tests drift/reference/parity; расширить browser acceptance на портал, длинное руководство и широкий reference в обеих локалях на 360/768/1440, light/dark, 200% reflow, keyboard/focus, no-JS, deep reload, старые anchors, язык+hash, поиск ≥10 запросов/язык и отсутствие неожиданных внешних запросов. Добавить проверку сохранения полного baseline routes/anchors после возможного split статей.
2. Обновить manual workflow: только build/полные обязательные checks/upload проверенного артефакта, без deploy-pages и write-прав. Обновить runbook фактической legacy gh-pages публикации; не переключать Pages source. Дополнительные browser dependencies не устанавливать без разрешения, ограничения фиксировать.
3. Прогнать итоговый набор, визуально осмотреть captures и прочитать сквозные сценарии из spec. Отчёт указывает source commit, явную diff base, версии инструментов, исходы команд, coverage каждой пары, media provenance и незапущенные проверки; свежая проверка security/dependency advisories не подменяется старым clean audit.
4. Передать Astra один пакет всей wiki-итерации: spec/plan/diff/coverage/ledger/результаты. Один fix pass Sol с RED→GREEN для дефектов. Minor deferrals явно сообщить пользователю; обязательные критерии не прятать как minors.
5. После подходящих проверок выполнить Git identity/staged/secrets gate и при необходимости один локальный коммит своих изменений: «Развернуть wiki в двуязычную энциклопедию». До коммита обязательны dotnet test Rimlink.sln -nologo -v q и project mod-build gate; если недоступны — остановить коммит/публикацию и сообщить. При разрешённом push проверить все исходящие commits; чужие untracked материалы не включать.
6. Main отдельно публикует проверенный dist в существующий gh-pages по авторизации пользователя, сохраняя предыдущий deploy commit. Без force-push; подтвердить live RU/EN portal, deep article, assets, поиск и hash после обновления. Не объявлять deploy успешным по локальному build или одному HTTP200. При несоответствии остановиться и сообщить; предыдущий артефакт доступен для согласованного возврата.

**Final commands (из корня, явная база вместо placeholder):**

~~~sh
npm run test:checks --prefix wiki
npm run check:content --prefix wiki
npm run check:reference --prefix wiki
npm run check:drift --prefix wiki -- --base <explicit-base-commit>
npm run build --prefix wiki
npm run check:links --prefix wiki
npm run check:browser --prefix wiki
git diff --check
~~~

**Expected:** zero structural/reference/drift/link errors, подтверждённый semantic coverage, браузерные evidence, один завершённый review и проверенный live результат после отдельной публикации main. Никаких новых игровых гарантий из этого набора; недоступный runtime/GOG/production restore остаётся явно непроверенным.
