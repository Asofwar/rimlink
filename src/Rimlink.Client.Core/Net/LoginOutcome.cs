namespace Rimlink.Client.Net;

public enum LoginOutcomeKind
{
    /// <summary>A save (local or the server's) is being loaded.</summary>
    Loading,
    /// <summary>The new-colony pages are open.</summary>
    NewColony,
    /// <summary>The mods do not match the server's modpack; the diff dialog is open.</summary>
    ModpackDiff,
    Failed,
    Cancelled,
}

/// <summary>Where a login flow ended; <see cref="Message"/> carries the server's or the client's error text.</summary>
public sealed record LoginOutcome(LoginOutcomeKind Kind, string? Message = null);
