using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

/// <summary>What the player sees of the economy: balance, parcels waiting to drop, own open lots. Fed by the event stream.</summary>
public sealed class EconomyState
{
    private readonly List<Parcel> pending = [];
    private readonly HashSet<long> pendingIds = [];

    public long Balance { get; private set; }
    public IReadOnlyList<Parcel> Pending => pending;
    public List<Lot> MyLots { get; } = [];

    public event Action<Parcel>? ParcelArrived;
    /// <summary>The sale and the lot as this client knew it (null when it was not among <see cref="MyLots"/>).</summary>
    public event Action<LotSold, Lot?>? LotSold;
    public event Action<LotClosed>? LotClosed;
    public event Action<WalletChanged>? WalletChanged;

    public void Load(WalletState wallet, ParcelList parcels, LotList myLots)
    {
        Balance = wallet.Balance;
        pending.Clear();
        pendingIds.Clear();
        foreach (var p in parcels.Parcels) AddPending(p);
        MyLots.Clear();
        MyLots.AddRange(myLots.Lots);
    }

    /// <summary>False when the parcel is already known (replayed event, or a gift whose decision changed: replaced in place).</summary>
    public bool AddPending(Parcel parcel)
    {
        if (!pendingIds.Add(parcel.Id))
        {
            int at = pending.FindIndex(p => p.Id == parcel.Id);
            if (at >= 0) pending[at] = parcel;
            return false;
        }
        pending.Add(parcel);
        return true;
    }

    public void MarkDelivered(long parcelId)
    {
        if (pendingIds.Remove(parcelId)) pending.RemoveAll(p => p.Id == parcelId);
    }

    public void Apply(ServerEvent ev)
    {
        switch (ev.BodyCase)
        {
            case ServerEvent.BodyOneofCase.ParcelArrived:
                if (AddPending(ev.ParcelArrived.Parcel)) ParcelArrived?.Invoke(ev.ParcelArrived.Parcel);
                break;
            case ServerEvent.BodyOneofCase.LotSold:
                var lot = MyLots.FirstOrDefault(l => l.Id == ev.LotSold.LotId);
                MyLots.RemoveAll(l => l.Id == ev.LotSold.LotId);
                LotSold?.Invoke(ev.LotSold, lot);
                break;
            case ServerEvent.BodyOneofCase.LotClosed:
                MyLots.RemoveAll(l => l.Id == ev.LotClosed.LotId);
                LotClosed?.Invoke(ev.LotClosed);
                break;
            case ServerEvent.BodyOneofCase.WalletChanged:
                Balance = ev.WalletChanged.Balance;
                WalletChanged?.Invoke(ev.WalletChanged);
                break;
            case ServerEvent.BodyOneofCase.GiftRefused:
                MarkDelivered(ev.GiftRefused.ParcelId); // a gift to us that was refused leaves the mailbox; ours is not in it
                break;
        }
    }

    public void Reset()
    {
        Balance = 0;
        pending.Clear();
        pendingIds.Clear();
        MyLots.Clear();
    }
}
