using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public sealed record BattleRuntimeRootPath(BattleInventoryRoot Kind,string Id,string Path);

// Only an explicit process argument opens this operator file. No bearer is stored in settings or status.
public sealed record BattleExecutorSettings
{
    public int Version { get; init; }
    public string ExecutorId { get; init; } = "";
    public string AcceptanceStandId { get; init; } = "";
    public string WorldInstanceId { get; init; } = "";
    public string RestoreBoundary { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string CertificateSha256 { get; init; } = "";
    public string ProfilePath { get; init; } = "";
    public string ProfileSha256 { get; init; } = "";
    public string ProfileDigest { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string CredentialPath { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    public BattleRuntimeRootPath[] Roots { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,MaxDepth=8 };
    private const string Argument = "-rimlinkExecutorConfig=";
    public string RestoreCleanupPath() => Path.Combine(StateDirectory, "restore-cleanup.json");

    public (BattleScopeMessage Original, string TargetBoundary, string? ReplacementAssignmentId, string? ReplacementStartDigest) ReadRestoreCleanup()
    {
        byte[] bytes = ReadFile(RestoreCleanupPath(), 2048);
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            var selection = JsonSerializer.Deserialize<RestoreCleanupSelection>(bytes, Json) ?? throw Invalid();
            if (selection.Version != 1 || !Uuid(selection.BattleId) || !Uuid(selection.OriginalBoundary) || !Uuid(selection.TargetBoundary)
                || selection.TargetBoundary == RestoreBoundary || selection.TargetBoundary == selection.OriginalBoundary
                || selection.Attempt <= 0 || selection.Fence is <= 0 or long.MaxValue
                || (selection.ReplacementAssignmentId is null) != (selection.ReplacementStartDigest is null)
                || selection.ReplacementAssignmentId is not null && (!Uuid(selection.ReplacementAssignmentId)
                    || selection.ReplacementStartDigest != "" && !Hash(selection.ReplacementStartDigest))) throw Invalid();
            return (new() { WorldInstanceId = WorldInstanceId, RestoreBoundary = selection.OriginalBoundary, BattleId = selection.BattleId,
                Attempt = selection.Attempt, Fence = selection.Fence }, selection.TargetBoundary, selection.ReplacementAssignmentId, selection.ReplacementStartDigest);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private sealed record RestoreCleanupSelection(int Version, string BattleId, long Attempt, long Fence, string OriginalBoundary, string TargetBoundary,
        string? ReplacementAssignmentId = null, string? ReplacementStartDigest = null);
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;

    public static string? ConfigurationArgument(IReadOnlyList<string> arguments)
    {
        var selected = arguments.Where(a => a.StartsWith(Argument,StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0) return null;
        if (selected.Length != 1 || !LocalPath(selected[0][Argument.Length..])) throw Invalid();
        return selected[0][Argument.Length..];
    }
    public static BattleExecutorSettings Read(string path)
    {
        byte[] bytes = ReadFile(path,16384);
        try
        {
            using var document = JsonDocument.Parse(bytes,new() { MaxDepth=8 }); RejectDuplicates(document.RootElement);
            var value = JsonSerializer.Deserialize<BattleExecutorSettings>(bytes,Json) ?? throw Invalid();
            if (value.Version != 1 || !Identifier(value.ExecutorId) || !Text(value.WorldInstanceId,256)
                || !Guid.TryParseExact(value.RestoreBoundary,"D",out var boundary) || boundary==Guid.Empty || boundary.ToString("D")!=value.RestoreBoundary
                || !Uri.TryCreate(value.Endpoint,UriKind.Absolute,out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0
                || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Host.Length == 0
                || !Hash(value.CertificateSha256) || !Hash(value.ProfileSha256) || !Hash(value.ProfileDigest)
                || !LocalPath(value.ProfilePath) || !LocalPath(value.ArchiveRoot) || !LocalPath(value.CredentialPath) || !LocalPath(value.StateDirectory)
                || value.Roots is null || value.Roots.Length is < 1 or > 258) throw Invalid();
            if(value.AcceptanceStandId!="")new GuildAcceptanceSelection(value.AcceptanceStandId,value.CertificateSha256).RequireEndpoint(endpoint);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in value.Roots)
                if (root is null || root.Kind is not (BattleInventoryRoot.Game or BattleInventoryRoot.Runtime or BattleInventoryRoot.Mod)
                    || !Identifier(root.Id) || !LocalPath(root.Path) || !roots.Add(((int)root.Kind)+"/"+root.Id)) throw Invalid();
            return value;
        }
        catch (JsonException) { throw Invalid(); }
    }
    public string ReadCredential()
    {
        byte[] bytes = ReadFile(CredentialPath,256);
        if (bytes.Length < 32 || bytes.Any(b => b < 33 || b > 126)) throw Invalid();
        return Encoding.ASCII.GetString(bytes);
    }
    internal static byte[] ReadFile(string path,int maximum)
    {
        if (!LocalPath(path)) throw Invalid(); RequireNoLinks(path);
        using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) throw Invalid();
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw Invalid(); return bytes;
    }
    internal static void RequireNoLinks(string path)
    {
        for (string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw Invalid();
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool LocalPath(string value) => !string.IsNullOrWhiteSpace(value) && value.Length<=4096 && Path.IsPathFullyQualified(value)
        && !value.StartsWith(@"\\",StringComparison.Ordinal) && !value.StartsWith("//",StringComparison.Ordinal) && !value.Any(char.IsControl);
    private static bool Text(string value,int max) => !string.IsNullOrWhiteSpace(value) && value.Length<=max && !value.Any(char.IsControl);
    private static bool Identifier(string value) => Text(value,128) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Hash(string? value) => value is {Length:64} && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidDataException Invalid() => new("battle-executor-configuration-invalid");
}
