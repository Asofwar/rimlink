using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rimlink.OutpostAcceptanceHost;

internal sealed record AcceptanceControlBinding(int Version, string StandId, string RunId, string SourceIdentity, string PipeName, string Nonce);
internal sealed record AcceptanceControlCommand(string Action, string? StepId = null, long DurationTicks = 0, string? Reason = null,
    string? Boundary = null, AcceptanceFaultTarget? Target = null, bool Drop = false, AcceptanceOfflineControl? Offline = null);
internal sealed record AcceptanceOfflineArm(string ArmId, string Hook, string? Path, int Occurrence);
internal sealed record AcceptanceOfflineControl(string CommandId, string? ArmId = null, AcceptanceOfflineArm? Arm = null, long Sequence = 0, string? EventSha256 = null);
internal sealed record AcceptanceControlRequest(int Version, string StandId, string RunId, string SourceIdentity, string Nonce, AcceptanceControlCommand Command);
internal sealed record AcceptanceControlReply(bool Success, string Code, System.Text.Json.JsonElement? Data = null);
internal interface IAcceptanceControlTarget
{
    ValueTask<AcceptanceControlReply> HandleAsync(AcceptanceControlCommand command, CancellationToken ct);
}
internal static class AcceptanceControlJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
}

internal sealed class AcceptanceControlPipe(AcceptanceControlBinding binding, IAcceptanceControlTarget target)
{
    private const int MaximumBytes = 16 * 1024;

    internal async Task ServeAsync(CancellationToken ct)
    {
        Validate(binding);
        // Keep the Unix listening socket alive while disconnecting accepted clients.
        await using var server = new NamedPipeServerStream(binding.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await server.WaitForConnectionAsync(ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5)); // Real transport time, never the accelerated outpost clock.
            try
            {
                var request = JsonSerializer.Deserialize<AcceptanceControlRequest>(await ReadAsync(server, deadline.Token), AcceptanceControlJson.Options)
                    ?? throw new InvalidDataException("acceptance-control-request-empty");
                bool authorized = request.Version == 1 && request.StandId == binding.StandId && request.RunId == binding.RunId
                    && request.SourceIdentity == binding.SourceIdentity && Hash(request.Nonce)
                    && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Nonce), Encoding.ASCII.GetBytes(binding.Nonce));
                var result = authorized ? await target.HandleAsync(request.Command, deadline.Token)
                    : new AcceptanceControlReply(false, "acceptance-control-run-mismatch");
                await WriteAsync(server, JsonSerializer.SerializeToUtf8Bytes(result, AcceptanceControlJson.Options), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A partial/disconnected control request has an unknown outcome. Never fabricate a no-effect reply.
            }
            finally { server.Disconnect(); }
        }
    }

    internal static async Task<AcceptanceControlReply> SendAsync(AcceptanceControlBinding binding, AcceptanceControlCommand command, CancellationToken ct)
    {
        Validate(binding);
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new AcceptanceControlRequest(1, binding.StandId, binding.RunId,
            binding.SourceIdentity, binding.Nonce, command), AcceptanceControlJson.Options);
        if (request.Length > MaximumBytes) throw new InvalidDataException("acceptance-control-frame-limit");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var client = new NamedPipeClientStream(".", binding.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token);
        await WriteAsync(client, request, deadline.Token);
        return JsonSerializer.Deserialize<AcceptanceControlReply>(await ReadAsync(client, deadline.Token), AcceptanceControlJson.Options)
            ?? throw new InvalidDataException("acceptance-control-reply-empty");
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("acceptance-control-frame-limit");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, ct); return body;
    }
    private static async Task WriteAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        if (body.Length is < 1 or > MaximumBytes) throw new InvalidDataException("acceptance-control-frame-limit");
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        await stream.WriteAsync(size, ct); await stream.WriteAsync(body, ct); await stream.FlushAsync(ct);
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Validate(AcceptanceControlBinding value)
    {
        if (value.Version != 1 || !Guid.TryParseExact(value.StandId, "D", out var stand) || stand == Guid.Empty
            || !Guid.TryParseExact(value.RunId, "D", out var run) || run == Guid.Empty || !Hash(value.SourceIdentity) || !Hash(value.Nonce)
            || value.PipeName.Length is < 1 or > 80 || !value.PipeName.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new InvalidDataException("acceptance-control-binding-invalid");
    }
}
