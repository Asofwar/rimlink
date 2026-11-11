using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildAcceptanceSelectionTests
{
    [Theory]
    [InlineData("https://127.0.0.1:29991",true)][InlineData("https://[::1]:29991",true)]
    [InlineData("http://127.0.0.1:29991",false)][InlineData("https://example.com",false)][InlineData("https://192.168.1.2",false)]
    public void Explicit_test_selection_is_TLS_and_loopback_only(string address,bool valid)
    {
        var selection=new GuildAcceptanceSelection(Guid.NewGuid().ToString("D"),new('a',64));
        if(valid)selection.RequireEndpoint(new(address));else Assert.Throws<InvalidDataException>(()=>selection.RequireEndpoint(new(address)));
    }
    [Theory]
    [InlineData("valid")][InlineData("default-client")][InlineData("missing-reply")][InlineData("stand")][InlineData("mode")]
    public void Client_must_select_the_exact_server_mode_and_stand_before_accepting_a_session(string fault)
    {
        var selection=new GuildAcceptanceSelection(Guid.NewGuid().ToString("D"),new('a',64));
        GuildRuntimeTarget? reply=fault=="missing-reply"?null:selection.Target;
        if(fault=="stand")reply!.StandId=Guid.NewGuid().ToString("D");if(fault=="mode")reply!.Mode=GuildRuntimeMode.Unspecified;
        if(fault=="valid")GuildAcceptanceSelection.RequireReply(selection,reply);
        else Assert.Throws<InvalidDataException>(()=>GuildAcceptanceSelection.RequireReply(fault=="default-client"?null:selection,reply));
        GuildAcceptanceSelection.RequireReply(null,null);
    }
}
