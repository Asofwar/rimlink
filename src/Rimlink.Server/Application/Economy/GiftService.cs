using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using Transfer = Rimlink.Server.Storage.Entities.Transfer;

namespace Rimlink.Server.Application.Economy;

/// <summary>
/// A player's gift waits for the recipient's decision. Accepted, it drops like any parcel; refused (or left unanswered for
/// <c>Economy:GiftDecisionDays</c>), its items and pawns go back to the sender as a <c>GiftReturn</c> parcel and its silver to
/// the sender's wallet, in one transaction. Nothing is destroyed.
/// </summary>
public sealed class GiftService(RimlinkDbContext db, MailboxService mailbox, WalletService wallet, EventBus events, IOptions<EconomyOptions> options, ILogger<GiftService> log)
{
    public async Task Answer(long playerId, long parcelId, bool accept)
    {
        var parcel = await db.Parcels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parcelId && p.ToPlayerId == playerId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "no such parcel in your mailbox"));
        if (parcel.Decision == ParcelDecisionDb.None) throw new RpcException(new Status(StatusCode.FailedPrecondition, "this parcel needs no decision"));
        if (parcel.Decision != ParcelDecisionDb.Awaiting) return; // decided already: the same answer again changes nothing

        if (accept)
        {
            int changed = await db.Parcels.Where(p => p.Id == parcelId && p.Decision == ParcelDecisionDb.Awaiting)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Decision, ParcelDecisionDb.Accepted));
            if (changed == 1)
            {
                parcel.Decision = ParcelDecisionDb.Accepted;
                await mailbox.Announce(parcel); // the flag is cleared: the client drops it now
            }
            return;
        }
        await Refuse(parcelId, expired: false);
    }

    /// <summary>Refuses every gift that waited longer than <c>Economy:GiftDecisionDays</c>; returns how many.</summary>
    public async Task<int> RefuseOverdue(DateTime now)
    {
        var before = now - TimeSpan.FromDays(options.Value.GiftDecisionDays);
        var due = await db.Parcels.AsNoTracking()
            .Where(p => p.Decision == ParcelDecisionDb.Awaiting && p.CreatedAt < before)
            .Select(p => p.Id).ToListAsync();
        int n = 0;
        foreach (long id in due)
            if (await Refuse(id, expired: true)) n++;
        return n;
    }

    private async Task<bool> Refuse(long parcelId, bool expired)
    {
        Parcel? back = null;
        Parcel parcel;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            var found = await db.Parcels.FirstOrDefaultAsync(p => p.Id == parcelId && p.Decision == ParcelDecisionDb.Awaiting);
            if (found is null) return false; // answered meanwhile
            parcel = found;
            long sender = parcel.FromPlayerId ?? throw new InvalidOperationException($"gift {parcel.Id} has no sender");
            var items = ItemList.Parser.ParseFrom(parcel.Items);
            var pawns = PawnList.Parser.ParseFrom(parcel.Pawns);

            parcel.Decision = ParcelDecisionDb.Refused;
            // Closed at tick 0, like an admin refund: no save can be older than it, so it never drops again.
            parcel.DeliveredAt = DateTime.UtcNow;
            parcel.DeliveredTick = 0;
            if (items.Items.Count > 0 || pawns.Pawns.Count > 0)
                back = await mailbox.Create(sender, parcel.ToPlayerId, ParcelKindDb.GiftReturn, items, pawns, 0, null, ignoreMailboxLimit: true);
            if (parcel.Silver > 0) await wallet.Apply(sender, parcel.Silver, "refund", parcel.Id);
            db.Transfers.Add(new Transfer { FromPlayerId = parcel.ToPlayerId, ToPlayerId = sender, Kind = "gift-refuse", GameTick = 0, Summary = $"parcel {parcel.Id}", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        wallet.FlushEvents();
        if (back is not null) await mailbox.Announce(back);

        var by = await db.Players.AsNoTracking().FirstAsync(p => p.Id == parcel.ToPlayerId);
        string returned = TransferService.Summary(ItemList.Parser.ParseFrom(parcel.Items), PawnList.Parser.ParseFrom(parcel.Pawns), parcel.Silver);
        var ev = new GiftRefused { ParcelId = parcel.Id, By = by.ToRef(), Returned = returned, Expired = expired };
        events.SendTo(parcel.FromPlayerId!.Value, new ServerEvent { GiftRefused = ev });
        events.SendTo(parcel.ToPlayerId, new ServerEvent { GiftRefused = ev.Clone() });
        EconomyMetrics.GiftsRefused.Inc();
        log.LogInformation("Gift {Parcel} of player {From} {How} by player {To}: {Summary}", parcel.Id, parcel.FromPlayerId, expired ? "expired" : "refused", parcel.ToPlayerId, returned);
        return true;
    }
}

/// <summary>Sweep of overdue gift decisions and expired trades at start (a restart does not postpone them), then hourly.</summary>
public sealed class SafeTradesSweep(IServiceScopeFactory scopes, ILogger<SafeTradesSweep> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    int gifts = await scope.ServiceProvider.GetRequiredService<GiftService>().RefuseOverdue(DateTime.UtcNow);
                    if (gifts > 0) log.LogInformation("Refused {Count} gifts left without an answer", gifts);
                    int trades = await scope.ServiceProvider.GetRequiredService<Trades.TradeService>().ExpireDue(DateTime.UtcNow);
                    if (trades > 0) log.LogInformation("Expired {Count} trades", trades);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Safe trades sweep failed; next try in an hour");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
