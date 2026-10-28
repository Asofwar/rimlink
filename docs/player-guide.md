# Rimlink — player guide

Rimlink lets a group of players share one RimWorld 1.7 planet. Each player runs their own
colony in their own game; the server keeps the shared world map, everyone's settlements, the
chat and your save games. Your colony is frozen while you are offline.

## 1. Install

1. Subscribe to **Harmony** on the Steam Workshop (`brrainz.harmony`) or install it by hand.
2. Install the Rimlink mod: unpack `rimlink-client-<version>.zip` into `RimWorld/Mods/` so that
   `RimWorld/Mods/Rimlink/About/About.xml` exists.
3. Enable **Harmony** and **Rimlink** in the game's mod list (Harmony first), restart.
   The main menu gains a **Multiplayer** button.

Rimlink needs the exact server version it was built with (the server tells you when it does not match).

## 2. Connect

1. **Multiplayer** → **Add server**: name, address (host or IP) and port (default `25560`), **Connect**.
2. The first time you connect, the game shows the server's certificate fingerprint. Compare it with the
   fingerprint your admin gave you and confirm. If the fingerprint later **changes**, the game asks again —
   do not accept a changed fingerprint unless the admin announced a new certificate.
3. Login:
   - With Steam running, the server verifies your Steam account; nothing to type.
   - If the server has password accounts enabled (or Steam is unavailable), the account dialog opens.
     **Log in** with an existing account or **Create account** with a login, a password and the player name
     others will see (defaults to the login).
4. A server with a join password asks for it once (it is not your account password; the admin gives it out).
5. Your mods are compared with the server's modpack. If they differ, a dialog lists what is missing, has the
   wrong version or is not allowed. Use **Subscribe in Steam** / **Open download links**, then
   **Use server mod order** and **Restart game**. Your previous mod list is kept as
   `Config/ModsConfig.rimlink-backup.xml`.

## 3. Play

- **First login**: the usual new-colony pages. The world page is locked to the server's planet (same seed and
  settings for everyone). Other players' settlements are already on the map; pick a free tile.
- **Later logins**: your save is downloaded from the server and loaded.
- **Leaving**: use the vanilla **Quit to main menu**. The game saves, uploads the save and only then returns to
  the menu. Autosaves are uploaded too. Saves live in `Saves/Rimlink_<host>_<port>_<player>.rws`; manual saves
  under other names stay local and are not sent to the server.
- If the connection is lost for good or your account logs in elsewhere, the game saves locally and returns to the menu;
  that save is sent to the server at your next login, before anything is downloaded. Loading another (single-player)
  save from inside a multiplayer game ends the session the same way.
- **Chat / Players** buttons in the top-right corner: global chat, direct messages (click a player → Message),
  who is online. Moderators and admins type `/help` in chat for commands.
- Settling, abandoning or losing a settlement is reported to the server; other players see it on their map.
- If the server shuts down, you get a warning; the game saves and uploads before the server stops.

## 4. Trading and gifts

Everything goes through the **comms console**: select a colonist, right-click the console, choose **Rimlink**.
Items you can send are the ones under a powered **orbital trade beacon**, exactly like trading with a ship.

- **Market**: lots posted by every player. **Buy** pays from your wallet; the goods arrive by drop pod.
- **My lots**: tick items (or one colonist/animal), set a price, **Post lot**. The goods leave your colony and wait on the
  server in escrow; **Withdraw** brings them back by drop pod. Lots expire after 30 days (the server returns them).
- **Send**: a gift to another player — items, pawns and silver from your wallet. They get it next time they are in
  their colony, whether or not they are online now.
- **Wallet**: your silver on the server. **Deposit** takes silver from under the beacons; **Withdraw** drops it in
  your colony. Sales are credited here, purchases are paid from here.
- **Mailbox**: parcels waiting for you (gifts, purchases, returned lots). They drop automatically while you are on
  your colony map; **Accept now** does it immediately.
- **Caravan**: right-click another player's settlement on the world map with a caravan selected and choose
  **Transfer to <player>…**. On arrival the same window opens with the caravan's inventory and members; the caravan
  stays there afterwards.

Sending a colonist asks for confirmation: they leave your colony for good, with their skills, health, gear and
story. A thing that is not in the server's modpack cannot be sent.

Every transfer first saves your game and uploads the save (a short "Sending your last save…" pause). If the upload
fails, nothing is sent and the goods are put back. After a transfer the server refuses any **older** save from you:
the goods are already in someone else's mailbox, so an older file would duplicate them.

If the server does not answer a transfer (connection lost, trading paused), the goods are neither sent nor put back:
the request waits in `Saves/Rimlink/<server>_<player>/outbox.json` and is sent again, as the same request, at your
next login or reconnect. A request the server then refuses comes back to your colony by drop pod.

## 5. When something goes wrong

| What you see | What it means / what to do |
|---|---|
| `Steam ticket is not valid` + the account dialog | The server cannot verify Steam logins (no Web API key) or Steam is offline. Use a password account. |
| `login details are invalid` | Wrong login or password. |
| `login already taken` / `player name already taken` | Pick another login or player name. |
| Certificate fingerprint changed | Ask the admin. Accept only if they confirm a new certificate. |
| `Your mods do not match the server` | Follow the dialog (subscribe / download, apply the server order, restart). |
| `Connection to the server lost, reconnecting...` | The game pauses and reconnects for up to 2 minutes (same session). After that (`Could not reconnect …`) it returns to the menu; your save is kept locally and uploaded at the next login. |
| `This account logged in from another place` | Someone used your account elsewhere; the game returns to the menu. |
| `An admin restored your save to version N` | Return to the menu and log in again to load that version. |
| `This save is older than your last transfer` | You loaded a save from before something you sent. Go back to the menu and log in to get the server copy, or keep playing without uploads. The refused file is kept as `*.stale-<date>.rws`. |
| `Transfer refused: not enough silver` / `lot was already sold` | Wallet too low, or someone bought the lot first; nothing left your colony. |
| `Transfer refused: def 'X' is not in the server modpack` | That item comes from a mod the server does not allow; it was put back. |
| `The server did not answer (…). The transfer is kept …` | The outcome is unknown; nothing is lost. Log in again later and the transfer is settled. |
| `A refused transfer is coming back by drop pod` | A kept transfer was refused when retried (recipient gone, not in the modpack…); its contents are back. |
| `A parcel could not be unpacked` | A mod needed by that item is missing or broken on your side; the parcel stays in your mailbox for later. |
| `The server refused this settlement` | The tile was taken meanwhile or you already have a settlement. Choose another tile. |
| Save upload failed (message in the log) | The upload is retried at your next login; do not delete `Saves/Rimlink/`. |

Logs: `Player.log` in RimWorld's log folder; Rimlink lines are prefixed with `[Rimlink]`.
