using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The host game's speed during a live session (spec §2): the slower of the two wishes, a pause by either side,
/// a waiting pause while the guest is gone that the host cannot lift, and the guest's pause budget.
/// </summary>
public static class LiveTempo
{
    public const int GuestPauses = 10;
    public static readonly TimeSpan GuestPauseLength = TimeSpan.FromSeconds(60);

    public static bool Active { get; private set; }
    public static TimeSpeed HostWish { get; set; } = TimeSpeed.Normal;
    public static TimeSpeed GuestWish { get; set; } = TimeSpeed.Normal;
    public static bool GuestPaused { get; private set; }
    public static int GuestPausesUsed { get; private set; }
    public static DateTime GuestPauseUntil { get; private set; }
    /// <summary>The guest is gone: paused until they come back or the session ends; the host's play button does nothing.</summary>
    public static bool WaitingPause { get; set; }
    /// <summary>True while this class itself is writing the time speed (the setter patch lets it through).</summary>
    public static bool Applying { get; private set; }

    /// <summary>0 none, 1 host, 2 guest (as the delta reports it).</summary>
    public static int PausedBy => WaitingPause || HostWish == TimeSpeed.Paused ? 1 : GuestPaused ? 2 : 0;

    public static TimeSpeed Effective =>
        WaitingPause || GuestPaused || HostWish == TimeSpeed.Paused || GuestWish == TimeSpeed.Paused ? TimeSpeed.Paused
        : (TimeSpeed)Math.Min((int)HostWish, (int)GuestWish);

    public static void Begin()
    {
        Active = true;
        HostWish = TimeSpeed.Paused;
        GuestWish = TimeSpeed.Normal;
        GuestPaused = false;
        GuestPausesUsed = 0;
        WaitingPause = true; // until the guest's Hello: the raiders stand idle and would be shot without their player
        Apply();
    }

    public static void Reset()
    {
        Active = false;
        WaitingPause = false;
        GuestPaused = false;
    }

    public static void Apply()
    {
        if (Find.TickManager is not { } tm) return;
        Applying = true;
        try { tm.CurTimeSpeed = Effective; }
        finally { Applying = false; }
    }

    /// <summary>Null when accepted; otherwise why the guest's pause was refused.</summary>
    public static string? SetGuestPause(bool on)
    {
        if (!on)
        {
            GuestPaused = false;
            Apply();
            return null;
        }
        if (GuestPaused) return null;
        if (GuestPausesUsed >= GuestPauses) return $"pause budget spent ({GuestPauses} pauses)";
        GuestPausesUsed++;
        GuestPaused = true;
        GuestPauseUntil = DateTime.UtcNow + GuestPauseLength;
        Apply();
        return null;
    }

    /// <summary>Speed 0 is a pause and comes out of the guest's budget; otherwise the wish is 1..3 and lifts a guest pause.</summary>
    public static string? SetGuestSpeed(int speed)
    {
        if (speed <= 0) return SetGuestPause(true);
        GuestWish = (TimeSpeed)Math.Clamp(speed, 1, 3);
        GuestPaused = false;
        Apply();
        return null;
    }

    /// <summary>A guest pause runs out after its minute.</summary>
    public static void Tick()
    {
        if (!Active || !GuestPaused || DateTime.UtcNow < GuestPauseUntil) return;
        GuestPaused = false;
        Apply();
    }
}
