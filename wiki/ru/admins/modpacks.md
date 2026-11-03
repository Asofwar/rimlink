---
pageId: admins/modpacks
contentRevision: 1
---

# Управление набором модов

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

1. Подготовь проверенный набор в отдельной установке игры. Зафиксируй ModsConfig.xml, порядок, версии и hash.
2. Выполни modpack import --from &lt;ModsConfig.xml&gt; --mods-dir &lt;folders&gt; --dry-run. После проверки добавь --server/--token для отправки требований.
3. modpack defs собирает разрешённые ThingDef/PawnKindDef; пустой список выключает эту проверку. Общие настройки импортируются только для выбранных --mod, загружаемых ниже Rimlink.
4. Для серверной доставки выполни modpack package requirements --output &lt;new-file&gt;, затем package pack --source &lt;redistributable-folders&gt; --requirements &lt;file&gt; --output &lt;new-directory&gt;.
5. Проверь archive.zip и manifest.json, лицензии и настройки. package publish --from &lt;directory&gt; требует --confirm-redistribution; --confirm-public и --confirm-shared-settings подтверждают соответствующее раскрытие. Проверь package status; unpublish снимает публикацию.

Протестируй чистого клиента после изменения коллекции.

## Ограничения и безопасность {#limits}

Не упаковывай каталог игры, Core/DLC и секреты модов. Pack требует полный подготовленный набор и новый output. Публикация — отдельное внешнее действие оператора. Изменение требований может сделать прежний опубликованный пакет неактуальным.

## См. также {#related}

- [Справочник CLI](./cli.md)
- [Набор модов сервера](../players/modpacks.md)
- [Обновление сервера и клиентов](./updating.md)
