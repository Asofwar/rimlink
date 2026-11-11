using System.Globalization;
using System.Text.Json;

namespace Rimlink.Tools.Commands;

public static partial class AdminCommand
{
    private const string GuildCaptureUsage = "guild-capture inspect [limit] | guild-capture place preview FILE | guild-capture place|cancel|freeze|abort FILE | guild-capture profile stage KIND FILE | guild-capture profile approve|revoke FILE";

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
}
