using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.GuildCapture;

public enum BattleMapRole { None, Executor, Viewer }

// Main-thread lifecycle state. Enter before changing Game; an exception or Entry screen never clears a viewer's home fence.
public sealed class BattleMapGuard
{
    private BattleScopeMessage? scope;
    private GuildSnapshotProof? home;
    public BattleMapRole Role { get; private set; }
    public bool Active => Role != BattleMapRole.None;
    public bool MayUseOwnGame => !Active;
    public BattleScopeMessage? Scope => scope?.Clone();
    public GuildSnapshotProof? Home => home?.Clone();
    public int HomeVersion { get; private set; }

    public void BeginViewer(BattleScopeMessage battle,GuildSnapshotProof canonicalHome,int version)
    {
        RequireEmpty(); ValidateScope(battle); ValidateHome(canonicalHome,version);
        if (canonicalHome.Scope.WorldInstanceId != battle.WorldInstanceId || canonicalHome.Scope.RestoreBoundary != battle.RestoreBoundary) throw Invalid();
        scope = battle.Clone(); home = canonicalHome.Clone(); HomeVersion = version; Role = BattleMapRole.Viewer;
    }
    public void BeginExecutor(BattleScopeMessage battle)
    {
        RequireEmpty(); ValidateScope(battle); scope = battle.Clone(); Role = BattleMapRole.Executor;
    }
    public void RestoreViewerHome(GuildSnapshotProof canonicalHome,int version)
    {
        if (Role != BattleMapRole.Viewer) throw Invalid();
        ValidateHome(canonicalHome,version);
        if (version != HomeVersion || !canonicalHome.Equals(home)) throw Invalid();
        Clear();
    }
    public void CompleteExecutor(BattleCleanupReceipt receipt)
    {
        BattleCleanupContracts.ValidateReceipt(receipt);
        if(Role!=BattleMapRole.Executor || !Equals(receipt.Scope,scope))throw Invalid();
        Clear();
    }
    private static void ValidateHome(GuildSnapshotProof proof,int version)
    {
        if (proof is null || version <= 0 || proof.RawSha256.Length != 32 || string.IsNullOrEmpty(proof.SnapshotId)
            || !(Guid.TryParseExact(proof.SnapshotId,"N",out var id) || Guid.TryParseExact(proof.SnapshotId,"D",out id)) || id == Guid.Empty) throw Invalid();
        GuildResourceContracts.ValidateScope(proof.Scope);
    }
    private static void ValidateScope(BattleScopeMessage value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.WorldInstanceId) || value.WorldInstanceId.Length > 256 || value.WorldInstanceId.Any(char.IsControl)
            || !Uuid(value.RestoreBoundary) || !Uuid(value.BattleId) || value.Attempt <= 0 || value.Fence <= 0) throw Invalid();
    }
    private void RequireEmpty() { if (Active) throw new InvalidOperationException("another battle context must be reconciled first"); }
    private void Clear() { scope = null; home = null; HomeVersion = 0; Role = BattleMapRole.None; }
    private static bool Uuid(string value) => Guid.TryParseExact(value,"D",out var id) && id != Guid.Empty;
    private static InvalidDataException Invalid() => new("battle-map-home-evidence-invalid");
}
