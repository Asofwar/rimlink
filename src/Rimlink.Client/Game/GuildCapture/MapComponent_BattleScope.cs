using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.GuildCapture;

// Battle-local identity, never a player owner or pawn-custody scope. Empty home components write no marker.
public sealed class MapComponent_BattleScope(Map map) : MapComponent(map)
{
    private string encoded = "";
    public bool Present => encoded.Length != 0;
    public BattleScopeMessage? Identity
    {
        get
        {
            if (!Present) return null;
            if (encoded.Length > 4096) throw new InvalidDataException("battle map marker is oversized");
            byte[] bytes = Convert.FromBase64String(encoded);
            var value = BattleScopeMessage.Parser.WithDiscardUnknownFields(true).ParseFrom(bytes);
            if (!value.ToByteArray().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("battle map marker is noncanonical");
            var guard = new BattleMapGuard(); guard.BeginExecutor(value); return value;
        }
    }
    public void Bind(BattleScopeMessage scope)
    {
        if (!BattleGameContext.Active || !Equals(BattleGameContext.Scope,scope)) throw new InvalidDataException("battle map context differs");
        encoded = Convert.ToBase64String(scope.ToByteArray());
    }
    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving && encoded.Length == 0) return;
        Scribe_Values.Look(ref encoded,SaveRecoveryData.BattleScopeNode,""); encoded ??= "";
    }
}
