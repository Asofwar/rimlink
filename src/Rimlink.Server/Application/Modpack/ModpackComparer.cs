using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Modpack;

public static class ModpackComparer
{
    public static ModpackDiff? Compare(IReadOnlyList<ModpackEntry> server, IReadOnlyList<ModRef> client, IReadOnlyList<ModConfig>? configs = null, IReadOnlyList<ModSettingsHash>? settings = null) => ModpackComparison.Compare(server, client, configs, settings);
    public static string Normalize(string packageId) => ModpackComparison.Normalize(packageId);
}
