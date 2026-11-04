using System.Runtime.CompilerServices;
using Google.Protobuf;
using RimWorld;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Outposts;

public sealed record CustodyDepartureEvidence(string OperationId, string Kind, string AssignmentId = "", bool ClearReference = false);

/// <summary>Real Scribe references, never a thing-id lookup. Only explicit proof-bearing ingress calls Bind.</summary>
public sealed class PawnCustodyRegistry : GameComponent
{
    private List<SavedPawn> entries = [];
    private readonly ConditionalWeakTable<Pawn, PawnRegistryEnvelope> transported = new();
    private readonly ConditionalWeakTable<Pawn, ReferenceKey> references = new();
    private sealed class ReferenceKey { public readonly string Value = Guid.NewGuid().ToString("N"); }
    public bool HasRecords => entries.Count != 0;
    public readonly string GameBranchId = Guid.NewGuid().ToString("D"); // intentionally NOT Scribed: a cold load is not continuity
    public PawnCustodyRegistry(Verse.Game game) { }
    public static PawnCustodyRegistry? CurrentRegistry => Current.Game?.GetComponent<PawnCustodyRegistry>();
    public static bool IsOwnGame => !Visits.VisitSession.InVisit && Live.LiveGuest.Session is null;

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving && IsOwnGame && ValidateOwnGame().RecoveryRequired)
            throw new InvalidDataException("ambiguous own pawn custody registry");
        Scribe_Collections.Look(ref entries, "rimlinkPawnCustody", LookMode.Deep);
        entries ??= [];
        if (entries.Count > OutpostRecoveryData.MaxEntries) throw new InvalidDataException("pawn registry limit exceeded");
    }

    public PawnRegistryEnvelope Bootstrap(Pawn pawn, OutpostScopeMessage scope)
    {
        if (!IsOwnGame) throw new InvalidDataException("no own custody scope");
        if (Capture(pawn) is { } known)
        {
            if (!known.Scope.Equals(scope) || known.NonAuthoritativeMirror || known.Tombstone) throw new InvalidDataException("cannot bootstrap known foreign custody");
            return known;
        }
        if (entries.Any(e => e.Envelope.PawnReferenceToken == pawn.GetUniqueLoadID()))
            throw new InvalidDataException("a reused thing id is not a new custody identity");
        var envelope = new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Guid.NewGuid().ToString("D"),
            Scope = scope.Clone(), PawnReferenceToken = pawn.GetUniqueLoadID() };
        Bind(pawn, envelope);
        return envelope.Clone();
    }

    public void Bind(Pawn pawn, PawnRegistryEnvelope envelope)
    {
        if (!IsOwnGame || envelope.NonAuthoritativeMirror || envelope.Tombstone) throw new InvalidDataException("non-authoritative custody ingress");
        var bound = envelope.Clone(); bound.PawnReferenceToken = pawn.GetUniqueLoadID();
        var check = PawnRegistryChecks.Validate([new(bound, "bound", bound.PawnReferenceToken)]);
        if (check.RecoveryRequired) throw new InvalidDataException("invalid custody ingress envelope");
        var previous = entries.SingleOrDefault(e => e.Envelope.PawnCustodyId == bound.PawnCustodyId);
        if (previous is not null && (ReferenceEquals(previous.Pawn, pawn) || previous.KnownTombstone))
        { previous.Pawn = pawn; previous.Envelope = bound; previous.KnownTombstone = false; previous.DepartureId = ""; }
        else entries.Add(new SavedPawn { Pawn = pawn, Envelope = bound }); // a distinct live object stays ambiguous, never replaced
        transported.Remove(pawn);
    }

    public PawnRegistryEnvelope? Capture(Pawn pawn)
    {
        var row = entries.FirstOrDefault(e => ReferenceEquals(e.Pawn, pawn));
        if (row is not null) return row.Envelope.Clone();
        return transported.TryGetValue(pawn, out var envelope) ? envelope.Clone() : null;
    }

    public void TrackBlob(Pawn pawn, PawnRegistryEnvelope? envelope, bool mirror)
    {
        if (envelope is null) return;
        var copy = envelope.Clone(); copy.NonAuthoritativeMirror |= mirror;
        transported.Remove(pawn); transported.Add(pawn, copy); // transport provenance alone NEVER joins entries
    }

    public void RecordDeparture(Pawn pawn, CustodyDepartureEvidence evidence)
    {
        var row = entries.FirstOrDefault(e => ReferenceEquals(e.Pawn, pawn));
        if (row is null) return;
        if (string.IsNullOrEmpty(evidence.OperationId) || evidence.OperationId.Length > 128 || string.IsNullOrEmpty(evidence.Kind))
            throw new InvalidDataException("departure requires its actual durable operation identity");
        if (!string.IsNullOrEmpty(evidence.AssignmentId))
        {
            if (!Guid.TryParse(evidence.AssignmentId, out _)) throw new InvalidDataException("invalid departure assignment");
            row.Envelope.AssignmentId = evidence.AssignmentId;
        }
        if (evidence.ClearReference) row.Envelope.PawnReferenceToken = "";
        row.Envelope.Tombstone = true; row.KnownTombstone = true; row.DepartureId = evidence.OperationId; row.Pawn = null;
    }

    public void RefreshBoundReference(Pawn pawn)
    {
        foreach (var row in entries.Where(e => ReferenceEquals(e.Pawn, pawn))) row.Envelope.PawnReferenceToken = pawn.GetUniqueLoadID();
    }

    public RegistryValidation ValidateOwnGame()
    {
        if (!IsOwnGame) return new([], []);
        var present = ScanPawns();
        var observations = new List<RegistryObservation>();
        foreach (var row in entries)
        {
            if (row.Pawn is { Dead: true } dead)
                RecordDeparture(dead, new("death:" + dead.GetUniqueLoadID(), "death"));
            bool exists = row.Pawn is not null && present.Contains(row.Pawn);
            observations.Add(new(row.Envelope, exists ? references.GetValue(row.Pawn!, _ => new()).Value : null,
                exists ? row.Pawn!.GetUniqueLoadID() : null, row.KnownTombstone));
        }
        // A deserialized own pawn is visible evidence, not authority. In particular a definitive old
        // outgoing restore must not silently hide behind the saved departure tombstone.
        foreach (var pawn in present.Where(p => p.Faction == Faction.OfPlayer && entries.All(e => !ReferenceEquals(e.Pawn, p))))
            if (transported.TryGetValue(pawn, out var envelope))
                observations.Add(new(envelope, references.GetValue(pawn, _ => new()).Value, pawn.GetUniqueLoadID(), AuthoritativeBinding: false));
        return PawnRegistryChecks.Validate(observations);
    }

    private static HashSet<Pawn> ScanPawns()
    {
        var result = new HashSet<Pawn>(ReferenceEqualityComparer.Instance);
        var roots = new List<Thing>();
        foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead) roots.Add(pawn);
        foreach (var map in Find.Maps)
        {
            var things = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.Everything), things);
            roots.AddRange(things);
        }
        foreach (var worldObject in Find.WorldObjects.AllWorldObjects)
            if (worldObject is IThingHolder holder) { var contents = new List<Thing>(); ThingOwnerUtility.GetAllThingsRecursively(holder, contents); roots.AddRange(contents); }
        foreach (var root in roots)
        {
            if (root is Pawn pawn) result.Add(pawn);
            if (root is Corpse corpse && corpse.InnerPawn is { } inner) result.Add(inner);
            if (root is not IThingHolder holder) continue;
            var contents = new List<Thing>(); ThingOwnerUtility.GetAllThingsRecursively(holder, contents);
            foreach (var nested in contents.OfType<Pawn>()) result.Add(nested);
        }
        return result;
    }

    public sealed class SavedPawn : IExposable
    {
        public Pawn? Pawn;
        public PawnRegistryEnvelope Envelope = new();
        public bool KnownTombstone;
        public string DepartureId = "";
        public void ExposeData()
        {
            string wire = Scribe.mode == LoadSaveMode.Saving ? Convert.ToBase64String(Envelope.ToByteArray()) : "";
            Scribe_Values.Look(ref wire, "envelope", "");
            Scribe_References.Look(ref Pawn, "pawn");
            Scribe_Values.Look(ref KnownTombstone, "knownTombstone");
            Scribe_Values.Look(ref DepartureId, "departureId", "");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (wire.Length > 65536) throw new InvalidDataException("pawn envelope limit exceeded");
                using var input = CodedInputStream.CreateWithLimits(new MemoryStream(Convert.FromBase64String(wire)), 49152, 16);
                Envelope = PawnRegistryEnvelope.Parser.ParseFrom(input);
            }
        }
    }
}
