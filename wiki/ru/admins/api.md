---
pageId: admins/api
contentRevision: 17
---

# Справочник REST API

Применимость: Rimlink 0.10.4 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

REST доступен на том же сервере. Административные маршруты начинаются с /api/admin и требуют Authorization: Bearer &lt;TOKEN&gt;. Читай с минимальным scope. JSON поля сериализуются в camelCase. Методы POST/PUT/DELETE меняют состояние; примеры ниже — контракт, не приглашение выполнить их на живом сервере.

Пример чтения: `curl --header "Authorization: Bearer <TOKEN>" https://play.example.org:25560/api/admin/status`. Не вставляй реальный токен в общедоступную команду. Cookie-вход панели дополнительно требует CSRF для мутаций; предпочитай bearer для автоматизации.

При отказе сохрани HTTP status и тело ошибки. 401 означает отсутствие допустимой аутентификации, 403 — недостаточные права/защиту запроса; 404 — отсутствующий объект/выключенную поверхность. Конфликты проверяются по ответу операции. Для influence читай setting, передавай requestId, expectedVersion и context; на InfluenceConflict обнови setting, не выдумывай новую версию.

## Интеграция: начать с чтения и правильно повторять {#api-client}

Начни с GET /api/admin/status с отдельным read Bearer. Ожидай JSON и проверяй HTTP status до разбора тела; 401 означает неверную авторизацию, 403 — недостаточный scope/CSRF, а не сетевой timeout. Для SteamID используй строку, не JavaScript Number. Пагинацию и ограниченные списки читай по контрактам каждой операции ниже.

Мутация с потерянным ответом могла выполниться. Не повторяй автоматически POST wallet/refund/reset как новый запрос. Сначала сверяй audit и текущее состояние; где API задаёт requestId/expectedVersion/context, сохраняй исходное намерение. Idempotency игровых gRPC не означает универсальную идемпотентность любого REST POST.

События SSE — уведомления, не база: после gap перечитай состояние. Минимизируй scope, не логируй Authorization/cookies и не сохраняй приватные payload в аналитике.

## Ограничения и безопасность {#limits}

Выдача полного admin-токена боту не требуется. X-Rimlink-Actor — лишь подпись аудита. SSE /events не включает приватный чат, кошельки и посылки. Bootstrap доставки использует отдельный короткоживущий доступ; публичные status/health/metrics не требуют admin-токена.

## См. также {#related}

- [TLS, вход и права](./security-auth.md)
- [Справочник CLI](./cli.md)
- [Мониторинг и журнал событий](./monitoring.md)

## Все HTTP-операции {#reference}

`read`, `chat`, `moderate`, `admin` — минимальный scope. `admin` включает всё; `moderate` включает read, но не chat. У special-маршрутов отдельные условия ниже. Таблица не включает gRPC и статические файлы. Трейлинг / нормализован.

| Method | Маршрут | Доступ |
|---|---|---|
| POST | `/api/panel/login` | special |
| POST | `/api/panel/logout` | special |
| GET | `/api/panel/session` | special |
| GET | `/api/public/status` | special |
| GET | `/api/admin/events` | read |
| GET | `/api/admin/players` | read |
| GET | `/api/admin/players/{id:long}` | moderate |
| POST | `/api/admin/players/{id:long}/role` | admin |
| POST | `/api/admin/players/{id:long}/economy` | admin |
| POST | `/api/admin/players/{id:long}/rename` | admin |
| GET | `/api/admin/economy/wallet/{id:long}` | moderate |
| POST | `/api/admin/saveall` | moderate |
| GET | `/api/admin/status` | read |
| GET | `/api/admin/config` | admin |
| GET | `/api/admin/backups` | admin |
| POST | `/api/admin/backups` | admin |
| GET | `/api/admin/backups/{name}` | admin |
| GET | `/api/admin/chat` | read |
| GET | `/api/admin/players/online` | moderate |
| POST | `/api/admin/players/{id:long}/kick` | moderate |
| POST | `/api/admin/players/{id:long}/mute` | moderate |
| DELETE | `/api/admin/players/{id:long}/mute` | moderate |
| GET | `/api/admin/players/{id:long}/saves` | admin |
| POST | `/api/admin/players/{id:long}/saves/{version:int}/rollback` | admin |
| POST | `/api/admin/players/{id:long}/reset` | admin |
| GET | `/api/admin/bans` | moderate |
| POST | `/api/admin/bans` | admin |
| DELETE | `/api/admin/bans/{id:long}` | admin |
| GET | `/api/admin/archive` | moderate |
| POST | `/api/admin/players/{id:long}/archive` | admin |
| POST | `/api/admin/archive/{id:long}/restore` | admin |
| POST | `/api/admin/chat` | chat |
| POST | `/api/admin/broadcast` | moderate |
| POST | `/api/admin/shutdown` | admin |
| POST | `/api/admin/world/recreate` | admin |
| GET | `/api/admin/actions` | moderate |
| GET | `/api/admin/modpack` | read |
| GET | `/api/admin/modpack/configs` | read |
| PUT | `/api/admin/modpack/defs` | admin |
| POST | `/api/admin/economy/wallet/{id:long}` | admin |
| GET | `/api/admin/economy/parcels/{id:long}` | moderate |
| GET | `/api/admin/economy/lots` | read |
| POST | `/api/admin/economy/refund/lot/{id:long}` | admin |
| POST | `/api/admin/economy/refund/parcel/{id:long}` | admin |
| POST | `/api/admin/economy/freeze` | admin |
| GET | `/api/admin/economy/stats` | read |
| GET | `/api/admin/raids` | read |
| GET | `/api/admin/trades` | moderate |
| POST | `/api/admin/trades/{id:long}/cancel` | admin |
| POST | `/api/admin/raids/{id:long}/cancel` | admin |
| POST | `/api/admin/raids/freeze` | admin |
| GET | `/api/admin/raids/stats` | read |
| GET | `/api/admin/live` | read |
| POST | `/api/admin/live/{id:long}/end` | admin |
| GET | `/api/admin/shop/{id:long}` | moderate |
| GET | `/api/admin/shop/{id:long}/removals` | moderate |
| POST | `/api/admin/players/{id:long}/peaceful` | admin |
| PUT | `/api/admin/modpack` | admin |
| PUT | `/api/admin/modpack/configs` | admin |
| GET | `/api/admin/economy/forbidden-defs` | read |
| PUT | `/api/admin/economy/forbidden-defs` | admin |
| GET | `/api/admin/incidents` | read |
| POST | `/api/admin/incidents/{id:long}/cancel` | admin |
| GET | `/api/admin/colonies` | moderate |
| DELETE | `/api/admin/colonies/{owner:long}/{member:long}` | admin |
| GET | `/api/admin/influence` | read |
| GET | `/api/admin/influence/settings/{id:long}` | admin |
| GET | `/api/admin/influence/archive/{id:long}/setting` | admin |
| POST | `/api/admin/influence/settings/{id:long}/disable` | admin |
| POST | `/api/admin/influence/archive/{id:long}/disable` | admin |
| POST | `/api/admin/influence/alliances/{id:long}/color` | admin |
| GET | `/api/admin/modpack/delivery/requirements` | read |
| GET | `/api/admin/modpack/delivery/publication` | read |
| POST | `/api/admin/modpack/delivery/packages` | admin |
| PUT | `/api/admin/modpack/delivery/packages/{id}/archive` | admin |
| POST | `/api/admin/modpack/delivery/packages/{id}/publish` | admin |
| DELETE | `/api/admin/modpack/delivery/publication` | admin |
| GET | `/api/modpack-delivery/capabilities` | special |
| POST | `/api/modpack-delivery/access` | special |
| GET | `/api/modpack-delivery/packages/{id}/manifest` | special |
| GET | `/api/modpack-delivery/packages/{id}/settings` | special |
| GET | `/api/modpack-delivery/packages/{id}/archive` | special |
| GET | `/api/admin/auctions` | moderate |
| POST | `/api/admin/auctions/{id:long}/cancel` | moderate |
| GET | `/api/public/profiles/{id:long}` | special |
| GET | `/api/admin/profiles/{id:long}` | moderate |
| GET | `/api/admin/procurement` | moderate |
| POST | `/api/admin/procurement/{id:long}/cancel` | admin |
| GET | `/health` | public |
| GET | `/metrics` | public |

### Тела запросов и ответы

Поля JSON — camelCase от имён ниже, `?` допускает null. DTO с одинаковым именем принадлежит файлу своего маршрута; ReasonBody/cancel передаёт reason. GET принимает фильтры в query, не в JSON. Важные query: players q/limit/offset; chat playerId/before/after/limit; actions limit/before; archive state; lots/raids/trades/live/colonies playerId; procurement page; events after либо Last-Event-ID.

| DTO | Поля |
|---|---|
| `LoginBody` | `string? Token` |
| `RoleBody` | `string Role` |
| `RenameBody` | `string Name, string? FactionName` |
| `EconomyFreezeBody` | `bool Frozen, string? Reason` |
| `BanBody` | `long? PlayerId, long? AccountId, long? SteamId, string? Ip, string Reason, double? Days` |
| `KickBody` | `string? Reason` |
| `MuteBody` | `double Minutes` |
| `TextBody` | `string Text` |
| `ChatBody` | `string Author, string Text` |
| `ShutdownBody` | `int InSeconds, string? Reason` |
| `ModpackEntryBody` | `int Position, string PackageId, string? Version, ulong WorkshopId, string? Url, [property: JsonConverter(typeof(JsonStringEnumConverter))] EntryKind Kind, string? FilesHash = null` |
| `ModConfigBody` | `string PackageId, string Handle, string ContentBase64` |
| `ModConfigsBody` | `List<ModConfigBody> Configs` |
| `ModpackBody` | `List<ModpackEntryBody> Entries` |
| `DefsBody` | `List<string> DefNames` |
| `ForbiddenDefsBody` | `List<string>? Defs` |
| `WalletBody` | `long Delta, string? Reason` |
| `FreezeBody` | `bool Frozen, string? Reason` |
| `ReasonBody` | `string? Reason` |
| `CancelRaidBody` | `string? Reason, bool ReturnPawns = false` |
| `PeacefulBody` | `bool Peaceful` |
| `ContextBody` | `string WorldInstanceId, long SettlementId, long SaveGeneration, long AllianceId` |
| `DisableBody` | `string RequestId, long ExpectedVersion, ContextBody Context` |
| `ColorBody` | `string WorldId, int PaletteIndex, string RequestId` |
| `SettingBody` | `ContextBody Context, long Version, bool Enabled, bool? RootSurface, int Tile, long ArchiveId` |
| `AllianceBody` | `long Id, string Name, int PaletteIndex` |
| `SourceBody` | `long Id, long AllianceId, int Tile, int Radius` |
| `SnapshotBody` | `string WorldInstanceId, string ProcessInstanceId, long Revision, int AlgorithmVersion, AllianceBody[] Alliances, SourceBody[] Sources` |
| `AuctionCancelBody` | `string? Reason` |
| `CancelBody` | `string? Reason` |

Без Body-параметра операция не требует JSON (например saveall, backup, reset, rollback). Успешные списки возвращают JSON-массивы или страницы; status/config — объекты, wallet — balance, backup — name/size, бан — id, закупка cancel — closed. Backup download возвращает бинарный архив; SSE — text/event-stream. 404/409 различаются по причине, не повторяй мутацию вслепую.

Public status/profile возвращают 404 при отключённых Panel.Enabled/Public. /health и /metrics доступны без входа: ограничь сетью. /api/panel требует включённую панель и Admin:ApiToken; login принимает {token}, возвращает expiresAt и Secure HttpOnly SameSite=Strict cookie. Все panel POST требуют X-Rimlink-Panel/проверку Origin; неправильные данные дают 401, блокировка IP — 429. Интеграции используют bearer, не cookie.

Delivery: capabilities публичный; access принимает serverPassword (не данные входа учётной записи Steam/парольного входа) и выдаёт краткоживущий bearer. manifest/settings/archive требуют этот bearer для id пакета, не admin token. Archive поддерживает один Range и ETag/If-Range (200/206/416); upload archive — бинарный ZIP, остальные тела JSON. Для публикации безопаснее CLI package: проверка прав распространения обязательна. Ошибки delivery: 401/404/409/413/429/503/507; 429 содержит Retry-After.

### Контракт каждой операции

Вход — DTO, query/header или «—» без тела; path-параметры остаются в URL. 200 без объекта — пустой успешный ответ. JSON использует camelCase. Сначала обрабатывай HTTP status и Content-Type: неверный/отсутствующий bearer или просроченная cookie дают 401 с пустым телом, cookie-CSRF — пустой 403. Отказ scope и перехваченные сервисные RpcException обычно возвращают ProblemDetails; отдельные 400/404 могут быть пустыми либо JSON-строкой. Универсального JSON error-body нет. D — DeliveryError: {code,messageKey,args,retryable}, 400/401/404/409/413/429/503/507, Retry-After: 60 при 429.

| Операция | Вход | Успех | Дополнительные отказы |
|---|---|---|---|
| `POST /api/panel/login` | `LoginBody` | `{expiresAt} + Set-Cookie` | `401 / 403 / 404 / 429` |
| `POST /api/panel/logout` | `—` | `200` | `403 / 404` |
| `GET /api/panel/session` | `—` | `{expiresAt}` | `401 / 404` |
| `GET /api/public/status` | `—` | [PublicStatus](#response-schemas) | `404` |
| `GET /api/admin/events` | `after=epoch:seq; Last-Event-ID` | `SSE: id/event/data; gap` | `—` |
| `GET /api/admin/players` | `q; limit=50 (1..200); offset=0` | `{total,items: PlayerRow[]}` | `400` |
| `GET /api/admin/players/{id:long}` | `—` | [PlayerCard](#response-schemas) | `404` |
| `POST /api/admin/players/{id:long}/role` | `RoleBody` | `200` | `400 / 404` |
| `POST /api/admin/players/{id:long}/economy` | `EconomyFreezeBody` | `200` | `400 / 404` |
| `POST /api/admin/players/{id:long}/rename` | `RenameBody` | `200` | `400 / 404` |
| `GET /api/admin/economy/wallet/{id:long}` | `—` | `{balance}` | `404` |
| `POST /api/admin/saveall` | `—` | `200` | `—` |
| `GET /api/admin/status` | `—` | [AdminStatus](#response-schemas) | `—` |
| `GET /api/admin/config` | `—` | `ServerStatus.Config (redacted)` | `—` |
| `GET /api/admin/backups` | `—` | `{name,size,createdAt}[]` | `—` |
| `POST /api/admin/backups` | `—` | `{name,size}` | `—` |
| `GET /api/admin/backups/{name}` | `—` | `application/octet-stream` | `400 / 404` |
| `GET /api/admin/chat` | `playerId; before; after; limit=100` | [ChatLine[]](#response-schemas) | `400` |
| `GET /api/admin/players/online` | `—` | [OnlinePlayer[]](#response-schemas) | `—` |
| `POST /api/admin/players/{id:long}/kick` | `KickBody` | `JSON string` | `400 / 404` |
| `POST /api/admin/players/{id:long}/mute` | `MuteBody` | `200` | `400 / 404` |
| `DELETE /api/admin/players/{id:long}/mute` | `—` | `200` | `404` |
| `GET /api/admin/players/{id:long}/saves` | `—` | `{version,size,gameTick,isCurrent,createdAt,devMode,wealthTotal}[]` | `—` |
| `POST /api/admin/players/{id:long}/saves/{version:int}/rollback` | `—` | `200` | `404` |
| `POST /api/admin/players/{id:long}/reset` | `—` | `200` | `400 / 404` |
| `GET /api/admin/bans` | `—` | `{id,accountId,steamId,steamIdText,ip,reason,expiresAt,createdAt,playerName}[]` | `—` |
| `POST /api/admin/bans` | `BanBody` | `{id}` | `400 / 404` |
| `DELETE /api/admin/bans/{id:long}` | `—` | `200` | `404` |
| `GET /api/admin/archive` | `state=archived` | [ArchivedRow[]](#response-schemas) | `400` |
| `POST /api/admin/players/{id:long}/archive` | `—` | `{id}` | `409: no colony / online` |
| `POST /api/admin/archive/{id:long}/restore` | `—` | `200` | `404 / 409: restored / occupied / damaged` |
| `POST /api/admin/chat` | `ChatBody` | `{id}` | `400` |
| `POST /api/admin/broadcast` | `TextBody` | `200` | `400` |
| `POST /api/admin/shutdown` | `ShutdownBody` | `200` | `400` |
| `POST /api/admin/world/recreate` | `—` | `200` | `400` |
| `GET /api/admin/actions` | `limit=100; before` | `{id,at,actorAccountId,actorName,action,targetAccountId,targetName,details}[]` | `—` |
| `GET /api/admin/modpack` | `—` | `ModpackBody` | `—` |
| `GET /api/admin/modpack/configs` | `—` | `{packageId,handle,hash,size}[]` | `—` |
| `PUT /api/admin/modpack/defs` | `DefsBody` | `{count}` | `400` |
| `POST /api/admin/economy/wallet/{id:long}` | `WalletBody` | `{balance}` | `400 / 404` |
| `GET /api/admin/economy/parcels/{id:long}` | `—` | `{id,kind,fromPlayerId,fromName,toPlayerId,toName,silver,sizeBytes,createdAt,deliveredAt,deliveredTick}[]` | `—` |
| `GET /api/admin/economy/lots` | `playerId` | [LotView[]](#response-schemas) | `—` |
| `POST /api/admin/economy/refund/lot/{id:long}` | `—` | `200` | `404` |
| `POST /api/admin/economy/refund/parcel/{id:long}` | `—` | `200` | `404` |
| `POST /api/admin/economy/freeze` | `FreezeBody` | `200` | `—` |
| `GET /api/admin/economy/stats` | `—` | `{silverInWallets,openLots,pendingParcels,tradesToday,frozen,freezeReason,storageFeesCollected}` | `—` |
| `GET /api/admin/raids` | `playerId` | `{id,attackerPlayerId,attackerName,targetPlayerId,targetName,state,pawnMode,timing,lootCap,createdAt,landAtTick,landedTick,resolvedAt,expiresAt}[]` | `—` |
| `GET /api/admin/trades` | `playerId` | [TradeView[]](#response-schemas) | `—` |
| `POST /api/admin/trades/{id:long}/cancel` | `ReasonBody` | `200` | `404` |
| `POST /api/admin/raids/{id:long}/cancel` | `CancelRaidBody` | `200` | `404` |
| `POST /api/admin/raids/freeze` | `FreezeBody` | `200` | `—` |
| `GET /api/admin/raids/stats` | `—` | `{queued,announced,landed,resolvedToday,expired,cancelled,frozen,freezeReason}` | `—` |
| `GET /api/admin/live` | `playerId` | `{id,kind,hostPlayerId,hostName,guestPlayerId,guestName,state,raidId,snapshotVersion,endReason,createdAt,startedAt,endedAt}[]` | `—` |
| `POST /api/admin/live/{id:long}/end` | `ReasonBody` | `200` | `404` |
| `GET /api/admin/shop/{id:long}` | `—` | `{thingId,defName,stuffDef,count,quality,price,saveTick,label,soldAt}[]` | `—` |
| `GET /api/admin/shop/{id:long}/removals` | `—` | `{id,thingId,count,createdAt}[]` | `—` |
| `POST /api/admin/players/{id:long}/peaceful` | `PeacefulBody` | `200` | `400 / 404` |
| `PUT /api/admin/modpack` | `ModpackBody` | `200` | `400` |
| `PUT /api/admin/modpack/configs` | `ModConfigsBody` | `{count}` | `400: base64 / validation` |
| `GET /api/admin/economy/forbidden-defs` | `—` | `{defs:string[]}` | `—` |
| `PUT /api/admin/economy/forbidden-defs` | `ForbiddenDefsBody` | `{defs:string[]}` | `400` |
| `GET /api/admin/incidents` | `playerId` | `{id,buyerPlayerId,buyerName,targetPlayerId,targetName,entryId,level,pods,price,state,fireTick,createdAt,resolvedAt,expiresAt,reason}[]` | `—` |
| `POST /api/admin/incidents/{id:long}/cancel` | `ReasonBody` | `200` | `404` |
| `GET /api/admin/colonies` | `playerId; max 200` | `{ownerPlayerId,memberPlayerId,accepted,pawnIds}[]` | `—` |
| `DELETE /api/admin/colonies/{owner:long}/{member:long}` | `—` | `200` | `400 / 404` |
| `GET /api/admin/influence` | `—` | `SnapshotBody` | `503` |
| `GET /api/admin/influence/settings/{id:long}` | `—` | `SettingBody` | `400 / 404` |
| `GET /api/admin/influence/archive/{id:long}/setting` | `—` | `SettingBody` | `400 / 404` |
| `POST /api/admin/influence/settings/{id:long}/disable` | `DisableBody` | `{appliedVersion,publicRevision}` | `400 / 409 / 503` |
| `POST /api/admin/influence/archive/{id:long}/disable` | `DisableBody` | `{appliedVersion,publicRevision}` | `400 / 409 / 503` |
| `POST /api/admin/influence/alliances/{id:long}/color` | `ColorBody` | `{appliedVersion,publicRevision}` | `400 / 409 / 503` |
| `GET /api/admin/modpack/delivery/requirements` | `—` | `CollectionDescriptor` | `D` |
| `GET /api/admin/modpack/delivery/publication` | `—` | `DeliveryPublication` | `D` |
| `POST /api/admin/modpack/delivery/packages` | `UploadOffer` | `UploadAccepted` | `D` |
| `PUT /api/admin/modpack/delivery/packages/{id}/archive` | `application/zip binary` | `{verified:true}` | `D` |
| `POST /api/admin/modpack/delivery/packages/{id}/publish` | `PublishPackage` | `DeliveryPublication` | `D` |
| `DELETE /api/admin/modpack/delivery/publication` | `—` | `DeliveryPublication` | `D` |
| `GET /api/modpack-delivery/capabilities` | `—` | `DeliveryCapabilities` | `D` |
| `POST /api/modpack-delivery/access` | `AccessRequest` | `AccessGrant` | `D` |
| `GET /api/modpack-delivery/packages/{id}/manifest` | `delivery Bearer` | `PackageManifest` | `D` |
| `GET /api/modpack-delivery/packages/{id}/settings` | `delivery Bearer` | `SettingsSnapshot` | `D` |
| `GET /api/modpack-delivery/packages/{id}/archive` | `delivery Bearer; Range; If-Range` | `ZIP: 200 / 206; ETag; Accept-Ranges` | `D / 416` |
| `GET /api/admin/auctions` | `max 500; active only` | `{id,sellerPlayerId,startPrice,buyoutPrice,bid,bidderPlayerId,expiresAt}[]` | `—` |
| `POST /api/admin/auctions/{id:long}/cancel` | `AuctionCancelBody` | `JSON bool` | `—` |
| `GET /api/public/profiles/{id:long}` | `—` | [PublicProfileDto](#response-schemas) | `404` |
| `GET /api/admin/profiles/{id:long}` | `—` | [PublicProfileDto](#response-schemas) | `404` |
| `GET /api/admin/procurement` | `page=0` | `{total,entries:[{id,kind,buyerId,buyerName,supplierId,defName,quantity,supplied,reward,escrow,status,expiresAt,contributions}]}` | `400` |
| `POST /api/admin/procurement/{id:long}/cancel` | `CancelBody; reason <=200` | `{closed:bool}` | `400` |
| `GET /health` | `—` | `text: Healthy` | `503` |
| `GET /metrics` | `—` | `Prometheus text` | `—` |

Пагинация: players возвращает total/items и использует offset; chat — before/after по id (after сортирует по возрастанию, before — новые первыми); actions — before по id; procurement — страницы с нуля. Списки colonies (200) и auctions (500) ограничены без курсора; отсутствие строки не доказывает отсутствие объекта. SSE использует Last-Event-ID приоритетнее after; при gap перечитай состояние. Cookie POST/PUT/DELETE требуют `X-Rimlink-Panel: 1`; Origin должен отсутствовать или совпадать со схемой/хостом/портом запроса.

### DTO доставки

| DTO | JSON-поля |
|---|---|
| `UploadOffer` | `manifest: PackageManifest; redistributionConfirmed: bool; publicDistributionConfirmed: bool` |
| `UploadAccepted` | `packageId: string; uploadExpiresAt: UTC` |
| `PublishPackage` | `expectedCollectionRevision: string; expectedCatalogHash: string; sharedSettingsConfirmed: bool` |
| `DeliveryPublication` | `available: bool; packageId?, collectionRevision?, catalogHash?: string` |
| `DeliveryCapabilities` | `formatVersion: int; deliveryAvailable, requiresServerPassword, passwordLoginAvailable, steamLoginAvailable: bool` |
| `AccessRequest` | `serverPassword?: string; expectedPackageId?: string` |
| `AccessGrant` | `token: string; expiresAt: UTC; packageId, collectionRevision, catalogHash: string` |
| `CollectionDescriptor` | `revision, gameVersion, catalogHash: string; entries: DeliveryEntry[]` |
| `DeliveryEntry` | `position: int; packageId, version, filesHash: string; kind: numeric EntryKind` |
| `SharedConfig` | `packageId, handle, hash: string; content: base64` |
| `SettingsSnapshot` | `packageId, catalogHash: string; configs: SharedConfig[]` |
| `PackageManifest` | `formatVersion; packageId; collectionRevision; catalogHash; gameVersion; archiveSha256; archiveBytes; expandedBytes; fileCount; entries: DeliveryEntry[]; prerequisites: DeliveryEntry[]; mods: DeliveryMod[]` |
| `DeliveryMod` | `packageId, displayName, version, filesHash, contentHash, folderId; position; supportedVersions[], dependencies[], incompatibilities[], loadBefore[], loadAfter[]; expandedBytes; fileCount` |

Delivery JSON регистрозависим, неизвестные/повторные поля отвергаются, глубина ≤32. Секрет access token не сохраняй в журнал. Безопасный поток публикации: requirements → локальный package → UploadOffer → PUT ZIP → PublishPackage с ожидаемыми revision/hash; 409 требует заново сверить каталог, а не подставить устаревший hash.

## Схемы ответов {#response-schemas}

Ниже JSON-поля фактических REST-проекций, а не внутренняя структура protobuf/C# records. `?` после типа допускает null, `[]` — массив, UTC — ISO 8601 строка с Z. int/int64/uint64 — JSON numbers; не округляй 64-битные ID в JavaScript: используй lossless JSON-парсер либо храни исходный текст. Для Steam ID предпочитай строковые steamId/steamIdText. Строковые статусы регистрозависимы; Role в OnlinePlayer числовой, role в каталоге — строка player/moderator/admin. ModpackBody.kind — строковое enum; delivery EntryKind — число. Не переноси enum-предположения между API.

| Схема | JSON |
|---|---|
| [PublicStatus](#response-schemas) | `serverName, description, version, certificateFingerprint, address: string; protocolVersion, playersOnline, maxPlayers: int; worldExists, passwordRequired, steamLogin, passwordLogin: bool; modpack: PublicModpackEntry[]; players: PublicPlayer[]?; world: PublicWorld` |
| `PublicModpackEntry` | `position: int; packageId, version, url, kind: string; workshopId: uint64` |
| `PublicPlayer / PublicWorld` | `PublicPlayer: {name, factionName: string}; PublicWorld: {settlements, tradesToday, raidsResolvedToday, liveActive: int; economyFrozen, raidsFrozen: bool}` |
| `PlayerRow` | `id, accountId: int64; name, factionName, role: string; steamId, login: string?; online, peaceful, banned: bool; lastSeenAt, mutedUntil: UTC?; createdAt: UTC` |
| `PlayerCard` | `все поля PlayerRow в корне; lastIp: string?; session: {ip: string; since: UTC}?; settlements: {id: int64; tile: int; name: string}[]; walletBalance, wealthTotal: int64; firstSaveAt: UTC?; currentSave: {version: int; size, gameTick: int64; createdAt: UTC}?; activeBans: {id: int64; reason: string; expiresAt: UTC?}[]; visitsClosed: bool; peacefulOffAt, economyFrozenAt: UTC?; blocks: string[]?; economyFreezeReason: string?` |
| `ChatLine` | `id: int64; at: UTC; fromPlayerId: int64?; fromName, author: string?; kind, text: string` |
| `OnlinePlayer` | `playerId, accountId: int64; name, factionName, ip: string; role: int (0 Player, 1 Moderator, 2 Admin); since: UTC` |
| [AdminStatus](#response-schemas) | `version, certificateFingerprint: string; protocolVersion, playersOnline, maxPlayers, playersTotal, settlements, liveActive: int; startedAt: UTC; uptimeSeconds: int64; worldExists, certificateSelfSigned: bool; worldCreatedAt: UTC?; economy: {frozen: bool; reason: string?}?; raids: {frozen: bool; reason: string?; active: int}?; saves: {count: int; totalBytes, diskBytes: int64}?; backups: {count: int; lastAt: UTC?}?` |
| `ArchivedRow` | `id, playerId, wealthTotal: int64; playerName: string?; colony, reason, archivedBy, state: string; tile: int; settledAt, archivedAt: UTC; colonyGameDays: int?; lastActiveAt, restoredAt: UTC?; restoredBy: string?` |
| `LotView` | `id, sellerPlayerId, price: int64; sellerName, status, summary: string; createdAt, expiresAt: UTC` |
| `TradeView` | `id, initiatorId, partnerId: int64; initiatorName, partnerName, status, initiatorSide, partnerSide: string; revision: int; createdAt, expiresAt: UTC; closedAt: UTC?; closeReason: string?` |
| `PublicProfileDto (ProfileView)` | `playerId: int64; name, bio: string; lastSeen: UTC?; invitationsOpen, notifications: bool` |
| `Save list row` | `version: int; size, gameTick, wealthTotal: int64; isCurrent, devMode: bool; createdAt: UTC` |
| `Ban list row` | `id: int64; accountId, steamId: int64?; steamIdText, ip, playerName: string?; reason: string; expiresAt: UTC?; createdAt: UTC` |
| `Action list row` | `id, actorAccountId: int64; actorName: string?; action, details: string; targetAccountId: int64?; targetName: string?; at: UTC` |
| `Parcel list row` | `id, toPlayerId, silver: int64; fromPlayerId, deliveredTick: int64?; kind: string; fromName, toName: string?; sizeBytes: int; createdAt: UTC; deliveredAt: UTC?` |
| `Raid list row` | `id, attackerPlayerId, targetPlayerId, lootCap: int64; attackerName, targetName: string?; state, pawnMode, timing: string; createdAt, expiresAt: UTC; landAtTick, landedTick: int64?; resolvedAt: UTC?` |
| `Live list row` | `id, hostPlayerId, guestPlayerId: int64; kind, state: string; hostName, guestName, endReason: string?; raidId: int64?; snapshotVersion: int?; createdAt: UTC; startedAt, endedAt: UTC?` |
| `Incident list row` | `id, buyerPlayerId, targetPlayerId, price: int64; buyerName, targetName, reason: string?; entryId, state: string; level: int; pods: bool; fireTick: int64?; createdAt, expiresAt: UTC; resolvedAt: UTC?` |
| `Shop / removal rows` | `shop: {thingId, price, saveTick: int64; defName, stuffDef, label: string; count, quality: int; soldAt: UTC?}; removal: {id, thingId: int64; count: int; createdAt: UTC}` |
| `Auction list row` | `id, sellerPlayerId, startPrice, buyoutPrice, bid: int64; bidderPlayerId: int64?; expiresAt: UTC` |
| `Procurement page` | `total: int; entries: {id, buyerId, reward, escrow: int64; supplierId: int64?; kind, buyerName, defName, status: string; quantity, supplied: int; expiresAt: UTC; contributions: {playerId, paid: int64; name: string; quantity: int}[]}[]` |
| `EconomyStats / RaidStats` | `EconomyStats: {silverInWallets, storageFeesCollected: int64; openLots, pendingParcels, tradesToday: int; frozen: bool; freezeReason: string?}; RaidStats: {queued, announced, landed, resolvedToday, expired, cancelled: int; frozen: bool; freezeReason: string?}` |
| `Colony permission row` | `ownerPlayerId, memberPlayerId: int64; accepted: bool; pawnIds: string` |
| `Small receipts / backup` | `{id: int64}, {balance: int64}, {count: int}, {closed: bool}, {verified: bool}, {appliedVersion, publicRevision: int64}; backup POST: {name: string; size: int64}; backup GET list adds createdAt: UTC; panel session/login: {expiresAt: UTC}` |
| `Mod config row` | `packageId, handle, hash: string; size: int` |

PlayerCard — плоский объект: нет свойства player. Read-only GET players возвращает только id/name/factionName/online/role; остальные поля **отсутствуют**, не обнулены. Полная строка и карточка требуют moderate/admin. PublicStatus.players=null при скрытых публичных именах. AdminStatus может потерять отдельный объект economy/raids/saves/backups (null) при сбое подсистемы; числовые счётчики возвращают default 0 — это не доказательство пустой базы.

GET config возвращает только секции server/tls/auth/backup/saves/economy/raids/live/panel/hygiene/admin в camelCase. Значения secret serverPassword/steamWebApiKey/apiToken заменены на "set" либо ""; admin.tokens содержит только {name,scopes}, без token. Это не полный экспорт всех Options: например Social/Incidents отсутствуют. Значения и смысл — в [конфигурации](./configuration.md#reference). SSE и бинарные ответы не являются JSON DTO; входные DTO и delivery-схемы описаны выше.

Безопасный пример read-only каталога (вымышленные данные):

~~~json
{"total":1,"items":[{"id":42,"name":"Nova","factionName":"Cedar","online":true,"role":"player"}]}
~~~

Пример status-first клиента для GET JSON; не используй его как автоматический повтор POST:

~~~js
const response = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
const text = await response.text();
if (!response.ok) {
  throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
}
const data = text ? JSON.parse(text) : null;
~~~

### Администрирование аванпостов

Точные команды, sidecar, ограничения и запреты повторной выдачи описаны в [руководстве](./passive-outposts.md). GET не меняет authority; preview — advisory.

| Method | Path | Scope |
|---|---|---|
| GET | `/api/admin/outposts/manifest/binding` | admin |
| POST | `/api/admin/outposts/manifest/stage` | admin |
| POST | `/api/admin/outposts/manifest/read` | admin |
| POST | `/api/admin/outposts/manifest/confirm` | admin |
| GET | `/api/admin/outposts` | read |
| GET | `/api/admin/outposts/authority` | admin |
| POST | `/api/admin/outposts/preview` | admin |
| POST | `/api/admin/outposts/initialize` | admin |
| POST | `/api/admin/outposts/activate-domain-format` | admin |
| POST | `/api/admin/outposts/manifests/{id}/confirm` | admin |
| POST | `/api/admin/outposts/manifests/confirm` | admin |
| POST | `/api/admin/outposts/resolve` | admin |
| GET | `/api/admin/outposts/manifests/{id}` | read |
| POST | `/api/admin/outposts/manifests/chunks` | admin |

## Подготовка гильдейского захвата {#guild-capture-setup}

Эти операторские маршруты используют Bearer/панель и scope `admin`; обычный сервер с OFF не открывает гильдейские операции. Загрузка документа сохраняет только content-addressed bytes и не утверждает профиль. Утверждение проверяет полный профиль, inventory, template, compatibility и qualification closure через общую authority.

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/api/admin/guild-capture` | чтение статуса authority и допуска, форпостов, активных кампаний и боёв, ID доступного арбитра |
| POST | `/api/admin/guild-capture/cancel` | отмена до старта с параметрами scope, requestId, campaignId, expectedHeadId и reason |
| POST | `/api/admin/guild-capture/freeze` | изменение допуска захвата с параметрами scope, requestId, expectedHeadId, frozen и reason |
| POST | `/api/admin/guild-capture/abort` | остановка начатого боя с параметрами scope, requestId, expectedHeadId, battleId и reason |
| POST | `/api/admin/guild-capture/place/preview` | проверка размещения без записи с параметрами tile, resource и reason |
| POST | `/api/admin/guild-capture/place` | запись по сохранённому предпросмотру с параметрами command, intentDigest и confirmed=true |
| POST | `/api/admin/guild-capture/profile/stage/{kind}` | бинарное тело одного документа, максимум 64 MiB; отдельные виды имеют меньшие ограничения |
| POST | `/api/admin/guild-capture/profile/approve` | явное утверждение точных ссылок profile/qualification для executor |
| POST | `/api/admin/guild-capture/profile/revoke` | фиксируемый отзыв профиля с параметрами scope, requestId, approvalTransitionId и reason |

Просмотр читает текущее зафиксированное состояние V3. `limit` ограничивает каждый список 1–100 строками (по умолчанию 32). `hasMore` сообщает о сокращении списков. Запас отражает последнее зафиксированное значение. Ответ не содержит учётных данных и токенов аренды. Отзыв профиля закрывает затронутые разрешения на управление, а арена остаётся занятой до очистки.

Для стали используется resource 1, для дерева — 2. Предпросмотр проверяет манифест мира, занятость клетки и лимит в 32 форпоста. Он возвращает команду для текущего мира, границы и версии состояния. Сохраните команду и хеш и после проверки явно задайте `confirmed:true`. Для другого оператора, изменённой команды или нового состояния нужен свежий предпросмотр. Точный повтор возвращает прежний результат.

Для отмены возьмите `scope`, `expectedHeadId` (поле `headId` в ответе) и кампанию из одного снимка состояния. Сохраните запрос с UUID и причиной длиной до 500 символов. Сервер отклоняет устаревшее состояние и отмену после зафиксированного старта. Точный повтор возвращает прежнюю квитанцию. Подготовленная арена остаётся занятой до подтверждения очистки. Отмена сохраняет владельца и запас и не вводит штрафную задержку для игроков.

В том же снимке есть поля `frozen` и `freezeTransitionId`. Отправьте сохранённую команду заморозки с наблюдёнными границей и версией состояния. Пока захват заморожен, сервер отклоняет новые кампании, старт и применение ещё не принятого итога. Планировщик отменяет незапущенные кампании. Зафиксированные итоги сохраняются. Возобновление требует завершения всех активных кампаний.

Для остановки начатого боя отправьте сохранённую команду с наблюдёнными границей, версией состояния и ID боя. Сервер связывает остановку с зафиксированным Start, отзывает разрешения и бронирование, сохраняет владельца и запас. Назначенный арбитр получает остановку и подтверждает выход в главное меню и очистку. После этого арена освобождается. Точный повтор возвращает прежнюю квитанцию. Уже зафиксированный Final сохраняется.

Для V3 действие `activate-guild-domain-format` в существующем `/api/admin/outposts/preview` возвращает привязанную к текущему cut команду; подтверждение выполняется через существующий `/api/admin/outposts/activate-domain-format`. Для Product по-прежнему нужны квалифицированная сборка и допуск сервера. Поддержка панели и игровая приёмка ещё впереди.
