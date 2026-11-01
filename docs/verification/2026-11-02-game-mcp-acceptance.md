# Partial game MCP acceptance — 2026-11-02

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

## Final cleanup and integrity

**E/game-cleanup.json** records both private acceptance games stopped; guest workload PID 78080
was verified gone. It also preserves the last partner save path and the rebuilt mod/companion
SHA-256 digests. **E/process-cleanup.json** identifies and records shutdown of only the owned
Rimlink server **PID 65152** (the integration-tree executable) and private GABS **PID 78740**
(`--configDir .../next-features/gabs`, port 8767). Other processes were left untouched.
SQLite `PRAGMA quick_check` returned **`ok`** after shutdown.
The post-fix full Windows result is **1457/1457 passed**, as recorded above.

## Explicit untested edges

The completed coop rerun does not cover server restart, host-side connection loss, a concurrent
second partner, or an assigned pawn's death/capture. Competing auction bids from a third player,
natural expiry and multi-supplier goal allocation were not exercised by this two-client run.
Biography editing and actual invitation clipboard copy/paste remain outside the recorded checks.

No physical-keyboard claim is made for typing `g` in chat: semantic/root key dispatch does not
establish normal focused text-field input. Real Steam refusal/retry and live Discord acceptance
remain deferred because they require the user's credentials/environment. Other unexecuted
residual scenarios in the linked checklist remain unverified; automated tests do not turn them
into game acceptance passes.
