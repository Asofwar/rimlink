# Rimlink

Persistent multiplayer for RimWorld 1.7: every player runs their own colony in a
shared world hosted by a dedicated server. Colonies are simulated by the player's
game and frozen while they are offline; the server owns everything shared
(accounts, the world map, player settlements, chat, the mod list) and stores the
save games.

Rimlink is a clean-room project: its own protocol, server and client. It is not
compatible with, and shares no code with, other RimWorld multiplayer mods.

## Status

Core complete: dedicated server (accounts via Steam or password, shared world and settlements, versioned saves,
chat, modpack check, admin API, CLI, Docker image) and the RimWorld 1.7 client mod (server list, certificate
pinning, login, locked world creation, settlements, save sync, chat, players, reconnect). Economy (sub-project 2):
server mailbox, escrow market board, silver wallets, direct transfers of items/pawns/silver, caravan hand-overs at
other players' settlements, anti-duplication by save tick; server-held wealth counts in threats and loot caps, nothing
leaves a colony under attack, a storage fee on pawns and perishables kept on the server, admin-forbidden defs, a
delivery spot. Raids and visits (sub-project 3a): asynchronous raids with escrowed pawns, loot caps, cooldowns and peaceful mode; visits to a copy of another player's colony with a shop. Live raids and visits (sub-project 3b): when both players are online the
guest's pawns act on the host's live map, the guest sees it through a mirrored copy and gives orders to their own pawns, the server
relays frames and falls back to the asynchronous raid when the host declines; pings (map and world), place cards in chat, a
shared pause on threat letters by each player's threshold, both speed wishes on the time buttons, server refusals in the
player's language. Web admin panel: the server serves
`/admin/` (every admin operation, player directory, chat history, backups, modpack editor; English and Russian) and a
public status page at `/`; integrations use named API tokens with scopes. Discord bot: server events in Discord
channels, the public chat bridged both ways, public queries and role-gated admin commands, through the admin event feed
(Server-Sent Events) and the REST API with a scoped token. Safe trades: a player's gift waits for accept or refuse,
a personal block list (DMs, gifts, trades, public chat lines), and two-way trades on server escrow with counter-offers
and double confirmation. Integrity: dev mode only for admins while connected, saves made with dev mode flagged, a
wealth-jump check per save (admin alert, then a freeze of that player's trading), the world's storyteller and difficulty
held for every colony, and the colony saved and uploaded when the game is closed.

- Players: `docs/player-guide.md`
- Server admins: `docs/server-setup.md`
- Design: `docs/superpowers/specs/`

## Layout

| Path | Purpose |
|---|---|
| `src/Rimlink.Protocol` | gRPC/protobuf contracts shared by server and client |
| `src/Rimlink.Server` | ASP.NET Core dedicated server (gRPC game API, REST admin API, SQLite) |
| `src/Rimlink.Client.Core` | Game-independent client: connection, pinning, reconnect, shared state, save transfer |
| `src/Rimlink.Client` | RimWorld 1.7 mod (Harmony): UI, patches, session — built against the game, not in the solution |
| `src/Rimlink.Tools` | Admin CLI (backups, mod list import, admin operations) |
| `tests/` | Server and client-core tests (`WebApplicationFactory` against the real server) |
| `mod/` | Mod metadata, defs, languages |
| `scripts/` | `Build-ClientMod.ps1` (compile + assemble the mod), `Package-Release.ps1` (zips) |
| `deploy/` | Dockerfile, docker-compose, sample configuration |

## Build and test

```bash
dotnet build Rimlink.sln
dotnet test Rimlink.sln
docker build -f deploy/Dockerfile -t rimlink-server .
```

Client mod (needs RimWorld 1.7 and the Harmony mod installed; PowerShell):

```powershell
.\scripts\Build-ClientMod.ps1 -RimWorldDir 'C:\...\RimWorld' -InstallTo 'C:\...\RimWorld\Mods'
.\scripts\Package-Release.ps1   # dist/rimlink-client-<ver>.zip, dist/rimlink-server-<ver>-<rid>.zip
```

## License

MIT, see `LICENSE`.
