using Rimlink.OutpostAcceptanceHost;
using Xunit;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceControlPipeTests
{
    [Fact]
    public async Task A_queued_unix_client_survives_completion_of_the_previous_connection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var binding = Binding(); var target = new HeldTarget(); var pipe = new AcceptanceControlPipe(binding, target);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var server = pipe.ServeAsync(stop.Token);
        try
        {
            var first = AcceptanceControlPipe.SendAsync(binding, new("observe"), stop.Token);
            await target.Entered.Task.WaitAsync(stop.Token);
            await using var queued = new NamedPipeClientStream(".", binding.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await queued.ConnectAsync(stop.Token);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new AcceptanceControlRequest(1, binding.StandId, binding.RunId,
                binding.SourceIdentity, binding.Nonce, new("observe")), AcceptanceControlJson.Options);
            byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
            await queued.WriteAsync(size, stop.Token); await queued.WriteAsync(body, stop.Token);
            target.Continue.TrySetResult(); Assert.True((await first).Success);
            await queued.ReadExactlyAsync(size, stop.Token); byte[] reply = new byte[BinaryPrimitives.ReadInt32LittleEndian(size)];
            await queued.ReadExactlyAsync(reply, stop.Token);
            Assert.True(JsonSerializer.Deserialize<AcceptanceControlReply>(reply, AcceptanceControlJson.Options)!.Success);
            Assert.Equal(2, target.Calls);
        }
        finally { target.Continue.TrySetResult(); stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task Consecutive_clients_do_not_lose_a_request_during_server_connection_turnover()
    {
        var binding = Binding(); var target = new Target(); var pipe = new AcceptanceControlPipe(binding, target);
        using var stop = new CancellationTokenSource(); var server = pipe.ServeAsync(stop.Token);
        try
        {
            for (int i = 0; i < 200; i++)
            {
                var reply = await AcceptanceControlPipe.SendAsync(binding, new("observe"), default).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(reply.Success, $"request {i}: {reply.Code}");
            }
            Assert.Equal(200, target.Calls);
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task Only_the_exact_owned_run_can_invoke_the_private_target()
    {
        var binding = Binding(); var target = new Target(); var pipe = new AcceptanceControlPipe(binding, target);
        using var stop = new CancellationTokenSource(); var server = pipe.ServeAsync(stop.Token);
        try
        {
            var good = await AcceptanceControlPipe.SendAsync(binding, new("observe"), default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(good.Success, good.Code); Assert.Equal(1, target.Calls);
            foreach (var wrong in new[] { binding with { RunId = Guid.NewGuid().ToString("D") }, binding with { Nonce = new string('2', 64) }, binding with { SourceIdentity = new string('0', 64) } })
            {
                var rejected = await AcceptanceControlPipe.SendAsync(wrong, new("observe"), default).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(rejected.Success); Assert.Equal(1, target.Calls);
            }
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task Cancellation_stops_an_idle_listener_and_oversized_input_never_reaches_the_target()
    {
        var binding = Binding(); var target = new Target(); var pipe = new AcceptanceControlPipe(binding, target);
        using var stop = new CancellationTokenSource(); var server = pipe.ServeAsync(stop.Token);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => AcceptanceControlPipe.SendAsync(binding,
                new("advance", Guid.NewGuid().ToString("D"), 1, new string('a', 20000)), default));
            Assert.Equal(0, target.Calls);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server); }
    }

    private static AcceptanceControlBinding Binding()
    {
        string run = Guid.NewGuid().ToString("D");
        return new(1, Guid.NewGuid().ToString("D"), run, Rimlink.Server.Application.Outposts.OutpostProductQualification.SourceIdentity,
            "rlo-test-" + Guid.Parse(run).ToString("N"), new string('1', 64));
    }
    private sealed class Target : IAcceptanceControlTarget
    {
        internal int Calls;
        public ValueTask<AcceptanceControlReply> HandleAsync(AcceptanceControlCommand command, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return ValueTask.FromResult(new AcceptanceControlReply(true, "observed")); }
    }
    private sealed class HeldTarget : IAcceptanceControlTarget
    {
        internal int Calls;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AcceptanceControlReply> HandleAsync(AcceptanceControlCommand command, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1) { Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); }
            return new(true, "observed");
        }
    }
}
