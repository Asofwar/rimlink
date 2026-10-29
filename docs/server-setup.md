# Rimlink server setup

The server is a single .NET 10 process. It needs one open TCP port (25560 by default), a data directory and,
for Steam logins, a Steam Web API key. Everything shared (accounts, world, settlements, chat, modpack, saves)
lives in the data directory.

## 1. Run it

**Docker (Linux):**

```bash
mkdir -p rimlink/data && cd rimlink
curl -O https://raw.githubusercontent.com/Asofwar/rimlink/main/deploy/docker-compose.yml
printf 'RIMLINK_API_TOKEN=%s\nSTEAM_WEB_API_KEY=%s\nADMIN_STEAM_ID=%s\n' "$(openssl rand -hex 32)" "<key>" "<your steamid64>" > .env
docker compose up -d
docker compose logs -f
```

**Bare (Windows or Linux):** unpack the release, copy `appsettings.example.json` next to the executable as
`appsettings.Local.json` (or set `Rimlink__Section__Key` environment variables) and run `rimlink-server`.

The data directory layout: `rimlink.db` (SQLite), `saves/<playerId>/<n>.rws.zst`, `tls/`, `logs/`, `backups/`,
`archive/` (data of reset players and recreated worlds), `server.lock` while running.

## 2. TLS

The server refuses to start without an HTTPS endpoint. Two options:

- **Own certificate** (recommended with a domain): `Tls:CertificatePath` and `Tls:KeyPath` pointing at PEM files,
  e.g. Let's Encrypt `fullchain.pem` / `privkey.pem`. Restart after renewal.
- **Self-signed** (default): generated on first start into `tls/server.pfx`. Players see the certificate fingerprint
  the first time they connect and confirm it; the client remembers it (pinning). If you replace the certificate,
  tell players, or they get a "certificate changed" warning.

## 3. Logins

- **Steam** (default): set `Auth:SteamWebApiKey` to a key from https://steamcommunity.com/dev/apikey. Accounts are
  created on first login; the player name starts as `Player<last 4 digits of SteamID>` and can be changed in game
  (`/rename`). Family-shared copies are tied to the owner's SteamID for bans.
- **Login + password** (fallback): `Auth:AllowPassword=true` enables `Register` and password logins. Passwords are
  stored as Argon2id; player names are checked server-side.

The first admin is whoever logs in with a SteamID listed in `Auth:AdminSteamIds` (or a login in `Auth:AdminLogins`).
Admins can promote others: `/role <player> moderator|admin`.

## 4. World

Only admins can log in until the world exists. The admin creates the planet in the game as usual; the client sends
the generation parameters to the server and every other player generates the same planet from them.
`/recreateworld` (or `rimlink-tools admin recreateworld`) archives everything and starts over.

## 5. Modpack

Players are checked by `packageId` (+ `modVersion` when pinned) only. Set the list from the admin's own game
(client button) or from a RimWorld install:

```bash
rimlink-tools modpack import --from "<Config>/ModsConfig.xml" \
  --mods-dir "<RimWorld>/Data" "<RimWorld>/Mods" "<steamapps>/workshop/content/294100" \
  --server https://host:25560 --token <Admin:ApiToken> --insecure
```

An empty list disables the check. Entries with `kind=allowed` may be missing on the client (UI mods and the like).

### Modpack defs (economy)

Items and pawns travel as game data; the server refuses blobs whose ThingDef / PawnKindDef is not in the modpack when the
def list is set (empty list = check off):

```bash
rimlink-tools modpack defs --mods-dir "<RimWorld>/Data" "<RimWorld>/Mods" "<steamapps>/workshop/content/294100" \
  --server https://host:25560 --token <Admin:ApiToken> --insecure
```

## 5a. Economy

Market board with escrow, server-side silver wallets, mailbox for offline recipients and direct transfers. Settings
under `Economy` (defaults): `Enabled` true, `MaxParcelBytes` 921600 (900 KB, below the 1 MB gRPC message limit), `MaxItemsPerParcel` 200, `MaxPawnsPerParcel` 10,
`MaxLotsPerPlayer` 20, `MaxPendingParcels` 50, `LotLifetimeDays` 30, `MarketFeePercent` 0 (fee goes to the bank account,
player id 0), `SendCooldownSeconds` 5. A save uploaded with a game tick older than the player's last transfer is refused
(`SAVE_STALE`): that file would bring back what was given away. An admin rollback behind a transfer is allowed and
logged as such.

## 5b. Raids, visits and the shop

Asynchronous raids: an attacker's pawns wait on the server until the target next plays, fight there as a vanilla raid
and the outcome comes back as parcels. Settings under `Raids` (defaults): `Enabled` true, `MaxRaiders` 10,
`MaxActiveRaidsPerAttacker` 1, `TargetCooldownHours` 8, `PairCooldownHours` 24, `NewbieProtectionDays` 3,
`MinColonyWealth` 20000, `LootCapPercent` 10, `LootCapSilver` 5000, `AllowKidnap` false, `WarnTicks` 60000,
`TimeoutDays` 3, `ExpireRealDays` 7, `PeacefulOffDelayHours` 24, `VisitHours` 12. Wealth and the colony's age come
from the save header: players who last saved before this version count as wealth 0 (protected) until their next save,
and their oldest save counts as their first. A raid expires after `ExpireRealDays` real days without a landing, and a
landed raid gets the same window again for the report; either way the attacker's Real pawns come home as a parcel.
Raids are refused while frozen (`/raids freeze`); visits and the shop keep working. Visits download the host's current
save to the visitor (only while a visit is open; a ticket nobody ended stops working after `VisitHours`); the shop lists
what a host priced in their colony and sells it from the visitor's copy, the host's game removes the sold things at its
next login.

## 5c. Live raids and visits

When both players are online, a raid or a visit can run live: the guest's pawns act on the host's live map and the guest
controls them from a mirrored copy, the server only relays frames between the two streams. Settings under `Live` (defaults):
`Enabled` true, `InviteSeconds` 60 (time the host has to answer an invitation), `ReconnectWaitMinutes` 5 (how long a session
waits for a dropped side), `GuestPauses` 10 and `GuestPauseSeconds` 60 (the guest's pause budget per session), `MaxFrameBytes`
262144, `DeltasPerSecond` 20, `OrdersPerSecond` 30 (frames above these limits are dropped with a notice), `HeartbeatTimeoutSeconds`
10, `SweepSeconds` 5. The host must be online and at their colony (`HOST_OFFLINE` otherwise); one live session or visit per
player (`LIVE_ACTIVE`); the raid rules of 5b (cooldowns, protection, peaceful mode, loot caps) apply to live raids, which hold a
raid row with the escrowed pawns. A declined or unanswered invitation falls back: the raid goes on as an asynchronous raid with
the timing the attacker chose, a visit's pawns come home by parcel. A lost guest's raid is handed to the host's AI after the wait;
a lost host leaves the session in `Ending` until the host's next login, which reports it (`pending_live_ends`); a session whose
host or guest never opened its stream (a crash before the snapshot, a server restart) ends the same way after the wait; an admin
can end any session (`live end`), and ending one that already waits for the host's report sends the escrowed pawns home as they
left. A guest may ask the host for a fresh snapshot twice in a burst and then once per ten seconds. Every pawn or thing movement
still ends as a mailbox parcel, as in 5a/5b.

## 6. Administration

- In game, moderators and admins type `/help` in chat.
- REST API under `/api/admin/*`, bearer token `Admin:ApiToken` (leave it empty to disable the API). The CLI wraps it:
  `rimlink-tools admin online|kick|ban|pardon|bans|mute|unmute|saves|rollback|reset|broadcast|shutdown|recreateworld|modpack|actions --server URL --token T [--insecure]`.
- Economy: chat `/wallet <player> [+n|-n] [reason]`, `/parcels <player>`, `/lots [player]`, `/refund lot <id>`,
  `/refund parcel <id>` (undelivered gift back to its sender), `/economy freeze [reason]|unfreeze|stats`; the same via
  `rimlink-tools admin wallet|parcels|lots|refund|economy …` and REST `/api/admin/economy/*`.
- Raids: chat `/raids list [player]|cancel <id> [--return] [reason]|freeze [reason]|unfreeze|stats`,
  `/peaceful <player> on|off` (no delay), `/shop <player>`, `/removals <player>`; the same via
  `rimlink-tools admin raids|peaceful|shop|removals …` and REST `/api/admin/raids/*` (cancel body
  `{"reason": "...", "returnPawns": true}`), `/api/admin/players/{id}/peaceful`, `/api/admin/shop/{id}[/removals]`.
  Cancelling a raid that has not landed returns the pawns to the attacker; a landed one stays with the defender's map
  unless `--return` sends the attacker's originals home as well (the copies on the defender's map are not touched).
- Live sessions: chat `/live list [player]`, `/live end <id> [reason]`; the same via `rimlink-tools admin live list [playerId]|end <id> [reason]`
  and REST `GET /api/admin/live[?playerId=]`, `POST /api/admin/live/{id}/end` (body `{"reason": "..."}`). Ending a running session makes
  the host report the guest's pawns as they stand (a live raid is not continued by the AI); ending one that is already waiting for
  that report returns the escrowed pawns to the guest instead.
- Every admin action is logged (`/actions`).

## 7. Backups

- Automatic: `Backup:IntervalHours` (default 24, 0 = off), `Backup:Keep` archives kept in `backups/`.
- Manual, safe while running: `rimlink-tools backup --data <dir> [--out file.tar.zst]`.
- Restore (server stopped): `rimlink-tools restore --data <dir> --from file.tar.zst`.

## 8. Monitoring

`GET /health` (used by the Docker health check) and Prometheus metrics at `GET /metrics` (economy: `rimlink_trades_total{kind}`, `rimlink_silver_moved_total`, `rimlink_parcels_pending`, `rimlink_economy_errors_total{code}`; raids: `rimlink_raids_total{outcome}`, `rimlink_raids_active`, `rimlink_visits_total`, `rimlink_shop_sales_total`; live sessions:
`rimlink_live_proposed_total{kind}`, `rimlink_live_sessions_total{kind,end_reason}`, `rimlink_live_frames_total{dir}`, `rimlink_live_active`). Logs go to the console
and `logs/server-<date>.log`.

## 9. Limits (built in)

Player limit `Server:MaxPlayers`, one settlement per player (`Server:MaxSettlementsPerPlayer`), 5 chat messages per
5 s, 3 save uploads per 30 s, saves up to 64 MB compressed, 5 unauthenticated connections per IP, 30 s to log in.
