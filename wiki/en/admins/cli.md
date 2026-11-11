---
pageId: admins/cli
contentRevision: 12
---

# CLI reference

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

rimlink-tools has four roots: backup, restore, modpack and admin. Local backup/restore operates on disk; admin and modpack publication operations contact the server.

Read-only example: `rimlink-tools admin status --server https://play.example.org:25560 --token <TOKEN>`. Tokens in arguments can appear in shell history and process listings: never save a real-secret command in tickets or screenshots. --insecure trusts an unverified certificate and is suitable only for isolated tests.

Use the appropriate root's --help for exact arity. Every admin command is listed below; modpack and disk backup commands are explained in the linked guides. IDs are numeric; CLI decimal parsing uses invariant culture (a dot).

## A safe first CLI session {#cli-read-first}

Inspect --help, HTTPS endpoint and certificate trust. Remote admin commands require --server and --token; local backup/restore use --data without HTTP. Read status/players, resolve a persistent playerId, then inspect player/actions before mutating.

For example, admin status → admin players → admin player 42 → admin actions is read-only. Supply operator address/secrets outside shared chat. Wallet delta is required despite ambiguous help brackets; negative values debit funds. Never test syntax with a real compensation.

The groups below retain all commands. Before an irreversible operation record target, reason, backup and expected result, then verify response/audit.

## Limits and safety {#limits}

The current wallet command requires a delta despite the short help describing it as optional: read balances through GET economy/wallet/{id} or player details. raids cancel supports --return for returning pawns. A CLI/HTTP error does not prove an operation had no effect: inspect state before retrying.

## See also {#related}

- [REST API reference](./api.md)
- [Managing modpacks](./modpacks.md)
- [Backup and recovery](./backup-recovery.md)

## All admin commands {#reference}

Each line follows `rimlink-tools admin`; `--server URL --token TOKEN` is required, `--insecure` is optional for tests only. `<...>` is required, `[...]` optional, `|` a choice. Quote multi-word reasons. Permissions and effects match the corresponding REST operation; the API table gives minimum scopes.

```text
actions
archive <playerId>
archived [archived|restored|all]
auction cancel <id> [reason]
auctions
backup
backup-get <name> <file>
backups
ban <playerId> [days] [reason]
bans
broadcast <text>
chat [playerId]
colonies [playerId]
colony revoke <ownerPlayerId> <memberPlayerId>
config
economy freeze [reason]|unfreeze|stats
forbid list|add <def...>|remove <def...>
incidents list [playerId]|cancel <id> [reason]
influence
influence-archive-disable <archiveId> <settlementId> <requestId> <worldId> <generation> <allianceId> <version>
influence-archive-setting <archiveId>
influence-color <allianceId> <worldId> <paletteIndex> <requestId>
influence-disable <settlementId> <requestId> <worldId> <generation> <allianceId> <version>
influence-setting <settlementId>
kick <playerId> [reason]
live list [playerId]|end <id> [reason]
lots [playerId]
modpack
mute <playerId> <minutes>
online
parcels <playerId>
pardon <banId>
peaceful <playerId> on|off
player <playerId>
player-freeze <playerId> [reason]
player-unfreeze <playerId>
players [query]
procurement list [page]|cancel <id> [reason]
raids list [playerId]|cancel <id> [reason] [--return]|freeze [reason]|unfreeze|stats
recreateworld
refund lot|parcel <id>
removals <playerId>
rename <playerId> <name> [faction]
reset <playerId>
restore-colony <archiveId>
role <playerId> player|moderator|admin
rollback <playerId> <version>
saveall
saves <playerId>
say <author> <text>
shop <playerId>
shutdown <seconds> [reason]
status
trade cancel <id> [reason]
trades [playerId]
unmute <playerId>
wallet <playerId> <+n|-n> [reason]
```

`wallet` requires delta: despite brackets in current --help, omitting it is not a balance query. `raids cancel --return` requests pawn return; it is not arbitrary compensation. Read an influence setting first: requestId and context/version prevent unsafe retries and stale intent. `reset`, `recreateworld`, rollback and restore change/destroy data: back up and obtain confirmation first.

### Local commands and modpack

```text
rimlink-tools backup --data <dir> [--out <archive.tar.zst>]
rimlink-tools restore --data <dir> --from <archive.tar.zst>
rimlink-tools modpack import --from <ModsConfig.xml> [--mods-dir <folders...>] [--dry-run]
rimlink-tools modpack defs --mods-dir <folders...> [--dry-run]
rimlink-tools modpack configs import --config-dir <dir> --mods-dir <folders...> --mod <packageIds...> [--dry-run]
rimlink-tools modpack configs import --clear [--dry-run]
rimlink-tools modpack configs list --server <URL> --token <TOKEN>
rimlink-tools modpack package requirements --output <new-file> --server <URL> --token <TOKEN>
rimlink-tools modpack package pack --source <prepared-dir> --requirements <file> --output <new-dir>
rimlink-tools modpack package publish --from <dir> --confirm-redistribution [--confirm-public] [--confirm-shared-settings] --server <URL> --token <TOKEN>
rimlink-tools modpack package status --server <URL> --token <TOKEN>
rimlink-tools modpack package unpublish --server <URL> --token <TOKEN>
```

Without --dry-run, import/defs/configs import require --server/--token; HTTP commands support --insecure. Backup is safe while running; restore requires a stopped server. Pack is offline and uses a separately prepared redistributable collection, never the game root; output paths must be new. configs import shares selected mods below Rimlink: inspect files for secrets. requirements/status read the server; publish/unpublish change package availability and consent flags must not be supplied automatically.

### Outposts

`outposts inspect [--player ID] [--operation ID] [--limit N] [--cursor VALUE]`

`outposts preview|initialize|activate-domain-format|resolve --request FILE`

`outposts manifest inspect ID|upload FILE|confirm --request FILE`

These are remote REST commands. Retain the original command and explicitly set confirmed=true after review. The CLI does not silently re-preview/retry or open a running database. See the [guide](./passive-outposts.md) for bounds and recovery.

### Guild capture {#guild-capture}

```text
guild-capture inspect [limit]
guild-capture place preview FILE
guild-capture place FILE
guild-capture cancel FILE
guild-capture freeze FILE
guild-capture abort FILE
guild-capture profile stage KIND FILE
guild-capture profile approve FILE
guild-capture profile revoke FILE
```

Run these through `rimlink-tools admin` with the server URL and admin token. `inspect` reads the committed state and Product admission. Placement, pre-Start cancellation, approval and revocation take a retained JSON request object. Keep the same file for an exact retry. Staging sends a binary document with its protocol kind number (1–7). See the [REST reference](./api.md#guild-capture-setup) for request fields and limits.

For placement, pass a JSON draft with `tile`, `resource` and `reason` to `place preview`. Save the response, add `"confirmed": true` after review and pass that file to `place`.

For `freeze`, pass a retained JSON object with the current `scope`, `expectedHeadId`, a UUID `requestId`, a reason and `frozen` set to true to freeze or false to reopen.

For `abort`, pass the current `scope`, `expectedHeadId`, a UUID `requestId`, the started `battleId` and a reason in the retained JSON object.
