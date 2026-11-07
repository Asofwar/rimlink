using Google.Protobuf;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Grpc;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    public async Task<OutpostManifestAdminPreview> PreviewManifestTransportAsync(Actor actor, OutpostManifestAdminPreviewRequest request, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        try
        {
            if (request.ExpectedDigest.Length != 64 || request.ExpectedDigest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException();
            var preview = await PreviewConfirmManifestAsync(actor, request.ManifestId, Convert.FromHexString(request.ExpectedDigest), request.ClientRequestId, request.Reason, ct);
            var result = new OutpostManifestAdminPreview { ManifestId = request.ManifestId, Digest = request.ExpectedDigest };
            result.Blockers.AddRange(preview.Blockers);
            if (preview.Command is { } command)
            {
                result.CanonicalCommand = ByteString.CopyFrom(OutpostAdminIntentCodec.Encode(command.Intent));
                result.IntentSha256 = ByteString.CopyFrom(command.IntentDigest); result.Revision = command.Intent.Manifest!.Revision;
            }
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or OutpostEvidenceUnavailableException or FormatException)
        { throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, "outpost-admin-preview-unavailable"); }
    }

    public async Task<ManifestRevision> ConfirmManifestTransportAsync(Actor actor, OutpostManifestConfirmation request, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        try
        {
            var intent = OutpostAdminIntentCodec.Decode(request.CanonicalCommand.ToByteArray());
            if (intent.Action != OutpostAdminActionKind.ConfirmManifest || intent.Manifest is not { } selected
                || selected.ManifestId != request.ManifestId || Convert.ToHexStringLower(selected.Digest) != request.ExpectedDigest)
                throw new InvalidDataException();
            var committed = await ConfirmManifestAsync(actor, new(intent, request.IntentSha256.ToByteArray(), request.Confirmed), ct);
            return new ManifestRevision { ManifestId = selected.ManifestId, Digest = Convert.ToHexStringLower(selected.Digest), Revision = selected.Revision,
                CommandReceiptId = committed.Outcome.ReceiptId, AuthorityHeadId = committed.Head.Id,
                AuthorityHeadSha256 = committed.Head.Digest, Replay = committed.Replay };
        }
        catch (Exception error) when (error is InvalidDataException or OutpostEvidenceUnavailableException)
        { throw RpcErrors.Failed(ErrorCode.RecoveryRequired, RefusalKeys.OutpostsOperation, "outpost-admin-command-unavailable; preserve the original command for explicit retry"); }
    }
}
