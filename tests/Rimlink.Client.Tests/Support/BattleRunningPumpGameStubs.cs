using Google.Protobuf;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.UI
{
    internal static class MainThread
    {
        internal static Task<T> Get<T>(Func<T> work) => Task.FromResult(work());
    }
}

namespace Rimlink.Client.Game.GuildCapture
{
    internal sealed class BattleTickController
    {
        internal static BattleTickController? Current { get; set; }
        internal Exception? Failure { get; private set; }
        internal event Action<BattleQueuedCommand, BattleCommandReceipt>? Acknowledged;
        internal event Action<string>? CutApplied;
        internal ByteString CaptureView() => ByteString.Empty;
        internal void TransportFailed() { }
        internal void Fail(Exception error) => Failure = error;

        internal void ApplyRunningWork(BattleRunningWork work)
        {
            foreach (var command in work.Commands)
                Acknowledged?.Invoke(command, new() { Side = command.Side, Sequence = command.Sequence });
            foreach (var cut in work.Cuts)
                CutApplied?.Invoke(cut.CutId);
        }
    }
}
