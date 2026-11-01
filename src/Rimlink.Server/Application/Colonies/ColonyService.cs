using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Social;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Colonies;

public sealed class ColonyService(RimlinkDbContext db, LiveSessionService live, LiveStateLock stateLock, AllianceService alliances)
{
    public const int MaxMembers = 8, MaxPawns = 32;
    private static RpcException Denied() => RpcErrors.With(StatusCode.PermissionDenied, ErrorCode.Unspecified, RefusalKeys.ColoniesPermission, "you do not have permission for this colony");
    private static RpcException Invalid(string detail) => RpcErrors.With(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.ColoniesInvalid, detail);

    public async Task<ColonyAccessList> List(long playerId)
    {
        var rows = await db.Set<ColonyMember>().AsNoTracking().Where(x => x.OwnerPlayerId == playerId || x.MemberPlayerId == playerId).ToListAsync();
        var result = new ColonyAccessList();
        foreach (var row in rows) result.Entries.Add(await ToProto(row));
        return result;
    }

    public async Task<ColonyAccess> Invite(long owner, long member)
    {
        if (member <= 0 || member == owner) throw Invalid("choose another player");
        using var held = await stateLock.Hold();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await alliances.RequireMayInvite(owner, member);
        var row = await db.Set<ColonyMember>().FindAsync(owner, member);
        if (row is not null) return await ToProto(row);
        if (await db.Set<ColonyMember>().CountAsync(x => x.OwnerPlayerId == owner) >= MaxMembers) throw Invalid("this colony already has eight members or invitations");
        if (await db.Set<ColonyMember>().CountAsync(x => x.MemberPlayerId == member) >= MaxMembers) throw Invalid("this player already has eight colony memberships or invitations");
        row = new ColonyMember { OwnerPlayerId = owner, MemberPlayerId = member };
        db.Add(row);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await ToProto(row);
    }

    public async Task Answer(long member, long owner, bool accept)
    {
        using (await stateLock.Hold())
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var row = await db.Set<ColonyMember>().FindAsync(owner, member) ?? throw Denied();
            if (accept)
            {
                await alliances.RequireMayInvite(owner, member);
                row.Accepted = true;
            }
            else db.Remove(row);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        if (!accept) await EndPair(owner, member);
    }

    public async Task<ColonyAccess> Assign(long owner, ColonyAssignment assignment)
    {
        var ids = assignment.PawnIds.Distinct().Order().ToArray();
        if (ids.Length > MaxPawns || ids.Any(id => id is <= 0 or > int.MaxValue)) throw Invalid("assign at most 32 valid pawn ids");
        ColonyAccess result;
        using (await stateLock.Hold())
        {
            var row = await db.Set<ColonyMember>().FindAsync(owner, assignment.MemberPlayerId);
            if (row is not { Accepted: true }) throw Denied();
            row.PawnIds = string.Join(',', ids);
            await db.SaveChangesAsync();
            result = await ToProto(row);
        }
        // A changed grant ends the old session: buffered orders cannot keep yesterday's assignment.
        await EndPair(owner, assignment.MemberPlayerId);
        return result;
    }

    public async Task Revoke(long caller, long owner, long member)
    {
        if (caller != owner && caller != member) throw Denied();
        using (await stateLock.Hold())
        {
            var row = await db.Set<ColonyMember>().FindAsync(owner, member);
            if (row is not null) { db.Remove(row); await db.SaveChangesAsync(); }
        }
        await EndPair(owner, member);
    }

    private async Task EndPair(long owner, long member)
    {
        var sessions = await db.LiveSessions.AsNoTracking().Where(s => s.HostPlayerId == owner && s.GuestPlayerId == member && s.Kind == (int)LiveKind.LiveCoop && !LiveSessionService.FinalStates.Contains(s.State)).Select(s => s.Id).ToListAsync();
        foreach (long id in sessions) await live.MarkEnding(id, LiveEndReason.EndHostAborted);
    }

    private async Task<ColonyAccess> ToProto(ColonyMember row)
    {
        var players = await db.Players.AsNoTracking().Where(p => p.Id == row.OwnerPlayerId || p.Id == row.MemberPlayerId).ToListAsync();
        var result = new ColonyAccess { Owner = players.Single(p => p.Id == row.OwnerPlayerId).ToRef(), Member = players.Single(p => p.Id == row.MemberPlayerId).ToRef(), Accepted = row.Accepted };
        result.PawnIds.AddRange(ParseIds(row.PawnIds));
        return result;
    }

    public static long[] ParseIds(string? text) => string.IsNullOrEmpty(text) ? [] : text.Split(',').Select(long.Parse).ToArray();
}
