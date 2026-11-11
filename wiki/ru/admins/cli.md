---
pageId: admins/cli
contentRevision: 13
---

# Справочник CLI

Применимость: Rimlink 0.10.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Утилита rimlink-tools имеет четыре корня: backup, restore, modpack, admin. Команды local backup/restore работают с диском; admin и операции публикации modpack обращаются к серверу.

Пример чтения: `rimlink-tools admin status --server https://play.example.org:25560 --token <TOKEN>`. Токен в аргументах виден истории shell и спискам процессов: не сохраняй команду с реальным секретом в тикетах/скриншотах. --insecure принимает недоверенный сертификат и пригоден лишь для изолированного теста.

Для полной точной арности используй --help соответствующего корня. Ниже перечислены все команды admin; команды модов и дисковой копии описаны в связанных руководствах. Идентификаторы числовые, дробные числа в CLI разбираются invariant culture (точка).

## Безопасный первый сеанс CLI {#cli-read-first}

Проверь --help, адрес HTTPS и доверие сертификату. Для удалённых admin-команд обязательны --server и --token; локальные backup/restore работают с --data и не используют HTTP. Перед любой мутацией прочитай status и players, найди постоянный playerId, затем player и actions.

Например, последовательность admin status → admin players → admin player 42 → admin actions не изменяет мир. Подставляй адрес/секрет оператора вне общего чата. Для wallet значение delta обязательно, несмотря на неоднозначные квадратные скобки help; отрицательное значение списывает средства. Не проверяй синтаксис реальной компенсацией.

Группы ниже сохраняют полный набор команд. Для необратимой операции заранее запиши цель, причину, backup и ожидаемый результат, затем проверь ответ и audit.

## Ограничения и безопасность {#limits}

wallet в текущей реализации требует delta, хотя краткая help-строка показывает её необязательной: для чтения баланса используй API GET economy/wallet/{id} или карточку игрока. raids cancel поддерживает --return для возврата пешек. Ошибка CLI/HTTP не доказывает отсутствие выполненной операции: сначала проверь состояние.

## См. также {#related}

- [Справочник REST API](./api.md)
- [Управление набором модов](./modpacks.md)
- [Резервные копии и восстановление](./backup-recovery.md)

## Все admin-команды {#reference}

Каждая строка следует после `rimlink-tools admin`; обязательны `--server URL --token TOKEN`, необязателен `--insecure` только для тестов. `<...>` — обязательный аргумент, `[...]` — необязательный, `|` — выбор. Причину с пробелами заключай в кавычки. Права и эффекты соответствуют одноимённому REST-маршруту; таблица API указывает минимальный scope.

```text
actions
archive <playerId>
archived [archived|restored|all]
auction cancel <id> [reason]
auctions
backup
backup-get <name> <file>
backups
ban <playerId> [days] [reason]
bans
broadcast <text>
chat [playerId]
colonies [playerId]
colony revoke <ownerPlayerId> <memberPlayerId>
config
economy freeze [reason]|unfreeze|stats
forbid list|add <def...>|remove <def...>
incidents list [playerId]|cancel <id> [reason]
influence
influence-archive-disable <archiveId> <settlementId> <requestId> <worldId> <generation> <allianceId> <version>
influence-archive-setting <archiveId>
influence-color <allianceId> <worldId> <paletteIndex> <requestId>
influence-disable <settlementId> <requestId> <worldId> <generation> <allianceId> <version>
influence-setting <settlementId>
kick <playerId> [reason]
live list [playerId]|end <id> [reason]
lots [playerId]
modpack
mute <playerId> <minutes>
online
parcels <playerId>
pardon <banId>
peaceful <playerId> on|off
player <playerId>
player-freeze <playerId> [reason]
player-unfreeze <playerId>
players [query]
procurement list [page]|cancel <id> [reason]
raids list [playerId]|cancel <id> [reason] [--return]|freeze [reason]|unfreeze|stats
recreateworld
refund lot|parcel <id>
removals <playerId>
rename <playerId> <name> [faction]
reset <playerId>
restore-colony <archiveId>
role <playerId> player|moderator|admin
rollback <playerId> <version>
saveall
saves <playerId>
say <author> <text>
shop <playerId>
shutdown <seconds> [reason]
status
trade cancel <id> [reason]
trades [playerId]
unmute <playerId>
wallet <playerId> <+n|-n> [reason]
```

`wallet` обязательно принимает delta: несмотря на квадратные скобки в текущем --help, без него операция не является просмотром баланса. `raids cancel --return` просит возврат пешек; не используй как произвольное возмещение. Для influence сначала получи setting: requestId и context/version нужны для контроля повторов и устаревшего состояния. `reset`, `recreateworld`, rollback и restore меняют/уничтожают данные — сначала резервная копия и подтверждение.

### Локальные команды и модпак

```text
rimlink-tools backup --data <dir> [--out <archive.tar.zst>]
rimlink-tools restore --data <dir> --from <archive.tar.zst>
rimlink-tools anchor provision --data <dir> --anchor <dir> --storage-contract sqlite-local-v1 [--qualified-linux-filesystem ext4|xfs|btrfs]
rimlink-tools modpack import --from <ModsConfig.xml> [--mods-dir <folders...>] [--dry-run]
rimlink-tools modpack defs --mods-dir <folders...> [--dry-run]
rimlink-tools modpack configs import --config-dir <dir> --mods-dir <folders...> --mod <packageIds...> [--dry-run]
rimlink-tools modpack configs import --clear [--dry-run]
rimlink-tools modpack configs list --server <URL> --token <TOKEN>
rimlink-tools modpack package requirements --output <new-file> --server <URL> --token <TOKEN>
rimlink-tools modpack package pack --source <prepared-dir> --requirements <file> --output <new-dir>
rimlink-tools modpack package publish --from <dir> --confirm-redistribution [--confirm-public] [--confirm-shared-settings] --server <URL> --token <TOKEN>
rimlink-tools modpack package status --server <URL> --token <TOKEN>
rimlink-tools modpack package unpublish --server <URL> --token <TOKEN>
```

Для import/defs/configs import без --dry-run требуются --server/--token; --insecure доступен для HTTP-клиентов. Backup безопасен при работающем сервере; restore требует остановленного. `anchor provision` один раз создаёт пустое хранилище authority аванпостов (`authority.db`) в существующем каталоге вне каталога данных: сервер должен быть остановлен, существующее хранилище не заменяется, логическая authority не создаётся. На Linux укажите фактическую файловую систему тома; на Windows нужен локальный NTFS. После этого задайте `Outposts:AnchorDirectory`, `Outposts:StorageContract` и запустите сервер. Pack работает офлайн с отдельной разрешённой коллекцией, никогда с корнем игры; новые выходные пути не должны существовать. configs import делится только выбранными модами ниже Rimlink; проверь файлы на секреты. requirements/status читают сервер; publish/unpublish меняют доступность пакета, подтверждения нельзя добавлять автоматически.

### Аванпосты

`outposts inspect [--player ID] [--operation ID] [--limit N] [--cursor VALUE]`

`outposts preview|initialize|activate-domain-format|resolve --request FILE`

`outposts manifest inspect ID|upload FILE|confirm --request FILE`

Это remote REST-команды. Сохраните исходную команду и явно задайте confirmed=true после проверки. CLI не выполняет silent re-preview/retry и не открывает работающую БД. Подробности и пределы — в [руководстве](./passive-outposts.md).

### Гильдейский захват {#guild-capture}

```text
guild-capture inspect [limit]
guild-capture place preview FILE
guild-capture place FILE
guild-capture cancel FILE
guild-capture freeze FILE
guild-capture abort FILE
guild-capture profile stage KIND FILE
guild-capture profile approve FILE
guild-capture profile revoke FILE
```

Выполняйте эти команды через `rimlink-tools admin` с URL сервера и токеном администратора. `inspect` читает зафиксированное состояние и допуск Product. Размещение, отмена до старта, утверждение и отзыв принимают сохранённый JSON-объект запроса. Для точного повтора используйте тот же файл. Загрузка отправляет бинарный документ с номером вида из протокола (1–7). Поля и ограничения описаны в [справочнике REST](./api.md#guild-capture-setup).

Для размещения передайте в `place preview` JSON с полями `tile`, `resource` и `reason`. Сохраните ответ, после проверки добавьте `"confirmed": true` и передайте этот файл в `place`.

Для `freeze` передайте сохранённый JSON с текущими `scope` и `expectedHeadId`, UUID `requestId`, причиной `reason` и полем `frozen`. Значение true замораживает захват, false возобновляет.

Для `abort` передайте сохранённый JSON с текущими `scope` и `expectedHeadId`, UUID `requestId`, ID начатого боя `battleId` и причиной `reason`.
