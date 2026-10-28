# Rimlink

Persistent multiplayer for RimWorld 1.7: every player runs their own colony in a
shared world hosted by a dedicated server. Colonies are simulated by the player's
game and frozen while they are offline; the server owns everything shared
(accounts, the world map, player settlements, chat, the mod list) and stores the
save games.

Rimlink is a clean-room project: its own protocol, server and client. It is not
compatible with, and shares no code with, other RimWorld multiplayer mods.

## Status

Design stage. See `docs/superpowers/specs/` for the design documents.

## Planned layout

| Path | Purpose |
|---|---|
| `src/Rimlink.Protocol` | gRPC/protobuf contracts shared by server and client |
| `src/Rimlink.Server` | ASP.NET Core dedicated server (gRPC game API, REST admin API, SQLite) |
| `src/Rimlink.Client` | RimWorld 1.7 mod (Harmony) |
| `src/Rimlink.Tools` | Admin CLI (backups, mod list, migrations) |
| `tests/` | Server unit and integration tests |
| `mod/` | Mod metadata, defs, languages, textures |
| `deploy/` | Dockerfile, docker-compose, sample configuration |

## License

MIT, see `LICENSE`.
