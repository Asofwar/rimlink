using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>ObjectIdentity is a process-local reference key, never Thing.Equals or an authority coordinate.</summary>
public sealed record RegistryObservation(PawnRegistryEnvelope Envelope, string? ObjectIdentity = null,
    string? CurrentReferenceToken = null, bool KnownTombstone = false, bool AuthoritativeBinding = true);
public sealed record RegistryIssue(string Kind, string CustodyId);
public sealed record RegistryValidation(IReadOnlyList<PawnRegistryEnvelope> Registry, IReadOnlyList<RegistryIssue> Issues)
{
    public bool RecoveryRequired => Issues.Count != 0;
}

public static class PawnRegistryChecks
{
    public static RegistryValidation Validate(IReadOnlyList<RegistryObservation> observations)
    {
        var issues = new List<RegistryIssue>();
        if (observations.Count > 10000) return new([], [new("registry-limit", "")]);
        var identities = new Dictionary<Guid, RegistryObservation>();
        var objects = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var envelope = observation.Envelope;
            if (envelope.NonAuthoritativeMirror) continue; // a mirror cannot contribute authority, including a duplicate claim
            if (!observation.AuthoritativeBinding) { issues.Add(new("unbound-transported-pawn", envelope.PawnCustodyId)); continue; }
            if (envelope.SchemaVersion != 1 || !Guid.TryParse(envelope.PawnCustodyId, out var custody)
                || envelope.PawnCustodyId != custody.ToString("D") && envelope.PawnCustodyId != custody.ToString("N")
                || !OutpostReconciliation.ValidScope(envelope.Scope) || envelope.PawnReferenceToken.Length > 128
                || envelope.AssignmentId.Length > 0 && !Guid.TryParse(envelope.AssignmentId, out _))
            { issues.Add(new("malformed-registry", envelope.PawnCustodyId)); continue; }
            bool hasObject = !string.IsNullOrEmpty(observation.ObjectIdentity);
            if (envelope.Tombstone ? !observation.KnownTombstone || hasObject
                : !hasObject || envelope.PawnReferenceToken.Length == 0
                    || envelope.PawnReferenceToken != observation.CurrentReferenceToken)
                issues.Add(new("unproven-reference", envelope.PawnCustodyId));
            if (identities.TryGetValue(custody, out var previous))
            {
                if (previous.ObjectIdentity != observation.ObjectIdentity || !previous.Envelope.Equals(envelope))
                    issues.Add(new("duplicate-custody", envelope.PawnCustodyId));
            }
            else identities.Add(custody, observation);
            if (hasObject && objects.TryGetValue(observation.ObjectIdentity!, out var priorObject) && priorObject != custody)
                issues.Add(new("multiple-custodies-for-object", envelope.PawnCustodyId));
            else if (hasObject) objects[observation.ObjectIdentity!] = custody;
            if (envelope.PawnReferenceToken.Length > 0)
            {
                if (tokens.TryGetValue(envelope.PawnReferenceToken, out var priorToken) && priorToken != custody)
                    issues.Add(new("reused-reference-token", envelope.PawnCustodyId));
                else tokens[envelope.PawnReferenceToken] = custody;
            }
        }
        // Never leak a plausible partial registry from an ambiguous own-game graph.
        return new(issues.Count == 0 ? identities.Values.Select(p => p.Envelope.Clone()).ToArray() : [], issues.ToArray());
    }
}
