namespace Rimlink.Server.Storage.Entities;

public sealed class PlayerProfile
{
    public long PlayerId { get; set; }
    public string Bio { get; set; } = "";
    public bool InvitationsOpen { get; set; } = true;
    public bool Notifications { get; set; } = true;
}
