---
pageId: admins/api
contentRevision: 13
---

# REST API reference

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

REST is hosted on the same server. Administrative routes start at /api/admin and require Authorization: Bearer &lt;TOKEN&gt;. Read with the least scope. JSON fields use camelCase. POST/PUT/DELETE mutate state; examples below describe the contract, not permission to run against production.

Read example: `curl --header "Authorization: Bearer <TOKEN>" https://play.example.org:25560/api/admin/status`. Never put a real token in a public command. Panel-cookie mutations additionally require CSRF; prefer bearer authentication for automation.

On refusal, retain HTTP status and error body. 401 means missing valid authentication; 403 means insufficient rights/request protection; 404 means a missing object/disabled surface. Interpret conflicts using the operation response. For influence, read the setting and submit requestId, expectedVersion and context; on InfluenceConflict refresh the setting rather than inventing a version.

## Integration: start read-only and retry correctly {#api-client}

Begin with GET /api/admin/status using a separate read Bearer. Check HTTP status before parsing JSON: 401 is authentication, 403 is scope/CSRF, not a network timeout. Keep SteamIDs as strings rather than JavaScript Number. Follow per-operation pagination/caps below.

A mutation with a lost response may already have executed. Do not blindly replay wallet/refund/reset POSTs as new requests. Reconcile audit/current state first; where requestId/expectedVersion/context exist, preserve original intent. Game gRPC idempotency does not make every REST POST idempotent.

SSE events notify; they are not a database. Refresh state after gap. Minimize scope and never log Authorization/cookies or send private payloads to analytics.

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

Input names a DTO, query/header or “—” for no body; path parameters remain in the URL. 200 without an object is an empty success. JSON uses camelCase. Handle HTTP status and Content-Type first: an invalid/missing bearer or expired cookie returns an empty 401; cookie-CSRF returns an empty 403. Scope refusal and caught service RpcException normally use ProblemDetails; individual 400/404 responses can be empty or a JSON string. There is no universal JSON error body. D denotes DeliveryError: {code,messageKey,args,retryable}, statuses 400/401/404/409/413/429/503/507, Retry-After: 60 on 429.

| Operation | Input | Success | Additional failures |
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

## Response schemas {#response-schemas}

These are JSON fields from actual REST projections, not internal protobuf/C# record nesting. A type suffix `?` permits null, `[]` denotes an array, UTC is an ISO 8601 string ending in Z. int/int64/uint64 are JSON numbers; do not round 64-bit IDs in JavaScript: use a lossless JSON parser or preserve raw text. Prefer string steamId/steamIdText for Steam IDs. String status casing is significant; OnlinePlayer.role is numeric, directory role is player/moderator/admin text. ModpackBody.kind is a string enum; delivery EntryKind is numeric. Do not transfer enum assumptions between APIs.

| Schema | JSON |
|---|---|
| [PublicStatus](#response-schemas) | `serverName, description, version, certificateFingerprint, address: string; protocolVersion, playersOnline, maxPlayers: int; worldExists, passwordRequired, steamLogin, passwordLogin: bool; modpack: PublicModpackEntry[]; players: PublicPlayer[]?; world: PublicWorld` |
| `PublicModpackEntry` | `position: int; packageId, version, url, kind: string; workshopId: uint64` |
| `PublicPlayer / PublicWorld` | `PublicPlayer: {name, factionName: string}; PublicWorld: {settlements, tradesToday, raidsResolvedToday, liveActive: int; economyFrozen, raidsFrozen: bool}` |
| `PlayerRow` | `id, accountId: int64; name, factionName, role: string; steamId, login: string?; online, peaceful, banned: bool; lastSeenAt, mutedUntil: UTC?; createdAt: UTC` |
| `PlayerCard` | `all PlayerRow fields at root; lastIp: string?; session: {ip: string; since: UTC}?; settlements: {id: int64; tile: int; name: string}[]; walletBalance, wealthTotal: int64; firstSaveAt: UTC?; currentSave: {version: int; size, gameTick: int64; createdAt: UTC}?; activeBans: {id: int64; reason: string; expiresAt: UTC?}[]; visitsClosed: bool; peacefulOffAt, economyFrozenAt: UTC?; blocks: string[]?; economyFreezeReason: string?` |
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

PlayerCard is flat: there is no player property. Read-only GET players returns only id/name/factionName/online/role; other fields are **omitted**, not replaced by null. Full rows and cards require moderate/admin. PublicStatus.players is null when public names are hidden. AdminStatus may return null for economy/raids/saves/backups on a subsystem failure; numeric counters fall back to 0, not proof of an empty database.

GET config returns only server/tls/auth/backup/saves/economy/raids/live/panel/hygiene/admin sections with camelCase properties. Secret serverPassword/steamWebApiKey/apiToken values become "set" or ""; admin.tokens contains only {name,scopes}, without token. It is not a full Options export: Social/Incidents, for example, are absent. See [configuration](./configuration.md#reference) for values and semantics. SSE and binary responses are not JSON DTOs; input DTOs and delivery schemas are defined above.

Safe read-only directory example (fictional data):

~~~json
{"total":1,"items":[{"id":42,"name":"Nova","factionName":"Cedar","online":true,"role":"player"}]}
~~~

Status-first client for a JSON GET; this is not an automatic POST retry policy:

~~~js
const response = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
const text = await response.text();
if (!response.ok) {
  throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
}
const data = text ? JSON.parse(text) : null;
~~~

### Outpost administration

Exact commands, sidecars, bounds and no-replay rules are described in the [guide](./passive-outposts.md). GET does not mutate authority; preview is advisory.

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

## Guild capture setup {#guild-capture-setup}

These operator routes require a Bearer token/panel session and the `admin` scope; an ordinary OFF server does not admit guild operations. Uploading a document stores content-addressed bytes without approving a profile. Approval checks the complete profile, inventory, template, compatibility and qualification closure through the common authority.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/admin/guild-capture` | read-only authority and admission status, outposts, active campaigns and battles, available executor ID |
| POST | `/api/admin/guild-capture/cancel` | pre-Start cancellation: scope, requestId, campaignId, expectedHeadId, reason |
| POST | `/api/admin/guild-capture/freeze` | change guild capture admission: scope, requestId, expectedHeadId, frozen, reason |
| POST | `/api/admin/guild-capture/abort` | stop a started battle: scope, requestId, expectedHeadId, battleId, reason |
| POST | `/api/admin/guild-capture/place/preview` | read-only placement check: tile, resource, reason |
| POST | `/api/admin/guild-capture/place` | commit the saved preview: command, intentDigest, confirmed=true |
| POST | `/api/admin/guild-capture/profile/stage/{kind}` | one binary document body, at most 64 MiB; individual kinds have smaller bounds |
| POST | `/api/admin/guild-capture/profile/approve` | explicit approval of exact profile/qualification references for an executor |
| POST | `/api/admin/guild-capture/profile/revoke` | durable profile revocation with scope, requestId, approvalTransitionId and reason |

Inspection reads the current committed V3 cut. `limit` bounds each list to 1–100 rows (default 32). `hasMore` signals truncation. Stock reflects the last committed value. The response omits credentials and lease tokens. Profile revocation closes affected control grants and preserves battle occupancy until cleanup.

Placement uses resource 1 for steel and 2 for wood. Preview validates the world manifest, tile occupancy and the 32-outpost limit. It returns a command for the current world, boundary and head. Save the returned command and digest and explicitly set `confirmed:true` after review. A different actor, edited command or advanced state requires a fresh preview. An exact retry returns the original result.

For cancellation, take `scope` and `expectedHeadId` (the returned `headId`) from the same inspection as the campaign. Save the request with a UUID and a reason up to 500 characters. The server refuses a stale head or a committed Start. Repeating the identical request returns the existing receipt. A prepared arena remains occupied until cleanup proof. Cancellation leaves ownership and stock intact and applies no player cooldown.

The same inspection reports `frozen` and `freezeTransitionId`. Submit the saved freeze command with the observed scope and head. While frozen, the server refuses new campaigns, Starts and unapplied Finals. The scheduler cancels campaigns that have yet to start. Existing committed outcomes are retained. Reopening requires all active campaigns to reach a terminal state.

To stop a started battle, submit the saved abort command with the observed scope, head and battle ID. The server records the abort against the committed Start, closes grants and reservations and leaves ownership and stock intact. The assigned executor receives the abort and must prove Entry and cleanup before the arena becomes available. An exact retry returns the same receipt. An already committed Final is preserved.

For V3, `activate-guild-domain-format` in the existing `/api/admin/outposts/preview` returns a command bound to the current cut; confirm through the existing `/api/admin/outposts/activate-domain-format`. Product still requires a qualified build and runtime admission. Panel parity and game acceptance are pending.
