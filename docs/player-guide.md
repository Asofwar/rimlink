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

Many servers have a public page at `https://<address>:<port>/`: it lists the modpack in load order with Workshop
links (subscribe before the first connection), the login modes, the address to add and the certificate fingerprint.

1. **Multiplayer** → **Add server**: name, address (host or IP) and port (default `25560`), **Connect**.
2. The first time you connect, the game shows the server's certificate fingerprint. Compare it with the
   fingerprint your admin gave you, or with the one on the server's public page, and confirm. If the fingerprint later **changes**, the game asks again —
   do not accept a changed fingerprint unless the admin announced a new certificate.
3. Login:
   - With Steam running, the server verifies your Steam account; nothing to type.
   - If the server has password accounts enabled (or Steam is unavailable), the account dialog opens.
     **Log in** with an existing account or **Create account** with a login, a password and the player name
     others will see (defaults to the login).
4. A server with a join password asks for it once (it is not your account password; the admin gives it out).
5. Your mods are compared with the server's modpack. If they differ, a dialog lists them by category, each with what to
   do and who has to act:
   - **Missing**: install them (Steam subscription or download link).
   - **Wrong version**: update your copy, or, when yours is newer, the server's modpack is behind: ask the admin.
   - **Different files**: same version, other files (local edits, another Workshop update): verify or re-subscribe.
   - **Not allowed**: turn them off or ask the admin to allow them. Translations and mods made only of textures or sounds
     (no defs, patches or code) are always allowed.
   - **Load order**: your mods load in another order than the server's.
   - **Server mod settings**: the server shares the settings of some mods (for example raid point multipliers).

   **Fix and restart** does everything you can do yourself: it subscribes to the missing Workshop mods and waits for Steam
   to download them, puts your mod list in the server order (turning off the mods the server refused; your previous list is
   kept as `Config/ModsConfig.rimlink-backup.xml`), takes the server's mod settings, and restarts the game. After the
   restart the game logs in to that server by itself (a password account gets the login window with the login filled in;
   the password is never stored).

   The server's mod settings are used instead of your own for those mods only, from the next start on, every time you
   start the game, until you choose otherwise. Your own settings files are not touched. The main menu shows
   **Mod settings of <server> in effect: use mine** while they apply; it switches back (one restart). A single-player game
   started in the same session uses the server's settings too.

## 3. Play

- **First login**: the usual new-colony pages. The world page is locked to the server's planet (same seed and
  settings for everyone). Other players' settlements are already on the map; pick a free tile. On most servers the
  storyteller and difficulty are the world's too ("Storyteller and difficulty are set by …"): the storyteller page shows
  them and they cannot be changed, neither there nor later in game; a colony that plays with others gets the server's when
  it loads ("The server's storyteller and difficulty were applied").
- **Later logins**: your save is downloaded from the server and loaded.
- **Leaving**: use the vanilla **Quit to main menu**. The game saves, uploads the save and only then returns to
  the menu. **Quit to OS**, Alt+F4 and the window's close button do the same before the game closes (a few seconds; when the
  server cannot be reached the save is kept and sent at your next login). Autosaves are uploaded too. Saves live in `Saves/Rimlink_<host>_<port>_<player>.rws`; manual saves
  under other names stay local and are not sent to the server.
- If the connection is lost for good or your account logs in elsewhere, the game saves locally and returns to the menu;
  that save is sent to the server at your next login, before anything is downloaded. Loading another (single-player)
  save from inside a multiplayer game ends the session the same way: a message in the loaded game says the session
  ended and the colony goes to the server at your next login (during a visit nothing is saved: the server keeps the
  colony as it was uploaded before the visit).
- **Chat / Players** buttons in the top-right corner: global chat, direct messages (click a player → Message),
  who is online. Moderators and admins type `/help` in chat for commands. When the server has a Discord bot, the
  public chat is shared with a Discord channel: lines from Discord show as `[Discord] Name: text`, and your public lines
  appear in that channel (direct messages never leave the game).
- **Pings**: press **G** (Options → Keyboard → Rimlink) to mark the place under the mouse for another player. On a map you
  share (a live session, a visit) it goes straight to the other player; on your own map and on the world map a menu lists
  the players online (the last visitor first). The receiver hears a bell and gets a message that jumps to the place; a yellow
  ring marks it for 8 seconds. Typing in a text field never pings. Pings are not stored and are limited to 12 a minute; a block stops them both ways.
- **Places in chat**: **Place** next to the input adds the selected world object (or the tile of the map you look at) to the
  line. Everyone sees it as a button with the settlement and its owner, or the tile and its biome; a click opens the world
  map there and selects it.
- **Dev mode** is for admins on most servers: while you are connected it is off and cannot be turned on ("Dev mode is reserved
  for admins on this server"); your setting comes back when you leave the session. A save in which dev mode was on (also one
  played offline) is flagged when it reaches the server, and the server freezes your trading until an admin looks at it.
- **Frozen trading**: an admin, a flagged save or a sudden jump in colony wealth can freeze your trading. You get a message
  with the reason; sending, the wallet, the market, shops, trades and raids are refused ("Your trading is frozen: …", nothing
  leaves your colony), parcels for you still arrive and you can take your own lots back or cancel a trade. Only an admin opens it again.
- Settling, abandoning or losing a settlement is reported to the server; other players see it on their map.
- **Other players on the world map**: a green light on a settlement means its owner is online now, grey means offline; the bar
  under it shows their wealth tier (1–5, not the exact wealth). Select the settlement for its owner, faction, when they were last
  seen and how many game days the colony has lived.
- **While you were away**: after a login the game shows one letter with what happened since your last session — raids on you
  and your own raids, visitors, parcels, market and shop sales, chat lines naming you or sent to you, and admin actions on your
  account. Nothing happened, no letter.
- **A long absence**: a young, poor colony whose owner has not played for two weeks (the server's settings decide) is moved to
  the server's archive, not deleted. Log in and it comes back by itself. If someone settled its tile meanwhile, you start a new
  colony and an admin can bring the old one back once the tile is free.
- If the server shuts down, you get a warning; the game saves and uploads before the server stops.

## 4. Trading and gifts

Everything goes through the **comms console**: select a colonist, right-click the console, choose **Rimlink**.
Items you can send are the ones under a powered **orbital trade beacon**, exactly like trading with a ship.

- **Market**: lots posted by every player. **Buy** pays from your wallet; the goods arrive by drop pod.
- **My lots**: tick items (or one colonist/animal), set a price, **Post lot**. The goods leave your colony and wait on the
  server in escrow; **Withdraw** brings them back by drop pod. Lots expire after 30 days (the server returns them).
  When a lot sells while you play, a message names the lot, the buyer, the price and what reached your wallet.
- **Send**: a gift to another player — items, pawns and silver from your wallet. They get it next time they are in
  their colony, whether or not they are online now. A gift waits for the recipient's answer (see *Gifts* below).
- **Wallet**: your silver on the server. **Deposit** takes silver from under the beacons; **Withdraw** drops it in
  your colony. Sales are credited here, purchases are paid from here.
- **Mailbox**: parcels waiting for you (gifts, purchases, returned lots). They drop automatically while you are on
  your colony map; **Accept now** does it immediately. A gift from a player waits for your **Accept** or **Refuse**.
- **Trades**: two-way trades with another player (see *Trades* below).
- **Caravan**: right-click another player's settlement on the world map with a caravan selected and choose
  **Transfer to <player>…**. On arrival the same window opens with the caravan's inventory and members; the caravan
  stays there afterwards.

Sending a colonist asks for confirmation: they leave your colony for good, with their skills, health, gear and
story. A thing that is not in the server's modpack cannot be sent, and neither can a def the admin forbade: such things
are greyed out in the list ("Forbidden on this server").

- **Delivery spot**: Architect → Misc → **Rimlink delivery spot** (free, placed at once). Parcels and returned goods
  land next to it; a new spot replaces the old one. Without one they land at your first trade beacon, else where trade
  ships drop.
- **What the server holds counts**: silver in your wallet, your open lots and parcels on their way to you count as your
  colony's wealth for the storyteller's threats, for the loot a raider may take and for the poor-colony protection.
  Keeping wealth on the server does not make your colony look poor.
- **Under attack nothing leaves**: while a raid from another player waits for you (warned or not), is on your map, a
  live raid you host is under way, or enemies are on your map, deposits, new lots and sends (wallet silver included)
  are closed; the window says so. Withdrawals, purchases, taking a lot back and parcels still arrive. A caravan already on
  the world map can still hand things over.
- **Storage fee**: pawns and things that rot cost silver while they sit on the server: 0.5 % of their value per real day
  after 3 free days (the server's settings may differ). It is paid when they leave: from the sale for a lot (the rest
  from your wallet), from your wallet when you take a lot back, when it expires, or when a gift or purchase reaches you.
  A message names the parcel or lot and the amount. A parcel's fee takes at most the silver in your wallet; a lot's fee
  can leave your wallet below zero: until deposits or sales pay it off, buying, posting lots and sending are closed (the
  balance turns red).
- Values in gifts, loot, raid returns and captives are declared by the other player and do not count as your wealth on
  the server; only their silver does.

A pawn of yours that comes home (a transfer the server refused, an unsold lot, raiders back from a raid, guests back
from a live session, a colonist someone sends back to you) gets its family, partners and bonded animals among your
pawns back, on its side and on theirs, also when several of them left together or one after another. While it is away
your colonists do not show it in their Social tab. Relatives living outside your colony may be forgotten while it is
away; relations it started in another colony, and what your colonists remembered about it, do not come home.

Every transfer first saves your game and uploads the save (a short "Sending your last save…" pause). If the upload
fails, nothing is sent and the goods are put back. After a transfer the server refuses any **older** save from you:
the goods are already in someone else's mailbox, so an older file would duplicate them.

### Gifts

A gift from another player arrives as a letter: **Accept** lets it land by drop pod, **Refuse** sends everything back to
the sender (things by drop pod, silver to their wallet), **Postpone** keeps the letter. The Mailbox tab has the same two
buttons. A gift nobody answers goes back on its own after a week, and the sender gets a letter either way. Tick **Accept
gifts without asking** (Raids tab, next to peaceful mode) to have gifts drop at once, as before.

### Trades

A trade is an exchange with another player that needs no trust and no market: both sides go into escrow on the server
and change hands only when both players confirmed the same offer. Start one with **New trade** in the Trades tab or
**Trade** in the Players window (an offline player works too: anyone with a settlement).

- **Your offer** takes things and pawns from under the beacons, silver from your wallet and a short note; they leave
  your colony at once, as when sending. An offer is not a promise yet: nothing changes hands until you confirm too.
- **Change my side** adds more things to what you offered; tick **Take back what is already offered** to replace it
  (the old goods come back by drop pod). Silver and note are the new totals. Your changed side counts as confirmed,
  the other side as not; if the other player changed something while your window was open, yours is not confirmed
  either, so you never agree to an offer you have not seen.
- **Confirm** agrees to the trade as you see it. When both sides confirmed, each gets the other side by drop pod.
- **Cancel trade** (either player) sends each side back to its owner. An open trade expires after a week the same way.

Tick **Not accepting trades** (Raids tab) to refuse new offers; open trades go on.

A trade is a way out of your colony too. While the colony is under threat or you owe storage fees, nothing new joins a trade:
no new offer with goods or silver, no additions to your side. Taking things back, **Confirm** and **Cancel** always work. Things
the admin forbade cannot join a trade. What sits in your open trades still counts as your wealth, and a finished trade's parcel
pays the storage fee like a gift if it stays undelivered past the free days.

### Blocking

**Block** in the Players window: that player's direct messages, gifts and trade offers to you are refused, their lines
in the public chat no longer reach you, and open trades between you end (each side gets its things back). Gifts already
waiting stay, so you can refuse them. You cannot message, gift or trade with someone you blocked either; **Unblock**
(also for blocked players who are offline, listed at the bottom) lifts it. Moderators and admins cannot be blocked.
Blocking does not affect the market, raids or visits.

If the server does not answer a transfer (connection lost, trading paused), the goods are neither sent nor put back:
the request waits in `Saves/Rimlink/<server>_<player>/outbox.json` and is sent again, as the same request, at your
next login or reconnect. A request the server then refuses comes back to your colony by drop pod; one that took nothing
from the colony (wallet silver only, a raid by copies) is simply dropped.

## 5. Raids and visits

Nobody needs to be online at the same time: a raid waits on the server until its target next plays, a visit loads a
copy of the host's colony.

- **Raid**: take a caravan to another player's settlement (or launch transport pods at it) and choose **Raid**.
  Pick the raiders, then **Real** (they leave your colony; survivors come back as a parcel with their wounds, the
  captured stay with the defender) or **Copies** (nothing leaves, half the loot), and **Warned** (the target gets a
  letter and a day of their play to prepare) or **Instant** (half the loot). The raiders fight as a normal raid in the
  target's game; what they steal arrives in your mailbox, capped by the target's wealth and the server's limit. What
  the raiders took along themselves is not loot: real raiders bring it home, copies lose it with the copy.
- **Being raided**: a letter announces the raid; captured raiders are yours, what they carry away is gone. The
  **Raids** tab of the Rimlink window lists raids coming at you and your own.
- **Peaceful mode** (Raids tab): nobody can raid you and you cannot raid; switching it off takes a day. While it is
  switching off the box is unticked and shows when peaceful mode ends; tick it again to stay peaceful. New or poor
  colonies are protected by the server anyway.
- **Visit**: with a caravan (or pods) at another player's settlement choose **Visit**. Your own colony is saved and
  uploaded, then a copy of the host's colony loads with copies of your pawns walking in. Nothing taken there goes
  home; things the host marked **For sale** show a **Buy** button, paid from your wallet and delivered to your colony
  by drop pod after the visit. **End visit** brings your own colony back. Tick **Do not receive visitors** to opt out.
  A host who plays with mods you do not have cannot be visited: the visit stops before the copy loads and names the
  missing mods; you stay in your colony. The same goes for live sessions.
- **Selling**: in your colony select an item and use **For sale...**; the shelf is published with your next save and
  sold things are collected from your stockpiles at your next login. A sale while you play shows a message with the
  buyer, the item and the price.

### Paid events

The **Events** tab of the economy window orders an event against another player's colony for wallet silver. Pick the
player and the event, a level for hostile ones (level 1 is half of the colony's threat points, 2 all of them, 3 one and a
half, 4 double) and **By drop pods** where it is offered; the price comes from the server before you pay and follows the
target's wealth, server-held wealth included, the level and the event.

- **Hostile** (raids, infestation) and **weather** events obey the same rules as raids: nobody in peaceful mode on either
  side, no new colonies, no colonies under the poor-colony threshold, and they share the cooldown on a target with raids.
- **Gifts** (a trader, a resource pod, a wanderer) only have a short cooldown per target.
- A colony holds at most two events waiting. They run in the target's game, when they play: hostile events of level 3 and
  4 are announced by a letter with your name half a day ahead, the others come at once. The target is told who ordered
  each one.
- While a hostile event waits for your colony, deposits, new lots and sends are closed, as under a raid.
- **My orders** shows each order's state. An event that could not start (say, an infestation on a map without mountains),
  or that the target did not play in time (7 days), or that an admin cancelled, is refunded in full. The silver of an event
  that happened goes to the bank, the target gets nothing.

## 6. Live raids and visits

When both players are online, a raid or a visit can happen live: your pawns walk onto the host's map while the host plays,
you see their map through a mirrored copy and give orders to your own pawns. The host's game decides everything; you see
the result a moment later.

- **Starting**: with a caravan (or pods) at another player's settlement choose **Live raid** or **Live visit**. The host gets
  an invitation and a minute to answer. Declined or unanswered: a live raid goes on as a normal raid with the timing you chose,
  a live visit's pawns come home by parcel. The host must be online and at their colony.
- **As the host**: a letter says who arrived; raiders are hostile and your colonists fight them as usual, visitors are
  friendly. Your game pauses until the guest's view is ready, then runs at your speed; the panel in the top right shows the
  state and **Abort session**. You cannot select or command the guest's pawns.
- **As the guest**: your own colony is saved and uploaded, then the host's snapshot loads. The panel shows the session, the
  tick and your pauses. Your pawns are in the colonist bar: select, draft, move, attack, give jobs, set work priorities and
  the schedule; everything else on the map is view only. You may pause a limited number of times (the server's limit, 10
  pauses of a minute by default); the host pauses freely. If your view falls behind, it asks the host for a fresh snapshot
  and reloads it.
- **Speed**: each side wishes a speed and the session runs at the slower one. The time buttons show both wishes: **H** in
  the top-left corner of the host's choice, **G** in the bottom-right corner of the guest's (tooltip: both wishes).
- **Pause on letters** (mod settings: Never / Major threats / Any threat): when a threat letter comes on the shared map, the
  session pauses if either player's setting asks for it (the stricter one applies). Both see the letter; the pause does not
  use the guest's budget, and either side lifts it by choosing a speed (the host also with Space). Vanilla's own automatic
  pause on letters is off during a session: this setting replaces it.
- **Ending**: **End session** (guest) or **Abort session** (host). The session also ends by itself once none of the guest's
  pawns is left to play (all dead, captured or gone; one still alive anywhere, even downed or carried, keeps it going). Your pawns come home by parcel
  as they are, with their wounds and inventory; a raid's loot follows the normal raid rules. If a connection drops, the other side waits a few
  minutes: a lost guest's raid is then handed to the host's AI, a lost host's session ends and the host's game reports it at
  their next login.
- **Auto-accept live sessions** (Raids tab) accepts invitations without the dialog. Peaceful mode and **Do not receive
  visitors** refuse live sessions as well.

## 7. When something goes wrong

| What you see | What it means / what to do |
|---|---|
| `Steam ticket is not valid` + the account dialog | The server cannot verify Steam logins (no Web API key) or Steam is offline. Use a password account. |
| `login details are invalid` | Wrong login or password. |
| `login already taken` / `player name already taken` | Pick another login or player name. |
| Certificate fingerprint changed | Ask the admin. Accept only if they confirm a new certificate. |
| `Your mods do not match the server` | Follow the dialog: **Fix and restart**, then the game logs in by itself. A version marked "the server runs an older version" or files that still differ after re-subscribing need the admin. |
| `Connection to the server lost, reconnecting...` | The game pauses and keeps reconnecting (first after 1 s, then less often, at the latest every minute); the corner shows how long it has been. After 2 minutes a window offers to keep waiting or go back to the menu: the attempts go on behind it, and it closes by itself when the server is back. Back in the menu your save is kept locally and uploaded at the next login. |
| `The server refused to reconnect: …` | The server turned the login down for good (ban, changed password or server password, new server version, the world was deleted to be created anew, an untrusted new certificate): back to the menu, your save is kept locally. A Steam ticket the server could not check (Steam down) is not a refusal: the attempts go on. |
| `This account logged in from another place` | Someone used your account elsewhere; the colony continues there. This copy is paused and disconnected, nothing from it is uploaded; "Back to main menu" leaves it. |
| `An admin restored your save to version N` | Return to the menu and log in again to load that version. |
| `This save is older than your last transfer` | You loaded a save from before something you sent. Go back to the menu and log in to get the server copy, or keep playing without uploads. The refused file is kept as `*.stale-<date>.rws`. |
| `Transfer refused: not enough silver` / `lot was already sold` | Wallet too low, or someone bought the lot first; nothing left your colony. |
| `Transfer refused: def 'X' is not in the server modpack` | That item comes from a mod the server does not allow; it was put back. |
| `The server did not answer (…). The transfer is kept …` | The outcome is unknown; nothing is lost. Log in again later and the transfer is settled. |
| `A refused transfer is coming back by drop pod` | A kept transfer was refused when retried (recipient gone, not in the modpack…); its contents are back. |
| `Raid refused: …` (cooldown, protected, peaceful, already under way) | Nothing left your colony; try later or another target. |
| `The raid by X is over: …` | Your colony reported the outcome; the attacker gets their parcels. |
| `The visit could not start: …` | The host closed visits, has no colony yet, plays with mods you do not have (they are named), or their save could not load on your side; you stay in or return to your own colony. |
| `Purchase refused: …` | Sold out meanwhile or not enough silver; nothing was paid. |
| `Invitation: live raid` / `live visit` | Another player wants to play on your map right now. Decline or wait: a raid goes on as a normal raid, a visit's pawns go home. |
| `X is not online at their colony.` | A live session needs the host online and in their colony. Make a normal raid or visit, or try later. |
| `Order refused (…)` | The host's game could not carry out that order (pawn dead or downed, target gone); the view shows the host's state. |
| `Connection to X lost; waiting for them.` | The other side dropped. The host's game pauses for a few minutes; then the session ends (a raid goes to the AI, a visit's pawns come home). |
| `The live session with X is over (…)` | Ended by a player, by a limit or by an admin. A guest's pawns come home by parcel once the host reports. |
| `None of your pawns at X can act any more …` | The host's game ended the live session because all your pawns there are dead, captured or gone. Those still alive come home by parcel. |
| `A parcel could not be unpacked` | A mod needed by that item is missing (the item is named) or broken on your side; the parcel stays in your mailbox for later. |
| `The server refused this settlement` | The tile was taken meanwhile or you already have a settlement. Choose another tile. |
| Save upload failed (message in the log) | The upload is retried at your next login; do not delete `Saves/Rimlink/`. |

Logs: `Player.log` in RimWorld's log folder; Rimlink lines are prefixed with `[Rimlink]`.
