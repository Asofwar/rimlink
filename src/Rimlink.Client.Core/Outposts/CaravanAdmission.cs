using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>Game-free facts captured together on the main thread; checks never grant server custody.</summary>
public sealed record CaravanAdmissionFacts(OutpostCaravanAttestation Caravan, IReadOnlyList<OutpostWorkerCapture> Workers,
    OutpostProfile Profile, bool OwnCaravan = true, bool Arrived = true, bool ManifestMatches = true,
    bool TileAvailable = true, bool LooseCargoRemains = false);
public sealed record AdmissionDecision(bool Allowed, string Reason, bool RemoveEmptyCaravan = false);

public static class CaravanAdmission
{
    public static AdmissionDecision Check(CaravanAdmissionFacts facts)
    {
        var c = facts.Caravan; var workers = facts.Workers;
        if (!facts.OwnCaravan || !facts.Arrived || c.SurfaceId != "planet" || c.Tile < 0) return new(false, "arrival");
        if (!facts.ManifestMatches || c.ManifestDigest.Length != 32 || !facts.TileAvailable) return new(false, "geography");
        if (workers.Count is < 1 or > 4 || facts.Profile is not (OutpostProfile.Wood or OutpostProfile.Steel)
            || c.CaravanId.Length is < 1 or > 256 || !Guid.TryParse(c.GameBranchId, out _) || !Guid.TryParse(c.ClientRequestId, out _)) return new(false, "shape");
        if (!c.SelectedCargoEmpty && !c.SelectedCargoCaptured || facts.LooseCargoRemains) return new(false, "cargo");
        var selected = workers.Select(w => w.Pawn?.Registry?.PawnReferenceToken ?? "").ToArray();
        if (c.RosterReferenceTokens.Count > 1024 || selected.Any(string.IsNullOrEmpty)
            || c.RosterReferenceTokens.Distinct().Count() != c.RosterReferenceTokens.Count
            || c.RemainingReferenceTokens.Distinct().Count() != c.RemainingReferenceTokens.Count
            || selected.Distinct().Count() != selected.Length || workers.Select(w => w.PawnCustodyId).Distinct().Count() != workers.Count
            || !c.SelectedCustodyIds.Order().SequenceEqual(workers.Select(w => w.PawnCustodyId).Order())
            || selected.Any(s => !c.RosterReferenceTokens.Contains(s))
            || !c.RosterReferenceTokens.Except(selected).Order().SequenceEqual(c.RemainingReferenceTokens.Order())) return new(false, "roster");
        if (c.RemainingReferenceTokens.Count > 0 && !c.RemainingMobile) return new(false, "remaining");
        foreach (var w in workers)
            if (!Guid.TryParse(w.PawnCustodyId, out _) || !Guid.TryParse(w.AssignmentId, out _) || w.Pawn?.Registry is not { } e
                || e.PawnCustodyId != w.PawnCustodyId || e.Tombstone || e.NonAuthoritativeMirror
                || !w.AdultLifeStage || !w.FreeColonist || !w.PlayerOwned || !w.BaselineHuman || w.Downed || w.MentalState
                || !float.IsFinite(w.Consciousness) || !float.IsFinite(w.Manipulation) || !float.IsFinite(w.Moving)
                || Math.Min(w.Consciousness, Math.Min(w.Manipulation, w.Moving)) <= 0
                || facts.Profile == OutpostProfile.Wood && !w.PlantsCapable || facts.Profile == OutpostProfile.Steel && !w.MiningCapable
                || w.BlobSha256.Length != 32 || !w.BlobSha256.Span.SequenceEqual(SHA256.HashData(w.Pawn.ToByteArray()))) return new(false, "worker");
        return new(true, "", c.RemainingReferenceTokens.Count == 0);
    }
    public static AdmissionDecision Revalidate(CaravanAdmissionFacts before, CaravanAdmissionFacts after)
    {
        var current = Check(after); if (!current.Allowed) return current;
        return before.Profile == after.Profile && before.Caravan.Equals(after.Caravan)
            && before.Workers.SequenceEqual(after.Workers) ? current : new(false, "changed");
    }
    public static ByteString IntentDigest(OutpostPrepareRequest request)
    { var copy = request.Clone(); copy.IntentDigest = ByteString.Empty; return ByteString.CopyFrom(SHA256.HashData(copy.ToByteArray())); }
}

/// <summary>A second click joins the same in-process run; durable recovery belongs to TransferOutbox.</summary>
public sealed class CaravanAdmissionGate
{
    private readonly Dictionary<string, string> running = new(StringComparer.Ordinal);
    public bool TryBegin(string caravanKey, out string requestId)
    {
        if (string.IsNullOrEmpty(caravanKey) || caravanKey.Length > 512) throw new ArgumentException("invalid caravan run key", nameof(caravanKey));
        if (running.TryGetValue(caravanKey, out requestId!)) return false;
        requestId = Guid.NewGuid().ToString("D"); running.Add(caravanKey, requestId); return true;
    }
    public void Finish(string caravanKey) => running.Remove(caravanKey);
}
