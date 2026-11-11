using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildOwnedProcess(string Host, int ProcessId, long StartedUtcTicks)
{
    [JsonIgnore]
    internal bool Valid => Host is { Length: 64 } && Host.All(char.IsAsciiHexDigit)
        && ProcessId > 0 && StartedUtcTicks > 0 && StartedUtcTicks <= DateTime.MaxValue.Ticks;
}

internal sealed class GuildOwnedProcessWitness(DataDirectory data)
{
    private sealed record Selection(string Executable, string? ReplacementBattleId = null, string? ReplacementBoundary = null);
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, MaxDepth = 4 };

    internal Task<GuildOwnedProcess?> ObserveHello(string challenge, byte[] nonce) =>
        ReadSelection() is { } selection ? Observe(challenge, nonce, selection.Executable) : Task.FromResult<GuildOwnedProcess?>(null);

    internal bool Configured => File.Exists(Path.Combine(data.Root, "guild-executor-local.json"));

    internal bool ReplacementSelected(string battleId, string boundary) =>
        ReadSelection() is { } selection && selection.ReplacementBattleId == battleId && selection.ReplacementBoundary == boundary;

    internal bool Exited(GuildOwnedProcess original)
    {
        if (!original.Valid || original.Host != LocalHost()) return false;
        Process process;
        try { process = Process.GetProcessById(original.ProcessId); }
        catch (ArgumentException) { return true; } // The local PID no longer exists.
        using (process)
        {
            try { return process.HasExited || process.StartTime.ToUniversalTime().Ticks != original.StartedUtcTicks; }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
        }
    }

    internal bool Alive(GuildOwnedProcess identity)
    {
        if (!identity.Valid || identity.Host != LocalHost()) return false;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private Selection? ReadSelection()
    {
        string path = Path.Combine(data.Root, "guild-executor-local.json");
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows() || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 2 or > 4096) throw Invalid();
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        Selection? result;
        try { result = JsonSerializer.Deserialize<Selection>(bytes, Json); }
        catch (JsonException) { throw Invalid(); }
        if (result is null || !Path.IsPathFullyQualified(result.Executable) || !File.Exists(result.Executable)
            || (result.ReplacementBattleId is null) != (result.ReplacementBoundary is null)
            || result.ReplacementBattleId is not null && (!Guid.TryParseExact(result.ReplacementBattleId, "D", out _)
                || !Guid.TryParseExact(result.ReplacementBoundary, "D", out _))) throw Invalid();
        return result;
    }

    private static async Task<GuildOwnedProcess?> Observe(string challenge, byte[] nonce, string executable)
    {
        try
        {
            using var expires = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeServerStream("Rimlink.Executor." + challenge, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(expires.Token);
            byte[] response = new byte[32];
            await pipe.ReadExactlyAsync(response, expires.Token);
            if (!CryptographicOperations.FixedTimeEquals(nonce, response)
                || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue) return null;
            using var process = Process.GetProcessById((int)pid);
            if (!Path.GetFullPath(executable).Equals(process.MainModule?.FileName, StringComparison.OrdinalIgnoreCase)) return null;
            var witnessed = new GuildOwnedProcess(LocalHost(), process.Id, process.StartTime.ToUniversalTime().Ticks);
            await pipe.WriteAsync(new byte[] { 1 }, expires.Token);
            return witnessed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static string LocalHost()
    {
        if (!OperatingSystem.IsWindows()) throw Invalid();
        string? id = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        if (!Guid.TryParse(id, out var host)) throw Invalid();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(host.ToString("D"))));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    private static InvalidDataException Invalid() => new("guild-owned-process-witness-unavailable");
}
