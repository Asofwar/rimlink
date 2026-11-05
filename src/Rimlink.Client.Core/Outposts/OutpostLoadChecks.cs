using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

public sealed record OutpostLoadDecision(bool Allowed, string? RefusalKey, OutpostSaveMetadata? Metadata, bool RequiresFreshCapture);

/// <summary>Stage the complete mutable metadata update before the main thread changes any live bindings.</summary>
public static class OutpostLoadChecks
{
    public static OutpostLoadDecision Prepare(SaveHeader header, SaveRecoveryData recovery, byte[] rawHash, LoginReply? authentication)
    {
        var checkedDownload = OutpostDownloadChecks.Validate(header, recovery, rawHash, authentication);
        OutpostLoadDecision Refuse(string? key) => new(false, key, null, false);
        if (!checkedDownload.Allowed) return Refuse(checkedDownload.RefusalKey);
        var metadata = recovery.Outposts.Metadata.Clone();
        bool active = header.OutpostRollbackBridge is not null || header.OutpostRestoreBridge is not null || header.OutpostRollbackFromViewBridge is not null;
        if (active)
        {
            metadata.RollbackBridge = null; metadata.RestoreBridge = null; metadata.RollbackFromViewBridge = null;
            if (header.OutpostRollbackBridge is { } rollback)
            { metadata.Scope = rollback.CurrentScope.Clone(); metadata.RollbackBridge = rollback.Clone(); }
            else if (header.OutpostRestoreBridge is { } restore)
            { metadata.Scope = OutpostSourceGraph.ValidateRoot(restore).EffectiveScope.Clone(); metadata.RestoreBridge = restore.Clone(); }
            else if (header.OutpostRollbackFromViewBridge is { } view)
            {
                metadata.Scope = OutpostSourceGraph.ValidateRoot(view).EffectiveScope.Clone(); metadata.RollbackFromViewBridge = view.Clone();
            }
            else return Refuse("Rimlink.Outposts.RestoreIncompatible");
            metadata.Registry.Clear(); metadata.Registry.Add(checkedDownload.Registry.Select(r => r.Clone()));
        }
        try { new OutpostRecoveryData { Metadata = metadata }.Validate(); }
        catch (InvalidDataException) { return Refuse(checkedDownload.RefusalKey ?? "Rimlink.Outposts.RestoreIncompatible"); }
        return new(true, null, metadata, active || recovery.Version != SaveRecoveryData.CurrentVersion);
    }
}
