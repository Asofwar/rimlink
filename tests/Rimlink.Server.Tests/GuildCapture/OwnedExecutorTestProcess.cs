using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.GuildCapture;
using Xunit;

namespace Rimlink.Server.Tests.GuildCapture;

internal sealed class OwnedExecutorTestProcess : IAsyncDisposable
{
    private readonly Process child;
    internal static string Executable => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    internal OwnedExecutorTestProcess()
    {
        const string script = """
            while ($null -ne ($line = [Console]::ReadLine())) {
                $fields = $line.Split(' ')
                $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('Rimlink.Executor.' + $fields[0]), 'InOut', 'Asynchronous', 'Identification')
                try {
                    $pipe.Connect(10000)
                    $bytes = [Convert]::FromBase64String($fields[1])
                    $pipe.Write($bytes, 0, $bytes.Length)
                    [Console]::WriteLine($pipe.ReadByte())
                } finally { $pipe.Dispose() }
            }
            """;
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        child = Process.Start(start) ?? throw new InvalidOperationException();
    }

    internal static void Select(string data, string? battle = null, string? boundary = null) =>
        File.WriteAllText(Path.Combine(data, "guild-executor-local.json"),
            JsonSerializer.Serialize(new { Executable, ReplacementBattleId = battle, ReplacementBoundary = boundary }));

    internal async Task<GuildOwnedProcess> ObserveAsync(GuildOwnedProcessWitness witness)
    {
        var challenge = new BattleExecutorHelloChallenge { ChallengeId = Guid.NewGuid().ToString("D"),
            Nonce = Google.Protobuf.ByteString.CopyFrom(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
        var observation = witness.ObserveHello(challenge.ChallengeId, challenge.Nonce.ToByteArray());
        await AnswerAsync(challenge);
        var identity = await observation;
        Assert.NotNull(identity);
        Assert.Equal(child.Id, identity.ProcessId);
        Assert.Equal(child.StartTime.ToUniversalTime().Ticks, identity.StartedUtcTicks);
        return identity;
    }

    internal async Task AnswerAsync(BattleExecutorHelloChallenge challenge)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.StandardInput.WriteLineAsync(challenge.ChallengeId + " " + challenge.Nonce.ToBase64());
        Assert.Equal("1", await child.StandardOutput.ReadLineAsync(timeout.Token));
    }

    internal async Task CloseAsync()
    {
        if (child.HasExited) return;
        child.StandardInput.Close();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(); }
        finally { child.Dispose(); }
    }
}
