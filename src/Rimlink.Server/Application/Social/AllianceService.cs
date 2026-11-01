using System.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using PublicProfile = Rimlink.Protocol.V1.PublicProfile;

namespace Rimlink.Server.Application.Social;

/// <summary>Small consent-based groups. All mutations and their replay receipts commit together.</summary>
public sealed class AllianceService(RimlinkDbContext db, WalletService wallet, EconomyGate gate, EconomyGuard guard, EventBus events, TimeProvider clock, IServiceProvider services)
{
    public const int MaxMembers = 16, MaxInvitations = 32, MaxFriends = 100;
    public const long MaxSilver = 1_000_000_000, MaxTreasury = 1_000_000_000_000;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Call inside the same transaction as creating a harmful action. Covers snapshot, real, live and hostile/weather incidents.</summary>
    public async Task RequireMayAttack(long attackerId, long targetId, CancellationToken ct = default)
    {
        if (await AreAllies(attackerId, targetId, ct))
            throw RpcErrors.Failed(ErrorCode.AlliedTarget, RefusalKeys.CommunityAlliedTarget, "you cannot attack an ally");
    }

    public Task<bool> AreAllies(long firstId, long secondId, CancellationToken ct = default) =>
        db.Set<AllianceMember>().AnyAsync(a => a.PlayerId == firstId && db.Set<AllianceMember>().Any(b => b.PlayerId == secondId && b.AllianceId == a.AllianceId), ct);

    public async Task<PublicProfile> Profile(long playerId, CancellationToken ct = default)
    {
        var p = await db.Players.AsNoTracking().Where(p => p.Id == playerId)
            .Select(p => new { p.Id, p.Name, p.Account.LastSeenAt }).SingleOrDefaultAsync(ct)
            ?? throw RpcErrors.Failed(ErrorCode.RecipientUnknown, RefusalKeys.CommunityNoPlayer, "no such player");
        var settings = await db.Set<PlayerProfile>().AsNoTracking().SingleOrDefaultAsync(x => x.PlayerId == playerId, ct);
        return new PublicProfile
        {
            PlayerId = p.Id, Name = p.Name, Bio = settings?.Bio ?? "",
            LastSeen = p.LastSeenAt is { } at ? Stamp(at) : null,
            InvitationsOpen = settings?.InvitationsOpen ?? true, Notifications = settings?.Notifications ?? true,
        };
    }

    public async Task<PublicProfiles> Directory(CancellationToken ct = default)
    {
        // A bounded directory, also makes offline invitations possible in the game.
        var ids = await db.Players.AsNoTracking().OrderBy(p => p.Name).Take(200).Select(p => p.Id).ToListAsync(ct);
        var result = new PublicProfiles();
        foreach (long id in ids) result.Players.Add(await Profile(id, ct));
        return result;
    }

    public async Task<CommunityState> Get(long playerId, CancellationToken ct = default)
    {
        // One read snapshot prevents a concurrent leave from exposing the former group's history.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var result = new CommunityState { Profile = await Profile(playerId, ct) };
        var blocked = await db.PlayerBlocks.AsNoTracking().Where(b => b.BlockerPlayerId == playerId || b.BlockedPlayerId == playerId)
            .Select(b => b.BlockerPlayerId == playerId ? b.BlockedPlayerId : b.BlockerPlayerId).ToListAsync(ct);
        var links = await db.Set<Friendship>().AsNoTracking().Where(x => x.FirstId == playerId || x.SecondId == playerId).ToListAsync(ct);
        var friends = links.Select(x => x.FirstId == playerId ? x.SecondId : x.FirstId).Except(blocked).ToList();
        result.Friends.AddRange((await db.Players.AsNoTracking().Where(p => friends.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync(ct)).Select(p => p.ToRef()));
        var invites = await db.Set<CommunityInvitation>().AsNoTracking()
            .Where(i => (i.FromId == playerId || i.ToId == playerId) && i.State == "pending" && i.ExpiresAt > Now
                && !blocked.Contains(i.FromId) && !blocked.Contains(i.ToId)).OrderBy(i => i.Id).ToListAsync(ct);
        foreach (var i in invites)
        {
            var group = i.AllianceId == 0 ? null : await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(g => g.Id == i.AllianceId, ct);
            if (group is { Disbanded: true } || group is not null && group.OwnerId != i.FromId) continue;
            result.Invitations.Add(new CommunityInvite
            {
                Id = i.Id, From = (await db.Players.AsNoTracking().SingleAsync(p => p.Id == i.FromId, ct)).ToRef(),
                To = (await db.Players.AsNoTracking().SingleAsync(p => p.Id == i.ToId, ct)).ToRef(),
                AllianceId = i.AllianceId, AllianceName = group?.Name ?? "",
            });
        }
        var member = await db.Set<AllianceMember>().AsNoTracking().SingleOrDefaultAsync(m => m.PlayerId == playerId, ct);
        if (member is not null)
        {
            var group = await db.Set<AllianceGroup>().AsNoTracking().SingleAsync(g => g.Id == member.AllianceId, ct);
            var members = await db.Set<AllianceMember>().AsNoTracking().Where(m => m.AllianceId == group.Id).OrderBy(m => m.PlayerId).ToListAsync(ct);
            var players = await db.Players.AsNoTracking().Where(p => members.Select(m => m.PlayerId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            result.Alliance = new AllianceInfo { Id = group.Id, Name = group.Name, OwnerId = group.OwnerId, Silver = group.Silver };
            result.Alliance.Members.AddRange(members.Select(m => new AllianceMemberInfo
            {
                Player = players[m.PlayerId].ToRef(), MayWithdraw = m.MayWithdraw, DailyCap = m.DailyCap,
                WithdrawnToday = m.WithdrawalDay == Now.Date ? m.Withdrawn : 0,
            }));
            var audit = await db.Set<AllianceAudit>().AsNoTracking().Where(a => a.AllianceId == group.Id).OrderByDescending(a => a.Id).Take(100).ToListAsync(ct);
            result.Audit.AddRange(audit.Select(a => new AllianceAuditLine
            {
                Id = a.Id, ActorId = a.ActorId, SubjectId = a.SubjectId, Kind = a.Kind, Delta = a.Delta, Balance = a.Balance,
                DailyCap = a.DailyCap, MayWithdraw = a.MayWithdraw, At = Stamp(a.At),
            }));
            var ids = members.Select(m => m.PlayerId).Except(blocked).ToArray();
            var chat = await db.Set<AllianceMessage>().AsNoTracking()
                .Where(m => m.AllianceId == group.Id && m.At >= member.JoinedAt && ids.Contains(m.FromId))
                .OrderByDescending(m => m.Id).Take(50).ToListAsync(ct);
            result.Chat.AddRange(chat.OrderBy(m => m.Id).Select(m => new AllianceChatLine { Id = m.Id, From = players[m.FromId].ToRef(), Text = m.Text, At = Stamp(m.At) }));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<CommunityReceipt> Mutate(long playerId, CommunityRequest r, CancellationToken ct = default)
    {
        if (r.ClientRequestId.Length is < 1 or > 64 || r.ClientRequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw RpcErrors.Failed(ErrorCode.CommunityIntent, RefusalKeys.CommunityRequestId, "a request id of 1-64 letters, digits or hyphens is required");
        if (r.CalculateSize() > 8192 || r.Text.Length > 500)
            throw RpcErrors.Failed(ErrorCode.CommunityLimit, RefusalKeys.CommunitySize, "text must be at most 500 characters");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        byte[] intent = r.ToByteArray();
        var prior = await db.Set<CommunityOperation>().AsNoTracking().SingleOrDefaultAsync(o => o.PlayerId == playerId && o.RequestId == r.ClientRequestId, ct);
        if (prior is not null)
        {
            if (!prior.Intent.AsSpan().SequenceEqual(intent)) throw RpcErrors.Failed(ErrorCode.CommunityIntent, RefusalKeys.CommunityIntent, "this request id belongs to another operation");
            return CommunityReceipt.Parser.ParseFrom(prior.Result);
        }
        var notify = new HashSet<long> { playerId };
        long allianceId = r.AllianceId;
        switch (r.Action)
        {
            case CommunityAction.AllianceCreate:
            {
                if (await db.Set<AllianceMember>().AnyAsync(m => m.PlayerId == playerId, ct)) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityAlreadyMember, "leave your current alliance first");
                string name = r.Text.Trim();
                if (name.Length is < 1 or > 32 || name.Any(c => char.IsControl(c) || c is '<' or '>')) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityName, "alliance name must be 1-32 characters without control characters or markup");
                var group = new AllianceGroup { Name = name, OwnerId = playerId };
                db.Add(group); await db.SaveChangesAsync(ct);
                allianceId = group.Id;
                db.Add(new AllianceMember { AllianceId = group.Id, PlayerId = playerId, JoinedAt = Now, MayWithdraw = true, DailyCap = MaxSilver });
                Audit(group, playerId, playerId, "create");
                break;
            }
            case CommunityAction.AllianceInvite:
            case CommunityAction.FriendInvite:
            {
                await RequireMayInvite(playerId, r.PlayerId, ct);
                if (r.Action == CommunityAction.AllianceInvite)
                {
                    var group = await Owned(playerId, r.AllianceId, ct);
                    if (await db.Set<AllianceMember>().AnyAsync(m => m.PlayerId == r.PlayerId, ct)) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityAlreadyMember, "that player already has an alliance");
                    if (await db.Set<AllianceMember>().CountAsync(m => m.AllianceId == group.Id, ct) >= MaxMembers) throw RpcErrors.Failed(ErrorCode.CommunityLimit, RefusalKeys.CommunityMembers, "an alliance can have at most 16 members");
                }
                else
                {
                    allianceId = 0;
                    if (await Friend(playerId, r.PlayerId, ct) is not null) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityAlreadyFriend, "you are already friends");
                }
                if (await db.Set<CommunityInvitation>().AnyAsync(i => i.FromId == playerId && i.ToId == r.PlayerId && i.AllianceId == allianceId && i.State == "pending" && i.ExpiresAt > Now, ct))
                    throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityPending, "an invitation is already waiting");
                if (await db.Set<CommunityInvitation>().CountAsync(i => i.State == "pending" && i.ExpiresAt > Now && i.FromId == playerId, ct) >= MaxInvitations
                    || await db.Set<CommunityInvitation>().CountAsync(i => i.State == "pending" && i.ExpiresAt > Now && i.ToId == r.PlayerId, ct) >= MaxInvitations)
                    throw RpcErrors.Failed(ErrorCode.CommunityLimit, RefusalKeys.CommunityInvites, "at most 32 invitations may be pending");
                db.Add(new CommunityInvitation { FromId = playerId, ToId = r.PlayerId, AllianceId = allianceId, ExpiresAt = Now.AddDays(7) });
                notify.Add(r.PlayerId);
                break;
            }
            case CommunityAction.InviteAccept:
            case CommunityAction.InviteRefuse:
            case CommunityAction.InviteRevoke:
            {
                var invite = await db.Set<CommunityInvitation>().SingleOrDefaultAsync(i => i.Id == r.InviteId && i.State == "pending" && i.ExpiresAt > Now, ct)
                    ?? throw RpcErrors.Failed(ErrorCode.CommunityGone, RefusalKeys.CommunityInviteGone, "that invitation is no longer open");
                if ((r.Action == CommunityAction.InviteRevoke ? invite.FromId : invite.ToId) != playerId)
                    throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityPermission, "you cannot change this invitation");
                if (r.Action == CommunityAction.InviteAccept)
                {
                    await RequireMayInvite(invite.FromId, invite.ToId, ct);
                    if (invite.AllianceId == 0)
                    {
                        await RequireFriendSpace(invite.FromId, ct); await RequireFriendSpace(invite.ToId, ct);
                        if (await Friend(invite.FromId, invite.ToId, ct) is null) db.Add(new Friendship { FirstId = Math.Min(invite.FromId, invite.ToId), SecondId = Math.Max(invite.FromId, invite.ToId) });
                    }
                    else
                    {
                        var group = await Owned(invite.FromId, invite.AllianceId, ct);
                        if (await db.Set<AllianceMember>().AnyAsync(m => m.PlayerId == playerId, ct)) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityAlreadyMember, "leave your current alliance first");
                        var members = await db.Set<AllianceMember>().Where(m => m.AllianceId == group.Id).Select(m => m.PlayerId).ToListAsync(ct);
                        if (members.Count >= MaxMembers) throw RpcErrors.Failed(ErrorCode.CommunityLimit, RefusalKeys.CommunityMembers, "an alliance can have at most 16 members");
                        foreach (long other in members) await BlockRules.RequireNotBlocked(db, playerId, other);
                        await RequireNoActiveHarm(playerId, members, ct);
                        // Leaving/rejoining must not replenish a spent daily allowance.
                        long withdrawn = -await db.Set<AllianceAudit>().Where(a => a.AllianceId == group.Id && a.ActorId == playerId && a.Kind == "withdraw" && a.At >= Now.Date).SumAsync(a => a.Delta, ct);
                        db.Add(new AllianceMember { PlayerId = playerId, AllianceId = group.Id, JoinedAt = Now, WithdrawalDay = Now.Date, Withdrawn = withdrawn });
                        Audit(group, playerId, playerId, "join");
                        allianceId = group.Id;
                        notify.UnionWith(members);
                    }
                }
                invite.State = r.Action == CommunityAction.InviteAccept ? "accepted" : r.Action == CommunityAction.InviteRefuse ? "refused" : "revoked";
                notify.Add(invite.FromId); notify.Add(invite.ToId);
                break;
            }
            case CommunityAction.FriendRemove:
            {
                if (await Friend(playerId, r.PlayerId, ct) is { } link) db.Remove(link);
                var pending = await db.Set<CommunityInvitation>().Where(i => i.AllianceId == 0 && i.State == "pending" &&
                    (i.FromId == playerId && i.ToId == r.PlayerId || i.FromId == r.PlayerId && i.ToId == playerId)).ToListAsync(ct);
                foreach (var invite in pending) invite.State = "revoked";
                notify.Add(r.PlayerId);
                break;
            }
            case CommunityAction.ProfileRename:
            {
                var player = await db.Players.SingleAsync(p => p.Id == playerId, ct);
                // Reuse the existing self-rename validation and audit; resolving here avoids a service constructor cycle.
                try
                {
                    await services.GetRequiredService<Admin.AdminOperations>().RenamePlayer(
                        new Admin.Actor(player.AccountId, playerId, Storage.Entities.Role.Player, player.Name), playerId, r.Text, null);
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.InvalidArgument or StatusCode.AlreadyExists)
                {
                    throw RpcErrors.With(ex.StatusCode, ErrorCode.NameInvalid, RefusalKeys.CommunityRename, "that player name is invalid or already taken");
                }
                break;
            }
            case CommunityAction.ProfileUpdate:
            {
                if (r.Text.Any(c => char.IsControl(c) && c != '\n') || r.Text.Any(c => c is '<' or '>')) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityBio, "biography may not contain control characters or markup");
                var profile = await db.Set<PlayerProfile>().SingleOrDefaultAsync(p => p.PlayerId == playerId, ct);
                if (profile is null) { profile = new PlayerProfile { PlayerId = playerId }; db.Add(profile); }
                profile.Bio = r.Text.Trim(); profile.InvitationsOpen = r.InvitationsOpen; profile.Notifications = r.Notifications;
                if (!profile.InvitationsOpen)
                {
                    var pending = await db.Set<CommunityInvitation>().Where(i => i.ToId == playerId && i.State == "pending").ToListAsync(ct);
                    foreach (var i in pending) { i.State = "revoked"; notify.Add(i.FromId); }
                }
                break;
            }
            default:
            {
                var member = await Member(playerId, r.AllianceId, ct);
                var group = await db.Set<AllianceGroup>().SingleAsync(g => g.Id == member.AllianceId && !g.Disbanded, ct);
                var members = await db.Set<AllianceMember>().Where(m => m.AllianceId == group.Id).ToListAsync(ct);
                notify.UnionWith(members.Select(m => m.PlayerId));
                switch (r.Action)
                {
                    case CommunityAction.AllianceChat:
                    {
                        var sender = await db.Players.SingleAsync(p => p.Id == playerId, ct);
                        if (sender.MutedUntil > Now) throw RpcErrors.Failed(ErrorCode.Muted, RefusalKeys.CommunityMuted, "you are muted");
                        string text = ChatService.CleanText(r.Text);
                        if (text.Any(c => c is '<' or '>')) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityBio, "text may not contain markup");
                        db.Add(new AllianceMessage { AllianceId = group.Id, FromId = playerId, Text = text, At = Now });
                        break;
                    }
                    case CommunityAction.TreasuryDeposit:
                    case CommunityAction.TreasuryWithdraw:
                    {
                        if (r.Silver is < 1 or > MaxSilver) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityAmount, "silver must be 1-1000000000");
                        gate.RequireOpen();
                        await gate.RequireNotFrozen(playerId); await guard.RequireNoDebt(playerId); await guard.RequireNoThreat(playerId);
                        bool deposit = r.Action == CommunityAction.TreasuryDeposit;
                        if (deposit)
                        {
                            await gate.RequireNotFrozen(group.OwnerId); await guard.RequireNoDebt(group.OwnerId);
                            if (group.Silver > MaxTreasury - r.Silver) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityBalance, "treasury balance limit reached");
                            await wallet.Apply(playerId, -r.Silver, "alliance-deposit", group.Id);
                            group.Silver += r.Silver;
                        }
                        else
                        {
                            await RequireTreasuryOpen(members, ct);
                            if (playerId != group.OwnerId) await BlockRules.RequireNotBlocked(db, playerId, group.OwnerId);
                            if (member.WithdrawalDay != Now.Date) { member.WithdrawalDay = Now.Date; member.Withdrawn = 0; }
                            if (!member.MayWithdraw || r.Silver > member.DailyCap - member.Withdrawn)
                                throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityDailyCap, "withdrawal permission or remaining daily limit is insufficient");
                            if (group.Silver < r.Silver) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityInsufficient, "not enough silver in the treasury");
                            if (await wallet.Balance(playerId) > long.MaxValue - r.Silver) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityWalletLimit, "the receiving wallet cannot hold that much silver");
                            member.Withdrawn += r.Silver; group.Silver -= r.Silver;
                            await wallet.Apply(playerId, r.Silver, "alliance-withdraw", group.Id);
                        }
                        Audit(group, playerId, playerId, deposit ? "deposit" : "withdraw", deposit ? r.Silver : -r.Silver);
                        break;
                    }
                    case CommunityAction.TreasuryPermission:
                    {
                        RequireOwner(group, playerId);
                        if (r.DailyCap is < 0 or > MaxSilver) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityCap, "daily cap must be 0-1000000000");
                        var subject = members.SingleOrDefault(m => m.PlayerId == r.PlayerId) ?? throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityMember, "that player is not a member");
                        subject.MayWithdraw = r.MayWithdraw; subject.DailyCap = r.DailyCap;
                        Audit(group, playerId, subject.PlayerId, "permission", cap: r.DailyCap, mayWithdraw: r.MayWithdraw);
                        break;
                    }
                    case CommunityAction.AllianceLeave:
                        if (group.OwnerId == playerId) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityOwnerLeave, "transfer ownership or disband before leaving");
                        db.Remove(member); Audit(group, playerId, playerId, "leave");
                        await RevokeInvitations(playerId, group.Id, notify, ct);
                        break;
                    case CommunityAction.AllianceTransfer:
                    {
                        RequireOwner(group, playerId);
                        var successor = members.SingleOrDefault(m => m.PlayerId == r.PlayerId && m.PlayerId != playerId) ?? throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityMember, "choose another alliance member");
                        await BlockRules.RequireNotBlocked(db, playerId, successor.PlayerId);
                        if (group.Silver > 0) { gate.RequireOpen(); await RequireTreasuryOpen(members, ct); }
                        group.OwnerId = successor.PlayerId;
                        // Ownership is not permission: a previously revoked cap stays revoked.
                        await RevokeInvitations(playerId, group.Id, notify, ct);
                        Audit(group, playerId, successor.PlayerId, "transfer");
                        break;
                    }
                    case CommunityAction.AllianceDisband:
                    {
                        RequireOwner(group, playerId);
                        // Return to the same wealth owner, even during a freeze; no cross-player outflow.
                        long refund = group.Silver;
                        if (await wallet.Balance(group.OwnerId) > long.MaxValue - refund) throw RpcErrors.Failed(ErrorCode.TreasuryLimit, RefusalKeys.CommunityWalletLimit, "the receiving wallet cannot hold that much silver");
                        if (refund > 0) await wallet.Apply(group.OwnerId, refund, "alliance-refund", group.Id);
                        group.Silver = 0; group.Disbanded = true;
                        Audit(group, playerId, group.OwnerId, "disband", -refund);
                        db.RemoveRange(members);
                        await RevokeInvitations(null, group.Id, notify, ct);
                        break;
                    }
                    default: throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityAction, "unknown social operation");
                }
                break;
            }
        }
        var receipt = new CommunityReceipt { AllianceId = allianceId };
        db.Add(new CommunityOperation { PlayerId = playerId, RequestId = r.ClientRequestId, Intent = intent, Result = receipt.ToByteArray() });
        await db.SaveChangesAsync(ct);
        if (r.Action == CommunityAction.AllianceChat)
        {
            long? cutoff = await db.Set<AllianceMessage>().Where(m => m.AllianceId == allianceId).OrderByDescending(m => m.Id).Skip(200).Select(m => (long?)m.Id).FirstOrDefaultAsync(ct);
            if (cutoff is { } id) await db.Set<AllianceMessage>().Where(m => m.AllianceId == allianceId && m.Id <= id).ExecuteDeleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        wallet.FlushEvents();
        Changed(notify);
        return receipt;
    }

    /// <summary>Block cancels pending invitations, friendship and treasury permission, but never removes allied protection.</summary>
    public async Task OnBlock(long blockerId, long blockedId)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var pending = await db.Set<CommunityInvitation>().Where(i => i.State == "pending" &&
            (i.FromId == blockerId && i.ToId == blockedId || i.FromId == blockedId && i.ToId == blockerId)).ToListAsync();
        foreach (var invite in pending) invite.State = "revoked";
        if (await Friend(blockerId, blockedId, default) is { } link) db.Remove(link);
        var members = await db.Set<AllianceMember>().Where(m => m.PlayerId == blockerId || m.PlayerId == blockedId).ToListAsync();
        if (members.Count == 2 && members[0].AllianceId == members[1].AllianceId)
        {
            var group = await db.Set<AllianceGroup>().SingleAsync(g => g.Id == members[0].AllianceId);
            if (group.OwnerId == blockerId || group.OwnerId == blockedId)
            {
                var member = members.Single(m => m.PlayerId != group.OwnerId);
                member.MayWithdraw = false; Audit(group, blockerId, member.PlayerId, "block-revoke");
            }
        }
        await db.SaveChangesAsync(); await tx.CommitAsync(); Changed([blockerId, blockedId]);
    }

    private async Task RequireTreasuryOpen(List<AllianceMember> members, CancellationToken ct)
    {
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            await gate.RequireNotFrozen(member.PlayerId); await guard.RequireNoThreat(member.PlayerId); await guard.RequireNoDebt(member.PlayerId);
        }
    }

    /// <summary>Shared consent/preference gate for alliance, friendship and colony invitations; call in the invite transaction.</summary>
    public async Task RequireMayInvite(long fromId, long toId, CancellationToken ct = default)
    {
        if (fromId == toId) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunitySelf, "choose another player");
        if (!await db.Players.AnyAsync(p => p.Id == toId, ct)) throw RpcErrors.Failed(ErrorCode.RecipientUnknown, RefusalKeys.CommunityNoPlayer, "no such player");
        await BlockRules.RequireNotBlocked(db, fromId, toId);
        if (await db.Set<PlayerProfile>().AnyAsync(p => p.PlayerId == toId && !p.InvitationsOpen, ct)) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityInvitesClosed, "that player has closed invitations");
    }

    private async Task RequireFriendSpace(long id, CancellationToken ct)
    {
        if (await db.Set<Friendship>().CountAsync(f => f.FirstId == id || f.SecondId == id, ct) >= MaxFriends)
            throw RpcErrors.Failed(ErrorCode.CommunityLimit, RefusalKeys.CommunityFriends, "at most 100 friends are allowed");
    }

    private Task<Friendship?> Friend(long first, long second, CancellationToken ct) =>
        db.Set<Friendship>().SingleOrDefaultAsync(f => f.FirstId == Math.Min(first, second) && f.SecondId == Math.Max(first, second), ct);

    private async Task<AllianceMember> Member(long playerId, long allianceId, CancellationToken ct) =>
        await db.Set<AllianceMember>().SingleOrDefaultAsync(m => m.PlayerId == playerId && m.AllianceId == allianceId, ct)
        ?? throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityMember, "you are not a member of that alliance");

    private async Task<AllianceGroup> Owned(long playerId, long allianceId, CancellationToken ct)
    {
        await Member(playerId, allianceId, ct);
        var group = await db.Set<AllianceGroup>().SingleAsync(g => g.Id == allianceId && !g.Disbanded, ct);
        RequireOwner(group, playerId); return group;
    }

    private static void RequireOwner(AllianceGroup group, long playerId)
    {
        if (group.OwnerId != playerId) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityOwner, "only the alliance owner may do that");
    }

    private async Task RevokeInvitations(long? fromId, long groupId, HashSet<long> notify, CancellationToken ct)
    {
        var invites = await db.Set<CommunityInvitation>().Where(i => i.AllianceId == groupId && (fromId == null || i.FromId == fromId || i.ToId == fromId) && i.State == "pending").ToListAsync(ct);
        foreach (var invite in invites) { invite.State = "revoked"; notify.Add(invite.ToId); notify.Add(invite.FromId); }
    }

    private async Task RequireNoActiveHarm(long joining, List<long> members, CancellationToken ct)
    {
        bool raids = await db.Raids.AnyAsync(r =>
            (r.AttackerPlayerId == joining && members.Contains(r.TargetPlayerId) || r.TargetPlayerId == joining && members.Contains(r.AttackerPlayerId))
            && r.State != RaidStateDb.Resolved && r.State != RaidStateDb.Expired && r.State != RaidStateDb.Cancelled, ct);
        bool incidents = await db.IncidentOrders.AnyAsync(i =>
            (i.BuyerPlayerId == joining && members.Contains(i.TargetPlayerId) || i.TargetPlayerId == joining && members.Contains(i.BuyerPlayerId))
            && i.Kind != (int)IncidentKind.IncidentKind && (i.State == IncidentStateDb.Queued || i.State == IncidentStateDb.Announced), ct);
        bool live = await db.LiveSessions.AnyAsync(s => s.Kind == (int)LiveKind.LiveRaid && s.State != LiveStateDb.Ended && s.State != LiveStateDb.Fallback
            && (s.HostPlayerId == joining && members.Contains(s.GuestPlayerId) || s.GuestPlayerId == joining && members.Contains(s.HostPlayerId)), ct);
        if (raids || incidents || live) throw RpcErrors.Failed(ErrorCode.AllianceRefused, RefusalKeys.CommunityActiveHarm, "finish active hostile actions between members before joining");
    }

    private void Audit(AllianceGroup group, long actor, long subject, string kind, long delta = 0, long cap = 0, bool mayWithdraw = false) =>
        db.Add(new AllianceAudit { AllianceId = group.Id, ActorId = actor, SubjectId = subject, Kind = kind, Delta = delta, Balance = group.Silver, DailyCap = cap, MayWithdraw = mayWithdraw, At = Now });

    private void Changed(IEnumerable<long> players)
    {
        foreach (long id in players.Distinct()) events.SendTo(id, new ServerEvent { CommunityChanged = new CommunityChanged() });
    }

    private static Timestamp Stamp(DateTime value) => Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
