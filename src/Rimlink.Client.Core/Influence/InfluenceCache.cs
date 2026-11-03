using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Influence;

public readonly record struct InfluenceToken(long Epoch, long Invalidation, long Request, long Revision, string WorldId, string ProcessId);

/// <summary>The displayed snapshot changes only when both its response and its finished geometry are still current.</summary>
public sealed class InfluenceCache
{
    private long epoch, invalidation, request;
    private string worldId = "", processId = "";
    public long HighWater { get; private set; }
    public bool Stale { get; private set; } = true;
    public bool Unavailable { get; private set; }
    public bool RequiresWorldRefresh { get; private set; }
    public bool HasContext => worldId.Length != 0 && processId.Length != 0;
    public InfluenceSnapshot? Current { get; private set; }
    public InfluenceSnapshot? Candidate { get; private set; }
    public InfluenceToken? CandidateToken { get; private set; }
    public string WorldId => worldId;
    public string ProcessId => processId;

    /// <summary>Only a trusted World.Get/Create response may establish context, never the influence response itself.</summary>
    public void SetContext(string world, string process)
    {
        if (world == worldId && process == processId) { RequiresWorldRefresh = false; return; }
        Reset();
        if (!Guid.TryParseExact(world, "N", out _) || !Guid.TryParseExact(process, "N", out _)) { Unavailable = true; return; }
        worldId = world; processId = process;
    }

    public void Invalidate(InfluenceChanged? changed = null)
    {
        if (changed is not null && (changed.WorldInstanceId != worldId || changed.ProcessInstanceId != processId))
        {
            RequiresWorldRefresh = true;
            Stale = true;
            invalidation++;
            Candidate = null; CandidateToken = null;
            return;
        }
        if (changed?.HasRevision == true) HighWater = Math.Max(HighWater, changed.Revision);
        invalidation++;
        Stale = true;
        Unavailable = false;
        Candidate = null; CandidateToken = null;
    }

    public InfluenceToken BeginFetch() => new(epoch, invalidation, ++request, -1, worldId, processId);

    public bool TryAcceptCandidate(InfluenceSnapshot snapshot, InfluenceToken token, out InfluenceToken ready)
    {
        ready = default;
        if (!Matches(token) || snapshot.WorldInstanceId != worldId || snapshot.ProcessInstanceId != processId
            || snapshot.Revision < HighWater || snapshot.Revision < (Current?.Revision ?? 0) || !InfluenceValidation.IsValid(snapshot)) return false;
        Candidate = snapshot.Clone();
        ready = token with { Revision = snapshot.Revision };
        CandidateToken = ready;
        return true;
    }

    public bool CanPublish(InfluenceToken token) => CandidateToken == token && Candidate is not null && Matches(token) && token.Revision >= HighWater;

    public bool TryPublish(InfluenceToken token)
    {
        if (!CanPublish(token)) return false;
        Current = Candidate;
        Candidate = null; CandidateToken = null;
        Stale = false; Unavailable = false;
        return true;
    }

    public void MarkUnavailable()
    {
        invalidation++;
        Current = null; Candidate = null; CandidateToken = null;
        Stale = true; Unavailable = true;
    }

    private bool Matches(InfluenceToken token) => HasContext && !RequiresWorldRefresh && token.Epoch == epoch && token.Invalidation == invalidation
        && token.Request == request && token.WorldId == worldId && token.ProcessId == processId;

    public void Reset()
    {
        epoch++; invalidation++; request++;
        worldId = processId = ""; HighWater = 0;
        Current = null; Candidate = null; CandidateToken = null;
        Stale = true; Unavailable = false; RequiresWorldRefresh = false;
    }
}
