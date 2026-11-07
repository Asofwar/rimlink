namespace Rimlink.BridgeTools;

public static class ColonyCoverage
{
    public static string? Refusal(float coverage, bool isAdmin, bool worldExists, bool devMode)
    {
        if (coverage == 0f) return null;
        if (coverage is not (0.3f or 0.5f or 1f) && !(coverage == 0.05f && devMode))
            return "planetCoverage must be 0, 0.3, 0.5 or 1; 0.05 requires dev mode";
        if (!isAdmin || worldExists)
            return "planetCoverage can only be selected by an admin before the first world is generated";
        return null;
    }
}
