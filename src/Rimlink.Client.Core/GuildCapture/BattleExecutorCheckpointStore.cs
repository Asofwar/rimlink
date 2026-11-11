using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

// A cold read exposes immutable delivery evidence only; it never reconstructs a native execution context.
public sealed class BattleExecutorCheckpointStore
{
    private const int MaximumBytes=65536;
    private readonly BattleExecutorSettings settings;
    public string Path {get;}
    public BattleExecutorCheckpointStore(BattleExecutorSettings settings)
    {
        this.settings=settings;Path=System.IO.Path.Combine(System.IO.Path.GetFullPath(settings.StateDirectory),"executor.checkpoint");
        BattleExecutorSettings.RequireNoLinks(Path);
    }
    public BattleExecutorCheckpointV1 Create(BattleRuntimeAssignmentV1 assignment)
    {
        if (File.Exists(CompletedPath(BattleRuntimeAssignmentContracts.Read(assignment.ToByteArray()).Scope))) throw Invalid();
        var value=new BattleExecutorCheckpointV1{Version=1,Endpoint=settings.Endpoint,CertificateSha256=Bytes(settings.CertificateSha256),ProfileFileSha256=Bytes(settings.ProfileSha256),
            Assignment=assignment.Clone(),Stage=BattleExecutorCheckpointStage.Assigned};Validate(value);return value;
    }
    public BattleExecutorCheckpointV1? Read()
    {
        RequireNoPartial();if(!File.Exists(Path))return null;
        return ReadDocument(Path);
    }
    private BattleExecutorCheckpointV1 ReadDocument(string path)
    {
        byte[] bytes=BattleExecutorSettings.ReadFile(path,MaximumBytes);BattleExecutorCheckpointV1 value;
        try{value=BattleExecutorCheckpointV1.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes);}catch(InvalidProtocolBufferException){throw Invalid();}
        if(!value.ToByteArray().AsSpan().SequenceEqual(bytes))throw Invalid();Validate(value);return value;
    }
    public void Save(BattleExecutorCheckpointV1 value)
    {
        value=value.Clone();Validate(value);var prior=Read();
        if(prior is not null)
        {
            if(prior.Equals(value))return;
            if(!prior.Assignment.Equals(value.Assignment) || prior.Endpoint!=value.Endpoint || !prior.CertificateSha256.Equals(value.CertificateSha256)
                || !prior.ProfileFileSha256.Equals(value.ProfileFileSha256) || !Next(prior.Stage,value.Stage)
                || prior.Start is not null && !prior.Start.Equals(value.Start) || !prior.FinalEnvelopeSha256.IsEmpty && !prior.FinalEnvelopeSha256.Equals(value.FinalEnvelopeSha256)
                || prior.Applied is not null && !prior.Applied.Equals(value.Applied) || prior.Cleanup is not null && !prior.Cleanup.Equals(value.Cleanup)
                || prior.CancellationTransitionId!="" && prior.CancellationTransitionId!=value.CancellationTransitionId
                || prior.AbortTerminal is not null && !prior.AbortTerminal.Equals(value.AbortTerminal))throw Invalid();
            if(!prior.PreparedManifest.IsEmpty && !prior.PreparedManifest.Equals(value.PreparedManifest))
            {
                if(prior.Stage!=BattleExecutorCheckpointStage.Prepared || value.Stage!=BattleExecutorCheckpointStage.Prepared
                    || BattleManifestContracts.ReadPrepared(value.PreparedManifest.Span).ProposalSequence!=BattleManifestContracts.ReadPrepared(prior.PreparedManifest.Span).ProposalSequence+1)throw Invalid();
            }
        }
        else if(value.Stage!=BattleExecutorCheckpointStage.Assigned)throw Invalid();
        TransferOutbox.WriteDocument(Path,value.ToByteArray());
    }
    public string ArchiveCleaned()
    {
        var value=Read()??throw Invalid();if(value.Stage!=BattleExecutorCheckpointStage.Cleaned)throw Invalid();
        string archive=CompletedPath(value.Assignment.Scope);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(archive)!);
        File.Move(Path,archive,false);return archive;
    }
    public BattleExecutorCheckpointV1 ReadCompleted(BattleScopeMessage scope)
    {
        var value = ReadDocument(CompletedPath(scope));
        if (value.Stage != BattleExecutorCheckpointStage.Cleaned || !value.Assignment.Scope.Equals(scope)) throw Invalid();
        return value;
    }
    private string CompletedPath(BattleScopeMessage scope)
    {
        if (!Uuid(scope.BattleId) || scope.Attempt <= 0 || scope.Fence <= 0) throw Invalid();
        string archive = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "completed",
            scope.BattleId + "-" + scope.Attempt.ToString(CultureInfo.InvariantCulture) + "-" + scope.Fence.ToString(CultureInfo.InvariantCulture) + ".checkpoint");
        BattleExecutorSettings.RequireNoLinks(archive);
        return archive;
    }
    public BattleFinalEnvelope ReadColdEnvelope()
    {
        var state=Read()??throw Invalid();if(state.Start is null || state.FinalEnvelopeSha256.Length!=32 || state.AbortTerminal is not null)throw Invalid();
        string envelopePath=ArtifactPath(state,"envelope");byte[] bytes=BattleExecutorSettings.ReadFile(envelopePath,BattleManifestContracts.FinalBytes+1024);
        if(!SHA256.HashData(bytes).AsSpan().SequenceEqual(state.FinalEnvelopeSha256.Span))throw Invalid();
        var envelope=BattleManifestContracts.ReadFinalEnvelope(bytes);var start=BattleManifestContracts.ReadPrepared(state.Start.Manifest.Span);
        var final=BattleManifestContracts.ReadFinal(envelope.Manifest.Span,start);
        foreach(var(kind,descriptor)in new[]{("initial",final.InitialSnapshot),("final",final.FinalSnapshot),("simulation",final.SimulationJournal),("view",final.TerminalView)})
        {
            string path=ArtifactPath(state,kind);BattleExecutorSettings.RequireNoLinks(path);
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
                if(input.Length!=descriptor.SizeBytes || !SHA256.HashData(input).AsSpan().SequenceEqual(descriptor.Sha256.Span))throw Invalid();
            if(kind=="view")BattleManifestContracts.ReadTerminalView(BattleExecutorSettings.ReadFile(path,1048576),final);
        }
        return envelope;
    }
    public string ArtifactPath(string kind)=>ArtifactPath(Read()??throw Invalid(),kind);
    private string ArtifactPath(BattleExecutorCheckpointV1 state,string kind)
    {
        if(kind is not("initial" or "final" or "simulation" or "view" or "envelope" or "receipts"))throw Invalid();var scope=state.Assignment.Scope;
        string path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!,scope.BattleId,scope.Attempt.ToString(CultureInfo.InvariantCulture),scope.Fence.ToString(CultureInfo.InvariantCulture),kind+".artifact");
        BattleExecutorSettings.RequireNoLinks(path);return path;
    }
    private void Validate(BattleExecutorCheckpointV1 value)
    {
        if(value.Version!=1 || value.CalculateSize()>MaximumBytes || value.Endpoint!=settings.Endpoint || !value.CertificateSha256.Equals(Bytes(settings.CertificateSha256))
            || !value.ProfileFileSha256.Equals(Bytes(settings.ProfileSha256)) || value.Assignment is null || !Enum.IsDefined(value.Stage) || value.Stage==BattleExecutorCheckpointStage.Unspecified)throw Invalid();
        var assignment=BattleRuntimeAssignmentContracts.Read(value.Assignment.ToByteArray());
        if(assignment.ExecutorId!=settings.ExecutorId || assignment.Scope.WorldInstanceId!=settings.WorldInstanceId || assignment.Scope.RestoreBoundary!=settings.RestoreBoundary || !assignment.ProfileDigest.Equals(Bytes(settings.ProfileDigest)))throw Invalid();
        if(!value.PreparedManifest.IsEmpty)
        {
            var manifest=BattleManifestContracts.ReadPrepared(value.PreparedManifest.Span);
            if(manifest.WorldInstanceId!=assignment.Scope.WorldInstanceId || manifest.RestoreBoundary!=assignment.Scope.RestoreBoundary || manifest.BattleId!=assignment.Scope.BattleId
                || manifest.Attempt!=assignment.Scope.Attempt || manifest.Fence!=assignment.Scope.Fence || manifest.CampaignId!=assignment.CampaignId || manifest.ExecutorId!=assignment.ExecutorId
                || manifest.RuntimeInstanceId!=assignment.RuntimeInstanceId || manifest.ExecutorLeaseId!=assignment.OriginalLeaseId || manifest.ExecutorLeaseFence!=assignment.OriginalLeaseFence
                || !manifest.StartNonce.Equals(assignment.StartNonce) || !manifest.ProfileDigest.Equals(assignment.ProfileDigest) || !manifest.MeasuredInventory.Sha256.Equals(assignment.InventorySha256)
                || manifest.ApprovalTransitionId!=assignment.ApprovalTransitionId || manifest.PreparationTransitionId!=assignment.TransitionId)throw Invalid();
        }
        if(value.Start is {} authorization)
        {
            if(value.PreparedManifest.IsEmpty || !authorization.Manifest.Equals(value.PreparedManifest) || !authorization.Scope.Equals(assignment.Scope)
                || !authorization.ManifestDigest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest,authorization.Manifest.Span))
                || authorization.StartedAtUnixMs<=0 || !Uuid(authorization.TransitionId) || !Uuid(authorization.ReadyTransitionId) || !Uuid(authorization.AuthorityHeadId)
                || authorization.AuthorityHeadDigest.Length!=32)throw Invalid();
        }
        if(value.FinalEnvelopeSha256.Length is not(0 or 32) || !value.FinalEnvelopeSha256.IsEmpty && value.Start is null)throw Invalid();
        if(value.Applied is {} applied && (value.FinalEnvelopeSha256.Length!=32 || applied.Version!=1 || applied.WorldInstanceId!=assignment.Scope.WorldInstanceId
            || applied.RestoreBoundary!=assignment.Scope.RestoreBoundary || applied.BattleId!=assignment.Scope.BattleId || applied.Attempt!=assignment.Scope.Attempt || applied.Fence!=assignment.Scope.Fence
            || applied.CommittedFinalDigest.Length!=32 || !Uuid(applied.TransitionId)))throw Invalid();
        var abort = value.AbortTerminal;
        if (abort is not null)
        {
            BattleCleanupContracts.ValidateTerminal(abort);
            if (abort.Kind != BattleCleanupTerminalKind.StartedAbort || abort.AssignmentTransitionId != assignment.TransitionId
                || abort.TerminalTransitionId != value.CancellationTransitionId || value.PreparedManifest.IsEmpty
                || !abort.AbortedStartDigest.Span.SequenceEqual(BattleManifestContracts.Digest(BattleDocumentKind.PreparedManifest, value.PreparedManifest.Span)))
                throw Invalid();
        }
        if(value.CancellationTransitionId!="" && (!Uuid(value.CancellationTransitionId) || value.Applied is not null
            || abort is null && (value.Start is not null || !value.FinalEnvelopeSha256.IsEmpty)))throw Invalid();
        if(value.Cleanup is {} cleanupProof)BattleCleanupContracts.ValidateReceipt(cleanupProof);
        if(value.Cleanup is {} cleanup && (cleanup.Version!=1 || cleanup.ProofKind != BattleCleanupProofKind.SameProcessEntry
            || !cleanup.Scope.Equals(assignment.Scope) || !Uuid(cleanup.TransitionId) || cleanup.RuntimeInstanceId!=assignment.RuntimeInstanceId
            || value.Applied is null && (abort is null
                ? cleanup.Terminal.Kind != BattleCleanupTerminalKind.PreStartCancellation || cleanup.Terminal.TerminalTransitionId != value.CancellationTransitionId
                : !cleanup.Terminal.Equals(abort))
            || value.Applied is not null && (!cleanup.CommittedFinalDigest.Equals(value.Applied.CommittedFinalDigest) || cleanup.Terminal.Kind!=BattleCleanupTerminalKind.AppliedResult || cleanup.Terminal.TerminalTransitionId!=value.Applied.TransitionId)
            || cleanup.Terminal.AssignmentTransitionId!=assignment.TransitionId))throw Invalid();
        bool startedAbort = abort is not null && value.Stage == BattleExecutorCheckpointStage.Cancelled;
        if(value.Start is not null && !startedAbort && value.Stage is not(BattleExecutorCheckpointStage.Started or BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned)
            || !value.FinalEnvelopeSha256.IsEmpty && !startedAbort && value.Stage is not(BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned)
            || value.Applied is not null && value.Stage is not(BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned)
            || value.Cleanup is not null && value.Stage!=BattleExecutorCheckpointStage.Cleaned || value.CancellationTransitionId!="" && value.Stage is not(BattleExecutorCheckpointStage.Cancelled or BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned))throw Invalid();
        bool prepared=value.Stage is BattleExecutorCheckpointStage.Prepared or BattleExecutorCheckpointStage.Started or BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Applied;
        bool started=value.Stage is BattleExecutorCheckpointStage.Started or BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Applied;
        if(prepared && value.PreparedManifest.IsEmpty || started && value.Start is null || value.Stage==BattleExecutorCheckpointStage.FinalCaptured && value.FinalEnvelopeSha256.Length!=32
            || value.Stage==BattleExecutorCheckpointStage.Applied && value.Applied is null || value.Stage==BattleExecutorCheckpointStage.Cancelled && value.CancellationTransitionId==""
            || value.Stage is BattleExecutorCheckpointStage.AtEntry or BattleExecutorCheckpointStage.Cleaned && value.Applied is null && value.CancellationTransitionId==""
            || value.Stage==BattleExecutorCheckpointStage.Cleaned && value.Cleanup is null)throw Invalid();
        if(value.Stage is BattleExecutorCheckpointStage.Assigned or BattleExecutorCheckpointStage.Initial && (!value.PreparedManifest.IsEmpty || value.Start is not null || !value.FinalEnvelopeSha256.IsEmpty || value.Applied is not null || value.Cleanup is not null))throw Invalid();
    }
    private void RequireNoPartial()
    {
        BattleExecutorSettings.RequireNoLinks(Path);string directory=System.IO.Path.GetDirectoryName(Path)!;
        if(Directory.Exists(directory) && Directory.EnumerateFiles(directory,System.IO.Path.GetFileName(Path)+".tmp*").Any())throw Invalid();
    }
    private static bool Next(BattleExecutorCheckpointStage a,BattleExecutorCheckpointStage b)=>a switch
    {
        BattleExecutorCheckpointStage.Assigned=>b is BattleExecutorCheckpointStage.Initial or BattleExecutorCheckpointStage.Cancelled,
        BattleExecutorCheckpointStage.Initial=>b is BattleExecutorCheckpointStage.Prepared or BattleExecutorCheckpointStage.Cancelled,
        BattleExecutorCheckpointStage.Prepared=>b is BattleExecutorCheckpointStage.Prepared or BattleExecutorCheckpointStage.Started or BattleExecutorCheckpointStage.Cancelled,
        BattleExecutorCheckpointStage.Started=>b is BattleExecutorCheckpointStage.FinalCaptured or BattleExecutorCheckpointStage.Cancelled,
        BattleExecutorCheckpointStage.FinalCaptured=>b is BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.Cancelled,
        BattleExecutorCheckpointStage.Applied or BattleExecutorCheckpointStage.Cancelled=>b==BattleExecutorCheckpointStage.AtEntry,
        BattleExecutorCheckpointStage.AtEntry=>b==BattleExecutorCheckpointStage.Cleaned,
        _=>false
    };
    private static ByteString Bytes(string hex)=>ByteString.CopyFrom(Convert.FromHexString(hex));
    private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    private static InvalidDataException Invalid()=>new("battle-executor-checkpoint-invalid");
}
