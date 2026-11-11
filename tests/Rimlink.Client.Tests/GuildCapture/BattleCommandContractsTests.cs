using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleCommandContractsTests
{
    [Theory]
    [InlineData("Move")][InlineData("Attack")][InlineData("Draft")]
    public void Only_three_explicit_orders_roundtrip_for_the_authenticated_side(string kind)
    {
        var manifest=BattleManifestContractsTests.Manifest();var order=new BattleCommandV1{Version=1,FighterId="fighter0"};
        if(kind=="Move")order.Move=new(){X=20,Z=20};else if(kind=="Attack")order.AttackFighterId="fighter4";else order.Draft=false;
        Assert.Equal(order,BattleCommandContracts.Read(order.ToByteArray(),manifest,BattleFighterSide.Attacker));
    }
    [Theory]
    [InlineData("side")][InlineData("unknown-fighter")][InlineData("friendly-target")][InlineData("unknown-target")]
    [InlineData("outside")][InlineData("negative")][InlineData("no-order")][InlineData("version")][InlineData("unknown-wire")][InlineData("duplicate-wire")][InlineData("oversize")]
    public void Invalid_or_foreign_commands_never_turn_into_a_generic_game_job(string damage)
    {
        var manifest=BattleManifestContractsTests.Manifest();var order=new BattleCommandV1{Version=1,FighterId="fighter0",Move=new(){X=20,Z=20}};
        var side=BattleFighterSide.Attacker;
        switch(damage){case "side":side=BattleFighterSide.Defender;break;case "unknown-fighter":order.FighterId="other";break;
            case "friendly-target":order.AttackFighterId="fighter1";break;case "unknown-target":order.AttackFighterId="other";break;
            case "outside":order.Move.X=manifest.Arena.Width;break;case "negative":order.Move.Z=-1;break;
            case "no-order":order.ClearOrder();break;case "version":order.Version=2;break;}
        byte[] bytes=order.ToByteArray();
        bytes=damage switch{"unknown-wire"=>[..bytes,0xA0,0x06,1],"duplicate-wire"=>[8,1,..bytes],"oversize"=>new byte[16385],_=>bytes};
        Assert.Throws<InvalidDataException>(()=>BattleCommandContracts.Read(bytes,manifest,side));
    }
}
