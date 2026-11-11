using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed partial class GuildCaptureClient(RimlinkConnection connection)
{
    private async Task<T> Call<T>(LoginReply login, Func<Task<T>> send)
    {
        var reply = await connection.ForAuthentication(login, send);
        if (!ReferenceEquals(connection.Login, login)) throw new OperationCanceledException("guild reply belongs to a previous login");
        return reply;
    }
    private LoginReply Authentication(GuildColonyScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GuildResourceContracts.ValidateScope(scope);
        var login = connection.Login;
        if (login is null || login.GuildVersion != GuildResourceContracts.CurrentVersion || scope.PlayerId != login.Player.PlayerId
            || scope.SaveGeneration != login.SaveGeneration || scope.SaveWriteEpoch != login.SaveWriteEpoch
            || scope.RestoreBoundary != login.OutpostRestoreBoundary)
            throw new InvalidDataException("guild call belongs to another authentication boundary");
        return login;
    }
    public Task<GuildDeliveryAuthorization> BeginDelivery(GuildDeliveryRequest request, CancellationToken ct)
    {
        var frozen = request.Clone(); var login = Authentication(frozen.Scope, ct);
        return Call(login, () => connection.GuildCaptureApi.BeginDeliveryAsync(frozen,
            deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: ct).ResponseAsync);
    }
    public Task<OutpostManifestDocument> Manifest(Parcel parcel, CancellationToken ct)
    {
        parcel = parcel.Clone();
        var payload = parcel.GuildResourcePayload;
        _ = GuildResourceContracts.Validate(payload);
        var scope = GuildResourceContracts.DeliveryScope(parcel);
        var login = Authentication(scope, ct);
        if (parcel.Id <= 0) throw new InvalidDataException("guild manifest parcel");
        return Outposts.OutpostManifestClient.ReadDocument(payload.ManifestDigest, scope.WorldInstanceId, offset =>
            Call(login, () => connection.GuildCaptureApi.ManifestAsync(new() { Scope = scope.Clone(),
                ParcelId = parcel.Id, ManifestId = payload.ManifestId, ManifestDigest = payload.ManifestDigest, Offset = offset },
                deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: ct).ResponseAsync), ct);
    }
    public async Task<BattlePreparedViewReply> PreparedView(BattlePreparedViewRequest request,CancellationToken ct)
    {
        request=request.Clone();var login=Authentication(request.Home.Scope,ct);
        var reply=await Call(login,()=>connection.GuildCaptureApi.ReadPreparedViewAsync(request,deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:ct).ResponseAsync);
        _=BattlePreparedViewDownload.Validate(request,reply);return reply;
    }
    public Task<(BattleScopeMessage Scope, BattleExecutorProfile Profile)?> PreparedEntry(GuildWorldScope scope, CancellationToken ct)
    {
        scope = scope.Clone();
        var login = Authentication(scope, ct);
        return Call<(BattleScopeMessage, BattleExecutorProfile)?>(login, async () =>
        {
            using var call = connection.GuildCaptureApi.ReadPreparedEntry(scope, deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: ct);
            var stream = call.ResponseStream;
            if (!await stream.MoveNext(ct)) return null;
            var entry = stream.Current.Entry ?? throw new InvalidDataException("missing prepared battle entry");
            var battle = entry.Scope;
            if (battle is null || battle.WorldInstanceId != scope.WorldInstanceId || battle.RestoreBoundary != scope.RestoreBoundary
                || !Guid.TryParseExact(battle.BattleId, "D", out var id) || id == Guid.Empty || battle.Attempt <= 0 || battle.Fence <= 0)
                throw new InvalidDataException("prepared battle entry scope mismatch");
            var profile = BattleManifestContracts.ReadProfile(entry.Profile.Span);
            byte[] inventory = new byte[checked((int)profile.Inventory.SizeBytes)];
            int offset = 0;
            while (await stream.MoveNext(ct))
            {
                var part = stream.Current.Inventory;
                if (part.Length is < 1 or > 262144 || part.Length > inventory.Length - offset)
                    throw new InvalidDataException("invalid prepared battle inventory part");
                part.Span.CopyTo(inventory.AsSpan(offset));
                offset += part.Length;
            }
            if (offset != inventory.Length) throw new InvalidDataException("incomplete prepared battle inventory");
            return (battle, BattleExecutorProfile.LoadViewer(entry, inventory));
        });
    }
    public Task DownloadPreparedInitial(BattlePreparedViewRequest request,BattlePreparedViewReply view,string file,CancellationToken ct)
    {
        request=request.Clone();view=view.Clone();var login=Authentication(request.Home.Scope,ct);
        return BattlePreparedViewDownload.ReadAsync(request,view,file,(page,token)=>Call(login,()=>connection.GuildCaptureApi.ReadPreparedInitialAsync(page,
            deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:token).ResponseAsync),ct);
    }
}
