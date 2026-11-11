using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleFinalCaptureTests
{
    private static BattleDocumentDescriptor Doc(BattleDocumentKind kind)=>new(){Kind=kind,SizeBytes=10,Sha256=ByteString.CopyFrom(SHA256.HashData("fixture"u8))};
    [Theory]
    [InlineData(BattleOutcome.AttackerWin,BattleEndReason.ZoneHeld,3000,3000,1000)]
    [InlineData(BattleOutcome.DefenderWin,BattleEndReason.TickLimit,30000,0,1000)]
    [InlineData(BattleOutcome.TechnicalAbort,BattleEndReason.WallClockLimit,0,0,1200000)]
    [InlineData(BattleOutcome.TechnicalAbort,BattleEndReason.TransportLost,0,0,60000)]
    public void Final_view_and_envelope_bind_one_exact_native_terminal_sample(BattleOutcome outcome,BattleEndReason reason,int ticks,int held,int wall)
    {
        var start=BattleManifestContractsTests.Manifest();
        var pawns=start.Entities.Select(e=>new BattlePawn(e.FighterId,(BattleSide)(int)e.Side,held>0 && e.Side==BattleFighterSide.Attacker?25:40,25,true,false,false,true,1)).ToArray();
        var capture=BattleFinalCapture.Create(start,new(start.InitialTick+ticks,held,outcome,reason),pawns,TimeSpan.FromMilliseconds(wall),
            reason==BattleEndReason.TransportLost?TimeSpan.FromSeconds(60):TimeSpan.Zero,Doc(BattleDocumentKind.FinalSnapshot),Doc(BattleDocumentKind.SimulationJournal),7,Guid.NewGuid().ToString("D"));
        Assert.Equal(capture.Final,BattleManifestContracts.ReadFinal(capture.Envelope.Manifest.Span,start));
        Assert.Equal(capture.View,BattleManifestContracts.ReadTerminalView(capture.View.ToByteArray(),capture.Final));
        Assert.Equal(7,capture.Final.TerminalViewSequence);Assert.Equal(start.Entities.Select(e=>e.EntityId),capture.Final.Pawns.Select(p=>p.EntityId));
    }
    [Fact]
    public void A_partial_sample_or_late_gameplay_win_cannot_create_a_resendable_envelope()
    {
        var start=BattleManifestContractsTests.Manifest();var pawns=start.Entities.Select(e=>new BattlePawn(e.FighterId,(BattleSide)(int)e.Side,e.Side==BattleFighterSide.Attacker?25:40,25,true,false,false,true,1)).ToArray();
        foreach(var sample in new[]{pawns[..7],pawns})
            Assert.Throws<InvalidDataException>(()=>BattleFinalCapture.Create(start,new(start.InitialTick+3000,3000,BattleOutcome.AttackerWin,BattleEndReason.ZoneHeld),sample,
                TimeSpan.FromMinutes(20),TimeSpan.Zero,Doc(BattleDocumentKind.FinalSnapshot),Doc(BattleDocumentKind.SimulationJournal),1,Guid.NewGuid().ToString("D")));
    }
}
