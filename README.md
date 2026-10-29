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
other players' settlements, anti-duplication by save tick. Raids and visits (sub-project 3a): asynchronous raids with escrowed pawns, loot caps, cooldowns and peaceful mode; visits to a copy of another player's colony with a shop. Live raids and visits (sub-project 3b): when both players are online the
guest's pawns act on the host's live map, the guest sees it through a mirrored copy and gives orders to their own pawns, the server
relays frames and falls back to the asynchronous raid when the host declines.

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
