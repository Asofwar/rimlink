using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.GuildCapture;

internal static class BattleCommanderIdentity
{
    internal static ByteString Digest(Session session)
    {
        byte[] token = Encoding.UTF8.GetBytes(session.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Rimlink.BattleSession\0"u8); Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header,1); BinaryPrimitives.WriteInt32LittleEndian(header[4..],token.Length);
        hash.AppendData(header); hash.AppendData(token); return ByteString.CopyFrom(hash.GetHashAndReset());
    }
    internal static async Task<Session?> RequireAsync(RimlinkDbContext db,SessionRegistry sessions,BattleCommanderBinding binding,
        long alliance,long player,int epoch,bool allowAi,CancellationToken ct)
    {
        if (binding.DefenderAi)
        {
            if (!allowAi) throw Refused();
            return null;
        }
        var session = sessions.ByPlayer(player) ?? throw Refused();
        if (binding.PlayerId != player || binding.AllianceId != alliance || binding.ControlEpoch != epoch || session.Cts.IsCancellationRequested
            || session.GuildVersion != 1 || !Digest(session).Equals(binding.SessionDigest)) throw Refused();
        var member = await db.Set<AllianceMember>().AsNoTracking().SingleOrDefaultAsync(m => m.PlayerId == player && m.AllianceId == alliance,ct);
        var owner = await db.Players.AsNoTracking().SingleAsync(p => p.Id == player,ct);
        if (member is null || member.JoinedAt.Ticks != binding.MembershipJoinedUtcTicks || owner.SaveWriteEpoch != session.SaveWriteEpoch) throw Refused();
        return session;
    }
    internal static void RequireStillCurrent(SessionRegistry sessions,Session? session)
    {
        if (session is not null && (session.Cts.IsCancellationRequested || !ReferenceEquals(sessions.ByPlayer(session.PlayerId),session))) throw Refused();
    }
    private static OutpostEvidenceUnavailableException Refused() => new("guild-battle-commander-binding-stale");
}
