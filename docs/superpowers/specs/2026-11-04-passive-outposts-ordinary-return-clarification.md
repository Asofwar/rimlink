# Passive outposts: уточнение ordinary capture и доказанного локального возврата

Дата: 4 ноября 2026 года. Автор проектного уточнения: фактическая модель trusted runtime `gpt-6-astra`.

**Статус:** main принял уточнение как preservation существующего ordinary UX и разрешил только создание этого addendum. Это уточнение уже одобренных гарантий, **не новый игровой scope, не code review/финальный review и не утверждение готовности**. Реализация остаётся за основной Sol; единственное whole-branch Astra review — после всех задач. Здесь не выполнялись реализация, тесты, игра или коммит. Capability остаётся OFF до полного safety graph.

Основа: [approved design, §4–9](<2026-11-03-passive-outposts-design.md>), [approved plan, Tasks8/10/12–14](<../plans/2026-11-03-passive-outposts.md>), [recovery0.8.1](<2026-11-03-recovery-correctness-design.md>). Эти документы не изменяются. Названия новых wire types ниже предлагаемые; существующие API отмечены отдельно.

## 1. Решение и граница

Сохранить ordinary поведение: непосредственный отказ возвращает снятое туда, откуда взяли; replay-return использует существующий способ возврата. **Не заменять его mailbox-only** и не создавать одновременно local return и server parcel. Изменяются доказательства разрешения возврата, не цены, согласия, eligibility обычных передач или владение.

Помеченная пешка сохраняет прежний `PawnCustodyId` и фактический `AssignmentId`, в том числе пустой до первого назначения. Изменение здоровья, инвентаря, отношений, работоспособности или статуса пленника не превращает её в новую пешку и не навязывает ей worker eligibility при обычной передаче. Действуют прежние ограничения конкретного ordinary маршрута.

После accepted removal одного RPC refusal недостаточно. Нужны terminal negative fence для исходной операции и ровно один доказанный return-start. Unknown outcome не есть отказ; отсутствие source не есть разрешение spawn; TTL не освобождает custody. Ручным остаётся конкретный неопределённый crash/legacy случай, а не весь happy path.

## 2. Подтверждённые ingress seams

- [Outgoing.Run](<../../../src/Rimlink.Client/Game/Economy/Outgoing.cs#L485-L523>) строит actual request, журналирует его, делает `RecordDeparture`, `Commit`, `Removed(entry.Id)`, затем save/upload. Frozen outgoing blob сейчас не становится отдельным exact ordinary capture.
- [PawnCustodyBridge.ValidateCapturedAsync](<../../../src/Rimlink.Server/Application/Outposts/PawnCustodyBridge.cs#L54-L81>) ищет exact blob в `WorkerCaptures`. [OutpostWorkerCapture.Capture](<../../../src/Rimlink.Client/Game/Outposts/OutpostWorkerCapture.cs#L17-L42>) проверяет пригодность работника, а [ValidateAdmissions](<../../../src/Rimlink.Protocol/OutpostRecoveryData.cs#L91-L103>) требует непустой capture AssignmentId. Это не подходящий ordinary snapshot representation.
- [TransferOutbox.Add](<../../../src/Rimlink.Client.Core/Economy/TransferOutbox.cs#L107-L115>) генерирует `entry.Id` отдельно от `request.ClientRequestId`. [Removed/Returned](<../../../src/Rimlink.Protocol/SaveRecoveryData.cs#L9-L34>) принадлежат **local outbox ID**. Проверка `recovery.Removed.Contains(evidence.ClientRequestId)` не доказывает production removal. **Не переинтерпретировать legacy0.8.1 receipts.**
- [Immediate restore](<../../../src/Rimlink.Client/Game/Economy/Outgoing.cs#L544-L555>) и [replay return](<../../../src/Rimlink.Client/Game/Economy/Outgoing.cs#L336-L419>) не несут authoritative cancellation proof. [Registry.Bind](<../../../src/Rimlink.Client/Game/Outposts/PawnCustodyRegistry.cs#L51-L90>) — explicit ingress; transport provenance не authority. Safe unbound hold сохраняется, но не считается закрытым happy path.
- [SnapshotGuard.ValidateBranchAsync](<../../../src/Rimlink.Server/Application/Outposts/OutpostSnapshotGuard.cs#L251-L289>) запрещает alive из Home tombstone; существующее исключение относится к exact Started parcel delivery. [OutpostCustodyHandoff](<../../../src/Rimlink.Server/Application/Outposts/OutpostCustodyHandoff.cs#L62-L94>) предполагает реальный parcel, не local return.
- [CustodyTransaction](<../../../src/Rimlink.Server/Application/Outposts/CustodyTransaction.cs#L73-L102>) и [capture validation](<../../../src/Rimlink.Server/Application/Outposts/PawnCustodyBridge.cs#L44-L50>) выбирают highest Number. После rollback это **не обязательно IsCurrent**. Новый proof resolver обязан различать historical accepted removal и действующий canonical snapshot.

## 3. Bounded typed representation

Только additive protocol: новые сообщения/поля/enum values со свободными номерами; не переиспользовать и не переопределять опубликованные tags. Перед назначением tags Sol сверяет актуальное дерево. Не считать старый capability поддержкой новых обязательных proofs: readiness/version negotiation должен отвергать клиента, который не сохраняет/не понимает новый protected section. Legacy untagged semantics остаются прежними.

### 3.1. OrdinaryOutgoingCapture — отдельная секция metadata

Добавить repeated typed ordinary captures в [OutpostSaveMetadata](<../../../src/Rimlink.Protocol/Protos/outposts.proto#L205-L214>), не использовать `OutpostWorkerCapture` как универсальный blob carrier.

| Поле | Тип/граница и смысл |
|---|---|
| `schema_version` | Поддерживаемый явный version; неизвестный не legacy |
| `scope` | Existing `OutpostScopeMessage`: world, owner, generation, write epoch, restore boundary |
| `local_operation_id` | Canonical GUID N; именно `OutboxEntry.Id` |
| `request_kind` | Закрытый enum исходного RPC/маршрута; отличает trade.open/side, raid.launch/report и live.propose/report |
| `client_request_id` | Неизменный исходный request ID; для новых штатных ordinary requests canonical GUID N, без преобразования исторических IDs |
| `request_intent_sha256` | Ровно 32 байта `RequestIdentity.Hash` **полного финального исходного request** |
| `pawns` | Exact tagged pawn blobs с envelope, каждый с SHA256 полного protobuf blob; никаких worker facts |
| `derived_route` | Только при реальном nested route, например live raid: typed derived kind/id/hash и проверяемая связь с исходным request; не произвольный alias |

UUID canonical D; AssignmentId optional existing UUID, без генерации для обычной передачи; reference token до 256 символов без controls; scope использует существующие лимиты world/boundary 128 и положительный owner, неотрицательные generation/epoch. Один UUID встречается один раз в capture. Capture identity уникальна по local operation; conflicting дубль actor/kind/request не допускается.

Общий размер metadata не превышает существующие 4 MiB, число записей — 10 000, serialized transfer payload — `Limits.EconomyMaxParcelBytes`, весь bounded command — `Limits.MaxMessageBytes`, parser depth — 32. Проверять полный размер/shape до необратимого удаления. Переполнение сохраняет имущество и доказательства, а не запускает GC. Полный original request остаётся в durable outbox и передаётся resolver/cancel для проверки digest; server-authored terminal evidence сохраняет необходимые exact bytes для source verification и возврата. Не копировать все request bytes в каждый subsequent snapshot без необходимости.

Blob envelope отражает живую authoritative пешку непосредственно перед departure; registry в **том же removal snapshot** содержит tombstone с прежней UUID/reference/assignment. Это разные состояния, а не требование равенства двух envelope целиком. Проверяются их точные неизменные связи. `Removed(local_operation_id)` обязателен, `Returned(local_operation_id)` для нового outgoing запрещён.

Новый ordinary capture не даёт права outpost Prepare: worker admission продолжает требовать собственные факты, assignment и caravan proof. Исторический worker capture не перезаписывается под видом нового назначения.

### 3.2. Immutable request и snapshot

Финализировать исходный request до Prepared/capture; после этого не менять bytes. [ReconcileSnapshot](<../../../src/Rimlink.Client.Core/Economy/TransferOutbox.cs#L143-L161>) может менять game_tick: tagged branch не должна менять request после зафиксированного hash. Не вводить свой нормализованный hash, исключающий поля из существующего `RequestIdentity.Hash`.

Capture + registry tombstone + Removed сохраняются одним exact snapshot. Snapshot ID/raw SHA256 связываются с actual XML после capture через существующий [SaveSync immutable capture](<../../../src/Rimlink.Client/Game/SaveSync.cs#L153-L195>); hash файла не помещается внутрь этого же файла. Ready разрешён только после acceptance. Новые unrelated saves могут нести immutable capture дальше; RPC resolver проверяет совместимость с **IsCurrent**, а не подменяет proof последней по номеру версией.

## 4. Route identity и source-query

`RequestIdentity` — `(actor, original request kind, ClientRequestId)` плюс exact intent; scope fencing проверяется отдельно и раньше replay. Negative fence не должен становиться новым разрешением повторно использовать старый request ID в другом epoch/generation. Ниже mapping существующих источников, не объявление готового общего query RPC. Новый bounded query/resolver обязан читать эти реальные источники и исторические receipts, не только активный UI-list.

| Original route / actor | Существующий positive source и intent | Особенности cancellation/return |
|---|---|---|
| `transfer.send` / sender | `RequestIdentity.Parcel(actor, "transfer.send", id, request)`; Gift parcel с RequestActorId/RequestKind, exact RequestIntent | Не наличие send-строки из общего лога, а scoped parcel receipt; target и полный mixed payload входят в hash |
| `market.post` / seller | Lot: SellerPlayerId + ClientRequestId + RequestIntent, exact исходный Pawn/Items | Проданный/отозванный lot не становится отсутствовавшим post |
| `trade.open` / initiator | Trade: InitiatorPlayerId + ClientRequestId + RequestIntent; Transfers `trade-open`, RequestTargetId=trade.Id | Custody kind TradeSide не различает open/side; исходный kind хранить явно |
| `trade.side` / участник | Transfers: FromPlayerId + Kind=`trade-side` + ClientRequestId + RequestIntent; `RequestIdentity.TradeTarget`/RequestTargetId | Возвращаются только товары, снятые этим request; прежний escrow не включать в local return |
| `raid.launch` / attacker | Raid: AttackerPlayerId + ClientRequestId; Transfers `raid`, RequestTargetId=raid.Id, exact intent | Real pawn removal; snapshot/mirror launch не создаёт authoritative local-return pawn |
| `raid.report` / defender (source authorizer) | Transfers `raid-report`, From=target, To=attacker, RequestTargetId=raid.Id, id+hash отчёта; exact outcomes/source codec | Local restore относится только к новым own loot/captives; original raiders остаются в собственной server escrow/return цепочке |
| `live.propose`, visit / guest | LiveSession: GuestPlayerId+ClientRequestId; Transfers `live-launch`, target=session.Id, hash **LiveProposal**, retained SourcePayload | Ended/Fallback и очищенные Pawns не стирают факт принятого proposal |
| `live.propose`, raid / guest | Тот же `live-launch` original receipt **и** связанный `session.RaidId`, Raid + Transfers `raid` с hash **derived LaunchRequest** | Original и derived hashes различны. Проверить реальные поля mapping; fence закрывает оба входа, иначе direct/replayed derived launch может обойти отмену proposal |
| `live.report`, visit / host | Transfers `live-visit`, From=host, To=guest, target=session.Id, exact report id/hash и retained launch transition | Не путать source authorizer с прежним pawn custodian; existing returned guests не выдаются заново как local payload |

Доказательства mapping: [RequestIdentity](<../../../src/Rimlink.Server/Application/Economy/RequestIdentity.cs>), [TransferService](<../../../src/Rimlink.Server/Application/Economy/TransferService.cs#L16-L54>), [trade source codec](<../../../src/Rimlink.Server/Application/Outposts/OutpostTradeEvidence.cs#L11-L41>), [raid codec](<../../../src/Rimlink.Server/Application/Outposts/OutpostRaidEvidence.cs>), [live codec](<../../../src/Rimlink.Server/Application/Outposts/OutpostLiveEvidence.cs>), [live proposal mapping](<../../../src/Rimlink.Server/Application/Live/LiveSessionService.cs#L83-L108>).

Для live raid нынешний tagged derived request сохраняет ClientRequestId, untagged использует `live:` prefix. Не переименовывать legacy request keys. Доказательство original proposal hash нельзя сравнивать непосредственно с derived launch hash. Alias допускается только после проверки точной существующей трансформации и записывается в causal evidence. Изолированный orphan/несогласованный source — RecoveryRequired, не право отрицательного решения. Для report наличие raid/session само по себе не доказывает, что именно report применён; проверять report receipt и matching effects.

## 5. Authoritative cancellation и локальный return

### 5.1. Typed terminal evidence

Предлагаемые отдельные types: `OrdinaryCancelRequest`, `OrdinaryReturnProof`, `OrdinaryOperationQuery/Reply`; допускается размещение рядом с существующим custody service, не новый общий transaction framework.

Cancel/query несут scope, original kind/id, local operation ID, original exact request/digest и accepted removal proof. Return proof связывает terminal negative receipt/transition id+digest, original identity+intent, removal snapshot id/raw hash/scope, canonical snapshot id/raw hash, stable return attempt UUID, exact возвращаемый payload/digest и полный UUID/assignment набор. UUID/digests/sizes имеют границы §3; лишние/дублированные/неизвестные поля смысла не игнорируются. Scope не берётся из произвольного нового login.

Добавить typed route в [CustodyTransferEvidence](<../../../src/Rimlink.Protocol/Protos/custody.proto#L25-L43>) и source discriminator для negative ordinary return: original kind, local ID, terminal receipt reference, request mapping. Не выдавать его за GiftReturn/AdminRefund и не подставлять выдуманный SourceId/ParcelId. Их текущие source validators требуют реально принятого source.

Хранить terminal negative result и return state в компактной protected projection либо existing typed receipt projection с однозначным codec. Если нужен новый record, достаточен узкий `OutpostOrdinaryReturn` с original key, scope, intent/proof references, attempt, Started/Accepted и accepted snapshot. Уникальность original identity и единственного attempt обязательна; конкретная EF shape — работа Sol. Новые tables/columns/indexes только EF migration + schema/upgrade tests. Не перепрофилировать existing assignment operation/parcel без корректного discriminator и всех validators.

### 5.2. Последовательность

1. **Refusal/unknown.** Tagged immediate catch и replay не вызывают Restore/Contents по RpcException. Durable journal сохраняет original request и ожидание negative decision. Query возвращает Applied, Cancelled с exact proof либо Unknown/RecoveryRequired; Unknown не создаёт возврат. Exact cancel для доказанного снятого payload может завершить Unknown — не требуется indefinite hold.
2. **Locks и auth.** Outer entry: A → sorted owners O → S; для live затем LiveStateLock, после него DB transaction. Переиспользовать [CustodyEntry](<../../../src/Rimlink.Server/Application/Outposts/CustodyEntry.cs>) и [CustodyTransaction](<../../../src/Rimlink.Server/Application/Outposts/CustodyTransaction.cs>), без nested locks/transactions. Проверить boundary/generation/epoch/current session до replay, повторно внутри mutation boundary. [Auth capability gate](<../../../src/Rimlink.Server/Application/Auth/AuthService.cs#L119-L129>) и [bearer interceptor](<../../../src/Rimlink.Server/Grpc/SessionInterceptor.cs#L38-L48>) не заменяют этот proof.
3. **Resolve original source.** Matching committed source → Applied, без local return; known terminal negative → тот же result; changed intent → mismatch; legacy ambiguity/inconsistent history → RecoveryRequired. Source lookup и fence installation сериализованы с каждым original entry, включая nested live→raid.
4. **Install negative fence.** Только accepted exact removal + current compatible canonical branch + matching Home tombstone/UUID frontier + отсутствие positive outcome под тем же barrier разрешают terminal negative receipt. Original mutation проверяет его до любых business effects. Проверка отсутствия source без такой записи недостаточна: поздний RPC иначе может примениться после возврата. Fence бессрочно сохраняется после Accepted return и блокирует повтор initial mutation; mismatched scope/intent не переигрывается.
5. **Start return.** После terminal negative — один idempotent ReturnStarted с exact canonical proof. При готовности клиента и проверенном canonical proof эти решения могут быть одним protected transition. Это аналог Started parcel delivery, но отдельный typed local source без fake parcel. Возврат всей снятой local части mixed payload; wallet silver не материализуется и не списывается повторно. Trade/report ограничения — §4.
6. **Commit.** Negative projection/index + return-start + custody frontier + immutable typed evidence фиксируются через [OutpostAuthority.BeginHeldAsync/CompleteAsync](<../../../src/Rimlink.Server/Application/Outposts/OutpostAuthority.cs#L39-L143>): staged DB → durable external Pending → DB commit → external completion → response/events. Ошибка после Pending не превращается в definitive refusal; нельзя компенсировать потенциальный commit. Negative source verifier проверяет реальный terminal receipt и невозможность конкурирующего success, а не только отсутствие active source.
7. **Materialize.** На main thread заново проверить connection/authentication/game/scope. Fresh successful BeginReturn в непрерывной игре разрешает один restore в прежнее место. Explicit Bind прежних UUID/assignment с реальными references, RelationLedger для группы, Returned(local ID)+typed receipt+frontier сохраняются вместе с полным payload в exact snapshot. Partial placement/binding failure → BlockWrites/AbortToMenu; не продолжать в изменённой игре.
8. **Accept.** SnapshotGuard разрешает Home tombstone→alive исключительно по exact active ordinary return-start, полному UUID набору и отсутствию competing custody. В одной protected acceptance: Home/!tombstone/current reference, consumed attempt, accepted snapshot. Повтор acceptance не выдаёт второй предмет/credit. Outbox очищается только после accepted Returned snapshot; receipts/fence/history остаются.

### 5.3. Crash/continuity и unsent undo

- Selected snapshot уже содержит exact Returned/return receipt → upload/reconcile/ack-only, без spawn. Потерянный cancel/start reply запрашивает тот же attempt, не создаёт новый.
- First spawn после dropped Begin reply допустим только при сохранённой непрерывной игре и local доказательстве, что spawn ещё не вызывался.
- **Cold load + Started без receipt не означает, что spawn не было.** Сначала искать immutable pending/returned snapshot. Потерянная непрерывность и неопределённый local stage — индивидуальный RecoveryRequired/evidence-backed manual resolution согласно Tasks9/12; не новый attempt и не blanket отказ всех ordinary возвратов.
- До network/exact capture допустим локальный undo исходных объектов **вместе с предыдущей registry/metadata**, только если доказано, что Prepared никогда не отправлялся и removal snapshot/pending/upload не появился. Одного `TriggerSave=false` недостаточно. Нынешняя [ветка save-busy](<../../../src/Rimlink.Client/Game/Economy/Outgoing.cs#L506-L511>) находится после RecordDeparture; обычный Bind по догадке не исправление. При неопределённости сохранить journal.

## 6. Exact seams для реализации Sol

| Область | Узкая работа |
|---|---|
| [outposts.proto](<../../../src/Rimlink.Protocol/Protos/outposts.proto>), [custody.proto](<../../../src/Rimlink.Protocol/Protos/custody.proto>), [OutpostRecoveryData](<../../../src/Rimlink.Protocol/OutpostRecoveryData.cs>) | Additive capture/negative/return types, bounded parsing, capability compatibility; не ослаблять WorkerCaptures |
| [Outgoing](<../../../src/Rimlink.Client/Game/Economy/Outgoing.cs>), [TransferOutbox](<../../../src/Rimlink.Client.Core/Economy/TransferOutbox.cs>), [SaveSync](<../../../src/Rimlink.Client/Game/SaveSync.cs>) | LocalOperationId mapping, freeze final request, обе refusal ветки, durable return state и immutable snapshot reconciliation |
| [PawnCustodyRegistry](<../../../src/Rimlink.Client/Game/Outposts/PawnCustodyRegistry.cs>), [OutpostIncoming](<../../../src/Rimlink.Client/Game/Outposts/OutpostIncoming.cs>) | Proof-gated Bind, group placement/relations, all-or-nothing receipt, сохранение unbound hold |
| [PawnCustodyBridge](<../../../src/Rimlink.Server/Application/Outposts/PawnCustodyBridge.cs>), [CustodyTransaction](<../../../src/Rimlink.Server/Application/Outposts/CustodyTransaction.cs>), [RequestIdentity](<../../../src/Rimlink.Server/Application/Economy/RequestIdentity.cs>) | Exact ordinary capture resolver, actual source lookup/negative check, shared outer transaction, typed route/source verification |
| [TransferService](<../../../src/Rimlink.Server/Application/Economy/TransferService.cs>), [MarketService](<../../../src/Rimlink.Server/Application/Economy/MarketService.cs>), [TradeService](<../../../src/Rimlink.Server/Application/Trades/TradeService.cs>), [RaidService](<../../../src/Rimlink.Server/Application/Raids/RaidService.cs>), [LiveSessionService](<../../../src/Rimlink.Server/Application/Live/LiveSessionService.cs>) | Все соответствующие original ingress consult terminal fence; after-lock replay и auth/epoch/source checks; derived live-raid alias тоже закрыт |
| [OutpostSnapshotGuard](<../../../src/Rimlink.Server/Application/Outposts/OutpostSnapshotGuard.cs>), [OutpostCustodyHandoff](<../../../src/Rimlink.Server/Application/Outposts/OutpostCustodyHandoff.cs>) | Отдельный local-return source/receipt validator, Home revival и accepted receipt, retained historical evidence |
| [OutpostAuthority](<../../../src/Rimlink.Server/Application/Outposts/OutpostAuthority.cs>), [OutpostDomainDigest](<../../../src/Rimlink.Server/Application/Outposts/OutpostDomainDigest.cs>) | Новые projections в digest/evidence resolver; все transitions выдерживают startup reconciliation, no final refusal после Pending |
| [SaveStore](<../../../src/Rimlink.Server/Application/Saves/SaveStore.cs>) | Canonical IsCurrent resolver; Task13 preflight до rollback/reset/archive effects; Task14 restore authorization |

## 7. Короткая последовательность RED → GREEN

Это задания для Sol, **не выполненные проверки**. Для каждого пункта сначала наблюдать поведенческий RED, затем минимальный GREEN и повтор target tests. Новая API compilation failure — только начальный RED; environment/build failure не заменяет assertion. Не делать per-task Astra review.

| Шаг | RED assertions | GREEN / граница завершения |
|---|---|---|
| 12-A Capture/identity | Health/inventory/relations изменились; disabled/prisoner допускается прежним ordinary route; AssignmentId empty; entry.Id отличается от ClientRequestId; stale/missing capture и changed request hash отвергаются | Отдельный bounded capture, exact original/derived mapping, неизменный final request, Removed(local ID); WorkerCapture eligibility не ослаблена |
| 12-B Terminal decision | Send/post/open/side/real raid/live: success↔cancel обе очередности; same id другой actor/kind/intent; late RPC после fence; lost success reply; cleared mutable source не становится отсутствовавшей операцией | Source-specific resolver + durable negative fence, positive outcome выигрывает без возврата; unknown без доказательств не компенсируется; nested alias fenced |
| 12-C Return ingress | Immediate и replay rejection: один payload+UUID; mixed payload all-or-nothing; save-busy undo восстанавливает registry; чужой game/session callback; partial spawn | Fresh ReturnStarted → прежний local UX → explicit Bind/relations → Returned+receipt exact save; acceptance-only cleanup |
| 12-D Crash evidence | Before/AfterPending, AfterDomainCommit, AfterExternalCommit; dropped cancel/start; crash после spawn/до save, после save/до acceptance, после acceptance/до cleanup; cold Started без receipt | Pending reconciliation; exact pending upload/ack-only; никакого guessed re-spawn; только фактически неопределённый stage требует manual recovery |
| 13-A Lifecycle | Highest Number != IsCurrent; rollback до removal/return/reassignment; epoch race; archive/reset/recreate во время cancel/start; closed outpost/проданная lineage | Full-lineage preflight под общим barrier до IsCurrent/epoch/file/cancellation effects; current canonical proof; unrelated legacy0.8.1 rollback прежний |
| 14-A Restore | Backup без negative receipt/source snapshot; same-boundary stale DB; loss fence/anchor; interrupted restore; old outbox после fresh boundary | Coherent DB+saves+required archives cut, new evidence/digest coverage и retention, external reconciliation, controlled download; old scope не reauthorize |

Существующие места для targeted assertions: [OutpostCustodyBridgeTests](<../../../tests/Rimlink.Server.Tests/Outposts/OutpostCustodyBridgeTests.cs>), [PawnRegistryTests](<../../../tests/Rimlink.Client.Tests/Outposts/PawnRegistryTests.cs>), [OutpostDeliveryTests](<../../../tests/Rimlink.Client.Tests/Outposts/OutpostDeliveryTests.cs>). Новые proposed test files при необходимости: `tests/Rimlink.Server.Tests/Outposts/OrdinaryReturnTests.cs`, `tests/Rimlink.Client.Tests/Outposts/OrdinaryReturnRecoveryTests.cs`. Verse/Scribe/relations/место возврата проверяются в изолированной real-game acceptance G3/G4, не объявляются доказанными Core unit tests.

Task12 closure включает production capture + proof-gated ordinary return и необходимые уточнения недозакрытых Task8/10 seams; один safe unbound scan не завершение. Применяются existing commit/build/test gates approved plan; это addendum не разрешает обходить их или коммитить сейчас.

**Обязательная Task13 зависимость:** [нынешний rollback](<../../../src/Rimlink.Server/Application/Saves/SaveStore.cs#L243-L280>) меняет IsCurrent и epoch под S; отсутствие нового lifecycle preflight нельзя скрыть заявлением о сохранности tagged history. Local operation ID и ClientRequestId остаются разными; старые metadata не переписываются.

**Обязательная Task14 зависимость:** negative fences, return attempts, immutable original/derived intent, removal/returned snapshots и source history входят в retained proof graph и coherent backup/restore. [Domain digest](<../../../src/Rimlink.Server/Application/Outposts/OutpostDomainDigest.cs#L21-L34>) перечисляет конкретные tables — новая projection не включится сама. Утрата доказательства должна обнаруживаться до serving, не компенсироваться пустой историей.

До завершения Tasks13/14 и остальных approved readiness gates нельзя утверждать whole-branch no-loss/no-dup при rollback/restore, включать capability или считать это финальным review.
