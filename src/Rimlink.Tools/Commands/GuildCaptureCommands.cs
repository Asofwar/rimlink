using System.Globalization;
using System.Text.Json;

namespace Rimlink.Tools.Commands;

public static partial class AdminCommand
{
    private const string GuildCaptureUsage = "guild-capture inspect [limit] | guild-capture place preview FILE | guild-capture place|cancel|freeze|abort FILE | guild-capture profile stage KIND FILE | guild-capture profile approve|revoke FILE | guild-capture profile publish DIRECTORY EXECUTOR_ID REASON";

    private static async Task<string> RunGuildCapture(RestClient rest, string[] args)
    {
        const string root = "guild-capture";
        if (args is ["inspect"]) return await rest.Get(root);
        if (args is ["inspect", var limit] && int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            && count is >= 1 and <= 100)
            return await rest.Get(root + "?limit=" + count.ToString(CultureInfo.InvariantCulture));
        if (args is ["profile", "stage", var kind, var document] && int.TryParse(kind, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value is >= 1 and <= 7)
            return await rest.PostDocument(root + "/profile/stage/" + value.ToString(CultureInfo.InvariantCulture), document);
        if (args is ["profile", "publish", var directory, var executorId, var reason]) return await PublishProfile(rest, root, directory, executorId, reason);
        var (route, file) = args switch
        {
            ["place", "preview", var path] => ("/place/preview", path),
            ["place", var path] => ("/place", path),
            ["cancel", var path] => ("/cancel", path),
            ["freeze", var path] => ("/freeze", path),
            ["abort", var path] => ("/abort", path),
            ["profile", "approve" or "revoke", var path] => ("/profile/" + args[1], path),
            _ => throw new InvalidOperationException(GuildCaptureUsage),
        };
        var bytes = await ReadOutpostFile(file, OutpostRequestLimit);
        using var request = JsonDocument.Parse(bytes, new() { MaxDepth = 32, AllowDuplicateProperties = false });
        if (request.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Guild capture request must be a JSON object.");
        return await rest.Post(root + route, request.RootElement);
    }

    // Stages every document of a profile written by the arbiter's capture run and approves it for one executor.
    // The capture's own summary is the qualification record the approval refers to.
    private static async Task<string> PublishProfile(RestClient rest, string root, string directory, string executorId, string reason)
    {
        string summary = Path.Combine(Path.GetFullPath(directory), "profile-capture.json");
        using var capture = JsonDocument.Parse(await ReadOutpostFile(summary, OutpostRequestLimit));
        var documents = capture.RootElement.GetProperty("Documents");
        JsonElement profile = default;
        foreach (var (name, kind) in new[] { ("Inventory", 1), ("Config", 3), ("Defs", 4), ("Template", 5), ("ViewerCompatibility", 6), ("Profile", 2) })
        {
            using var staged = JsonDocument.Parse(await rest.PostDocument(root + "/profile/stage/" + kind, documents.GetProperty(name).GetString()!));
            if (kind == 2) profile = staged.RootElement.Clone();
        }
        using var qualification = JsonDocument.Parse(await rest.PostDocument(root + "/profile/stage/7", summary));
        using var status = JsonDocument.Parse(await rest.Get(root));
        var request = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["scope"] = status.RootElement.GetProperty("scope"), ["requestId"] = Guid.NewGuid().ToString("D"), ["profile"] = profile,
            ["qualification"] = qualification.RootElement, ["executorId"] = executorId, ["reason"] = reason,
        });
        return await rest.Post(root + "/profile/approve", request);
    }
}
