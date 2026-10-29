using Rimlink.Client.Live;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Live;

/// <summary>
/// The host game's speed during a live session (live spec §2, live UX spec §4): the rules live in client-core's <see cref="TempoRules"/>
/// (the slower wish, pauses, the guest's budget, the letter pause); this class writes their result into the game's time speed.
/// </summary>
public static class LiveTempo
{
    public const int DefaultGuestPauses = 10;
    public static readonly TimeSpan DefaultGuestPauseLength = TimeSpan.FromSeconds(60);

    public static TempoRules Rules { get; } = new();

    /// <summary>The guest's pause budget of the current session (server config Live:GuestPauses / GuestPauseSeconds).</summary>
    public static int GuestPauses => Rules.GuestPauses;
    public static TimeSpan GuestPauseLength => Rules.GuestPauseLength;

    /// <summary>Host and guest take the budget from the session the server sent; an older server sends none.</summary>
    public static void UseBudget(LiveSession session)
    {
        Rules.GuestPauses = session.GuestPauses > 0 ? session.GuestPauses : DefaultGuestPauses;
        Rules.GuestPauseLength = session.GuestPauseSeconds > 0 ? TimeSpan.FromSeconds(session.GuestPauseSeconds) : DefaultGuestPauseLength;
    }

    public static bool Active { get; private set; }
    public static TimeSpeed HostWish { get => (TimeSpeed)Rules.HostWish; set => Rules.SetHostWish((int)value); }
    public static TimeSpeed GuestWish => (TimeSpeed)Rules.GuestWish;
    public static bool GuestPaused => Rules.GuestPaused;
    public static int GuestPausesUsed => Rules.GuestPausesUsed;
    /// <summary>The guest is gone: paused until they come back or the session ends; the host's play button does nothing.</summary>
    public static bool WaitingPause { get => Rules.WaitingPause; set => Rules.WaitingPause = value; }
    /// <summary>True while this class itself is writing the time speed (the setter patch lets it through).</summary>
    public static bool Applying { get; private set; }

    /// <summary>0 none, 1 host, 2 guest, 3 letter (as the delta reports it).</summary>
    public static int PausedBy => Rules.PausedBy;

    public static TimeSpeed Effective => (TimeSpeed)Rules.Effective;

    /// <summary>The host's speed before the session: back when the session ends before the guest ever arrived.</summary>
    public static TimeSpeed SpeedBeforeSession { get; private set; } = TimeSpeed.Normal;

    public static void Begin()
    {
        SpeedBeforeSession = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Normal;
        Active = true;
        Rules.HostThreshold = RimlinkMod.Settings.LetterPause;
        Rules.Begin(); // the waiting pause holds the game until the guest's Hello: the raiders stand idle and would be shot without their player
        Apply();
    }

    /// <summary>The host's pause button (TogglePaused writes the speed field directly, past the setter); it also lifts a letter pause.</summary>
    public static void ToggleHostPause()
    {
        Rules.ToggleHostPause();
        Apply();
    }

    /// <summary>The session is over. <paramref name="restoreSpeed"/>: the guest never came, so the host gets back the speed it had.</summary>
    public static void Reset(bool restoreSpeed = false)
    {
        Active = false;
        Rules.WaitingPause = false;
        Rules.SetGuestPause(false, DateTime.UtcNow);
        if (!restoreSpeed || Find.TickManager is not { } tm) return;
        Applying = true;
        try { tm.CurTimeSpeed = SpeedBeforeSession; }
        finally { Applying = false; }
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
        var refused = Rules.SetGuestPause(on, DateTime.UtcNow);
        Apply();
        return refused;
    }

    /// <summary>Speed 0 is a pause and comes out of the guest's budget; otherwise the wish is 1..3 and lifts a guest or letter pause.</summary>
    public static string? SetGuestSpeed(int speed)
    {
        var refused = Rules.SetGuestSpeed(speed, DateTime.UtcNow);
        Apply();
        return refused;
    }

    /// <summary>A threat letter in the host's game while the guest is here: pauses when either player's threshold asks for it.</summary>
    public static bool OnThreatLetter(bool major, string label)
    {
        if (!Active || !Rules.OnThreatLetter(major, label)) return false;
        Apply();
        return true;
    }

    /// <summary>A guest pause runs out after its minute.</summary>
    public static void Tick()
    {
        if (!Active || !Rules.GuestPaused) return;
        Rules.Tick(DateTime.UtcNow);
        if (!Rules.GuestPaused) Apply();
    }

    /// <summary>This player changed their own threshold (settings window, dev tool): saved, and sent along if a live session runs.</summary>
    public static void ChangeOwnThreshold(LetterPause mode)
    {
        RimlinkMod.Settings.LetterPause = mode;
        RimlinkMod.Settings.Write();
        if (RimlinkSession.Live is { } live) live.HelloLetterPause = mode;
        if (LiveHost.Active) Rules.HostThreshold = mode;
        else if (LiveGuest.Session is not null) LiveGuest.SendLetterPause(mode);
    }
}
