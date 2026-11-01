using Rimlink.Protocol.V1;

namespace Rimlink.Client.Social;

/// <summary>Only the currently authenticated participant's snapshot; clear private history immediately on invalidation.</summary>
public sealed class CommunityCache
{
    private long revision;
    public CommunityState Current { get; private set; } = new();
    public bool Stale { get; private set; } = true;
    public long Revision => revision;
    public bool IsAlly(long playerId) => !Stale && Current.Alliance?.Members.Any(m => m.Player.PlayerId == playerId) == true;
    public void Invalidate()
    {
        revision++;
        Stale = true;
        Current = new CommunityState();
    }
    public bool Load(CommunityState state, long expectedRevision)
    {
        if (revision != expectedRevision) return false;
        Current = state;
        Stale = false;
        return true;
    }
    public void Reset() => Invalidate();
}
