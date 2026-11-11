using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleExecutorRunningPumpTests
{
    [Fact]
    public async Task Lost_acknowledgement_reply_is_retried_before_polling_for_another_delivery()
    {
        var scope = new BattleScopeMessage { WorldInstanceId = "world", RestoreBoundary = Guid.NewGuid().ToString("D"),
            BattleId = Guid.NewGuid().ToString("D"), Attempt = 1, Fence = 1 };
        var settings = new BattleExecutorSettings { ExecutorId = "arbiter-1", Endpoint = "https://localhost",
            CertificateSha256 = new string('a', 64), ProfileDigest = new string('b', 64) };
        var lease = new BattleExecutorLeaseMessage { ExecutorId = settings.ExecutorId, LeaseId = Guid.NewGuid().ToString("D"),
            Fence = 1, ProfileDigest = ByteString.CopyFrom(Convert.FromHexString(settings.ProfileDigest)) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var wire = new LostAckWire(lease, stop);
        using var connection = new BattleExecutorConnection(settings, new string('s', 32), new ConnectionTransport(new("http://localhost"), wire));
        await connection.Register(stop.Token);
        var controller = new BattleTickController();
        BattleTickController.Current = controller;
        BattleGameContext.Scope = scope;
        int reconnects = 0;
        try
        {
            var pump = new BattleExecutorRunningPump(() => connection, controller, scope,
                _ => { reconnects++; return Task.CompletedTask; });
            await pump.Run(stop.Token);
        }
        finally
        {
            BattleTickController.Current = null;
            BattleGameContext.Scope = null;
        }

        Assert.Null(controller.Failure);
        Assert.Equal(1, reconnects);
        Assert.Equal(new[] { "PollRunning", "AcknowledgeCommands", "AcknowledgeCommands", "PollRunning",
            "AcknowledgeCommands", "PollRunning" }, wire.Calls);
        Assert.Equal(3, wire.Acknowledgements.Count);
        Assert.Equal(wire.Acknowledgements[0], wire.Acknowledgements[1]);
        Assert.Equal(8, wire.Acknowledgements[0].Receipts.Count);
        Assert.Single(wire.Acknowledgements[0].AppliedCutIds);
        Assert.NotEqual(wire.Acknowledgements[0].DeliveryId, wire.Acknowledgements[2].DeliveryId);
        Assert.Equal(9, Assert.Single(wire.Acknowledgements[2].Receipts).Sequence);
    }

    private sealed class LostAckWire(BattleExecutorLeaseMessage lease, CancellationTokenSource stop) : HttpMessageHandler
    {
        private int polls;
        internal List<string> Calls { get; } = [];
        internal List<BattleExecutorCommandsAck> Acknowledgements { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string method = request.RequestUri!.Segments[^1];
            if (method == "Register") return Reply(lease);
            Calls.Add(method);
            if (method == "AcknowledgeCommands")
            {
                byte[] body = await request.Content!.ReadAsByteArrayAsync(ct);
                Acknowledgements.Add(BattleExecutorCommandsAck.Parser.ParseFrom(body[5..]));
                return Reply(new BattleTransportPosition(), lost: Acknowledgements.Count == 1);
            }
            Assert.Equal("PollRunning", method);
            var work = new BattleRunningWork { DeliveryId = Guid.NewGuid().ToString("D") };
            if (++polls == 1)
            {
                for (int i = 1; i <= 8; i++)
                    work.Commands.Add(new BattleQueuedCommand { Side = BattleFighterSide.Attacker, Sequence = i, DeliveryId = work.DeliveryId });
                work.Cuts.Add(new BattleControlCut { CutId = Guid.NewGuid().ToString("D") });
            }
            else if (polls == 2)
                work.Commands.Add(new BattleQueuedCommand { Side = BattleFighterSide.Attacker, Sequence = 9, DeliveryId = work.DeliveryId });
            else stop.Cancel();
            return Reply(work);
        }

        private static HttpResponseMessage Reply(IMessage message, bool lost = false)
        {
            byte[] bytes = message.ToByteArray(), frame = new byte[bytes.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), bytes.Length);
            bytes.CopyTo(frame, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new(2, 0), Content = new ByteArrayContent(lost ? [] : frame) };
            response.Content.Headers.ContentType = new("application/grpc");
            response.TrailingHeaders.Add("grpc-status", lost ? "14" : "0");
            return response;
        }
    }
}
