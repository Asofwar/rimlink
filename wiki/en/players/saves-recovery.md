---
pageId: players/saves-recovery
contentRevision: 4
---

# Saves and recovery

Applies to: Rimlink 0.9.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

<ol class="flow-diagram" aria-label="Process diagram"><li><strong>Stop</strong><span>Do not recreate the transfer.</span></li><li><strong>Preserve evidence</strong><span>Keep snapshot, upload and outbox.</span></li><li><strong>Rejoin</strong><span>Same account; original replay/server save.</span></li><li><strong>Verify outcome</strong><span>Map, status and mail; escalate refusals.</span></li></ol>

The server holds the canonical save. Let normal saving and upload finish before leaving. Reconnect after a disconnect: unanswered operations are retried from the outbox with their original identifiers, not recreated.

In 0.8.1 recovery of unfinished deliveries is tied to an exact save snapshot. When recovery is offered, read the selected version and warnings. Do not load an arbitrary old local save to “recover” items already sent away.

If recovery cannot finish, preserve journals and give the administrator the server, time, save version and operation ID. An administrator can inspect versions and perform a controlled rollback.

## How are transfers and deliveries saved? {#save-cycle}

A send follows: removal journal → exact home-map snapshot → server acceptance → original operation → result acknowledgement. Delivery is acknowledged only after a snapshot contains the received goods. Two saves on the same paused tick can still contain different state.

Login recovers an unfinished local snapshot before fetching mail. Do not grant returns just because a parcel still appears: the server may be awaiting evidence of delivery. Reconnection replays the original intent and ID, unlike a new request.

Normal return-to-menu lets saving finish. Killing the process is not a safe way to cancel a trade.

A new colony can be saved while paused before its first game tick. Tick zero comes from the game clock, independently of similarly named lists in event history. If upload is unfinished, preserve the original snapshot for recovery at login.

## Decision tree after a drop, rollback or reset {#recover-tree}

| Situation | Next action | Avoid |
|---|---|---|
| Unknown transfer result | Rejoin with the same account, wait for replay, inspect result | Creating a duplicate transfer |
| Save predates the last transfer | Load the server-accepted version | Renaming an old file as current |
| Administrator rolled back | Return to menu, then sign in again | Writing from the cancelled map |
| Colony reset; old file separated | Load current server colony/start as instructed | Uploading *.reset-*.rws as a new version |
| Insufficient replay evidence | Preserve the folder and request help | Deleting journals to dismiss the dialog |

Keep pending-snapshot.json, upload-*.rws and outbox.json in `Saves/Rimlink/<server>_<player>`. Share time, operation ID and refusal text safely. These files may contain colony data; do not publish them. Rollback changes SaveWriteEpoch; reset changes SaveGeneration. Neither rewinds the entire settled server economy.

## Limits and safety {#limits}

Never delete outbox or recovery journals based on an untrusted message. A rollback changes the colony timeline; it is not a rollback of the whole server economy.

## See also {#related}

- [Backup and recovery](../admins/backup-recovery.md)
- [Player troubleshooting](./troubleshooting.md)
- [Wallet, mail and market](./economy.md)
