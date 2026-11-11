using System.Security.Cryptography;
using System.Text;
using Rimlink.Protocol;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattlePersonalSaveTests
{
    [Theory]
    [InlineData("<savegame><game><maps><li><components><li Class='Rimlink.Client.Game.GuildCapture.MapComponent_BattleScope'><rimlinkBattleScope>scope</rimlinkBattleScope></li></components></li></maps></game></savegame>")]
    [InlineData("<savegame><game><components><li Class='Rimlink.Client.Game.RimlinkGameComponent'><rimlinkBattleScope>scope</rimlinkBattleScope></li></components></game></savegame>")]
    public void Battle_snapshot_bytes_are_never_accepted_as_personal_recovery_data(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.True(SaveRecoveryData.HasBattleScope(stream));
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => SaveRecoveryData.ReadXml(stream,out _));
    }
    [Fact]
    public void An_empty_component_on_a_normal_home_keeps_the_existing_raw_hash_and_legacy_metadata()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<savegame><game><maps><li><components><li Class='Rimlink.Client.Game.GuildCapture.MapComponent_BattleScope'/></components></li></maps></game></savegame>");
        using var stream = new MemoryStream(bytes);
        Assert.False(SaveRecoveryData.HasBattleScope(stream));
        stream.Position = 0;
        var recovery = SaveRecoveryData.ReadXml(stream,out var hash);
        Assert.Equal(SHA256.HashData(bytes),hash); Assert.Equal(0u,recovery.Version); Assert.False(recovery.Outposts.Present);
    }
}
