using RimBridgeServer.Sdk;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol.V1;

namespace Rimlink.BridgeTools;

public sealed class GuildPreparedViewTools
{
    [Tool("rimlink/guild_battle_control",Description="Request the shipping commander channel, with explicit attacker Ready when requested")]
    public Task<object> Control(IRimBridgeContext ctx,[ToolParameter(Required=true)]bool attackerReady) => Bridge.OnMain<object>(ctx,() =>
    {
        BattlePreparedViewer.BeginControl(attackerReady);return new{success=true,started=true};
    });

    [Tool("rimlink/guild_battle_view",Description="Read the last verified authoritative view and zone count from this native mirror")]
    public Task<object> View(IRimBridgeContext ctx) => Bridge.OnMain<object>(ctx,() =>
    {
        var view=BattlePreparedViewer.RunningView();
        return new{success=true,sequence=view?.Sequence,tick=view?.Tick,heldTicks=view?.HeldTicks,
            result=BattlePreparedViewer.CompletedResult(),
            fighters=view?.Pawns.Select(p=>new{p.Pawn.FighterId,p.Pawn.EntityId,p.Pawn.Side,p.Pawn.X,p.Pawn.Z,p.Pawn.Alive,p.Pawn.Downed,p.Drafted}).ToArray()};
    });

    [Tool("rimlink/guild_battle_order",Description="Send the same typed Move, Attack or Draft command as the native battle controls")]
    public async Task<object> Order(IRimBridgeContext ctx,[ToolParameter(Required=true)]string fighter,
        [ToolParameter(Required=true)]string order,int x=0,int z=0,string target="",bool drafted=true)
    {
        try
        {
            var command=new BattleCommandV1{Version=1,FighterId=fighter};
            switch(order)
            {
                case "Move":command.Move=new(){X=x,Z=z};break;
                case "Attack":command.AttackFighterId=target;break;
                case "Draft":command.Draft=drafted;break;
                default:return Bridge.Fail("order must be Move, Attack or Draft");
            }
            var reply=await (await Bridge.OnMain(ctx,()=>BattlePreparedViewer.Order(command)));
            return new{success=true,reply.Sequence,reply.Side,reply.Receipt};
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }

    [Tool("rimlink/guild_prepared_view",Description="Open the shipping pre-Start initial mirror from an own colony using its accepted HOME; no Ready, Start or simulation")]
    public async Task<object> Open(IRimBridgeContext ctx,
        [ToolParameter(Description="Explicit owned stand profile configuration; its credential is not read",Required=true)]string profileConfig,
        [ToolParameter(Description="Prepared battle UUID",Required=true)]string battleId,
        [ToolParameter(Description="Prepared attempt",Required=true)]long attempt,
        [ToolParameter(Description="Prepared fence",Required=true)]long fence)
    {
        try
        {
            var settings=BattleExecutorSettings.Read(profileConfig);var profile=BattleExecutorProfile.Load(settings);
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(300));
            await BattlePreparedViewer.Enter(new(){WorldInstanceId=settings.WorldInstanceId,RestoreBoundary=settings.RestoreBoundary,BattleId=battleId,Attempt=attempt,Fence=fence},profile,settings.Roots,stop.Token);
            return new{success=true,phase="PreparedView",simulation=false};
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
    [Tool("rimlink/guild_prepared_home",Description="Return a prepared shipping viewer to the exact accepted HOME and clear its durable marker only after verification")]
    public async Task<object> Home(IRimBridgeContext ctx)
    {
        try
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(150));await BattlePreparedViewer.ReturnHome(stop.Token);
            return new{success=true,phase="Home"};
        }
        catch(Exception error){return Bridge.Fail(error.Message);}
    }
}
