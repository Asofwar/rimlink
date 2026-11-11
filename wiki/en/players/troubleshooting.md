---
pageId: players/troubleshooting
contentRevision: 6
---

# Player troubleshooting

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

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

## Diagnose the failed stage, not the entire installation {#triage}

| Stage | Symptom | Safe check |
|---|---|---|
| Startup | No Multiplayer | Enabled mod, Harmony/version/folder, fresh log |
| TLS | Changed fingerprint | Address, clock, administrator confirmation |
| Login | Password/ban refusal | Exact method/account, no borrowed credentials |
| Collection | Mismatch | Diff category and conflicting-copy path |
| Home load | Old/reset state | Server save, generation/epoch, refusal reason |
| Economy | Unknown response | Original request, mailbox, journal; no new send |
| Live | Waiting/lag | Host online at home, stream state, fresh snapshot |

A report needs zoned time, server, version, action, exact refusal and operation ID, not passwords, Steam tickets or API tokens. A full Player.log may reveal personal paths and other mods' data.

## When should I stop and ask an administrator? {#escalate}

Stop on an unverified changed pin, insufficient replay evidence, repeated post-rollback load refusal or externally changed installation files. Preserve the local folder and diagnostic ZIP; do not edit evidence to produce a “clean” log.

Record whether a second login reproduces it and which checks were tried. Start with a minimal report and selected safe [Rimlink] lines. Share full saves/journals only through a separately agreed method. The built-in ZIP uploads nothing automatically.

## Limits and safety {#limits}

Diagnostics are not uploaded automatically and do not include Player.log or arbitrary logs. Game logs may contain personal data: do not publish them wholesale. Do not delete recovery journals or trust unknown certificates.

## See also {#related}

- [Connecting and signing in](./connect.md)
- [Saves and recovery](./saves-recovery.md)
- [Administrator troubleshooting](../admins/troubleshooting.md)
