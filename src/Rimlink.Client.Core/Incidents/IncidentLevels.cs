namespace Rimlink.Client.Incidents;

/// <summary>The share of the target's threat points a hostile paid incident fires with (paid incidents spec §1).</summary>
public static class IncidentLevels
{
    public static double Factor(int level) => level switch
    {
        1 => 0.5,
        2 => 1.0,
        3 => 1.5,
        4 => 2.0,
        _ => 1.0,
    };
}
