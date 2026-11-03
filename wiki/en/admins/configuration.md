---
pageId: admins/configuration
contentRevision: 2
---

# Configuration reference

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Settings bind to root JSON sections. Environment overrides replace colons with double underscores and add Rimlink__: for example Rimlink__Server__Port=25560 and Rimlink__Auth__AdminLogins__0=admin. Arrays use numeric indices, not comma-separated strings.

Set the service/container environment and restart after changes: this guide does not promise hot reload. The table below records Options initializers, not a sample populated with secrets. deploy/appsettings.example.json is a useful template but not an exhaustive contract (SelfSignedPath, for example, is absent).

The complete key inventory follows below. Read the setting, not just its suffix: Hygiene:ArchiveAfterDays counts real days of absence, Hygiene:ArchiveMaxColonyDays counts colony game days, and NewbieProtectionDays counts real days since the first save. Ticks means game ticks and Bytes means bytes. Preserve units when transferring settings.

## How do I change settings without guessing units? {#config-method}

Find the exact key and its group explanation. JSON uses Section/Key; environment variables use Rimlink__Section__Key, with array indices. Preserve working settings and test one related change set on a test server. Options validation may refuse startup: fix its named value, not the database.

Examples are not exhaustive: the table is checked against Options classes, while mechanic constants are separate. Zero does not universally mean disabled: Backup:IntervalHours=0 disables scheduling, other fields differ. Do not promise behavior from Raids:TimeoutDays; it is declared but the current client uses a constant.

Verify behavior and the redacted /api/admin/config after applying changes. Redaction is not a reason to dump the full environment into tickets.

## Limits and safety {#limits}

Admin:ApiToken, Admin:Tokens:*:Token, Auth:SteamWebApiKey and Server:ServerPassword are secrets. FeedBufferSize: 100–100000, FeedPingSeconds: 1–300; gift/trade lifetimes and MaxOpenTrades must be >=1. Integrity thresholds are nonnegative; Freeze must not be below Warn except 0. Hygiene requires a 1–168-hour sweep, nonnegative age/wealth values (0 disables the corresponding filter; ArchiveAfterDays=0 disables archiving) and 4 strictly increasing tiers. Incident catalogs require unique IDs, valid Kind and positive multipliers; a nonempty list replaces the entire built-in catalog.

## See also {#related}

- [TLS, authentication and permissions](./security-auth.md)
- [Server gameplay policy](./gameplay-policy.md)

## All bindable keys {#reference}

This table follows Options declarations, not the sample JSON. `null` means unset; `""` is an empty string; `[]` is an empty array. `Protocol.Limits.*` refers to protocol constants: MaxParcelBytes = 921600, MaxItemsPerParcel = 200, MaxPawnsPerParcel = 10. Replace `*` with a zero-based index.

| Key | Type | Initial default |
|---|---|---|
| `Panel:Enabled` | `bool` | `true` |
| `Panel:Public` | `bool` | `true` |
| `Panel:PublicPlayerNames` | `bool` | `true` |
| `Panel:SessionHours` | `int` | `12` |
| `Panel:ServerName` | `string` | `""` |
| `Panel:Description` | `string` | `""` |
| `Panel:PublicAddress` | `string` | `""` |
| `Server:Port` | `int` | `25560` |
| `Server:DataDir` | `string` | `"./data"` |
| `Server:MaxPlayers` | `int` | `50` |
| `Server:ServerPassword` | `string?` | `null` |
| `Server:MaxSettlementsPerPlayer` | `int` | `1` |
| `Tls:Required` | `bool` | `true` |
| `Tls:CertificatePath` | `string?` | `null` |
| `Tls:KeyPath` | `string?` | `null` |
| `Tls:SelfSignedPath` | `string?` | `null` |
| `Auth:AllowPassword` | `bool` | `false` |
| `Auth:SteamWebApiKey` | `string?` | `null` |
| `Auth:AdminSteamIds` | `long[]` | `[]` |
| `Auth:AdminLogins` | `string[]` | `[]` |
| `Economy:Enabled` | `bool` | `true` |
| `Economy:MaxParcelBytes` | `int` | `Protocol.Limits.EconomyMaxParcelBytes` |
| `Economy:MaxItemsPerParcel` | `int` | `Protocol.Limits.EconomyMaxItemsPerParcel` |
| `Economy:MaxPawnsPerParcel` | `int` | `Protocol.Limits.EconomyMaxPawnsPerParcel` |
| `Economy:MaxLotsPerPlayer` | `int` | `20` |
| `Economy:MaxPendingParcels` | `int` | `50` |
| `Economy:LotLifetimeDays` | `int` | `30` |
| `Economy:MarketFeePercent` | `int` | `0` |
| `Economy:SendCooldownSeconds` | `int` | `5` |
| `Economy:GiftDecisionDays` | `int` | `7` |
| `Economy:TradeLifetimeDays` | `int` | `7` |
| `Economy:MaxOpenTrades` | `int` | `5` |
| `Economy:ServerWealthPercent` | `int` | `100` |
| `Economy:BlockOutflowUnderThreat` | `bool` | `true` |
| `Economy:StorageFeePercentPerDay` | `double` | `0.5` |
| `Economy:StorageFreeDays` | `double` | `3` |
| `Social:MaxBlocks` | `int` | `200` |
| `Raids:Enabled` | `bool` | `true` |
| `Raids:MaxRaiders` | `int` | `10` |
| `Raids:MaxActiveRaidsPerAttacker` | `int` | `1` |
| `Raids:TargetCooldownHours` | `int` | `8` |
| `Raids:PairCooldownHours` | `int` | `24` |
| `Raids:NewbieProtectionDays` | `int` | `3` |
| `Raids:MinColonyWealth` | `long` | `20000` |
| `Raids:LootCapPercent` | `int` | `10` |
| `Raids:LootCapSilver` | `long` | `5000` |
| `Raids:AllowKidnap` | `bool` | `false` |
| `Raids:WarnTicks` | `int` | `60000` |
| `Raids:TimeoutDays` | `int` | `3` |
| `Raids:ExpireRealDays` | `int` | `7` |
| `Raids:PeacefulOffDelayHours` | `int` | `24` |
| `Raids:VisitHours` | `int` | `12` |
| `Live:Enabled` | `bool` | `true` |
| `Live:InviteSeconds` | `int` | `60` |
| `Live:ReconnectWaitMinutes` | `int` | `5` |
| `Live:GuestPauses` | `int` | `10` |
| `Live:GuestPauseSeconds` | `int` | `60` |
| `Live:MaxFrameBytes` | `int` | `262144` |
| `Live:DeltasPerSecond` | `int` | `20` |
| `Live:OrdersPerSecond` | `int` | `30` |
| `Live:HeartbeatTimeoutSeconds` | `int` | `10` |
| `Live:SweepSeconds` | `int` | `5` |
| `Admin:ApiToken` | `string?` | `null` |
| `Admin:Tokens` | `List<ApiTokenOptions>` | `[]` |
| `Admin:FeedBufferSize` | `int` | `1000` |
| `Admin:FeedPingSeconds` | `int` | `15` |
| `Admin:Tokens:*:Name` | `string` | `""` |
| `Admin:Tokens:*:Token` | `string` | `""` |
| `Admin:Tokens:*:Scopes` | `string[]` | `[]` |
| `Integrity:DevMode` | `DevModePolicy` | `DevModePolicy.AdminsOnly` |
| `Integrity:WealthWarnPerDay` | `long` | `20000` |
| `Integrity:WealthFreezePerDay` | `long` | `100000` |
| `Integrity:StorytellerPolicy` | `StorytellerPolicy` | `StorytellerPolicy.Locked` |
| `Hygiene:ArchiveAfterDays` | `int` | `14` |
| `Hygiene:ArchiveMaxWealth` | `long` | `100000` |
| `Hygiene:ArchiveMaxColonyDays` | `int` | `60` |
| `Hygiene:SweepHours` | `int` | `6` |
| `Hygiene:WealthTiers` | `long[]?` | `null` |
| `Incidents:Enabled` | `bool` | `true` |
| `Incidents:MaxQueuedPerTarget` | `int` | `2` |
| `Incidents:BasePrice` | `double` | `100` |
| `Incidents:MinPrice` | `long` | `50` |
| `Incidents:WarnTicks` | `long` | `30000` |
| `Incidents:WarnFromLevel` | `int` | `3` |
| `Incidents:KindCooldownHours` | `double` | `1` |
| `Incidents:ExpireRealDays` | `double` | `7` |
| `Incidents:Catalog` | `List<IncidentCatalogEntry>` | `[]` |
| `Incidents:Catalog:*:Id` | `string` | `""` |
| `Incidents:Catalog:*:IncidentDef` | `string` | `""` |
| `Incidents:Catalog:*:FactionDef` | `string?` | `null` |
| `Incidents:Catalog:*:Kind` | `string` | `"Hostile"` |
| `Incidents:Catalog:*:PriceMultiplier` | `double` | `1` |
| `Incidents:Catalog:*:PodsMultiplier` | `double?` | `null` |
| `Saves:CompactOldVersions` | `bool` | `true` |

`Tls:SelfSignedPath=null` selects `<DataDir>/tls/server.pfx`. `Hygiene:WealthTiers=null` selects `[50000,150000,400000,1000000]`. Empty Panel:ServerName displays Rimlink; SessionHours is clamped to 1–168; Description is trimmed to 1000 characters. `Saves:CompactOldVersions=false` stops new delta creation without deleting existing deltas. `Server:ProtocolVersion` is derived, not configuration.

### Backup

| Key | Type | Default |
|---|---|---|
| `Backup:IntervalHours` | `int` | `24` |
| `Backup:Keep` | `int` | `7` |

IntervalHours <= 0 disables scheduling; Keep retains at least one newest archive. Manual backup does not replace a restore test.

### Section semantics

Server controls port, storage, capacity and shared server password, not an account password. Tls selects a PEM certificate/key pair or a persisted self-signed certificate. Auth allows password login and promotes Steam IDs/logins; SteamWebApiKey verifies Steam tickets.

Economy enables exchange and limits parcels (total bytes/items/pawns), lots/pending deliveries, lifetime, market fee and send rate. GiftDecisionDays and TradeLifetimeDays return escrow when a decision/trade expires. ServerWealthPercent includes server assets in wealth, BlockOutflowUnderThreat blocks outflow under threat; StorageFeePercentPerDay charges pawns/perishables after StorageFreeDays. Social:MaxBlocks caps a personal block list.

Raids controls availability, attacker/active-raid counts, target/pair cooldowns, newcomer protection and minimum wealth. LootCapPercent and LootCapSilver cap loot; AllowKidnap controls kidnapping; WarnTicks is an in-game warning, TimeoutDays is declared but not wired into behavior: the current client uses a constant 3 × 60000 game ticks, ExpireRealDays a real-time expiry, PeacefulOffDelayHours a delay leaving peaceful mode, VisitHours a visit expiry.

Live sets invite/reconnect waits, guest pause budget, frame bytes, delta/order rates and stream-silence detection. Admin configures the legacy panel token and named token/scopes, SSE replay capacity and ping interval. Integrity controls dev mode, wealth jumps and storyteller; 0 disables the corresponding threshold. Hygiene archives only colonies meeting absence/age/wealth criteria; 0 removes its filter (ArchiveAfterDays=0 disables the sweep).

Incidents enables paid events, per-target queue, BasePrice/MinPrice, warning from WarnFromLevel, cooldown by Kind and expiry. Catalog:* defines Id, IncidentDef, optional FactionDef, Kind (Hostile/Weather/Kind), PriceMultiplier and optional PodsMultiplier. Do not promise a def from an absent mod.
