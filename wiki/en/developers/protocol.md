---
pageId: developers/protocol
contentRevision: 34
---

# Protocol and idempotency

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The protocol only grows: add fields with new numbers, messages, proto files, ErrorCodes and ServerEvent bodies. Never reuse numbers. events.proto imports feature protos; features must not import events.proto and create a cycle.

Mutations use client_request_id. Unknown outcomes after disconnect are retried through the outbox with the same ID and original intent; a new ID means a new operation. Validate names, sizes, paths, limits and legal state at the boundary. Rate/size refusals return typed ErrorCodes rather than escaping crashes.

ProtocolVersion.Current is 1 and changes for incompatible contracts, not every release. Additive wire compatibility does not mean an old client displays a new feature.

## A mutation contract is more than a protobuf field {#mutation-contract}

Specify input, authority, state predicate, atomic result and retry behavior for a mutation. Persist client_request_id with original data until a definite outcome. Timeout is not proof of server rollback. Reusing the ID must retain result semantics; changing its payload must not create a new operation.

Test size/name/permission refusal before effects, concurrent requests and replay after commit before response. Live rows must be read inside LiveStateLock; checking outside races.

Wire compatibility, UI version and collection hash are separate. A new message does not authorize unsupported old-client UX without defined behavior.

## Executor startup and order contracts {#guild-executor-client-contracts}

Client.Core source has a separate BattleExecutorConnection using only the BattleExecutor service and RimlinkExecutor authentication, without player login. The operator supplies the TLS pin beforehand: first-seen/changed certificates are never trusted automatically. Returned executor/lease/fence/profile and work world are checked. Ordinary startup does not read operator settings: reading is selected only by an explicit `-rimlinkExecutorConfig=<absolute-path>`; wiring this argument into game startup is not finished. A separate credential path keeps bearer bytes out of JSON, profiles and status.

BattleExecutorSettings refuses unknown/duplicate JSON keys, HTTP, relative paths and invalid hashes. BattleExecutorProfile checks the profile's raw SHA separately from its semantic digest, the full reference closure and the complete file inventory under explicit roots. An extra file, changed mod/load order/DLC/config/loaded-defs projection, symlink or actually loaded assembly outside the inventory closes admission. Exclusions are exact approved operational paths only. The loaded-defs projection describes provenance/actual identities, not serialization of every runtime Def field or deterministic replay.

BattleCommandV1 is a canonical bounded document up to 16KiB with fighter_id and one Move/Attack/Draft. Actor/side come from server authorization; the parser checks the owned fighter, enemy target and arena bounds, rejecting unknown or duplicate wire content. BattleFinalCapture binds one terminal sample, final snapshot, simulation journal, terminal view and immutable envelope; premature/late gameplay wins fail the shared final validator. These contracts do not establish a complete network command/view loop or Product readiness.

GuildCapture.AttackerReady and GuildCapture.ReconcileControl are added as gated player RPCs. Ready binds the exact proposal; ReconcileControl after Start grants the preassigned commander control, replaces the epoch on relogin and preserves StartManifest. Capability is supplied as a digest; history contains no secret or bearer. Control TTL is at most two minutes and cannot exceed battle/slot end; new grants are limited to one per five seconds per side and 512 per battle, while the RPC allows one retry within 2/5s. Defender nomination exists in the internal common authority service before Start; its player screen/API is not wired yet. These RPCs do not enable gameplay without Product qualification.

The current WIP adds GuildCapture.AttachControl, SubmitCommand, ControlHeartbeat and executor PollRunning/AcknowledgeCommands. The server derives the side and connection epoch; it checks the capability SHA256 and binds the channel to current session/membership and the HTTP/2 connection ID. Only canonical Move/Attack/Draft commands are accepted, with at most 20 new commands/s per side and 64 pending per side. An identical UUID retry neither creates a command nor consumes that budget. Outer RPC limits allow retries: SubmitCommand and executor poll/ack 40/s, heartbeat 4/s, attach 4/5s.

Before returning PollRunning, the journal fsyncs Dispatched for at most eight commands. A lost reply replays the same delivery ID. Session loss, grant expiry or 30 seconds without activity closes the channel: the server marks only undispatched commands NotExecuted. Dispatched commands require actual executor receipts; late Executed remains truthful. A replacement channel waits for CutApplied, and Final refuses unresolved dispatch outcomes or cuts. Journal Position orders packets; an older reply cannot restore control. This is a server contract and connected native transport module, pending complete game acceptance.

HelloBegin/HelloComplete separately verify profile/build/inventory, world/boundary and a one-use server-boot/credential-generation challenge. The cache holds at most 32 handshake records with a 30-second TTL; an exact complete retry returns the original lease. Execution requires Entry without Game/player session, or the original runtime and retained native context when occupied. RecoveryDelivery binds to a specific battle/StartDigest and can deliver immutable Final using an as-of-Start historical profile after revocation or configuration rotation, while retaining current authentication. A courier receives no Poll/Prepare/Start/commands and cannot assert SameProcessEntry for the previous process. Legacy Register is health preflight without Execution admission.

The common GuildBattlePrepared receipt includes immutable RuntimeAssignment: original process/lease, nonce, scope/fence, profile approval/build/inventory and server time before the first work reply. Same-process transport reconnect never rewrites assignment or Start; Start replay returns the original authorization. This does not permit rebuilding simulation from initial after losing the process.

## Limits and safety {#limits}

A schema does not establish authorization: enforce permissions on the server. Influence expected version/context and requestId protect against delayed operations. Do not log secret-bearing payloads for debugging.

guild_resources.proto defines GuildColonyScope, GuildSnapshotProof, GuildResourceParcelPayload, GuildSaveMetadata, and GuildDeliveryReceipt. Parcel.guild_resource_payload uses field 14 and ParcelKind 12 identifies guild resources. The payload contains only Steel/WoodLog, quantity, confirmed manifest valuation, origin, and a fixed recipient; it has no Scribe blobs or PawnCustody. GuildSaveMetadata exists only in OutpostSaveMetadata.guild_metadata=12, the common rimlinkOutposts XML, and SaveHeader.outpost_metadata=16. GuildVersion is negotiated separately: LoginRequest/RegisterRequest field 9, LoginReply field 18, and SaveHeader field 20; the latter contains a version only, not a second metadata copy. Retained family history requires a compatible client even after delivery acceptance.

GuildCapture.BeginDelivery pins the exact BEFORE snapshot/hash and returns authorization; GuildCapture.Manifest reads the original catalog of a specific parcel. A local receipt has no self-referential future raw hash: that hash is stored externally in the new SnapshotAccepted/OutpostSnapshotLink. Acceptance updates the attempt, claim, parcel, and immutable first accepted snapshot in one authority transaction. GuildUnavailable=79 and GuildDeliveryInvalid=80 are additive errors. Until guild capture is enabled on the server and V3 is activated, the RPCs return typed refusals and login replies keep GuildVersion at 0. The contract and successful compilation do not enable gameplay.


guild_battle.proto contains separate BattleExecutor.Register, Heartbeat, Poll, UploadInitial, ProposePrepared, and Start calls. Registration returns a lease with executor ID, random lease ID, increasing fence, and semantic ProfileDigest. Register/Heartbeat/Poll have a 1 KiB semantic message limit; the service receive limit is 256 KiB + 16 KiB to accept a snapshot chunk ≤256 KiB with control headers. A proposal is limited to 16 KiB and its canonical prepared manifest to 8 KiB. The dedicated RimlinkExecutor credential is separate from player Bearer authentication; refusals use BattleExecutorRefused=81, while default OFF returns GuildUnavailable. Poll/upload/proposal require a verified published V3 cut and server readiness; a handshake does not enable capture.

battle_manifests.proto defines canonical Inventory/Profile/PreparedManifest documents. Inventory is bounded to 8 MiB/16384 files/256 mods, profile to 16 KiB, and the roster to exactly four fixed fighters per side. Unknown/duplicate fields, noncanonical bytes, ambiguous paths, extra orders, and altered numeric rules are refused. Artifact.sha256 hashes raw bytes; ProfileDigest and ManifestDigest use separate discriminators/versions and do not replace raw hashes. The server retains full approved bytes and reference closure with common approval/revocation receipts; a config digest alone is insufficient. Prepared proposals are durable and immutable, with at most 8; a newer sequence invalidates earlier Ready. Internal AttackerReady creates one grant for the exact manifest and current session/membership; bearer tokens never enter the archive. StartCommitted rechecks these bindings and copies the exact bytes into the protected GuildBattle with initial/start artifact descriptors. A lost reply returns the original StartedAt and authority head. Battle results, the native executor, and the client battle flow remain unqualified.

## See also {#related}

- [Storage and escrow](./storage.md)
- [Client, mod and live](./client.md)
- [REST API reference](../admins/api.md)

Server WIP also implements UploadResultArtifact, Final, CleanupChallenge, and Cleanup. The immutable executor envelope retains its original runtime/start lease/fence/nonce and StartDigest; the current transport lease may change during redelivery. Complete initial/final/simulation/view artifacts and formal terminal predicates are required. The server separately seals the command journal with watermarks and binds it to the envelope in BattleCommittedFinalV1. Raw artifact hashes differ from the semantic committed digest. Exact Final replay returns the original receipt/head, including after cleanup; a conflicting envelope is refused.

A cleanup challenge lasts 60 monotonic seconds and binds current lease/scope/result/runtime/inventory. Proof requires Entry without a loaded Game/map/tick runtime/home marker. Only common GuildExecutorCleanup releases the physical slot; registry projection is published under S/B after the external commit. A timeout, fresh registry, or absent process alone is not cleanup. These are tested server contracts, not a qualified native flow.

The server WIP ReadPreparation returns the full manifest basis: original RuntimeAssignment, stock/remainder at the production cut, acquisition reservation, slot, and commander session/membership bindings. The game adapter adds the measured initial snapshot, native tick, and eight entity IDs. Reading this basis neither writes a financial transition nor authorizes ticks.

ReadReadyOffer supplies a nonce and exact proposal; AcknowledgeReadyOffer automatically acknowledges delivery to that session and does not replace explicit AttackerReady. NoAttackerReady requires a healthy original executor, complete initial artifact, and acknowledged delivery of the current offer before the deadline. Other unproven cases end as TechnicalBeforeStart without a penalty. A prepared cancelled battle remains Active/CancelledAwaitingCleanup with no Start or Final.

Cleanup terminal uses an explicit AppliedResult or PreStartCancellation tag. The former references the committed Final digest; the latter references real terminal/assignment transition IDs without inventing a Final. The original runtime may confirm its own Entry through historically bound RecoveryDelivery; a different courier runtime cannot attest Entry for it. Replaying old cleanup cannot release a new active assignment's hold.

For a restored terminal assignment, RecoveryDelivery Hello binds the new boundary and fence, the original assignment and nonce, and a fresh retained Entry measurement. The lane authorizes cleanup only for that restored scope. A repeated CleanupChallenge can return `committed_receipt` after the common cleanup commit, including after lease rotation.

A pending guild parcel can carry `guild_restore_scope` in its mailbox envelope. This is the current scope proved by the restore plan. The immutable `guild_resource_payload` still carries the original scope and supplies the digest for BeginDelivery. The new authorization requires a current canonical snapshot, and old attempts cannot accept a receipt under the new boundary.

For local Windows attestation, `local_process_required` makes the Hello client answer the server nonce through a per-challenge named pipe. Windows supplies the client's PID. `replace_restored_executor` selects a distinct RecoveryDelivery Hello for the restored scope and a fresh Entry. That lease can submit `ProvedReplacement` for the selected assignment and replay the committed cleanup receipt.

After a lost Start response, the original live process retaining the unchanged initial can use StartAcknowledgementPending and receive StartReplayOnly: it retries the original request UUID without new simulation, proposals, or dispatch. RuntimeStarted records proof of the bound controller in the transport journal. Commands are not dispatched before this record, and the native adapter holds the first tick until it validates the reply. Retry preserves the original twenty-minute limit; a cold process cannot resume a battle. Native end-to-end qualification remains outstanding.

Additive `GuildRuntimeTarget` carries mode `IsolatedAcceptanceV1` and stand ID in Login/Register/reply and the executor Hello binding. Existing field numbers are preserved; the target participates in the challenge digest. In an isolated stand, `GuildVersion=1` requires common V3 readiness and guild admission. Before world binding, the target can confirm the selected connection with `GuildVersion=0`; a server without capture enabled answers 0. The target does not bypass session, role, phase or authority guards.

`ReadPreparedEntry` lets the current commander discover the prepared battle before saving HOME. It streams the approved profile, compatibility descriptor and inventory in chunks of up to 256 KiB. The client checks their digests and maps inventory roots to the loaded game, runtime and mods.

`ListOutposts` returns active campaigns for the caller's current guild in the requested world and boundary. Each row gives the scheduled UTC time and that guild's commander. The server marks defence assignment as available to the current defending leader while the window for assignment is open. `AssignDefenderCommander` rechecks the leader, membership, battle state and deadline when it commits.

`ReadPreparedView` and `ReadPreparedInitial` then expose the pre-Start map to the current commander with an accepted current HOME snapshot and current session, membership, profile and fence. Pages are limited to 256 KiB and pinned to the manifest digest. These reads have no Ready, Start or financial effect. `BattleViewerCompatibilityV1` references the complete inventory and exact viewer build, keeping the descriptor within 16 KiB.

## Guild setup and resource claims {#guild-governance}

`GuildCapture.ListOutposts`, `PreviewConsent`, `AcceptConsent`, `DeclareCampaign` and `ClaimResources` use `GuildWorldScope` and the current authenticated player identity. The server checks leader/member, colony, epoch, rules and common V3 authority. Consent includes the exact digest of the displayed terms; mutations carry a stable client_request_id. Consent replay returns a separate mutation receipt; read the complete state again. A declaration returns the actual reserved window and profile/rules digests. A claim fixes its source, revision, recipient, generation/epoch and quantity.

Reads do not settle production or change the head: ownership/windows are public to players; projected current stock and old-owner obligations are limited to current members of the corresponding guild. An ordinary OFF host refuses all five RPCs even for a new-version client. These interfaces do not prove complete in-game capture.

## Cut publication and brief admission refusal {#guild-cut-publication}

Protected writes and verification can temporarily withdraw GuildCut under S. Executor authentication on a qualified isolated host then returns retryable Unavailable and continues to enforce credential, source and role checks. Transport waits for S and compares the published cut with the committed head under S and the battle lock. Verification finishes under S if the request is cancelled after withdrawing a verified cut. A cancelled write can republish the previous cut only after rollback succeeds before the pending write. Failed evidence and interruptions after the pending write keep admission closed. Retry does not extend leases, the original wall budget or grace.

A server restart can leave a committed assignment that its executor has yet to receive. The same runtime can establish a new lease with a fresh Entry proof if its scope, profile, inventory and build match the assignment, the battle has no Start or Final, and there is no initial upload or prepared proposal. Poll returns the original preparation or, after expiry, the original tagged cancellation. Physical occupancy stays held until verified cleanup. A different runtime and an assignment with any uploaded arena evidence remain refused on this path.

When validation refuses a complete world manifest, the server frees the temporary upload slot. Correct the document or update its definition catalog, then upload with a new ID. Interrupted uploads can still resume with the original ID and digest during the existing retention window.

An exported `OutpostManifestDocument` includes the 32-byte `gameplay_defs_sha256` in the `outpost-game-defs-v2` format. The client checks this field when present. A document without it is checked against `game_defs_sha256` in the original v1 format. New documents keep both fields. The hash of the complete binary manifest and its binding to the specific parcel remain mandatory.

BattleFighterSpec.baseliner_appearance_version (13) binds standard appearance to the roster digest: version1 means fixed core Hair_DarkBlack/Skin_Melanin6 endogenes and fixed colors. Historical0 remains readable; unknown and mixed versions are refused. The current native adapter requires1. This is additive; ProtocolVersion.Current remains1.

PreparedView and initial-page reads check the guild client version at the RPC boundary, then validate full current admission under S→B in the owning service. A writer temporarily withdrawing the published cut requires waiting for the storage barrier, instead of refusing before the lock. An unknown battle/home/session identity is still refused after waiting; reads neither create Ready/Start nor admit economy effects.

`AssignDefenderCommander` selects a defender by campaign ID and player ID through the defending leader's current session. Reusing the same `client_request_id` with the exact intent returns the prior receipt. Assignment after Start is refused; if no player is assigned or available at preparation, defense remains AI. The client outbox stores the exact bytes; assignment changes neither ownership nor resource issuance.

Login from a compatible client to an explicitly selected isolated stand checks guild admission under S. Once the verified V3 cut is published, the server returns `GuildVersion` and its boundary in the same reply and admits a regular player with a guild save. Without admission this login still returns `RecoveryRequired`; an older client gets `RecoveryClientRequired`. This does not change the Product flags.

After acknowledged `RuntimeStarted`, the arbiter can attach a frame of up to 4096 bytes to `PollRunning`, with eight fighters, the exact Start digest and a number derived from the game tick. `ReadRunningView` reads frames only through the bound commander channel. The server sends a delta if the previous number matches, otherwise a full frame. Exact repeats are safe; a conflicting number or different attempt is refused and an old frame cannot rewind the map. `ReadCompletedView` returns the receipt only after common Apply; the outcome is checked against immutable Start/Final and the terminal frame hash.

After an operator abort, `BattleRunningWork.aborted` (8) carries the exact assignment and `StartedAbort` terminal (kind 3) to the assigned runtime. `BattleCompletedViewReply.abort` (4) lets a commander observe the same terminal without a result or a Final. `aborted_start_digest` (6) and the abort checkpoint bind the committed Start and leave the Final digest empty. Arena occupancy ends only after verified cleanup. All additions preserve protocol version 1. The setting is off by default.
