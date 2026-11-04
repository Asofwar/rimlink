# Pawn custody registry: локальный игровой прогон 2026-11-04

## Границы

Task10, отдельная копия RimWorld 1.7.4995 rev309, Harmony 3.0.1.0, RimBridgeServer 2.3.0; собственный профиль \registry-baseline. Production, общий Steam-клиент и чужие процессы не менялись. GABP использован как документированный loopback API уже установленного моста; секрет соединения не сохранён. Это **не** полный G1/G4 multiplayer acceptance: он остаётся в Task16 после production admission/materialization flows.

## RED

- Базовое сохранение без registry: отсутствовали rimlinkOutposts/rimlinkPawnCustody.
- 14 Core-сценариев первоначально падали на отсутствующей реализации. Они проверяют reference identity, duplicate UUID, tombstones, смену thing ID, mirrors и malformed observations.
- Реальная загрузка обнаружила ReadValueChunk NotSupportedException у XmlNodeReader. Воспроизведено отдельным unit test с тем же типом reader.
- Реальный duplicate UUID давал RecoveryRequired, но исключение в ExposeData игра подавляла и всё равно создавала файл. Следовательно, одного исключения внутри Scribe недостаточно.

## GREEN в настоящем game process

- Три настоящие пешки зарегистрированы через Scribe_References, затем сохранены и загружены.
- Blob roundtrip: Thing_Human778 → Thing_Human13912; UUID 22401d21-1add-4999-adfa-89a6343d5cc7 сохранён. Raw mirror не зарегистрировал authority. После roundtrip: 3 entries, RecoveryRequired=false.
- После холодной загрузки неизменны также UUID 43850350-ec8b-4723-ab50-572ad1fece27 и fa8b0862-b534-458b-8755-3f241ab857d5. GameBranchId меняется, то есть cold load не изображает непрерывную ветку.
- Исправленный reader сохранил metadata; новый writer save успешно загрузился повторно: 3 entries, issues=[], RecoveryRequired=false. Новый branch: 6d7e39b1-b3dc-41e9-9df8-94f694ff2500.
- Отдельный runtime duplicate одного UUID у двух объектов: RecoveryRequired=true, issue duplicate-custody.
- Pre-save guard отказал **до** оригинального SaveGame: новый duplicate-blocked файл не появился. Попытка перезаписать существующий checkpoint сохранила его размер 4 072 311 байт и SHA-256 fbe0d03e63721f0d503838be4aacee4b642f4c1b7a560cd21884f711941459ee.
- Harmony postfix учитывает __runOriginal: отказ prefix не запускает upload старого файла. Malformed loaded metadata остаётся явным recovery hold, а не пустым bootstrap.

## Ограничения

- В этом прогоне не выполнялись server founding/recall, генные/royalty transfer и полный G1/G4; соответствующие end-to-end проверки — Task16.
- Bridge pauseIfNeeded/pause_game в этом runtime сообщал paused=false; проверки identity/save выполнены на главном потоке игрового API, без утверждения, что симуляция была остановлена.
- Private GAME build/install прошёл; сохранены прежние warnings. Unit/full-suite результаты фиксируются отдельно в execution ledger, не заменяют перечисленные игровые проверки.
