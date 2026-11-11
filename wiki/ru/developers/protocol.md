---
pageId: developers/protocol
contentRevision: 34
---

# Протокол и идемпотентность

Применимость: Rimlink 0.10.0 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Протокол только растёт: добавляй поля с новыми номерами, сообщения, proto-файлы, ErrorCode и новые ServerEvent bodies. Не переиспользуй номера. events.proto импортирует feature proto; feature не должен импортировать events.proto, иначе цикл.

Мутации используют client_request_id. Неопределённый исход после обрыва повторяется с тем же id и исходным намерением через outbox; новый id означает новую операцию. Валидируй имена, размеры, пути, лимиты и допустимое состояние на границе. Rate/size refusal возвращает типизированный ErrorCode, не исключение наружу.

ProtocolVersion.Current сейчас 1 и меняется при несовместимом контракте, а не каждом релизе. Additive wire compatibility не означает, что старый клиент показывает новую функцию.

## Контракт мутации: не только protobuf-поле {#mutation-contract}

Для новой мутации опиши вход, полномочия, предикат состояния, атомарный результат и повтор. Сохраняй client_request_id вместе с исходными данными до определённого ответа. Получение timeout не доказывает rollback сервера. Повтор с прежним id должен вернуть тот же смысл результата, а изменение payload под тем же id — не стать новой операцией.

Тестируй отказ по размеру/имени/праву до побочного эффекта, конкурентные запросы и повтор после commit до получения ответа. Для live row читается внутри LiveStateLock; проверка снаружи подвержена гонке.

Wire-совместимость, version UI и collection hash — разные проверки. Добавленный новый message не разрешает использовать неподдерживаемый клиентом UX без согласованного поведения.

## Контракты запуска и приказов арбитра {#guild-executor-client-contracts}

В исходниках Client.Core отдельный BattleExecutorConnection использует только BattleExecutor service и RimlinkExecutor authentication, без player login. TLS pin задан оператором заранее: first-seen/changed certificate не принимается автоматически. Проверяются returned executor/lease/fence/profile и world задания. Обычный startup не читает операторский конфиг: чтение выбирается только явным `-rimlinkExecutorConfig=<absolute-path>`; подключение аргумента к игровому startup ещё не завершено. Отдельный credential path не содержит bearer в JSON, profile или status.

BattleExecutorSettings отказывает неизвестным/повторным JSON-ключам, HTTP, относительным путям и неверным hash. BattleExecutorProfile проверяет raw SHA профиля отдельно от semantic digest, всю reference closure и полный inventory файлов в явных roots. Лишний файл, изменённый мод/load order/DLC/config/loaded-defs projection, symlink или реально загруженная assembly вне inventory закрывает допуск. Исключения — только точные approved operational paths. Loaded-defs projection описывает provenance/actual identities, а не сериализацию всех runtime fields Def и не deterministic replay.

BattleCommandV1 — canonical bounded документ до 16KiB с fighter_id и одним Move/Attack/Draft. Actor/side берутся из серверной авторизации; parser проверяет своего бойца, вражескую цель и границы арены, неизвестный или повторный wire content отклоняется. BattleFinalCapture связывает один terminal sample, final snapshot, simulation journal, terminal view и immutable envelope; преждевременный/просроченный gameplay win не проходит общий final validator. Наличие этих контрактов не доказывает готовность сетевого command/view цикла или Product.

GuildCapture.AttackerReady и GuildCapture.ReconcileControl добавлены как gated player RPC. Ready фиксирует точный proposal; ReconcileControl после Start выдаёт grant заранее назначенному командиру, заменяет epoch при новом входе и не переписывает StartManifest. Capability передаётся как digest; в истории нет секрета или bearer. Control TTL — максимум две минуты и не далее конца battle/slot, новая выдача — не чаще раза в пять секунд на сторону, максимум 512 на бой; RPC допускает один повтор в окне 2/5s. Назначение защитника существует во внутреннем общем authority service до Start; пользовательский экран/API назначения ещё не подключены. Без включённой настройки и активированного формата эти RPC не включают игровой режим.

В текущем WIP добавлены GuildCapture.AttachControl, SubmitCommand, ControlHeartbeat и executor PollRunning/AcknowledgeCommands. Сервер сам назначает сторону и connection epoch; capability проверяется по SHA256, канал закреплён за текущими session/membership и HTTP/2 connection ID. Команды — только canonical Move/Attack/Draft, новая последовательность не более 20 команд/с на сторону, очередь не более 64. Повтор UUID с тем же содержимым не создаёт команду и не тратит этот бюджет. Внешние RPC-лимиты допускают повторы: SubmitCommand и executor poll/ack — 40/с, heartbeat — 4/с, attach — 4/5с.

Перед ответом PollRunning журнал fsync фиксирует Dispatched для пакета максимум из восьми команд. Потерянный ответ повторяет тот же delivery ID. При потере session, истечении grant или 30 секундах без активности сервер закрывает канал: сам помечает NotExecuted только ещё не отправленные команды. Уже отправленные требуют фактической квитанции исполнителя; поздняя Executed сохраняется. Новый канал ждёт CutApplied, Final отказывает при неизвестном результате доставки или незавершённом cut. Journal Position задаёт порядок пакетов; прошлый ответ не восстанавливает управление. Это серверный контракт и подключённый native transport module, пока без полной игровой приёмки.

HelloBegin/HelloComplete теперь отдельно проверяют profile/build/inventory, world/boundary и одноразовый server-boot/credential-generation challenge. В памяти ограничены 32 handshake записи с TTL 30 секунд; exact complete повторяет исходную lease. Execution требует Entry без Game/player session, а при занятом слоте — исходный runtime и сохранённый native context. RecoveryDelivery закреплён за конкретными battle/StartDigest и может доставлять immutable Final со старым as-of-Start профилем после его отзыва или смены конфигурации, сохраняя текущую аутентификацию. Courier не получает Poll/Prepare/Start/команды и не подтверждает SameProcessEntry за прежний процесс. Legacy Register — только health preflight без Execution admission.

Common GuildBattlePrepared receipt содержит immutable RuntimeAssignment: исходный процесс/lease, nonce, scope/fence, profile approval/build/inventory и server time до выдачи первой work. Transport reconnect того же процесса не переписывает assignment или Start; повтор Start возвращает исходную авторизацию. Это не разрешает восстанавливать симуляцию по initial snapshot после потери процесса.

## Ограничения и безопасность {#limits}

Схема не является доказательством авторизации: права проверяются на сервере. Для influence ожидаемая версия/контекст и requestId защищают от запоздавших операций. Не журналируй полезную нагрузку с секретами ради отладки.

Невыпущенный guild_resources.proto определяет GuildColonyScope, GuildSnapshotProof, GuildResourceParcelPayload, GuildSaveMetadata и GuildDeliveryReceipt. Parcel.guild_resource_payload занимает поле 14, ParcelKind 12 обозначает гильдейские ресурсы. Payload содержит только Steel/WoodLog, количество, оценку подтверждённого manifest, origin и закреплённого получателя; Scribe blobs и PawnCustody отсутствуют. GuildSaveMetadata находится только в OutpostSaveMetadata.guild_metadata=12, общем XML rimlinkOutposts и SaveHeader.outpost_metadata=16. GuildVersion согласуется отдельно: LoginRequest/RegisterRequest поле 9, LoginReply поле 18 и SaveHeader поле 20; последнее содержит только версию, а не вторую копию metadata. История семейства требует совместимый клиент и после принятия груза.

GuildCapture.BeginDelivery закрепляет exact BEFORE snapshot/hash и возвращает разрешение, GuildCapture.Manifest читает исходный каталог конкретной посылки. Локальная квитанция не содержит собственного будущего raw hash: он появляется снаружи в новом SnapshotAccepted/OutpostSnapshotLink. Принятие обновляет attempt, claim, parcel и первое подтверждённое сохранение в одной authority transaction. Ошибки GuildUnavailable=79 и GuildDeliveryInvalid=80 добавлены без изменения прежних номеров. Пока гильдейский захват на сервере не включён и V3 не активирован, RPC отказывают с typed error, GuildVersion в ответе входа остаётся 0. Контракт и успешная сборка не включают игровую возможность.


guild_battle.proto содержит отдельные BattleExecutor.Register, Heartbeat, Poll, UploadInitial, ProposePrepared и Start. Регистрация выдаёт lease с executor ID, случайным lease ID, возрастающим fence и semantic ProfileDigest. Register/Heartbeat/Poll ограничены 1 KiB на уровне сообщения; общий receive limit сервиса — 256 KiB + 16 KiB, чтобы принять chunk снимка ≤256 KiB с control headers. Proposal ограничен 16 KiB, сам canonical prepared manifest — 8 KiB. Dedicated RimlinkExecutor credential не является player Bearer; refusals используют BattleExecutorRefused=81, default OFF — GuildUnavailable. Poll/upload/proposal требуют проверенный опубликованный V3 cut и готовность сервера; handshake не включает захват.

battle_manifests.proto задаёт canonical Inventory/Profile/PreparedManifest. Inventory ≤8 MiB/16384 файла/256 mods; profile ≤16 KiB; roster содержит ровно четыре фиксированных бойца каждой стороны. Unknown/duplicate fields, неканонические bytes, неоднозначные пути, лишние команды и иные числовые правила отказаны. Artifact.sha256 — SHA256 исходных bytes; ProfileDigest и ManifestDigest имеют отдельные discriminator/version и не подменяют raw hash. До запуска сервер удерживает полные approved bytes и reference closure, common approval/revoke receipt; одного config digest недостаточно. Prepared proposals durable, immutable, максимум 8; новый sequence отменяет пригодность прежнего Ready. Внутренний AttackerReady создаёт один grant для точного manifest и текущей session/membership; bearer token в архив не попадает. StartCommitted повторно проверяет эти bindings и копирует точные bytes в защищённый GuildBattle вместе с initial/start artifact descriptors. Потерянный ответ возвращает прежние StartedAt и authority head. Результат боя, native executor и клиентский боевой flow ещё не квалифицированы.

## См. также {#related}

- [Хранилище и эскроу](./storage.md)
- [Клиент, мод и live](./client.md)
- [Справочник REST API](../admins/api.md)

Серверный WIP также реализует UploadResultArtifact, Final, CleanupChallenge и Cleanup. Immutable executor envelope сохраняет исходные runtime/start lease/fence/nonce и StartDigest; текущая transport lease может отличаться при повторной доставке. Полные initial/final/simulation/view artifacts и formal terminal predicates обязательны. Сервер отдельно запечатывает command journal с watermarks и связывает его с envelope в BattleCommittedFinalV1. Raw artifact hashes и semantic committed digest различаются. Final возвращает прежний receipt/head при exact replay, включая повтор после cleanup; конфликтующий envelope отказан.

Cleanup challenge действует 60 секунд по monotonic clock и связан с текущими lease/scope/result/runtime/inventory. Proof требует Entry без Game/map/tick runtime/home marker. Только common GuildExecutorCleanup освобождает physical slot; registry projection публикуется под S/B после внешнего commit. Timeout, новая registry и отсутствие процесса сами по себе не являются cleanup. Это проверенные серверные контракты, ещё не квалифицированный native flow.

В серверном WIP ReadPreparation возвращает полное основание манифеста: исходную RuntimeAssignment, отсечённые stock/remainder, резерв приобретения, слот и session/membership bindings командиров. Initial snapshot, native tick и восемь entity IDs добавляет измеривший их игровой адаптер. Чтение не создаёт финансового перехода и не авторизует тики.

ReadReadyOffer выдаёт nonce и точный proposal; AcknowledgeReadyOffer автоматически подтверждает доставку этому session и не заменяет явный AttackerReady. NoAttackerReady допустим только при здоровом исходном исполнителе, полном initial и подтверждённой до deadline доставке текущего предложения. Остальные недоказанные случаи завершаются TechnicalBeforeStart без штрафа. Подготовленный отменённый бой остаётся Active/CancelledAwaitingCleanup; Start и Final отсутствуют.

Cleanup terminal имеет явный tag AppliedResult или PreStartCancellation. Первый ссылается на committed Final digest, второй — на реальные terminal/assignment transition IDs без выдуманного Final. Исходный runtime может подтвердить собственный Entry через исторически связанный RecoveryDelivery; иной runtime-курьер не подтверждает Entry за него. Старый cleanup replay не снимает hold нового активного назначения.

Для восстановленного завершённого назначения RecoveryDelivery Hello связывает новую границу и fence, исходное назначение и nonce, а также свежее измерение Entry. Канал разрешает очистку только в этой восстановленной области. После общей фиксации повторный CleanupChallenge может вернуть `committed_receipt`, в том числе после смены аренды.

Ожидающая гильдейская посылка может содержать `guild_restore_scope` во внешнем сообщении почты. Это текущая область, подтверждённая планом восстановления. Неизменяемое `guild_resource_payload` по-прежнему содержит исходную область и используется для вычисления хеша BeginDelivery. Новому разрешению нужно текущее каноническое сохранение. Прежние попытки не принимают квитанции под новой границей.

Для локальной проверки в Windows флаг `local_process_required` требует ответить на nonce сервера через отдельный именованный канал для этого запроса Hello. PID клиента сообщает Windows. Флаг `replace_restored_executor` выбирает отдельный RecoveryDelivery Hello для восстановленной области со свежим Entry. С этой арендой можно отправить `ProvedReplacement` по выбранному назначению и повторно получить зафиксированную квитанцию очистки.

При потере ответа Start исходный живой процесс с неизменным initial может пройти StartAcknowledgementPending и получить StartReplayOnly: повторяется прежний request UUID, без новой симуляции, proposal или dispatch. RuntimeStarted сохраняет proof реально связанного контроллера в транспортном журнале. До этой записи команды не отправляются арбитру, а native адаптер удерживает первый тик до проверенного ответа. Повтор не обновляет исходный 20-минутный лимит; cold process не возобновляет бой. Native end-to-end квалификация ещё не выполнена.

Additive `GuildRuntimeTarget` передаёт mode `IsolatedAcceptanceV1` и stand ID в Login/Register/reply и executor Hello binding. Номера старых полей сохранены; target входит в digest challenge. В изолированном stand `GuildVersion=1` выдаётся лишь после common V3 readiness и guild admission. До привязки мира target может подтверждать выбранное подключение с `GuildVersion=0`; сервер без включённого захвата отвечает 0. Target не обходит session/role/phase или authority guards.

`ReadPreparedEntry` позволяет текущему командиру найти подготовленный бой до сохранения HOME. Запрос возвращает утверждённый профиль, дескриптор совместимости и инвентарь частями до 256 КиБ. Клиент проверяет хеши и сопоставляет корни инвентаря с загруженными игрой, средой выполнения и модами.

`ListOutposts` возвращает активные кампании текущей гильдии игрока в запрошенном мире и границе. В каждой строке указаны время боя по UTC и командир своей стороны. Сервер показывает возможность назначения, пока окно назначения открыто и игрок возглавляет обороняющуюся гильдию. При фиксации `AssignDefenderCommander` повторно проверяет главу, членство, состояние боя и срок.

`ReadPreparedView` и `ReadPreparedInitial` затем выдают карту до старта текущему командиру с принятым сохранением HOME и актуальными сессией, членством, профилем и fence. Страница ограничена 256 КиБ и закреплена хешем манифеста. Запросы не вызывают Ready, Start или финансовых эффектов. `BattleViewerCompatibilityV1` содержит ссылку на полный инвентарь и точную сборку клиента. Размер дескриптора ограничен 16 КиБ.

## Гильдейская подготовка и выдача {#guild-governance}

`GuildCapture.ListOutposts`, `PreviewConsent`, `AcceptConsent`, `DeclareCampaign` и `ClaimResources` используют `GuildWorldScope` и текущую авторизованную player identity. Сервер проверяет leader/member, colony, epoch, правила и общую V3 authority. Согласие содержит точный digest показанных правил, а изменяющие вызовы — стабильный client_request_id. Повтор согласия возвращает отдельную квитанцию мутации; полное состояние читается заново. Объявление возвращает фактически зарезервированное окно и profile/rules digests. Claim закрепляет источник, ревизию, получателя, generation/epoch и количество.

Просмотр не делает settlement и не меняет head: владелец/окно доступны игрокам, рассчитанный текущий склад и старые obligations — только текущим участникам соответствующей гильдии. Обычный OFF host отказывает всем пяти RPC даже клиенту новой версии. Наличие этих интерфейсов ещё не доказывает полный игровой захват.

## Публикация cut и краткий отказ допуска {#guild-cut-publication}

Защищённая запись и сверка могут временно снять GuildCut под блокировкой S. На квалифицированном изолированном стенде аутентификация арбитра в это время возвращает повторяемую ошибку Unavailable и проверяет учётные данные, исходники и роль. Транспорт ждёт S и под блокировками S и боя сверяет опубликованный срез с принятым состоянием. Если запрос отменён после снятия проверенного среза, сверка завершается под S. При отмене записи прежний срез публикуется только после успешного отката до записи pending. При ошибке сверки или прерывании после записи pending допуск остаётся закрыт. Повтор не продлевает lease, исходный лимит времени боя и льготный период.

После перезапуска сервера может остаться принятое назначение, которое арбитр ещё не получил. Тот же процесс может получить новый допуск со свежим доказательством Entry, если область, профиль, список файлов и сборка совпадают с назначением, у боя нет Start и Final, а исходная арена и её описание ещё не загружались. Poll возвращает исходное задание на подготовку либо исходную помеченную отмену после истечения срока. Слот остаётся занят до подтверждённой очистки. Другому процессу и назначению с уже переданными данными арены этот путь недоступен.

Когда сервер отклоняет полностью загруженный манифест мира, временный слот освобождается. Исправьте документ или обновите каталог определений, затем загрузите манифест с новым ID. Прерванную загрузку можно продолжить с прежними ID и хешем в пределах обычного срока хранения.

В новом `OutpostManifestDocument` поле `gameplay_defs_sha256` содержит 32 байта SHA-256 в формате `outpost-game-defs-v2`. При наличии поля клиент проверяет его. Документ без этого поля проверяется по прежнему `game_defs_sha256` в формате v1. Новые документы сохраняют оба поля. Хеш всего бинарного манифеста и его связь с конкретной посылкой остаются обязательными.

Поле BattleFighterSpec.baseliner_appearance_version (13) связывает с roster digest стандартную внешность: версия1 означает фиксированные core endogenes Hair_DarkBlack/Skin_Melanin6 и фиксированные цвета. Исторический0 остаётся читаемым; неизвестные и смешанные версии отклоняются. Текущий native adapter требует1. Изменение additive, ProtocolVersion.Current остаётся1.

Чтение PreparedView и страниц initial проверяет версию гильдейского клиента на RPC boundary, затем полный текущий допуск под S→B в owning service. Временное снятие опубликованного cut writer-ом требует ожидания storage barrier, а не преждевременного отказа до блокировки. Неизвестная battle/home/session identity после ожидания всё равно получает отказ; чтение не создаёт Ready/Start и не разрешает экономику.

`AssignDefenderCommander` выбирает защитника по campaign ID и player ID через текущую сессию лидера обороняющейся гильдии. Тот же `client_request_id` с точным намерением возвращает прежнюю квитанцию. Назначение после Start отказано; если игрок не назначен или недоступен к подготовке, сохраняется оборона ИИ. Клиентский outbox хранит точные bytes, операция не меняет владельца и не выдаёт ресурсы.

Вход совместимого клиента на явно выбранный изолированный стенд проверяет гильдейский допуск под S. После публикации проверенного V3 cut сервер возвращает `GuildVersion` и его boundary в одном ответе и допускает обычного игрока с гильдейским сохранением. Без допуска такой вход по-прежнему получает `RecoveryRequired`; старый клиент получает `RecoveryClientRequired`. Это не меняет флаги Product.

Арбитр после подтверждённого `RuntimeStarted` прикладывает к `PollRunning` кадр до 4096 байт с восемью бойцами, точным Start digest и номером от игрового тика. `ReadRunningView` читает кадр только через привязанный канал командира. Сервер выдаёт дельту при совпадении предыдущего номера, иначе полный кадр. Одинаковый повтор безопасен, конфликт номера или чужая попытка отклоняются, старый кадр не откатывает карту. `ReadCompletedView` возвращает квитанцию только после общего Apply. Исход проверяется по неизменяемым Start/Final и хешу терминального кадра.

После операторской остановки `BattleRunningWork.aborted` (8) передаёт назначенному арбитру точное назначение и признак `StartedAbort` (вид 3). `BattleCompletedViewReply.abort` (4) показывает командиру ту же остановку без результата или Final. `aborted_start_digest` (6) и локальная запись остановки связывают подтверждённый Start, а хеш Final остаётся пустым. Арена освобождается только после подтверждения очистки. Дополнения сохраняют версию протокола 1. По умолчанию настройка выключена.
