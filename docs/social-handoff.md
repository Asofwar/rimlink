# Social feature handoff

Worktree: `D:/Build/rimlink-next-wt/social`. Branch: `asofwar/next-social`, base `9e70b25`. No commits, push, release, installation or deployment. The user's game and production server were not modified. No reviewers were spawned; the coordinator owns the combined final review.

## Implemented contract

Alliance create/invite/accept/refuse/revoke/leave/transfer/disband, current-member private chat, map ally marks, silver-only treasury with permission/UTC cap/audit/refund, consent-based friends, public profile/preferences and existing self-rename, authenticated core/API/outbox, game Social window and EN/RU. Rules and limits: [community.md](community.md). New protocol: `alliances.proto`, event 100, errors 50–55. Version unchanged.

Treasury belongs to the current owner for wealth and disband refund; members do not count it again. No physical goods move through this module. Deposits/withdrawals and their immutable audit/intent receipt commit with wallet changes. Rejoining restores spent daily allowance from audit. Credits that would overflow a wallet are refused without losing treasury. Harm in progress prevents joining, including the live ending gap after a raid row completes.

## Public API for colony integration

Service namespace: `Rimlink.Server.Application.Social`. The following methods are public on `AllianceService`:

```csharp
public Task RequireMayAttack(long attackerId, long targetId, CancellationToken ct = default);
public Task<bool> AreAllies(long firstId, long secondId, CancellationToken ct = default);
public Task RequireMayInvite(long fromId, long toId, CancellationToken ct = default);
public Task<Rimlink.Protocol.V1.PublicProfile> Profile(long playerId, CancellationToken ct = default);
```

`RequireMayInvite` rejects self-invites, unknown targets, either direction of block and closed `InvitationsOpen`. Call inside colony invite creation/acceptance transactions; it does not replace the recipient accepting an actual invitation. `Profile` exposes `InvitationsOpen` and `Notifications`; the latter only controls popup delivery, not consent.

`RequireMayAttack` is called by `RaidRules.CheckTarget` and applies to snapshot/real/live raids and harmful paid incidents. It must **not** run for `LIVE_COOP`. No edits to `LiveSessionService.cs`, `live.proto`, `auth.proto` or `Session.cs`; coordinator's coop kind 2, pawn IDs field 13 and auth capability field 2 can merge unchanged. The social active-live query explicitly selects `LiveKind.LiveRaid`, never an unknown/non-visit catch-all.

## Evidence

- Initial four feature tests failed before endpoint implementation, then passed.
- Lost-answer outbox, exact persisted intent replay, definitive refusal and stale private cache: 4 client tests passed.
- Final targeted run: **42/42** server tests (41 feature/schema tests plus `EveryTypedRefusalHasAKey`), including the public invitation/preference gate for colony callers. Covers concurrency, block/revoke, privacy, attack guards, UTC rollover, restart, cap preservation on rejoin and overflow/live-ending regressions.
- Rejoin cap regression was observed RED (expected spent 100, actual 0), then GREEN. Invalid/colliding self-rename translation regressions were RED then GREEN. Wallet overflow and live-ending membership regressions were RED then GREEN.
- Unfiltered solution run: **1307 passed, 1 failed**, solely `Hosting.RefusalKeysTests.EveryRefusalKeyHasEnglishAndRussianText`. Its old `Keyed()` reads only `Rimlink.xml`. Coordinator explicitly owns updating it to aggregate `Keyed/*.xml`; that fixture is unchanged here. All Community constants and both translation files are checked separately by `CommunityRefusalKeysHaveBothTranslationsWithMatchingPlaceholders`.
- Final filtered solution run: **1312/1312 passed** (server 962, client 225, Discord 120, BridgeTools 5), including both boundary guards. The exclusion is exactly the coordinator-owned legacy translation fixture above. The existing invitation gate was then exposed for colony integration and verified in the final 42-test targeted run; no broad behavioral changes followed.
- Mod assembled under this worktree in `build/social-mod`, with the normal script and no `InstallTo`. BridgeTools built with `-NoInstall`. Existing game-reference warnings remain; no build errors. `git diff --check` clean.

PowerShell commands (all through RTK):

```powershell
rtk proxy dotnet test Rimlink.sln -nologo -v q
rtk proxy dotnet test Rimlink.sln -nologo -v q --filter 'FullyQualifiedName!~EveryRefusalKeyHasEnglishAndRussianText' --logger 'trx;LogFileName=social-acceptance.trx'
rtk proxy dotnet test tests/Rimlink.Server.Tests --filter 'FullyQualifiedName~CommunityTests|FullyQualifiedName~CommunitySchemaTests|FullyQualifiedName~EveryTypedRefusalHasAKey' -nologo -v q --logger 'trx;LogFileName=community-final.trx'
rtk proxy powershell -NoProfile -File scripts/Build-ClientMod.ps1 -RimWorldDir 'D:/SteamLibrary/steamapps/common/RimWorld' -OutputDir 'D:/Build/rimlink-next-wt/social/build/social-mod'
rtk proxy powershell -NoProfile -File scripts/Build-BridgeTools.ps1 -RimWorldDir 'D:/SteamLibrary/steamapps/common/RimWorld' -NoInstall
```

TRX evidence stays in each test project's ignored `TestResults/`; build logs in ignored `build/social-mod-build.log` and `build/social-bridge-build.log`.

## Integration-only work and real limits

Exclude the temporary feature migration and its model snapshot; regenerate the combined migration over 0.6.2. Preserve the coordinator's preexisting `ApplyConfigurationsFromAssembly` line in `RimlinkDbContext.cs`. Merge the small shared-file changes (Program, protocol imports/events/errors, outbox, connection/shared state, LoginFlow/game update, Players/settlement marker, block hook, wealth and RaidRules).

Two-client game execution/visual QA and live Discord proof were not performed: GABS game tools are not exposed in this session and no other worker's game/stand was reused. Checklist entries are explicitly unchecked. No Linux/two-CPU stream run was added because no timeout/stream machinery changed. This is implemented source with automated proofs and compiled game UI, not a claim of a completed in-game acceptance run.

## Changed source paths (55)

- `README.md`
- `docs/community.md`
- `docs/manual-checklist.md`
- `docs/player-guide.md`
- `docs/server-setup.md`
- `docs/social-handoff.md`
- `mod/Languages/English/Keyed/Community.xml`
- `mod/Languages/Russian/Keyed/Community.xml`
- `src/Rimlink.Client.Core/Economy/TransferOutbox.cs`
- `src/Rimlink.Client.Core/Net/RimlinkConnection.cs`
- `src/Rimlink.Client.Core/Social/CommunityCache.cs`
- `src/Rimlink.Client.Core/Social/CommunityClient.cs`
- `src/Rimlink.Client.Core/State/SharedState.cs`
- `src/Rimlink.Client/Game/CommunityDebugActions.cs`
- `src/Rimlink.Client/Game/CommunitySync.cs`
- `src/Rimlink.Client/Game/PlayerSettlement.cs`
- `src/Rimlink.Client/Game/RimlinkGameComponent.cs`
- `src/Rimlink.Client/Game/RimlinkSession.cs`
- `src/Rimlink.Client/UI/Dialog_Community.cs`
- `src/Rimlink.Client/UI/Dialog_Players.cs`
- `src/Rimlink.Client/UI/LoginFlow.cs`
- `src/Rimlink.Protocol/Protos/alliances.proto`
- `src/Rimlink.Protocol/Protos/common.proto`
- `src/Rimlink.Protocol/Protos/events.proto`
- `src/Rimlink.Server/Application/Economy/ServerWealth.cs`
- `src/Rimlink.Server/Application/Raids/RaidRules.cs`
- `src/Rimlink.Server/Application/Social/AllianceService.cs`
- `src/Rimlink.Server/Application/Social/CommunityFeature.cs`
- `src/Rimlink.Server/Application/Social/SocialService.cs`
- `src/Rimlink.Server/Grpc/CommunityGrpcService.cs`
- `src/Rimlink.Server/Grpc/RateLimitPolicy.cs`
- `src/Rimlink.Server/Grpc/RefusalKeys.cs`
- `src/Rimlink.Server/Program.cs`
- `src/Rimlink.Server/Storage/Configurations/AllianceAuditConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/AllianceGroupConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/AllianceMemberConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/AllianceMessageConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/CommunityInvitationConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/CommunityOperationConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/FriendshipConfiguration.cs`
- `src/Rimlink.Server/Storage/Configurations/PlayerProfileConfiguration.cs`
- `src/Rimlink.Server/Storage/Entities/AllianceAudit.cs`
- `src/Rimlink.Server/Storage/Entities/AllianceGroup.cs`
- `src/Rimlink.Server/Storage/Entities/AllianceMember.cs`
- `src/Rimlink.Server/Storage/Entities/AllianceMessage.cs`
- `src/Rimlink.Server/Storage/Entities/CommunityInvitation.cs`
- `src/Rimlink.Server/Storage/Entities/CommunityOperation.cs`
- `src/Rimlink.Server/Storage/Entities/Friendship.cs`
- `src/Rimlink.Server/Storage/Entities/PlayerProfile.cs`
- `tests/Rimlink.Client.Tests/Social/CommunityClientTests.cs`
- `tests/Rimlink.Server.Tests/Social/CommunityBoundaryTests.cs`
- `tests/Rimlink.Server.Tests/Social/CommunityPermissionsTests.cs`
- `tests/Rimlink.Server.Tests/Social/CommunityTests.cs`
- `tests/Rimlink.Server.Tests/Social/CommunityTreasuryTests.cs`
- `tests/Rimlink.Server.Tests/Storage/CommunitySchemaTests.cs`

## Test-only generated paths (exclude during integration)

- `src/Rimlink.Server/Storage/Migrations/RimlinkDbContextModelSnapshot.cs`
- `src/Rimlink.Server/Storage/Migrations/20261101204331_TemporaryCommunity.Designer.cs`
- `src/Rimlink.Server/Storage/Migrations/20261101204331_TemporaryCommunity.cs`

## Preexisting coordinator change, preserved without editing

- `src/Rimlink.Server/Storage/RimlinkDbContext.cs`
