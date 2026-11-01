using Grpc.Core;

namespace Rimlink.Client.Diagnostics;

public enum UploadFailure { None, Cancelled, Transport, Refused, LocalIo, Unexpected }
public sealed record UploadSnapshot(DateTime? LastSuccessUtc, int Version, UploadFailure Failure);

/// <summary>Per connection, so a different account/server never inherits another colony's upload status.</summary>
public sealed class UploadStatus
{
    private readonly object sync = new();
    private UploadSnapshot current = new(null, 0, UploadFailure.None);
    public UploadSnapshot Snapshot { get { lock (sync) return current; } }

    public void Succeeded(int version)
    {
        lock (sync) current = new(DateTime.UtcNow, version, UploadFailure.None);
    }

    public void Failed(Exception error)
    {
        var failure = error switch
        {
            OperationCanceledException => UploadFailure.Cancelled,
            RpcException { StatusCode: StatusCode.Cancelled } => UploadFailure.Cancelled,
            RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded } => UploadFailure.Transport,
            RpcException => UploadFailure.Refused,
            IOException or UnauthorizedAccessException => UploadFailure.LocalIo,
            _ => UploadFailure.Unexpected,
        };
        lock (sync) current = current with { Failure = failure };
    }
}
