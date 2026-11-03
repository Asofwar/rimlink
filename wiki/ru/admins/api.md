---
pageId: admins/api
contentRevision: 1
---

# Справочник REST API

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

REST доступен на том же сервере. Административные маршруты начинаются с /api/admin и требуют Authorization: Bearer &lt;TOKEN&gt;. Читай с минимальным scope. JSON поля сериализуются в camelCase. Методы POST/PUT/DELETE меняют состояние; примеры ниже — контракт, не приглашение выполнить их на живом сервере.

Пример чтения: `curl --header "Authorization: Bearer <TOKEN>" https://play.example.org:25560/api/admin/status`. Не вставляй реальный токен в общедоступную команду. Cookie-вход панели дополнительно требует CSRF для мутаций; предпочитай bearer для автоматизации.

При отказе сохрани HTTP status и тело ошибки. 401 означает отсутствие допустимой аутентификации, 403 — недостаточные права/защиту запроса; 404 — отсутствующий объект/выключенную поверхность. Конфликты проверяются по ответу операции. Для influence читай setting, передавай requestId, expectedVersion и context; на InfluenceConflict обнови setting, не выдумывай новую версию.

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

Вход — DTO из таблицы выше либо query/header; «—» означает отсутствие тела и дополнительных параметров (path-параметры остаются в адресе). 200 без объекта означает пустой успешный ответ. JSON использует camelCase. Все admin-операции дополнительно могут вернуть 401 (токен), 403 (scope/CSRF), 400 (валидация/предусловие) и 404 (сущность); ошибки — ProblemDetails. Не все отказы повторяемы. D — DeliveryError: {code,messageKey,args,retryable}, 400/401/404/409/413/429/503/507, Retry-After: 60 при 429.

| Операция | Вход | Успех | Дополнительные отказы |
|---|---|---|---|
| `POST /api/panel/login` | `LoginBody` | `{expiresAt} + Set-Cookie` | `401 / 403 / 404 / 429` |
| `POST /api/panel/logout` | `—` | `200` | `403 / 404` |
| `GET /api/panel/session` | `—` | `{expiresAt}` | `401 / 404` |
| `GET /api/public/status` | `—` | `PublicStatus` | `404` |
| `GET /api/admin/events` | `after=epoch:seq; Last-Event-ID` | `SSE: id/event/data; gap` | `—` |
| `GET /api/admin/players` | `q; limit=50 (1..200); offset=0` | `{total,items: PlayerRow[]}` | `400` |
| `GET /api/admin/players/{id:long}` | `—` | `PlayerCard (flattened)` | `404` |
| `POST /api/admin/players/{id:long}/role` | `RoleBody` | `200` | `400 / 404` |
| `POST /api/admin/players/{id:long}/economy` | `EconomyFreezeBody` | `200` | `400 / 404` |
| `POST /api/admin/players/{id:long}/rename` | `RenameBody` | `200` | `400 / 404` |
| `GET /api/admin/economy/wallet/{id:long}` | `—` | `{balance}` | `404` |
| `POST /api/admin/saveall` | `—` | `200` | `—` |
| `GET /api/admin/status` | `—` | `AdminStatus` | `—` |
| `GET /api/admin/config` | `—` | `ServerStatus.Config (redacted)` | `—` |
| `GET /api/admin/backups` | `—` | `{name,size,createdAt}[]` | `—` |
| `POST /api/admin/backups` | `—` | `{name,size}` | `—` |
| `GET /api/admin/backups/{name}` | `—` | `application/octet-stream` | `400 / 404` |
| `GET /api/admin/chat` | `playerId; before; after; limit=100` | `ChatLine[]` | `400` |
| `GET /api/admin/players/online` | `—` | `OnlinePlayer[]` | `—` |
| `POST /api/admin/players/{id:long}/kick` | `KickBody` | `JSON string` | `400 / 404` |
| `POST /api/admin/players/{id:long}/mute` | `MuteBody` | `200` | `400 / 404` |
| `DELETE /api/admin/players/{id:long}/mute` | `—` | `200` | `404` |
| `GET /api/admin/players/{id:long}/saves` | `—` | `{version,size,gameTick,isCurrent,createdAt,devMode,wealthTotal}[]` | `—` |
| `POST /api/admin/players/{id:long}/saves/{version:int}/rollback` | `—` | `200` | `404` |
| `POST /api/admin/players/{id:long}/reset` | `—` | `200` | `400 / 404` |
| `GET /api/admin/bans` | `—` | `{id,accountId,steamId,steamIdText,ip,reason,expiresAt,createdAt,playerName}[]` | `—` |
| `POST /api/admin/bans` | `BanBody` | `{id}` | `400 / 404` |
| `DELETE /api/admin/bans/{id:long}` | `—` | `200` | `404` |
| `GET /api/admin/archive` | `state=archived` | `ArchivedRow[]` | `400` |
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
| `GET /api/admin/economy/lots` | `playerId` | `LotView[]` | `—` |
| `POST /api/admin/economy/refund/lot/{id:long}` | `—` | `200` | `404` |
| `POST /api/admin/economy/refund/parcel/{id:long}` | `—` | `200` | `404` |
| `POST /api/admin/economy/freeze` | `FreezeBody` | `200` | `—` |
| `GET /api/admin/economy/stats` | `—` | `{silverInWallets,openLots,pendingParcels,tradesToday,frozen,freezeReason,storageFeesCollected}` | `—` |
| `GET /api/admin/raids` | `playerId` | `{id,attackerPlayerId,attackerName,targetPlayerId,targetName,state,pawnMode,timing,lootCap,createdAt,landAtTick,landedTick,resolvedAt,expiresAt}[]` | `—` |
| `GET /api/admin/trades` | `playerId` | `TradeView[]` | `—` |
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
| `GET /api/public/profiles/{id:long}` | `—` | `PublicProfileDto` | `404` |
| `GET /api/admin/profiles/{id:long}` | `—` | `PublicProfileDto` | `404` |
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
