# Caravan admission и geography export: проверки 2026-11-04

## Область проверки

Task11, собственная копия RimWorld 1.7.4995 rev309, Harmony 3.0.1.0, RimBridgeServer 2.3.0 и собственный профиль registry-baseline. Production, общий Steam-профиль и чужие процессы не менялись. Документированный loopback API использован без сохранения секрета.

## Автоматические RED → GREEN

- CaravanAdmission: фактическое прибытие, основной planet surface, 1–4 рабочих, оставшийся мобильный состав и cargo, пустой караван, повторный клик и fresh revalidation.
- Preview: независимые ожидаемые 80 wood / 40 steel в час, capacity 1000, стоимость основания 500 и остаток кошелька.
- Cancellation: только непрерывная ветка допускает NoRemoval; фактическое удаление требует точного receipt и server return, без local restore.
- Manifest: полные WorldParams/game/DLC/defs fingerprints, bounded staged upload и отдельный confirm; обычному игроку доступна только confirmed read.
- Receipt predecessor отсутствовал в ответе Prepare: реальный RPC RED, исправлено заполнение из immutable transition, без self-hash в Result.
- Replay Prepare удалял оригинальный intent до окончания admission: два реальных RPC RED, исправлено сохранение байтов журнала и дальнейший Operation-only query.
- Старый exact RPC contract перечислял семь методов: наблюдённый RED после добавления Manifest; сохранены все семь legacy signatures, добавлен точный восьмой метод. Legacy field/enum tags не ослаблялись.

## Настоящий game process

- Companion загружен из собственного стенда: 61 tool, 1 companion, 0 load warnings/errors.
- Новый offline world: 119904 последовательных tile records; land 59666, caravan-passable 57468, wooded 54422. WoodLog unit value 120, Steel 190 (x100). Экспорт не staged и не confirmed; серверная world identity не выдумывалась.
- GameVersion 1.7.4995; официальные DLC Anomaly, Biotech, Ideology, Odyssey, Royalty. Main surface planet.
- WorldParams SHA-256 29BF7A7A0EEDD76C6EAB17BD37E79C7754D23EB1C9AD07AAA13E52B7519E6236. Defs SHA-256 9A575E6A115126B08F3D3DE21C68A877155C3099F90BB201C026DED0424B8D0C.
- Сохранение [outpost-task11-export-new-world](<../../build/passive-outposts-stand/profiles/registry-baseline/Saves/outpost-task11-export-new-world.rws>): 4159833 bytes, оригинальные полные generation inputs присутствуют в Scribe. После холодной загрузки все counts и оба SHA совпали.
- Игра при начальном создании сообщала paused=true; после cold load — paused=false, несмотря на pauseIfNeeded. Все captures выполнялись на главном потоке; утверждения о постоянной паузе нет.
- Собственное окно закрыто штатным Alt+F4; отсутствие окна подтверждено отдельным CUA list_windows.

## Что пока НЕ проверено

- Native EN/RU staffing screen в этом прогоне не подтверждён: offline caravan_form отказал без Rimlink session; стандартный World tab в установленном bridge бросил NullReferenceException. CUA background click получил UIA timeout, разрешённый foreground fallback — foreground_unavailable. Отказы не обходились, guards не менялись.
- Полные multiplayer G1/G2, server founding/assign, 500 debit, actual removal/drop/recovery требуют готового materialization/lifecycle graph и остаются в Task16. Feature capability пока закрыта; local export/unit/RPC tests не подменяют эти сценарии.
- GAME/BRIDGE build и full-suite gate фиксируются отдельно в execution ledger; они не являются доказательством непроведённых UI/G1/G2 проверок.
