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
4. Your mods are compared with the server's modpack. If they differ, a dialog lists what is missing, has the
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
- **Chat / Players** buttons in the top-right corner: global chat, direct messages (click a player → Message),
  who is online. Moderators and admins type `/help` in chat for commands.
- Settling, abandoning or losing a settlement is reported to the server; other players see it on their map.
- If the server shuts down, you get a warning; the game saves and uploads before the server stops.

## 4. When something goes wrong

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
| `The server refused this settlement` | The tile was taken meanwhile or you already have a settlement. Choose another tile. |
| Save upload failed (message in the log) | The upload is retried at your next login; do not delete `Saves/Rimlink/`. |

Logs: `Player.log` in RimWorld's log folder; Rimlink lines are prefixed with `[Rimlink]`.
