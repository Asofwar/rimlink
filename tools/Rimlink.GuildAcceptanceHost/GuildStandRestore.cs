using Rimlink.OutpostAcceptanceHost;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Backup;

namespace Rimlink.GuildAcceptanceHost;

internal sealed record GuildStandRestore(int Version, string SourceRoot, GuildAcceptanceRestorePermit Permit);

internal sealed partial class GuildAcceptanceComposition
{
    private const string RestoreAdmission = "restore-admission.json";
    internal bool IsRestore => restore is not null;

    private void ReadRestore()
    {
        var stand = Bootstrap.Stand;
        string path = GuildStandFiles.PathIn(stand, RestoreAdmission);
        if (!File.Exists(path)) return;
        var selected = GuildStandFiles.Read<GuildStandRestore>(path);
        var target = selected.Permit.Target;
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(target);
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(selected.Permit.Source);
        if (selected.Version != 1 || target.StandId != stand.StandId || target.BootstrapDigest != BootstrapDigest
            || target.SourceIdentity != Bootstrap.SourceIdentity
            || selected.Permit.DataDirectory != GuildStandFiles.PathIn(stand, "data")
            || selected.Permit.AnchorDirectory != GuildStandFiles.PathIn(stand, "anchor")) throw GuildAcceptanceRestorePermit.Invalid();
        evidence.Pin(path);
        restore = selected;
    }

    internal static async Task RestoreOfflineAsync(string sourceRoot, string targetRoot, string relativeArchive, string archiveHash)
    {
        GuildStandFiles.RelativeFile(relativeArchive);
        if (!relativeArchive.StartsWith("evidence/", StringComparison.Ordinal) || !GuildStandFiles.HashText(archiveHash)
            || AcceptancePaths.Within(sourceRoot, targetRoot) || AcceptancePaths.Within(targetRoot, sourceRoot))
            throw GuildAcceptanceRestorePermit.Invalid();
        using var source = Open(sourceRoot);
        using var target = Open(targetRoot);
        var from = source.Bootstrap.Stand;
        var to = target.Bootstrap.Stand;
        if (source.Observe() is not { AutomatedPrerequisitesVerified: true, IsolationVerified: true }
            || target.Observe() is not { AutomatedPrerequisitesVerified: true, IsolationVerified: true }
            || source.scope != GuildAcceptanceScope.Slice || source.world is not { } original
            || from.StandId == to.StandId || from.Port == to.Port || from.ProfileDigest != to.ProfileDigest || from.ExecutorId != to.ExecutorId
            || from.AttackerLogin != to.AttackerLogin || from.DefenderLogin != to.DefenderLogin
            || target.restore is null && target.world is not null)
            throw GuildAcceptanceRestorePermit.Invalid();

        string sourceData = GuildStandFiles.PathIn(from, "data");
        using var stoppedSource = new FileStream(Path.Combine(sourceData, BackupArchive.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string archive = GuildStandFiles.PathIn(from, relativeArchive);
        source.evidence.Pin(archive, archiveHash);
        string data = GuildStandFiles.PathIn(to, "data"), anchor = GuildStandFiles.PathIn(to, "anchor");
        var origin = new GuildAcceptanceProvenance(1, GuildStandFiles.Mode, from.StandId, source.BootstrapDigest,
            source.Bootstrap.SourceIdentity, original.WorldId, original.Boundary);
        var permit = target.restore?.Permit ?? new GuildAcceptanceRestorePermit(origin,
            origin with { StandId = to.StandId, BootstrapDigest = target.BootstrapDigest, Boundary = Guid.NewGuid().ToString("D") },
            archive, archiveHash, data, anchor, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        if (permit.Source != origin || permit.ArchiveSha256 != archiveHash || target.restore is { } prior && prior.SourceRoot != sourceRoot)
            throw GuildAcceptanceRestorePermit.Invalid();
        permit.RequirePaths(archive, data, anchor);

        var procedure = new OutpostRestoreProcedure(permit, source.clock);
        var (inspection, clockPrefix) = await procedure.InspectIsolatedAsync(CancellationToken.None);
        var request = new RestoreRequest(permit.RestoreId, RestoreMode.ImportNewHost, inspection, permit.InstallationId,
            permit.Target.Boundary, [], "sqlite-local-v1");
        var selection = OutpostRestoreJournalCodec.Freeze(request, permit);
        string journal = Path.Combine(anchor, "restores", permit.RestoreId, "intent.json");
        if (!File.Exists(journal))
        {
            if (inspection.Destination.AnchorState != RestoreAnchorState.ProvisionedEmpty || inspection.Destination.DatabaseExists)
                throw GuildAcceptanceRestorePermit.Invalid();
            OutpostRestoreFence.RequireUnclaimedFreshAnchor(anchor);
            OutpostRestoreFence.RequireEmptyDestination(data);
        }
        source.clock.Synchronize(clockPrefix);
        if (target.restore is null)
            GuildStandFiles.Create(GuildStandFiles.PathIn(to, RestoreAdmission), new GuildStandRestore(1, sourceRoot, permit));
        var result = File.Exists(journal)
            ? await procedure.ResumeAsync(data, anchor, permit.RestoreId, CancellationToken.None)
            : await procedure.ExecuteAsync(request with { ConfirmationDigest = OutpostRestoreJournalCodec.Digest(selection) }, CancellationToken.None);
        if (result.Outcome != RestoreOutcome.Completed || result.Boundary != permit.Target.Boundary || result.InstallationId != permit.InstallationId)
            throw new InvalidDataException(result.Reason);
        if (target.world is null)
            GuildStandFiles.Create(GuildStandFiles.PathIn(to, "world.json"),
                new GuildStandWorld(1, to.StandId, target.BootstrapDigest, origin.WorldId, permit.Target.Boundary));
        Console.WriteLine($"Isolated RESTORE completed. RestoreId={permit.RestoreId} Boundary={permit.Target.Boundary} Product OFF.");
    }
}
