using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.Server.Backup;

internal sealed record GuildAcceptanceRestorePermit(GuildAcceptanceProvenance Source, GuildAcceptanceProvenance Target,
    string ArchivePath, string ArchiveSha256, string DataDirectory, string AnchorDirectory, string RestoreId, string InstallationId)
{
    internal void RequirePaths(string archive, string data, string anchor)
    {
        if (archive != ArchivePath || data != DataDirectory || anchor != AnchorDirectory) throw Invalid();
    }

    internal void RequireSelection(RestoreSelection selection)
    {
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(Source);
        GuildAuthorityEvidence.ValidateAcceptanceIdentity(Target);
        RequirePaths(selection.ArchivePath, selection.DataDirectory, selection.AnchorDirectory);
        if (Source.SourceIdentity != Target.SourceIdentity || Source.WorldId != Target.WorldId || Source.StandId == Target.StandId
            || Source.BootstrapDigest == Target.BootstrapDigest || Source.Boundary == Target.Boundary
            || selection.TargetBoundary != Target.Boundary || selection.Mode != RestoreMode.ImportNewHost
            || selection.RestoreId != RestoreId || selection.TargetInstallationId != InstallationId
            || Convert.ToHexStringLower(selection.ArchiveDigest) != ArchiveSha256) throw Invalid();
    }

    internal static InvalidDataException Invalid() => new("restore-isolated-guild-acceptance-forbidden");
}
