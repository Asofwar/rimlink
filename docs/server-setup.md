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

- **Steam** (default): set `Auth:SteamWebApiKey` to a key from https://steamcommunity.com/dev/apikey (an ordinary
  Steam account's key is enough, no publisher key is needed). Accounts are
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
Expired raids and market lots are swept when the server starts and then every hour.
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
- Every admin action is logged (`/actions`) with who did it: `api` (the `Admin:ApiToken`), `api:<token name>` (a named
  token, see §6b), `panel` (the web panel), or the player who typed a chat command. A REST caller may add
  `X-Rimlink-Actor: <label>` (1–64 characters, no control characters, otherwise 400) and is logged as
  `api:<label>` / `api:<token name>:<label>`; the Discord bot sends `discord:<user> (<id>)`.
- Panel and integration routes (all under `/api/admin`, JSON):
  - players: `GET /players?q=&limit=50&offset=0` (name/faction substring, exact SteamID, login or `#id`; exact name
    first, then online), `GET /players/{id}` (card), `POST /players/{id}/role {"role": "moderator"}`,
    `POST /players/{id}/rename {"name": "...", "factionName": "..."}`, `GET /economy/wallet/{id}`; `steamId` is a JSON
    string there (17 digits do not fit a JavaScript number);
  - server: `POST /saveall`, `GET /status`, `GET /config` (secrets shown as `set`), `GET /chat?playerId=&before=|after=&limit=`
    (public channel only), `GET /actions?before=`;
  - backups: `GET /backups`, `POST /backups` (make one now; 409 while one runs), `GET /backups/{name}` (download).
  - names next to ids (`null` when no player has that id any more): `GET /raids` `attackerName`, `targetName`;
    `GET /live` `hostName`, `guestName`; `GET /economy/parcels/{id}` `fromName`, `toPlayerId`, `toName`
    (`GET /economy/lots` carries `sellerName` and `summary`, below).
  - CLI: `rimlink-tools admin players [query]|player <id>|role <id> <role>|rename <id> <name> [faction]|saveall|chat [playerId]|status|config|backups|backup|backup-get <name> <file>`.
- Event feed for integrations: `GET /api/admin/events` (Server-Sent Events, scope `read`). Each event has an id
  `<epoch>:<seq>`; a client resumes with `Last-Event-ID` and gets a `gap` event when the server restarted or more than
  `Admin:FeedBufferSize` (default 1000) events passed. A `ping` comes every `Admin:FeedPingSeconds` (default 15) without
  events. Direct messages, wallets, parcels and not-yet-landed raids never appear in the feed.
- Chat from outside the game: `POST /api/admin/chat {"author": "Mod", "text": "..."}` (scope `chat`; author up to 32
  characters, text under the game chat's rules, never run as a `/` command). Players see `[Discord] Mod: ...`;
  `rimlink-tools admin say <author> <text>` does the same. `GET /api/admin/chat` shows such lines with `kind` `external`
  and their `author`; `GET /api/admin/economy/lots` names each lot's seller and contents (`sellerName`, `summary`).

## 6a. Web panel

- `https://<host>:<port>/admin/` — log in with the `Admin:ApiToken` value. The browser keeps a session cookie
  (`HttpOnly`, `Secure`, `SameSite=Strict`) for `Panel:SessionHours` (default 12, 1–168); the token itself is not
  stored. Sessions live in memory: a server restart logs everyone out. 5 wrong tokens from one IP within 10 minutes
  lock that IP out until the 10 minutes pass (failures are logged with the IP).
- Without `Admin:ApiToken` the panel is off (404); named tokens never open it.
- `https://<host>:<port>/` — the public page: status, how to connect (address, login modes, the certificate
  fingerprint players compare with the trust window on first connect), the modpack with Workshop links, players
  online, world stats. `Panel:Public=false` turns it off, `Panel:PublicPlayerNames=false` hides the names,
  `Panel:ServerName`, `Panel:Description` and `Panel:PublicAddress` (shown address; empty = the host and port of the
  request) fill it in. `Panel:Enabled=false` turns off both pages.
- With the self-signed certificate browsers show a warning first. For a public panel use your own certificate
  (§2): `Tls:CertificatePath` / `Tls:KeyPath`.
- Safe to expose: the public page and its `/api/public/status` (no IPs, SteamIDs or account ids, cached for 15 s).
  `/metrics` stays open as before; keep it behind a firewall if you do not want it public.

## 6b. API tokens

`Admin:ApiToken` is the full-admin token (CLI, panel, old configs). Integrations get their own named tokens, each
revocable on its own and named in the audit log:

```json
"Admin": {
  "ApiToken": "long-random-string",
  "Tokens": [ { "Name": "discord", "Token": "another-long-random-string", "Scopes": [ "read", "chat", "moderate" ] } ]
}
```

- `Name`: `a-z`, `0-9`, `-`, up to 32, unique; `Token`: at least 24 characters, unlike any other; `Scopes`: any of
  `read`, `chat`, `moderate`, `admin`. A bad entry stops the server at start with a message naming it.
- `admin` includes everything, `moderate` includes `read`, `chat` includes nothing else. A missing scope answers 403
  `this needs the <scope> scope`.
- `read`: `GET /status`, `/players` (without login, SteamID, ban/mute state and last seen unless the token also has
  `moderate`; search by login or SteamID then finds nothing), `/chat`, `/modpack`, `/economy/stats`, `/economy/lots`,
  `/raids`, `/raids/stats`, `/live`, `/events`.
- `chat`: `POST /chat`.
- `moderate`: `GET /players/online`, `/players/{id}`, `/economy/wallet/{id}`, `/economy/parcels/{id}`, `/shop/{id}`,
  `/shop/{id}/removals`, `/bans`, `/actions`; kick, mute, unmute, broadcast, save-all.
- `admin`: everything else — bans and pardons, saves and rollback, reset, role, rename, peaceful, wallet changes,
  refunds, freezes, raid cancel, live end, shutdown, world recreation, modpack changes, `/config`, backups.

## 7. Backups

- Automatic: `Backup:IntervalHours` (default 24, 0 = off), `Backup:Keep` archives kept in `backups/`.
- Manual, safe while running: `rimlink-tools backup --data <dir> [--out file.tar.zst]`, or the panel's Backups screen /
  `POST /api/admin/backups`. A backup made from the panel lands in `backups/` with the scheduled ones and counts towards
  `Backup:Keep`, so it can push out the oldest scheduled archive.
- Restore (server stopped): `rimlink-tools restore --data <dir> --from file.tar.zst`.

## 8. Monitoring

`GET /health` (used by the Docker health check) and Prometheus metrics at `GET /metrics` (economy: `rimlink_trades_total{kind}`, `rimlink_silver_moved_total`, `rimlink_parcels_pending`, `rimlink_economy_errors_total{code}`; raids: `rimlink_raids_total{outcome}`, `rimlink_raids_active`, `rimlink_visits_total`, `rimlink_shop_sales_total`; live sessions:
`rimlink_live_proposed_total{kind}`, `rimlink_live_sessions_total{kind,end_reason}`, `rimlink_live_frames_total{dir}`, `rimlink_live_active`;
admin feed: `rimlink_admin_feed_subscribers`). Logs go to the console
and `logs/server-<date>.log`.

## 9. Limits (built in)

Player limit `Server:MaxPlayers`, one settlement per player (`Server:MaxSettlementsPerPlayer`), 5 chat messages per
5 s, 3 save uploads per 30 s, saves up to 64 MB compressed, 5 unauthenticated connections per IP, 30 s to log in.

## 10. Discord bot

The bot (`src/Rimlink.Discord`, image `ghcr.io/asofwar/rimlink-discord`) posts server events into Discord channels, bridges
the public game chat with one Discord channel both ways, answers public queries and runs moderator and admin commands for
Discord roles. It talks to the server only through the admin REST API, with a token of its own.

1. **Discord application.** In the Discord Developer Portal create an application, add a bot, copy its token
   (`DISCORD_TOKEN`) and turn on the **Message Content** privileged intent (needed to read the chat channel). Invite the
   bot with the scopes `bot` and `applications.commands` and the permissions View Channels, Send Messages, Add Reactions
   and Read Message History.
2. **Ids.** Turn on Developer Mode in Discord (Settings → Advanced) and copy the server (guild) id, the ids of the
   moderator and admin roles, and the channel ids for each event group: `server` (joins, colonies, announcements,
   shutdowns, freezes), `raids` (landed and finished raids, visits, live sessions), `market` (lots posted and sold, shop
   sales), `moderation` (every admin action from the audit log, best a closed channel) and `chat` (the chat bridge).
   A group without a channel is off.
3. **Token.** Pick a random string of 24+ characters for `RIMLINK_BOT_TOKEN`. The server gets it as the `Admin:Tokens`
   entry `discord-bot`, the bot as `RimlinkBot:Server:ApiToken`. Its scopes decide what the bot may do on the server:
   - `admin`: everything (`admin` includes the other scopes);
   - `moderate` + `chat`: no admin commands (they answer "this server's bot token has no admin rights");
   - `read` + `chat`: events, the chat bridge and `/online`, `/status`, `/market`; `/player` needs `moderate`.
   Without `chat` the bridge works only from the game into Discord.
4. **Certificate.** The bot reaches the server as `https://rimlink:25560` inside the compose network.
   - Self-signed certificate: set `RIMLINK_CERT_SHA256` to `certificateFingerprint` from `rimlink-tools admin status` (or
     `GET /api/admin/status`); the bot trusts exactly that certificate.
   - Real certificate (`Tls:CertificatePath`): set `RIMLINK_CERT_NAME` to the name it is issued for (`play.example.com`);
     the chain is checked as usual, the name against that one. Renewals need no change.
   - `RimlinkBot:Server:Insecure=true` skips the check and is only acceptable inside the compose network.
5. **Run.** `docker compose -f docker-compose.yml -f docker-compose.discord.yml up -d` with the values above in `.env`
   (the file lists every variable); `deploy/discord.appsettings.example.json` shows the same settings as JSON. The bot
   stops at start when a token is missing; a channel it cannot write to or a role it cannot find only turns that part off
   (see its log).
   Without Docker: unpack `rimlink-discord-<version>-<runtime>.zip` from the release (needs the .NET 10 runtime), put the
   `RimlinkBot` section of `discord.appsettings.example.json` into its `appsettings.json` (or set `RimlinkBot__…` environment
   variables) and run `dotnet rimlink-discord.dll` from that folder.

Commands (Discord bot spec §5):

| Who | Commands |
|---|---|
| Everyone | `/online`, `/status`, `/market [seller]`, `/player <name>` |
| Moderator role | `/kick`, `/mute`, `/unmute`, `/broadcast`, `/bans`, `/actions`, `/raids list\|stats`, `/live list`, `/economy stats` |
| Admin role | `/ban`, `/pardon`, `/wallet`, `/refund lot\|parcel`, `/economy freeze\|unfreeze`, `/raids cancel\|freeze\|unfreeze`, `/live end`, `/peaceful`, `/saves` |
| Admin role, with a confirm button | `/shutdown`, `/rollback`, `/reset`, `/recreateworld` |

Every member sees every command; one without the role gets "You may not use this command" and nothing reaches the server.
To hide the moderator and admin commands, restrict them in Discord (Server Settings → Integrations → the bot) to the
moderator and admin roles. The server runs every bot call as the bot's token, so the Discord roles are the only thing that
tells a moderator from any member: give the roles carefully. Each action shows in the audit log as
`api:discord-bot:discord:<user> (<id>)`. Player-name autocomplete suggests names only, never a match by login or SteamID.

The bot keeps the last feed event id in `RimlinkBot:StateFile` (compose: the `rimlink-discord-state` volume), so after a
restart it posts what happened while it was down, as long as the server's buffer (`Admin:FeedBufferSize`) still holds it.
It says "the server is unreachable" in the server channel after a minute without the server and "reachable again" when it
is back.
