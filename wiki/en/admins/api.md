---
pageId: admins/api
contentRevision: 1
---

# REST API reference

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

REST is hosted on the same server. Administrative routes start at /api/admin and require Authorization: Bearer &lt;TOKEN&gt;. Read with the least scope. JSON fields use camelCase. POST/PUT/DELETE mutate state; examples below describe the contract, not permission to run against production.

Read example: `curl --header "Authorization: Bearer <TOKEN>" https://play.example.org:25560/api/admin/status`. Never put a real token in a public command. Panel-cookie mutations additionally require CSRF; prefer bearer authentication for automation.

On refusal, retain HTTP status and error body. 401 means missing valid authentication; 403 means insufficient rights/request protection; 404 means a missing object/disabled surface. Interpret conflicts using the operation response. For influence, read the setting and submit requestId, expectedVersion and context; on InfluenceConflict refresh the setting rather than inventing a version.

## Limits and safety {#limits}

Bots do not need unrestricted admin tokens. X-Rimlink-Actor is only an audit label. SSE /events excludes private chat, wallets and parcels. Delivery bootstrap uses separate short-lived access; public status/health/metrics do not require an admin token.

## See also {#related}

- [TLS, authentication and permissions](./security-auth.md)
- [CLI reference](./cli.md)
- [Monitoring and event feed](./monitoring.md)

## All HTTP operations {#reference}

`read`, `chat`, `moderate`, `admin` are minimum scopes. `admin` grants all; `moderate` grants read but not chat. Special routes have separate conditions below. gRPC and static files are outside this HTTP inventory. Trailing / is normalized.

| Method | Route | Access |
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

### Request bodies and responses

JSON fields use camelCase versions of the names below; `?` permits null. Duplicate DTO names belong to their route source; ReasonBody/cancel uses reason. GET filters go in the query, not JSON. Important queries: players q/limit/offset; chat playerId/before/after/limit; actions limit/before; archive state; lots/raids/trades/live/colonies playerId; procurement page; events after or Last-Event-ID.

| DTO | Fields |
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

Operations without a Body parameter need no JSON (for example saveall, backup, reset, rollback). Successful lists return arrays or pages; status/config return objects, wallet returns balance, backup name/size, ban id, procurement cancellation closed. Backup download returns a binary archive; SSE uses text/event-stream. Distinguish 404/409 using the response; do not blindly replay mutations.

Public status/profile return 404 with Panel.Enabled/Public disabled. /health and /metrics are unauthenticated: restrict them at the network. /api/panel requires the panel and Admin:ApiToken; login takes {token}, returns expiresAt and a Secure HttpOnly SameSite=Strict cookie. Panel POST requires X-Rimlink-Panel/Origin protection; invalid credentials give 401 and IP lockout 429. Integrations should use bearer tokens, not cookies.

Delivery: capabilities is public; access checks serverPassword (not a Steam/password account login) and issues a short-lived bearer. manifest/settings/archive require that bearer for the package id, not an admin token. Archive supports one Range and ETag/If-Range (200/206/416); archive upload is binary ZIP, other bodies are JSON. Prefer the package CLI for publishing: redistribution consent is mandatory. Delivery errors include 401/404/409/413/429/503/507; 429 includes Retry-After.

### Per-operation contracts

Input names a DTO above or query/header; “—” means no body or extra parameters (path parameters remain in the URL). 200 without an object denotes an empty successful response. JSON uses camelCase. Every admin operation can additionally return 401 (token), 403 (scope/CSRF), 400 (validation/precondition), or 404 (entity), using ProblemDetails. Do not retry all failures. D denotes DeliveryError: {code,messageKey,args,retryable}, statuses 400/401/404/409/413/429/503/507, Retry-After: 60 on 429.

| Operation | Input | Success | Additional failures |
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

Pagination: players returns total/items with offset; chat uses before/after IDs (after is ascending, before newest first); actions uses before ID; procurement is zero-based. Colonies (200) and auctions (500) are capped without a cursor: a missing row is not proof of absence. SSE prefers Last-Event-ID over after; refresh state on gap. Cookie POST/PUT/DELETE require `X-Rimlink-Panel: 1`; Origin must be absent or match request scheme/host/port.

### Delivery DTOs

| DTO | JSON fields |
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

Delivery JSON is case-sensitive; unknown/duplicate fields are rejected, maximum depth 32. Never log access tokens. Safe publication order: requirements → local package → UploadOffer → PUT ZIP → PublishPackage with expected revision/hash; 409 requires reviewing the current catalog, not replaying stale hashes.
