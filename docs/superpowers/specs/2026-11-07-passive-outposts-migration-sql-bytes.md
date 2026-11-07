# Passive outposts — стабильные байты SQL миграций

**Статус:** узкое техническое уточнение уже принятого контракта [Task 15 §10.7](2026-11-05-passive-outposts-admin-readiness-clarification.md#107-finite-exact-crlflf-profiles-and-cross-host-qualification). Это не повторное итоговое ревью ветки и не новая схема. Реализация и проверки ниже принадлежат Sol; автор уточнения их **не выполнял**. Product остаётся Pending/OFF, G1–G7 не получают дополнительного PASS; deploy не разрешён.

## Причина и граница доказательства

В шести сохранённых [дескрипторах](../../../src/Rimlink.Server/Application/Outposts/SchemaProfiles/) каждый присутствующий immutable trigger содержит ровно один CRLF и ни одного отдельного LF внутри своего SQL. Метки `crlf`/`lf` в ProfileId обозначают конкретные полные наблюдения; они не требуют одинакового перевода строк у всех SQL-объектов. Источником истины остаются неизменные descriptor bytes и их digest.

В двух миграциях эти строки сейчас записаны как многострочные raw literals. Рабочая копия имеет CRLF, а Git index и `.gitattributes` задают LF для `.cs`. Поэтому одинаковый commit, собранный из сохранённых Windows bytes и из свежего Git checkout, может выдавать разные trigger SQL. Сообщённый CI blocker — `outpost-schema-profile-mismatch`; проверка отдельных сохранённых каталогов подтверждает CRLF-контракт, но это уточнение само не воспроизводит CI.

[Квалификация 2026-11-05](../../verification/2026-11-05-passive-outposts-schema-profiles.md) относится к реально использованным fixture/source bytes. Linux-запуск после rsync Windows-исходников подтверждает только этот вход; он не доказывает результат сборки свежего Git LF checkout. Старое свидетельство сохраняется с этой границей, не переименовывается в fresh-checkout qualification.

## Выбранное исправление

Заменить только представление двух raw SQL literals обычными C# строками с явными `\r\n`:

| Миграция | SQL-объекты |
|---|---|
| `20261103205626_PassiveOutposts` | `outpost_transitions_no_update`, `outpost_transitions_no_delete` |
| `20261105014436_OutpostAdminCommands` | `outpost_admin_commands_no_update`, `outpost_admin_commands_no_delete` |

Каждый существующий `SqlOperation.Sql` сохраняет весь квалифицированный CRLF-текст: порядок update/delete, имена, сообщение RAISE, пробелы, обе точки с запятой на строке BEGIN/END, межстрочные разделители и отсутствие конечного перевода строки. Физические переводы строк `.cs`, `Environment.NewLine` и текущая ОС больше не определяют эти байты. Не вводить общий преобразователь SQL или дополнительную зависимость.

Не менять migration IDs/lineage, модель/Designer, `.gitattributes`, шесть дескрипторов, ProfileId/ProfileDigest, whitelist, pinned recipe digests, domain/v1/v2 codecs, activation, readiness или protocol/version. `OutpostDomainSchema` по-прежнему сравнивает полный фактический каталог и lineage без нормализации и отказывает неизвестной форме. Если реальная новая сборка выдаёт ещё одно отличие, этот ruling не разрешает расширить whitelist или заменить ожидаемые digest для получения PASS.

Правка влияет только на последующее выполнение ещё не применённой миграции. Уже применённые базы, старые heads/cuts/receipts, retained sources и внешние authority stores не переписываются; новой ремонтной миграции и автоматического DROP/CREATE triggers нет. База с неизвестным LF-trigger каталогом остаётся Unsupported, а не становится допустимой по сходству SQL.

## Минимальная проверка реализации

1. **RED → GREEN для compiled SQL.** В существующих migration tests проверить точные UTF-8 bytes обоих `UpOperations` SQL-блоков, включая разделитель между CREATE TRIGGER и отсутствие конечного newline. Ожидание независимо от физического формата test source: явные escape sequences либо уже замороженные bytes, без многострочного raw golden, `ReplaceLineEndings` и `Environment.NewLine`. Наблюдать RED на исходной реализации из свежего LF checkout, затем GREEN после правки.
2. **Два свежих Git checkout/build.** Отдельно Windows и Linux/ext4 с текущим закреплённым toolchain; зафиксировать commit/patch identity, SHA-256 обоих migration sources, `git ls-files --eol` (`i/lf`, `w/lf`) и идентичность реально собранной assembly/provider/runtime. Не копировать старые `bin`/`obj`, сборки или CRLF working tree через rsync. Если до коммита проверяется patch, явно записать base + patch digest и факт повторной проверки уже итогового commit; это не чистый checkout неизменённого base.
3. **Настоящие базы.** На обоих сборочных входах пройти реальную EF history до pre-admin и до актуального состояния, затем проверить весь SQLite catalog + lineage через действующий validator и существующие ID/digest. Для обоих исходных pre-admin profiles применить только реальный additive admin step на каждом из двух executors: четыре существующих ребра должны дать именно заранее связанный target. Сохранить старые base definitions, v1 vectors, rows, heads/prefix/cuts; не использовать `EnsureCreated`, редактирование fixture JSON или каталог как expected, сгенерированный тем же прогоном.
4. **Полный additive recipe.** Проверить bytes и SHA-256 `IMigrator.GenerateScript` от `20261104093958_OutpostOrdinaryReturns` до `20261105014436_OutpostAdminCommands` против существующей квалификации: CRLF recipe `64358bbb34d8080afaaeeb745041b9fdeccadc28af8ce84666734ce3b8324525`, LF recipe `a5ee5d52c1d25f13d4f1d75e74f3cd02763b0667d81f3a74f23d62e7cea8b3f4`. Выбор ребра остаётся по точному recipe и source profile, не по ОС.
5. **Отказ и совместимость.** Существующий полный набор шести профилей по-прежнему читается на обоих исполнителях; `NoDdl` сохраняет профиль. Узкий отрицательный случай меняет только CRLF внутри одного immutable trigger на LF и получает typed schema refusal без repair/activation. Существующие regression tests проверяют immutable UPDATE/DELETE, frozen v1 и restore/activation; новая реализация не ослабляет эти assertions. Затем штатные build/full-suite проверки и Linux CI выполняются на итоговом исходнике; проход старого CRLF stand их не заменяет.

Новая assembly закономерно имеет собственные hash/MVID. Равный SQL recipe не разрешает подменить executor уже замороженного restore plan: старый план не редактируется, прежние exact-executor ограничения сохраняются. Новая попытка связывает свой реально проверенный artifact штатным способом.

## Документирование результата

Добавить фактические fresh-Git результаты и ограничения в verification evidence, сохранив исходный `qualification.json` как provenance старого capture; не заменять его `migrationSourceSha256` задним числом и не объявлять непроведённую матрицу PASS. Требования проекта к Wiki RU/EN, проверкам drift/reference и обоснованным исключениям сохраняются. Само это уточнение не меняет пользовательские команды, настройки и эксплуатационные действия.

**Ruling:** фиксированные CRLF SQL literals допустимы как восстановление уже квалифицированных байтов. Условие завершения — совпадение реального каталога и полного recipe на свежих Git LF сборках при сохранённых отказах, а не только успешная компиляция или проход unit test из прежней рабочей копии.
