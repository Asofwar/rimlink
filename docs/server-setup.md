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
under `Economy` (defaults): `Enabled` true, `MaxParcelBytes` 4194304, `MaxItemsPerParcel` 200, `MaxPawnsPerParcel` 10,
`MaxLotsPerPlayer` 20, `MaxPendingParcels` 50, `LotLifetimeDays` 30, `MarketFeePercent` 0 (fee goes to the bank account,
player id 0), `SendCooldownSeconds` 5. A save uploaded with a game tick older than the player's last transfer is refused
(`SAVE_STALE`): that file would bring back what was given away. An admin rollback behind a transfer is allowed and
logged as such.

## 6. Administration

- In game, moderators and admins type `/help` in chat.
- REST API under `/api/admin/*`, bearer token `Admin:ApiToken` (leave it empty to disable the API). The CLI wraps it:
  `rimlink-tools admin online|kick|ban|pardon|bans|mute|unmute|saves|rollback|reset|broadcast|shutdown|recreateworld|modpack|actions --server URL --token T [--insecure]`.
- Economy: chat `/wallet <player> [+n|-n] [reason]`, `/parcels <player>`, `/lots [player]`, `/refund lot <id>`,
  `/refund parcel <id>` (undelivered gift back to its sender), `/economy freeze [reason]|unfreeze|stats`; the same via
  `rimlink-tools admin wallet|parcels|lots|refund|economy …` and REST `/api/admin/economy/*`.
- Every admin action is logged (`/actions`).

## 7. Backups

- Automatic: `Backup:IntervalHours` (default 24, 0 = off), `Backup:Keep` archives kept in `backups/`.
- Manual, safe while running: `rimlink-tools backup --data <dir> [--out file.tar.zst]`.
- Restore (server stopped): `rimlink-tools restore --data <dir> --from file.tar.zst`.

## 8. Monitoring

`GET /health` (used by the Docker health check) and Prometheus metrics at `GET /metrics` (economy: `rimlink_trades_total{kind}`, `rimlink_silver_moved_total`, `rimlink_parcels_pending`, `rimlink_economy_errors_total{code}`). Logs go to the console
and `logs/server-<date>.log`.

## 9. Limits (built in)

Player limit `Server:MaxPlayers`, one settlement per player (`Server:MaxSettlementsPerPlayer`), 5 chat messages per
5 s, 1 save upload per 30 s, saves up to 64 MB compressed, 5 unauthenticated connections per IP, 30 s to log in.
