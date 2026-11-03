---
pageId: players/troubleshooting
contentRevision: 1
---

# Player troubleshooting

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

| Symptom | Check |
|---|---|
| Multiplayer missing | Game/Harmony versions, enabled mods, nested folders |
| Certificate refusal | Address, system time, confirmed pin; do not disable TLS |
| Modpack mismatch | Order, versions, hash, settings; apply with game closed |
| Transfers refused | Console/beacons, eligible goods, debt, threat, freeze |
| Live stuck after a drop | Wait for reconnect, check host connection, request snapshot |
| Coop busy | One active partner, owner online at home, eligible assigned pawns |

First preserve the exact error, time, server, action and operation ID. Diagnostic export creates a local ZIP; inspect it before independently sending it to an administrator.

### Export a local report

1. Open **Multiplayer → Diagnostics** in the server list, or diagnostics from the in-game Rimlink overlay. The snapshot is captured when opened; close/reopen to refresh.
2. Inspect the preview and choose **Save local ZIP**. The destination is shown before saving and the filename afterwards. Reports go under `Rimlink/Diagnostics` inside the game's save-data directory; the ZIP contains diagnostics.json and bounded events.log without free-form messages.
3. The default Windows profile is `%USERPROFILE%/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios`; the separate game log is `Logs/Player.log`. A custom -savedatafolder changes this location. The report does not include that log; agree on a safe excerpt separately if required.

## Limits and safety {#limits}

Diagnostics are not uploaded automatically and do not include Player.log or arbitrary logs. Game logs may contain personal data: do not publish them wholesale. Do not delete recovery journals or trust unknown certificates.

## See also {#related}

- [Connecting and signing in](./connect.md)
- [Saves and recovery](./saves-recovery.md)
- [Administrator troubleshooting](../admins/troubleshooting.md)
