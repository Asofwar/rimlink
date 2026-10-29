using Rimlink.Discord.Commands;
using Rimlink.Discord.Options;
using Xunit;

namespace Rimlink.Discord.Tests.Commands;

public class PermissionsTests
{
    private static readonly Permissions Roles = new(new DiscordSection { ModeratorRoleIds = [100], AdminRoleIds = [200, 201] });

    [Fact]
    public void AdminRoleImpliesModerator()
    {
        Assert.Equal(Level.Admin, Roles.LevelOf([201]));
        Assert.Equal(Level.Admin, Roles.LevelOf([100, 200]));
    }

    [Fact]
    public void ModeratorRole() => Assert.Equal(Level.Moderator, Roles.LevelOf([5, 100]));

    [Fact]
    public void NoRoleIsEveryone() => Assert.Equal(Level.Everyone, Roles.LevelOf([5, 6]));

    [Fact]
    public void ActorLabelFitsTheHeaderLimit()
    {
        var caller = new Caller(1234567890123456789, new string('n', 32), Level.Moderator);

        Assert.True(caller.Actor.Length <= Caller.LabelMax);
        Assert.StartsWith("discord:nnn", caller.Actor);
        Assert.EndsWith(" (1234567890123456789)", caller.Actor);
        Assert.Equal("discord:Mod (7)", new Caller(7, "Mod", Level.Moderator).Actor);
    }
}
