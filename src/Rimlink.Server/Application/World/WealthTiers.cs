namespace Rimlink.Server.Application.World;

/// <summary>The coarse wealth others see on the map: 0 unknown, 1–5 by <c>Hygiene:WealthTiers</c> (four bounds).</summary>
public static class WealthTiers
{
    public static int Of(long wealth, bool known, long[] bounds)
    {
        if (!known || wealth <= 0) return 0;
        int tier = 1;
        foreach (long bound in bounds)
            if (wealth >= bound) tier++;
        return tier;
    }
}
