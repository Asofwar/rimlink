using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;

namespace Rimlink.Server.Tests.Support;

/// <summary>Typed economy clients over one channel, bound to one session token.</summary>
public sealed record EconomyClients(Protocol.V1.Wallet.WalletClient Wallet, Protocol.V1.Mailbox.MailboxClient Mailbox,
    Protocol.V1.Market.MarketClient Market, Protocol.V1.Transfer.TransferClient Transfer, Metadata Auth);

public static class EconomyFixtures
{
    private static readonly Random Random = new(42);

    public static ServerFixture EconomyServer() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    public static EconomyClients Clients(ServerFixture fixture, string token)
    {
        var channel = fixture.CreateChannel();
        return new EconomyClients(new Protocol.V1.Wallet.WalletClient(channel), new Protocol.V1.Mailbox.MailboxClient(channel),
            new Protocol.V1.Market.MarketClient(channel), new Protocol.V1.Transfer.TransferClient(channel), GrpcTestExtensions.Bearer(token));
    }

    public static ItemBlob Item(string def, int count, int blobBytes = 32, long marketValueX100 = 0)
    {
        byte[] blob = new byte[blobBytes];
        Random.NextBytes(blob);
        return new ItemBlob { DefName = def, Count = count, Quality = -1, Label = def, MarketValueX100 = marketValueX100, ScribeXmlZstd = ByteString.CopyFrom(blob) };
    }

    public static ItemList Items(params (string def, int count)[] items)
    {
        var list = new ItemList();
        foreach (var (def, count) in items) list.Items.Add(Item(def, count));
        return list;
    }

    public static PawnBlob Pawn(string kind, string name)
    {
        byte[] blob = new byte[64];
        Random.NextBytes(blob);
        return new PawnBlob { KindDef = kind, Name = name, Label = name, Humanlike = kind == "Colonist", ScribeXmlZstd = ByteString.CopyFrom(blob) };
    }

    public static PawnList Pawns(params (string kind, string name)[] pawns)
    {
        var list = new PawnList();
        foreach (var (kind, name) in pawns) list.Pawns.Add(Pawn(kind, name));
        return list;
    }

    public static TransferRequest Send(long to, ItemList? items = null, PawnList? pawns = null, long silver = 0, long tick = 100, string? requestId = null) => new()
    {
        ClientRequestId = requestId ?? Guid.NewGuid().ToString("N"),
        ToPlayerId = to,
        Items = items ?? new ItemList(),
        Pawns = pawns ?? new PawnList(),
        Silver = silver,
        GameTick = tick,
    };

    /// <summary>Credits a wallet straight through the service (what an admin adjustment does).</summary>
    public static async Task Fund(ServerFixture fixture, long playerId, long amount)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WalletService>().Apply(playerId, amount, "admin", 0);
    }

    public static async Task<T> ReadEvent<T>(AsyncServerStreamingCall<ServerEvent> call, ServerEvent.BodyOneofCase body, Func<ServerEvent, T> select, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            if (call.ResponseStream.Current.BodyCase == body) return select(call.ResponseStream.Current);
        }
        throw new TimeoutException($"no {body} event");
    }
}
