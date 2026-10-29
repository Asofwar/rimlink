using Rimlink.Protocol.V1;

namespace Rimlink.Client.Live;

/// <summary>
/// The host game's tempo during a live session, without the game (live spec §2, live UX spec §4): speeds are 0 (paused) to 3. The game
/// runs at the slower of the two wishes; a pause by either side, the waiting pause while the guest is gone, or a letter pause stops it.
/// The guest's own pauses come out of a budget; a letter pause does not, and either side lifts it.
/// </summary>
public sealed class TempoRules
{
    public int GuestPauses { get; set; } = 10;
    public TimeSpan GuestPauseLength { get; set; } = TimeSpan.FromSeconds(60);

    public int HostWish { get; private set; } = 1;
    public int GuestWish { get; private set; } = 1;
    public bool GuestPaused { get; private set; }
    public int GuestPausesUsed { get; private set; }
    public DateTime GuestPauseUntil { get; private set; }
    /// <summary>The guest is gone (or not here yet): paused until they come back; the host's play button does nothing.</summary>
    public bool WaitingPause { get; set; }
    /// <summary>A threat letter paused the session (named <c>LetterPaused</c>: <see cref="LetterPause"/> is the threshold's type).</summary>
    public bool LetterPaused { get; private set; }
    public string? LetterLabel { get; private set; }
    public LetterPause HostThreshold { get; set; }
    public LetterPause GuestThreshold { get; set; }

    private int hostWishBeforePause = 1;

    public int Effective => WaitingPause || GuestPaused || LetterPaused || HostWish == 0 || GuestWish == 0 ? 0 : Math.Min(HostWish, GuestWish);

    /// <summary>0 none, 1 host (or waiting for the guest), 2 guest, 3 letter; as the delta reports it.</summary>
    public int PausedBy => WaitingPause ? 1 : LetterPaused ? 3 : HostWish == 0 ? 1 : GuestPaused ? 2 : 0;

    /// <summary>A new session: normal speed, budget unused, held by the waiting pause until the guest's Hello.</summary>
    public void Begin()
    {
        HostWish = 1;
        GuestWish = 1;
        GuestPaused = false;
        GuestPausesUsed = 0;
        LetterPaused = false;
        LetterLabel = null;
        WaitingPause = true;
    }

    /// <summary>The host's pause button: lifts a letter pause, otherwise toggles the host's own pause.</summary>
    public void ToggleHostPause()
    {
        if (LetterPaused) { LetterPaused = false; return; }
        if (HostWish == 0) HostWish = hostWishBeforePause == 0 ? 1 : hostWishBeforePause;
        else
        {
            hostWishBeforePause = HostWish;
            HostWish = 0;
        }
    }

    /// <summary>The host chose a speed (0 pauses); any speed lifts a letter pause.</summary>
    public void SetHostWish(int speed)
    {
        HostWish = Math.Clamp(speed, 0, 3);
        if (HostWish > 0) LetterPaused = false;
    }

    /// <summary>Null when accepted; otherwise why the guest's pause was refused. During a letter pause it changes nothing.</summary>
    public string? SetGuestPause(bool on, DateTime now)
    {
        if (!on)
        {
            GuestPaused = false;
            return null;
        }
        if (GuestPaused || LetterPaused) return null;
        if (GuestPausesUsed >= GuestPauses) return $"pause budget spent ({GuestPauses} pauses)";
        GuestPausesUsed++;
        GuestPaused = true;
        GuestPauseUntil = now + GuestPauseLength;
        return null;
    }

    /// <summary>Speed 0 is a pause from the budget; 1..3 is the guest's wish and lifts the guest's pause and a letter pause.</summary>
    public string? SetGuestSpeed(int speed, DateTime now)
    {
        if (speed <= 0) return SetGuestPause(true, now);
        GuestWish = Math.Clamp(speed, 1, 3);
        GuestPaused = false;
        LetterPaused = false;
        return null;
    }

    /// <summary>A threat letter in the host's game: pauses when the stricter threshold of the two asks for it. True when it paused.</summary>
    public bool OnThreatLetter(bool major, string label)
    {
        if (WaitingPause) return false;
        var strict = (LetterPause)Math.Max((int)HostThreshold, (int)GuestThreshold);
        if (strict == LetterPause.Never || (strict == LetterPause.Major && !major)) return false;
        LetterPaused = true;
        LetterLabel = label;
        return true;
    }

    /// <summary>A guest pause runs out after its length.</summary>
    public void Tick(DateTime now)
    {
        if (GuestPaused && now >= GuestPauseUntil) GuestPaused = false;
    }
}
