# Alliances, treasury, friends and public profiles

Implements the social task of the approved `2026-11-01-rimlink-next-features-design.md`. This worktree does not bump the version, deploy or publish.

## Playing

Open **Players → Social**. The directory includes the first 200 players by name, including offline players. Public profiles contain name, biography (500 characters maximum), last activity and invitation/notification preferences. **My profile** edits biography and preferences. **Rename player** reuses `AdminOperations.RenamePlayer` with its existing validation and audit.

Friendship and alliance membership require an invitation and acceptance in **Invitations**. The sender can revoke; the recipient can refuse. Invitations expire after seven days. A player can have at most 32 pending incoming and 32 outgoing invitations, and 100 friends. Closing invitations revokes those already waiting. Blocking in either direction refuses new invitations and acceptance, revokes pending invitations, removes friendship and revokes a member's treasury permission when the other party is the owner. Unblocking does not restore consent or permission. A block does not remove alliance protection.

An alliance is a named group (1–32 characters, maximum 16 members). Only the owner invites, grants treasury permissions, transfers ownership or disbands. A player belongs to one active alliance. Members leave freely; the owner must transfer ownership or disband before leaving. A new owner keeps their current withdrawal permission and can change it themselves. Invitations from the former owner are revoked on transfer. These relationships belong to the player and survive reconnect, server restart and colony archive/reset.

Allied settlements have a green world icon, **[Ally]** label and inspect marker. Allies cannot launch snapshot, real or live raids or harmful paid incidents (hostile and weather) against one another. Friendly incidents, live visits and LiveCoop retain their normal rules. Joining is refused while a hostile action between prospective members is queued, announced, landed or live; the original action and escrow remain intact. Leaving never cancels an existing hostile action or changes its state.

Alliance chat is separate from public/direct chat and the Discord/admin chat feed. Only current members read or write it. History contains the last 50 visible lines since the reader joined, from senders still in the alliance and not blocked in either direction. Leaving and rejoining does not recover earlier history. At most 200 lines are stored per group. Mutes apply; a line beginning with `/` stays a private line and cannot execute admin commands. Notification preferences control invitation popups; invitations remain in the window.

## Treasury rules

Only wallet silver moves into and out of the treasury. To deposit physical stacks, first use the existing map-to-wallet deposit. Treasury withdrawal credits the member's wallet; the normal wallet/mailbox flow remains the only route back to physical silver. This module never creates items or pawns.

Each member, including the owner, has withdrawal permission and a daily cap measured by the UTC day. Permission/cap changes and leaving/rejoining do not reset the amount already withdrawn that day; the immutable audit restores the allowance on rejoin. New members have no permission; the creator starts with a cap of 1,000,000,000. Each transaction is limited to that amount and the total treasury to 1,000,000,000,000 silver.

Deposits and withdrawals require the economy to be open. Deposits check the depositor for debt, freeze and threat and the owner receiving the wealth for debt/freeze. Withdrawals and ownership transfers with a nonempty treasury additionally check **every current member** for debt, freeze and incoming threat. A block between a member and owner also prevents withdrawal.

The treasury counts exactly once in **the owner's server wealth**, under the existing `ServerWealthPercent`; it is not counted for members again. Ownership transfer changes that wealth's owner. On disband **the entire remaining balance returns to the current owner's wallet**, atomically. Members who leave get no automatic refund: deposits are contributions. Disband refunds remain allowed during freeze/debt since the same wealth owner receives them and no cross-player outflow occurs. A withdrawal or refund exceeding the receiving wallet's signed 64-bit balance is refused without changing either balance; spend or withdraw wallet silver before retrying.

Deposits, withdrawals, permissions, membership changes, owner transfer, block revocation and disband/refund append audit rows. The window shows the last 100; stored audit rows are never edited or trimmed. Wallet, treasury, membership, audit and replay receipt commit together. Events are sent only after commit. Every mutation requires a persisted `(player_id, client_request_id)` bound to the complete protobuf intent. A different operation under the same ID is refused. Exact replay returns a nonprivate acknowledgement even if the caller has left the alliance. All mutations use the existing client outbox; an unknown answer stays on disk until reconnect resolves it, and the window disables further mutations in the meantime.

## Integration

Public methods in `Rimlink.Server.Application.Social.AllianceService`:

```csharp
Task RequireMayAttack(long attackerId, long targetId, CancellationToken ct = default);
Task<bool> AreAllies(long firstId, long secondId, CancellationToken ct = default);
Task RequireMayInvite(long fromId, long toId, CancellationToken ct = default);
Task<PublicProfile> Profile(long playerId, CancellationToken ct = default);
```

Call `RequireMayAttack` inside the transaction creating a harmful action. It throws typed `ErrorCode.AlliedTarget`. `RaidRules.CheckTarget` calls it, covering snapshot/real raids, live raid launch and hostile/weather incident orders. **`LiveSessionService.cs`, `live.proto` and live enum values are unchanged**. The coordinator can merge `LIVE_COOP = 2` without changing this guard.

`RequireMayInvite` is also the colony-invitation gate: inside the invite-creation/acceptance transaction it rejects self-invites, unknown recipients, blocks in either direction and a recipient whose public `InvitationsOpen` preference is false. It does not imply consent; the target must still accept the particular invitation. `Profile` returns `Rimlink.Protocol.V1.PublicProfile`; `Notifications` is only the popup preference, never permission to bypass acceptance. `RequireMayAttack` is for harmful actions only; do not call it for LiveCoop.

`CommunityFeature.AddCommunity` / `MapCommunity` contain DI/gRPC/REST registration. `Program` only adds their two calls and a using. `alliances.proto` defines the authenticated `Community` API. `ServerEvent.community_changed = 100` carries a targeted invalidation only. Error codes occupy 50–55. No private history or membership is broadcast globally; invalidation immediately clears the client's private cache and rejects fetches from older revisions or sessions.

Public and moderation profile routes share the exact same DTO:

- `GET /api/public/profiles/{playerId}` obeys `Panel:Enabled` and `Panel:Public`.
- `GET /api/admin/profiles/{playerId}` uses existing admin authentication and `moderate` scope.

Fields: `playerId`, `name`, `bio`, `lastSeen`, `invitationsOpen`, `notifications`. No account ID, login, SteamID, IP, moderation details, friends, membership, treasury or chat. Last activity and the two preferences are intentionally public. Mutations are limited per account to five requests/five seconds, directory reads to five/ten seconds; other reads use the default policy. Mutation payloads are limited to 8192 bytes in addition to the existing gRPC size limit.

Separate EF entities/configurations use `db.Set<T>()` and assembly configuration discovery. Migration `20261101204331_TemporaryCommunity` and the changed snapshot are **test-only**: exclude them during integration and generate the single combined migration. The schema test upgrades the actual 0.6.2 migration and verifies existing data and unique membership. Other features' economy/procurement/auction files are untouched.

## Verification limits

Automated checks cover consent/permissions, block/revoke, chat and REST privacy, treasury conservation, concurrent withdrawals, caps, refund, restart, allied aggression and outbox persistence. The game mod is compiled locally; it is not installed into the user's game.

The two-client EN/RU acceptance run remains explicit in `manual-checklist.md`: visual layout, notifications, world marker, real chat and reconnect, every treasury flow, allied raid refusal and an allowed live visit/cooperative session. An assembly build or automated test does not prove those game interactions. GABS game tools are not exposed in this session; no other worker's stand or game process is reused.
