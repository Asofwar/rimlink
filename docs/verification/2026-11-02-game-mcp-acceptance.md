# Game MCP acceptance — 2026-11-02

Initial acceptance used the frozen **0.7.0** build of local implementation
`e10a77868e159f241f56a95164a329361fe8c675` (`asofwar/next-features`). The final coop and diagnostics
rerun used a rebuilt 0.7.0 with the one-line diagnostics fix described below. This report records
completed observations, not an all-features pass.
The earlier [implementation verification](2026-11-02-next-features.md) and
[residual checklist](2026-11-01-residual-verification.md) remain separate evidence; their initial
CUA blocker does not describe the later MCP run recorded here.

## Setup and evidence

Two real RimWorld 1.7 clients, `rimlink-next-host` and `rimlink-next-guest`, ran through GABS 1.1.2,
RimBridgeServer 2.3.0 and the matching development companion. The game copy was
`D:/Build/rimlink-next-wt/verification/build/verification/game`, with separate `host` and `guest`
savedata profiles. GABS used the private `.superpowers/sdd/next-features/gabs/config.json` and
loopback port 8767; the disposable Rimlink server used TLS port 25783 and
`D:/Build/rimlink-next/build/game-acceptance/server`. Development Harmony bundling applied only
to this isolated game copy. Test players were **Owner-07** (id 1) and **Partner-07** (id 2).

Actions used real game windows and Rimlink semantic tools. Disposable wallet funding was setup;
server wallet/parcel reads and SQLite snapshots corroborated outcomes rather than replacing UI
acceptance. Raw MCP responses, screenshots and result snapshots are local, ignored artifacts under
`D:/Build/rimlink-next/build/game-acceptance/evidence/` (abbreviated **E/** below).

## Completed flows

### Shared colony: first join and normal exit

The owner invited the online partner in **Colonies**; the partner accepted, and the owner assigned
only Alaska (`Human282`). Joining loaded the owner's map. Guest draft/movement orders and changes
to Research priority (3 → 1) and timetable hour 13 (Anything → Sleep) reached the host.
The clean host UI save retained `Human282` drafted at `(99, 0, 108)`.

The normal in-game Save dialog produced `host/Saves/acceptance-owner-coop-clean.rws` at tick 43336:
`rimlinkWealthTotal=14587`, `rimlinkWealthItems=9724`, `rimlinkCoopLoans={282:2}` and empty legacy
`rimlinkLiveOwners`/`rimlinkLiveIndexes`. After the guest ended the session and returned home,
normal UI saves retained exactly these colonist IDs:

| Colony | IDs after exit | Count |
|---|---|---|
| Owner | `Human282`, `Human285`, `Human288` | 3 |
| Partner | `Human558`, `Human561`, `Human564` | 3 |

Both resulting saves had empty coop-loan and legacy guest-owner dictionaries; pending parcels were
zero. This checkpoint proves first-exit conservation; the later restart/rejoin and revocation
checks are recorded separately below.
Evidence: **E/coop-first-end.json**, the two save paths it records, **E/coop-host-control.png**,
**E/coop-guest-control.png**, **E/coop-owner-after-exit.png** and **E/coop-partner-home.png**.

### Shared colony: persisted grant, reconnect and revocation

Both clients restarted with the rebuilt mod. The existing grant still showed membership and one
assigned pawn, and the partner joined new **session 2** without another invitation. The host
received the partner's Cooking priority 2. An eight-second guest `net_drop` then produced an
observed **Reconnecting → Connected** transition; after reattachment, Cooking priority 3 reached
the host. **E/coop-rejoin-reconnect.json** records session 2, both connection snapshots and
`controlAfterReconnect=3`. Delivery of that new command, not the connection label alone, establishes
that control resumed.

The owner then used **Revoke access → Confirm** in the real Colonies UI, ending session 2.
The partner returned home with `Human558`, `Human561`, `Human564`; the owner retained `Human282`,
`Human285`, `Human288`. Both loan ledgers were empty and the Join button was no longer offered.
Evidence: **E/coop-revocation-diagnostics-fixed.json**. These checks cover client restart,
guest reconnect and live UI revocation; they do not establish server-restart or host-drop behavior.

### Alliance, private chat and treasury lifecycle

Alliance creation, invitation/acceptance and two-way private chat completed in the real Social
window. The joining partner did not receive the owner's `BEFORE-JOIN` history; both saw
`A-PRIVATE`/`B-PRIVATE`. Leaving cleared the former member's alliance view; rejoining did not grant
old chat history. Permission/cap refusals left balances unchanged, including a member with no
withdrawal right, a request beyond the remaining cap and the owner's own cap.

| Observed checkpoint | Owner wallet | Partner wallet | Treasury | Total |
|---|---:|---:|---:|---:|
| Funded setup | 1000 | 1000 | 0 | 2000 |
| Deposits of 300 and 100; withdrawals of 10 and 40 | 710 | 940 | 350 | 2000 |
| Leave/rejoin, remaining-cap withdrawal, ownership transfer and disband | 710 | 1290 | 0 | 2000 |

The treasury audit displayed the deposits, withdrawals and permission changes. At the middle
checkpoint it showed owner `10 / 10` and partner `40 / 50`; the member lacked owner controls.
Evidence: **E/alliance-first-pass.json**, **E/community-lifecycle.json**,
**E/alliance-private-chat-owner.png**, **E/alliance-private-chat-partner.png** and
**E/alliance-treasury-owner.png**. Raw refusal capture:
**E/1793571984870090400-games_call_tool.json**.

### Friends and public-profile preferences

Friendship required the recipient's consent and appeared reciprocally; removal and a later
accepted request were exercised. Profile invitation/notification preferences were saved. Closing
invitations was visible to the other client as **Social invitations closed** and refused a new
request with **That player has closed social invitations**. Re-enabling preferences allowed a
new accepted friendship. Evidence: **E/profile-preferences.json**, **E/friends-consent.png**,
**E/public-profile-closed-invites.png** and raw refusal
**E/1793572093435350700-games_call_tool.json**. Biography editing is not covered: the bridge did
not capture its text area.

### Procurement: buy order, addressed contract and supply goals

All four orders were created and acted on through the real Supply UI. The final order/delivery
and parcel snapshots are in **E/procurement-acceptance.json**, corroborated by raw SQLite reads
**E/1793572410452537900-sqlite-read.json** and **E/1793572410454022300-sqlite-read.json**.

| Order | Observed result | Settlement |
|---|---|---|
| 1: buy 10 Steel at 10/unit | Supplied 4, then buyer cancelled | Supplier paid 40; unused 60 refunded; buyer Trade parcel 1 |
| 2: addressed contract, 5 Steel / 100 | Partial quantity refused; exact 5 completed | Supplier paid 100; buyer Trade parcel 2 |
| 3: supply goal, 3 Steel / 100 | Contributions of 1 and 2 completed the goal | Total reward 100 once; buyer Trade parcels 3 and 4 |
| 4: supply goal, 3 Steel / 100 | Contribution of 1 held, then buyer cancelled | Reward fund refunded; supplier TradeReturn parcel 5 |

After procurement, wallets were **470 + 1530 = 2000**, continuing from the alliance lifecycle's
710/1290. All four orders had zero escrow. The delivery ledger contained exactly five rows:
`(order, quantity, paid) = (1,4,40), (2,5,100), (3,1,100), (3,2,0), (4,1,0)`.
The goal's reward is recorded on its first contribution row after completion; it was not an
additional payment for each contribution. Buyer parcels 1–4 had delivery acknowledgements;
return parcel 5 still had `deliveredAt=null` at this checkpoint. The later auction snapshot records
its delivery acknowledgement at tick 3078. The final normal-UI saves below add actual saved-item
quantity evidence rather than relying only on parcel acknowledgements.

Rate limiting legitimately retained an unanswered contribution in the outbox. The UI displayed
**Retry pending** and the preserved-request explanation; an explicit retry subsequently completed
exactly one contribution. Order 3 ended at 3/3 with only the two expected delivery rows, not a
duplicate reward or supply. Evidence: **E/procurement-rate-limit-pending.png**,
**E/1793572365629612200-games_call_tool.json** (warning sequence 44: “transfer unanswered, kept
in the outbox: too many requests, slow down”), and the final delivery ledger above.
This is expected retry behavior, not a product failure. Multi-supplier reward allocation was not
tested with this two-player pair.

### Auctions: bidding, buyout and cancellations

The real Auctions UI completed the main two-player flows. On auction 1 the partner bid 100,
raised their own bid to 120 (another 20 debited), then bought out at 200 (another 80 debited).
The seller received 200 and the buyer's Purchase parcel 6 was acknowledged at tick 3078.
Auction 2 had no bids and was cancelled by its seller; LotReturn parcel 7 was acknowledged.
Auction 3 received a bid of 100, then the in-game admin cancellation with reason
`acceptance-refund` refunded that bid and created exactly one LotReturn parcel 8. Repeating the
admin cancellation produced no extra refund or parcel. Parcel 8 remained unacknowledged in the
auction snapshot; the later final-inventory check below establishes aggregate item conservation.

Final wallets were **670 + 1330 = 2000**, starting from procurement's 470/1530.
**E/auctions-acceptance.json** records exactly three closed auctions, with close reasons
`buyout`, `seller` and `acceptance-refund`, plus the corresponding wallet/parcel ledger.
Before the third posting, the save-upload limit (3 requests / 30 seconds) aborted the attempt
before the auction Post RPC; selected goods were restored. A later UI repost succeeded without
loss. Raw **E/1793572532313599300-games_call_tool.json** preserves the upload refusals
(`ResourceExhausted`, warning sequences 128 and 130). This refusal/repost is not an auction-loss
defect. Competing bids from a third player and natural expiry are outside this completed run.

### Final saved-inventory conservation

**E/final-inventory-conservation.json** compares the baseline home saves with normal in-game
UI saves `host/Saves/acceptance-final-economy-host.rws` and
`guest/Saves/acceptance-final-economy-guest.rws`, under the isolated savedata root above.

| Colony | Baseline Steel | Final Steel | Change | Final colonist IDs |
|---|---:|---:|---:|---|
| Owner | 1170 | 1184 | +14 | `Human282`, `Human285`, `Human288` |
| Partner | 1170 | 1172 | +2 | `Human558`, `Human561`, `Human564` |

The combined quantity rose from **2340 to 2356**, exactly the **16 fixture units** added
(13 to the partner and 3 to the owner). Thus the completed procurement, returns and auction
flows conserved the fixture's physical items in the saved games, with no net loss or duplication;
all six original colonist IDs remained. At this checkpoint both
outboxes empty and wallets still **670 + 1330 = 2000**. This strengthens the earlier parcel-ledger
checks; it does not prove unrelated item types or unexecuted failure scenarios.

After client restart and the second coop/reconnect run, the saved Steel counts remained
**1184 / 1172** (**E/post-reconnect-inventories.json**). The conserved inventory therefore also
survived reload and the recorded reconnect, rather than existing only in the first running games.

### Diagnostics: archive checks and verified live-role fix

The real dialog saved the frozen preview locally. **E/diagnostics-acceptance.json** records
`previewEqualsZip=true`; inspection of that ZIP confirmed exactly `diagnostics.json` (1439 bytes)
and `events.log` (6008 bytes): **7447 bytes uncompressed**, 1627 bytes in the ZIP, below the
64 KiB limit. The privacy checks passed; the snapshot anonymizes the unrecognized mod as
`anonymous-mod` and omits free-form log details.

The original artifact exposed a **real product defect**: after the coop host was inactive, the
snapshot still reported `liveRole=Host` and `liveState=LiveEnding` from retained session data.
The actual old ZIP reproduced the RED accuracy assertion (`Host` instead of expected `None`).
The one-line `Dialog_Diagnostics` fix, `var live = MirrorMode.Session ?? LiveHost.Session;`, uses
the authoritative runtime session cleared on End. The mod and BridgeTools were rebuilt
successfully, and both game clients restarted.

After session 2 was revoked, a fresh real-dialog ZIP exactly matched its UI preview and reported
`liveRole=None`, `liveState=None`, `liveSnapshotVersion=0`, `liveSpeed=0`, `pausedBy=0`.
**E/coop-revocation-diagnostics-fixed.json** records its path, snapshot and UI summary; inspection
of that archive's `diagnostics.json` also matched the recorded snapshot. Thus the old failing case
is closed by a rebuilt-game result, while the original error evidence is preserved.

The post-fix full Windows suite completed **1457/1457** tests:
1045 server + 285 client + 122 Discord + 5 BridgeTools.

### Certificate-pinned invitations: review, import and refusal

**E/invite-acceptance.json** records that reviewing and cancelling an invitation made no pin
writes. Explicit trust/import created the bookmark and pin without connecting; a conflicting B
fingerprint did not overwrite the existing pin, and an extra token parameter was rejected.
The active connection remained unchanged throughout. The export review displayed the server
address, certificate fingerprint and explicit copy confirmation. This check did not overwrite or
read the user's clipboard; actual clipboard copy/paste is not claimed.

## Provider and harness limitations

Native synchronous RimBridge `save_game`/`list_colonists` calls sometimes accessed map pawns off
the main thread. **E/save-error-full.json** preserves sequence 63 and the stack through
`RimBridgeServer.LifecycleCapabilityModule.SaveGame` → `RimBridgeMainThread.Invoke` → thread pool,
ending in `MapPawns.AssertMainThread` during wealth recount. This is provider-side invocation
evidence, not a demonstrated Rimlink UI-save defect. A native call's success/file-exists response
was insufficient; the normal UI Save route produced the clean wealth/loan metadata recorded above.

Early helpers also used wrong/capitalized labels, incomplete UI paging, stale target IDs or
incorrect cell-info assumptions, and attempted movement while paused. Those attempts were
corrected or excluded; they are not classified as product bugs. Neither a dispatched command nor
stale client `LiveStarting` text alone proves execution: host state and saved data were checked.

## First-run cleanup and integrity

**E/game-cleanup.json** records both private acceptance games stopped; guest workload PID 78080
was verified gone. It also preserves the last partner save path and the rebuilt mod/companion
SHA-256 digests. **E/process-cleanup.json** identifies and records shutdown of only the owned
Rimlink server **PID 65152** (the integration-tree executable) and private GABS **PID 78740**
(`--configDir .../next-features/gabs`, port 8767). Other processes were left untouched.
SQLite `PRAGMA quick_check` returned **`ok`** after shutdown.
The post-fix full Windows result is **1457/1457 passed**, as recorded above.

## Follow-up: three clients and remaining game scenarios

The continuation added **Third-07** (id 3), with a separate Russian savedata profile, on the same
private stand. Artifacts in this section are under **E/round2/** (the directory also contains
later rebuilds; timestamped raw responses preserve unsuccessful attempts).

### Coop: host drop, competing partner and restart

**coop-host-drop-second-partner.json** records a 20-second host transport outage: the same
session 3 reattached and a new Schedule command reached the host. Two partners held accepted
grants, but the second join was refused while the first remained in control. After the first
left, the second joined session 4 and controlled the assigned pawn. This verifies serialization
of active partners, not just the membership rows.

The refused second join exposed missing `{0}` arguments for the `Live.HostBusy` and
`Live.HostOffline` strings. The server now supplies the owner's name in both cases. The two
ColonyAccess tests first failed on missing arguments and then passed. A rebuilt-game rerun
displayed **Owner-07 is in a live session already**, with no formatting error
(**busy-refusal-fixed.png**).

After an actual server restart, accepted grants and assigned IDs matched the pre-restart
snapshot exactly. The partner joined session 5 without another invitation and changed Cooking
priority at the host: **server-restart-preserved-grants.json**.

### Coop: End during snapshot loading, capture and death

A reproducible race left the guest in the host's copy when End arrived during a snapshot load.
Late async callbacks could finish after the runtime live session had ended. The fix guards
callbacks by session ID and schedules the return after the load, on the game main thread. The
copy remains protected from home saves until the own game has finished loading; stale callbacks
are also guarded by connection, game instance and a return token.

An intermediate synchronous-return attempt was rejected: nested loading inside FinalizeInit
caused renderer errors and a wrong-map upload **only in this disposable test account**. Its prior
own version was restored before continuing. This attempt is not counted as a pass.

On the final rebuilt implementation, session 6 exercised the exact late-load branch:
snapshot v30 began loading, End arrived, the stale snapshot finished, and the guest returned
home. A normal UI save contained the original **Human558 / Human561 / Human564**, without the
host's colonists: **resync-end-fixed.json** and `acceptance-resync-end-fixed.rws`.

Session 7 ended with EndGuestDefeated after the host's normal **Try to arrest Alaska (100%)**
job made assigned pawn Human282 a prisoner. The pawn remained at the host; the guest returned
to its own three pawns. Session 8 repeated the end after dev-killing assigned Human285: the
host kept exactly one corpse containing that pawn, and no compensating pawn parcel appeared.
Evidence: **coop-assigned-pawn-arrest.json**, **coop-assigned-pawn-death.json**. Final save checks
found each of the original six pawn records exactly once, counting the intended corpse and
caravan member in their respective home worlds.

### Three-party economy and natural deadlines

- **three-player-economy.json**: competing auction bids 100 → 150 → 200 returned the previous
  bidder's money immediately. A Steel ×3 goal funded with 10 silver accepted one unit from the
  third player and two from the partner; no partial payout occurred, then payouts were 3 and 7.
  The owner received all three physical units and escrow reached zero.
- **auction-real-expiry-and-cap.json**: two real one-hour deadlines elapsed without changing
  clocks or database dates. Auction 4 expired without bids and returned its lot. Auction 5
  received ten successive 30-second extensions, stopped at the exact five-minute cap, and sold
  to player 3 for 210. Parcel counts remained unchanged on a later sweep. A watcher initially
  asserted the wrong enum for Expired; the corrected assertion checks the persisted state 3.
- **compacted-rollback-loaded-map.json**: third-player v5 existed as `5.d1.zst` against its base,
  with no full v5 file. After admin rollback and actual login, Steel ×7 remained at (100,100),
  while Wood ×9 added only in v6 at (102,100) was absent. Loading rebuilt the full old version.
  This proves the loaded map, not the admin panel's rollback button or a backup restore.

### Paid events and real threat restrictions

The original vanilla Infestation worker returned success on a map with no eligible spawn cell,
charging for no tunnel. The final client checks the current map with InfestationCellFinder,
keeps the vanilla insect-faction/hive-count limits and passes the chosen cell to the worker.
It deliberately avoids CanFireNow: review of the installed game showed that its tick cache
is shared across maps. The cache edge is established by source inspection, not a second-map
game reproduction.

**infestation-real-refusal-refund.json** records the final rebuilt negative case: all 125000
saved roof-grid bytes were zero, incident 5 failed with `Infestation could not start on this map`,
and the full price returned once. The repeat balance check found no second refund.
**infestation-real-positive-tunnel.json** records the positive control: after creating a valid
mountain-roof fixture near a colony building, incident 8 created new **TunnelHiveSpawner54932**
at (116,111), present in the normal game save. Earlier unsuitable fixtures correctly refunded;
they were not counted as positive tests.

**incident-real-report-drop.json** captures incident 4 firing while the target's transport was
down for 60 seconds: an actual pirate appeared, the client held `firedUnreported=[4]`, and the
server still held Announced with no ResolvedAt. Reconnection changed it to Fired and cleared
the client marker. Pawn IDs stayed unchanged across two further retry intervals. A normal raid
letter paused play_for after 834 ticks; its false return did not mean the raid had failed.

With that actual enemy still on the map and no paid incident waiting, deposit, lot posting and
map sending all refused. Normal before/after saves had equal Steel/Silver counts, and wallet,
lots and parcel history were unchanged: **real-hostile-outflow-blocked.json**.
The caravan formed before the threat then handed over Steel ×1 through its actual transfer UI.
The owner accepted gift 13 and received it once; carrier Human558 remained in the same caravan
with no Steel inventory: **caravan-real-handover-under-threat.json**.

**visit-real-server-wealth-excluded.json** supplies a positive control for wealth exclusion.
With the same Rough difficulty, adding one million fixture silver raised home threat points
from 35 to 2640.40. In the owner's snapshot they remained 35 with both one and two million in
the visitor's wallet. Returning home restored 2640.40 (the high-wealth curve had saturated).
Both million-silver fixtures were removed and Peaceful restored.

### Biography, clipboard and Russian screens

The real multiline profile field accepted **Партнёр колонии. Проверка биографии 07.**; the other
player's public profile displayed the saved text (**biography-public-read.png**).
The invitation's actual Copy button placed the expected 102-character pinned URI in the system
clipboard. Its prior plain text was restored; preservation of rich clipboard formats is not
claimed. Actual Ctrl+V remains unverified because CUA foreground acquisition failed.

The Russian client displayed procurement creation, the populated auction board, community tabs,
shared colony, diagnostics and server list: **russian-ui-labels.json**, **ru-procurement-create.png**,
**ru-auction-board.png**, **ru-community-1.png** through **ru-community-4.png**,
**ru-colonies.png**, **ru-diagnostics.png** and **ru-servers.png**. This is a normal-content screen
sample, not every long-text, permission or error combination. An older English away-letter title
was observed; full localization coverage is not claimed.

### Final checks and cleanup after the follow-up

One narrow review of the five acceptance-fix files found the Infestation cache issue described
above; it was fixed in the same pass. No other P1/P2 finding was reported. The final game mod
build succeeded with zero errors, and the full Windows suite again passed **1457/1457**
(1045 server + 285 client + 122 Discord + 5 BridgeTools). The prior two-CPU Linux run remains
evidence for the earlier feature implementation, not a fresh run of these game fixes.

**round6-game-cleanup.json** records both clients home, zero pending outbox/parcels, no final
game errors, exact original pawn occurrences, final mod SHA-256 and verified game shutdown.
Wallet totals reconcile: 10629 + 1222 + 793 = **12644**, equal to 13000 fixture capital minus
356 charged for the four Fired events. Failed orders refunded. No artificial wealth fixture
remains in those balances.
**round6-process-cleanup.json** records stopping only owned server PID 90920 and private GABS
PID 86204, with no private game remaining. **round6-db-integrity.json** records SQLite
`quick_check=ok`. The user's main checkout, game installation, Docker and Workshop were not
updated; the work remains on the local feature branch.

## Explicit remaining limits

The limits above describe rounds 1–6. [Rounds 7–8](2026-11-02-final-edges.md) subsequently verified G,
user-performed Ctrl+V, browser-button rollback, SteamWait with a controlled verifier, real upload
failure/recovery, offline/restarted auction delivery, Post/Bid/Cancel reply loss and full/corrupt mailbox.
Archive/reset escrow returns were tested on a separate copy of the stand.

The overall result is **not PASS**: [admin reset resurrected a local save](2026-11-02-reset-resurrection.md).
Live Discord still lacks a configured profile; the new Steam test did not verify the real Web API.
Power-loss recovery, all Scribe failure modes, all rate-limit/game combinations and long/error layouts
are not covered by these runs. Unchecked compound checklist items remain unchecked.
