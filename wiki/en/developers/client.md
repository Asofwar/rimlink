---
pageId: developers/client
contentRevision: 39
---

# Client, mod and live

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Client.Core uses async/cancellable network calls, SharedState for UI and an outbox for unanswered mutations. It has no Verse/RimWorld references. The mod touches game objects only on the main thread: post background results through MainThread.Post. Keep one Harmony patch per method and explain why it exists.

In the current source, LoginFlow reuses SaveMods.Missing for the player's own colony. Active mods are read on the main thread. An incompatible local recovery save is refused before upload; a downloaded save is refused before pending.Clear, RecoverSelection and map loading. The normal Fail path closes the session and retains the file. This protects against missing save dependencies even during a manual administrator login; release 0.9.2 does not contain this guard yet.

ThingBlobs transfers pawns/items. Entry into a world with possible ID collisions needs newIds:true and ResetMapIndex before spawning a previously spawned blob. References outside the blob must be reconciled to the receiving world. Pawn equality uses thingIDNumber: never Destroy a deserialized copy equal to a live pawn.

A live mirror is not an independent simulation. Coop rights over owner pawns differ from ownership of visiting guest pawns; cleanup must not mail owner pawns away.

## UI intent and the main-thread boundary {#client-intent}

UI captures selected data and creates intent; Core sends a cancellable request and updates SharedState. Network callbacks must not read/create Verse objects; apply game results through MainThread.Post. Cancelling a wait differs from cancelling an accepted server operation.

Transfers persist journals and an exact snapshot before mutation. Replay original intent instead of rebuilding goods from the current selection. For receipt, save the delivered map before acknowledging. Core tests do not verify Scribe/ThingBlobs or map loading.

## Pawns: IDs, relations and return {#pawn-lifecycle}

A deserialized pawn can equal a live one by thingIDNumber; destroying the copy may affect the real pawn. On cross-world entry regenerate IDs/map index and reconcile external faction/ideo/policy references. Use existing PawnExit/RelationLedger return paths to preserve bilateral relations and UI caches.

Distinguish object owner, command-authorized player and map writer. Coop assigns host pawns; live visits transfer guest pawns. Shared cleanup must never mail host pawns to a partner. Verify this with two actual clients.

## Guild battle map isolation in development {#guild-battle-isolation}

A server without the setting enabled refuses these calls. Source now contains a separate BattleGameContext installed before loading a foreign map and retained until the pinned home snapshot is verified. A durable marker binds the home's world/boundary/generation/epoch, version and raw hash; a damaged marker is retained for recovery. Startup reconciliation of this marker and the full battle viewer are not wired yet.

The context blocks personal save/upload and autosave, parcel/return materialization, item/pawn departures, ordinary raid/live/visit and shop flows. Deferred reconnect replies recheck the context. PawnCustodyRegistry does not treat the battle map as home; reserved rimlinkBattleScope XML is refused by the server's personal Upload without replacing the canonical save. Handoff requires a completed home upload, an empty unresolved outbox and no unfinished home operations. A failed home return retains the block and marker.

The shipping mod now has BattleScenario, BattleTickController and BattleArtifactCapture modules without a RimBridgeServer reference: an independent template, two factions/eight fighters, initial-save reload, a main-thread tick gate and durable final files. This is not an integrated end-to-end executor yet: startup, complete command/view transport, cold-final orchestration and native acceptance remain unfinished. Battle orders allow only Move/Attack/Draft; ordinary live/manual orders in this context are blocked until dedicated battle input is wired.

Additional isolation covers paid incident/raid callbacks: they do not create incidents, raid letters or personal receipt state on the battle map. Viewer handoff also refuses a threatened home colony. Standard fighters are generated without a random xenotype and with equal reset needs; these native conditions have not been exercised in-game yet.

BattleRunningInbox validates the whole packet before the first game callback, then delivers commands, cuts and control projection sequentially; older Position values are ignored and conflicting equal positions refused. A partially completed callback failure requires recovery. BattleExecutorRunningPump uses MainThread.Get, sends receipts and maintains executor heartbeat; ordinary player login never starts it. TickController accepts only its connection epoch and fsyncs truthful results to a separate bounded hash-chain BattleReceiptJournal before network acknowledgement. After terminal, a late unexecuted command receives NotExecuted/Terminal, while a previously executed command replays its original receipt. A cold journal read exposes observations and never resumes simulation.

The arbiter sends pending command receipts and control-cut acknowledgements before the next poll. If the reply is lost, the receipts stay queued for retry after reconnect, and the next delivery waits. When cleanup cancels its own heartbeat RPC, the cancellation lets the arbiter proceed to Entry and confirm cleanup.

Simulation ticks use compact 83-byte samples for the fixed 4×4 roster. A successful mod build verifies source compatibility with the installed game; operator startup, cold orchestration, viewer and the complete game flow still require integration and acceptance.

The executor checkpoint is atomically fsynced before game work and retains endpoint/pin/profile, original assignment and ordered stages. Unknown fields, truncated documents or retained partials require recovery and are preserved. Cold reads grant no tick authority: they require a complete immutable envelope and verify raw initial/final/simulation/view hashes. BattleFinalDelivery sends bounded chunks and exact Final; a lost reply retains FinalCaptured until the server reply is recovered. Incorrect artifact references, progress or receipts cannot mark Applied. The executor credential is never stored in the checkpoint. Full startup/cold orchestration and physical game acceptance remain unconnected.

## Limits and safety {#limits}

RimWorld 1.7 uses Waylith/CoreCLR, not Unity; UnityEngine is a stub. Inspect the actual game type before guessing behavior. Local compilation/unit tests do not replace two real clients for gameplay changes.


The GuildDeliveryJournal retains the exact request, typed parcel, BEFORE proof, a branch ID kept outside the save, durable SpawnStarted, authorization and accepted GuildDeliveryReceipt. It uses the existing TransferOutbox atomic writes and fsync with a separate family decoder. Legacy ParcelIds cannot complete GuildBeginDelivery. GuildCaptureClient binds calls and replies to the current login. On the main thread GuildIncoming checks manifest and defs, creates bounded stacks, and writes field 12 before final ACK. Both isolated test deliveries reached accepted HOME snapshots after a fresh login. Automatic delivery while connected still needs a native retest.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Persistent shared colony](../players/coop.md)
- [Protocol and idempotency](./protocol.md)

The local executor.checkpoint pins endpoint/pin/profile and the original RuntimeAssignment before game work. It checks legal transitions, prepared/Start/Final/cleanup references, and complete raw hashes of frozen artifacts. A cold read only permits resending a complete Final envelope; partial or unknown outcomes are preserved for recovery without replaying the initial. A confirmed Cleaned checkpoint is atomically moved to completed, preserving its receipt before another assignment. RestoreBoundary is mandatory in explicitly selected operator configuration.

Core now includes a manifest builder combining server ReadPreparation facts with native measurements, Final redelivery with progress/receipt checks, and the native RuntimeStarted barrier. The native controller does not tick before readiness acknowledgement and includes time already elapsed since the original Start. Operator startup is connected; the complete viewer and three-process acceptance remain pending. An ordinary client does not read operator credentials.

The operator starts with explicit `-rimlinkExecutorConfig`, its own `-savedatafolder`, a process lock and a checkpoint before loading a map. Ordinary login, saving and personal game ticks are blocked in the executor process. The isolated run used one independent executor and two ordinary player processes.

Pre-Start `BattlePreparedViewer` is wired: a dedicated durable HOME copy is created in the pre-upload callback, then checked against successful server SaveInfo and the raw XML hash. Initial bytes arrive in bounded pages, the marker is written before LoadGame, and the viewer does not tick. In one native run the attacker opened the mirror and returned home without saving the mirror. Both viewers, cold recovery, and running viewer/deltas have not completed verification.

## Guild command journal {#guild-operation-outbox}

Consent, campaign declaration, defender assignment and claims persist exact protobuf bytes before sending through the existing TransferOutbox. Cold recovery checks the digest, player, generation/epoch, restore boundary and selected acceptance stand. Replay retains the original request UUID; changed intent using that UUID is refused. An unknown reply stays on disk. Personal-save Removed/Returned lists cannot acknowledge these commands. These operations are not unresolved physical custody; the separate GuildBeginDelivery retains its stricter barriers.

The dev companion exposes `rimlink/guild_outposts`, `rimlink/guild_consent`, `rimlink/guild_campaign` and `rimlink/guild_claim`, calling the shipping Core API/outbox from the own colony. This is not a finished player screen or evidence of native resource delivery.

## Battle compatibility {#battle-compatibility}

A profile inventory holds four pinned files: the game build, the runtime build, the client core and the Rimlink client. The mods' gameplay content is pinned by the loaded-definitions document (the gameplay file hash of every mod and the list of its definitions). At start and before a battle the arbiter checks mods, DLC, mod settings, definitions and the pinned files under its own roots; other files in those directories are not examined. A viewer (`VerifyViewerRuntime`) checks builds, mods, DLC and definitions and does not check mod settings or where files live: it does not simulate the battle. The definitions document has the format `rimlink-battle-loaded-defs-v2` and no longer embeds the settings hash. A run with `-rimlinkExecutorCapture=<folder>` captures the profile of that installation: the roots are the game directory, the runtime directory and the directory of every loaded mod, wherever it is. The operator procedure is in [arbiter preparation](../admins/passive-outposts.md#guild-arbiter).

## Executor operator-file format {#executor-settings-format}

The separate BattleExecutorSettings JSON uses strict PascalCase: Version, ExecutorId, AcceptanceStandId, WorldInstanceId, RestoreBoundary, Endpoint, CertificateSha256, ProfilePath, ProfileSha256, ProfileDigest, ArchiveRoot, CredentialPath, StateDirectory and Roots with Kind/Id/Path fields. camelCase or an unknown field is refused before runtime startup. StateDirectory must be inside the actual selected separate savedatafolder. CredentialPath points to a separate local secret; settings and status do not contain the secret itself.

The idle original process reads an explicit `restore-cleanup.json` from its own StateDirectory and verifies the selected completed checkpoint, exact runtime identity and local profile. It checks Entry on the main thread again before sending the proof, and finishes in `RestoreCleaned` after verifying the new receipt. The archived checkpoint also prevents a delayed copy of the same assignment from being accepted as new work. See [backup and recovery](../admins/backup-recovery.md#guild-domain-v3).

A replacement starts at Entry with no unfinished local assignment. The operator supplies the exact historical assignment ID and Start digest in the request file. The client answers the local process challenge only over a pinned loopback TLS connection, then uses the recovery lane to attest Entry and verify the new cleanup receipt.

After a guild restore, the client keeps old delivery journals and local pending saves outside the replay queue. The authenticated source bridge changes the current metadata scope and preserves the original receipts. The client must capture a fresh save before it can materialize a pending parcel in the restored colony.

When the initial arena is refused, the executor log retains the original exception and stack. Initial-state validation reports the actual map, fighter or equipment parameters; the runtime and immutable assignment remain in RecoveryRequired. Diagnostics authorize neither simulation replay nor automatic cleanup of an unknown outcome.

The standard native baseliner has exactly two identical cosmetic endogenes, Hair_DarkBlack and Skin_Melanin6. It sets hair RGB(0.2,0.2,0.2), skin RGB(242/255,199/255,140/255) and alpha 1. Before initial save and after reload, validation checks the active set, vanilla types and definitions, identity and actual colors to within 1e-5, including lazy reads. RimWorld 1.7 generates appearance genes even without Biotech. The isolated run saved and loaded both mirrors with these checks.

During a transient transport outage RecoverTransport retries the current lease heartbeat on each iteration. Re-registration begins only after an explicit lease refusal; publishing a protected commit does not itself revoke the lease. This avoids an empty Entry hello loop when an assignment already exists but has not reached the runtime. In a native run the arbiter crossed real publishing cuts, received its assignment and saved/strictly reloaded the eight-fighter initial. Full HOME acceptance remains pending.

The prepared viewer matches fighters by ThingID, which the arbiter writes into EntityId. GetUniqueLoadID adds the Thing_ prefix and represents a different format. On the isolated stand both players opened the same eight-fighter map without Start or ticks. Attempting to save the mirror produced no file and left both current colony versions unchanged.

Cold login with a marker and return after reconnect compare the same endpoint, player, world, generation/epoch/boundary and current SaveInfo version with the pinned HOME before and after loading. Ordinary pending upload is skipped, mods are checked before loading, and the marker clears after verifying the colony. Unknown or replaced HOME stays in recovery. Outside the battle context the client checks the name and XML marker before loading: in game, a renamed file was refused. On the isolated build, the defender returned to the same colonists after a full restart and the attacker after reconnecting. Current server versions and hashes were unchanged. An isolated crash before mirror loading left the HOME marker and current server save intact. With a required test mod disabled, cold login stayed at the main menu and named the missing mod. After restoring the mod, login recovered the same colonists and cleared the marker. On a second isolated run, a forced exit from an already loaded mirror also restored the same colonists. Read-only server checks matched the original save version and raw SHA-256 after both recovery paths.

A new attempt to open the prepared battle after reconnecting uses the current authenticated login. The client pins that login for the handoff and stops loading the arena if it changes. The player and canonical HOME checks still apply.

The v2 gameplay definition fingerprint covers load order, content hashes, loaded definitions and gameplay mod settings. It excludes only the local `RimlinkMod` preferences file, including saved server addresses. Delivery still checks the canonical world, game build, DLC and confirmed fingerprint.

On the battle map the attacker explicitly confirms readiness. The defender joins the command channel separately. Select a fighter on your side, then right-click a cell for Move or an enemy for Attack. The fighter button sends Draft. Positions update in the mirror without local ticks. After reconnecting, the client requests a full frame from the current session. On the isolated stand both players controlled the battle, received the same committed result and returned to their own colonists. The arbiter now handles cancellation of its own running poll as a normal stop before cleanup. The native cleanup retest is pending.
