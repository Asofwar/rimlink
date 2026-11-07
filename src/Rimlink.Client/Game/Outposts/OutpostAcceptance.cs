using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using Rimlink.Client.UI;
using Verse;

namespace Rimlink.Client.Game.Outposts;

internal sealed record OutpostAcceptanceContext(Verse.Game Game, RimlinkConnection Connection, LoginReply Login,
    OutpostScopeMessage Scope, string OperationId, string Kind, string ObjectId, CancellationToken Lifetime, string IntentSha256 = "");
internal interface IOutpostAcceptanceObserver
{
    Task ReachAsync(string boundary, OutpostAcceptanceContext context, string? snapshotId, CancellationToken ct);
    Task VerifyDeliveryAsync(OutpostAcceptanceContext context, CancellationToken ct);
    bool DefinitionAvailable(OutpostAcceptanceContext context, Parcel parcel, string definition, bool actual);
    IDisposable? OpenPlacement(OutpostAcceptanceContext context, Parcel parcel, Map map, IReadOnlyList<Thing> things,
        IReadOnlyList<(Pawn Pawn, PawnRegistryEnvelope Envelope)> workers);
    void DeliveryFailed(OutpostAcceptanceContext context, Parcel parcel, bool spawnStarted, Exception error);
    void Invalidate();
}

// No controller is shipped or installed by the product; only the friend dev companion can attach it.
internal static class OutpostAcceptance
{
    internal static IOutpostAcceptanceObserver? Observer { get; set; }
    internal static void Reset() { var old = Observer; Observer = null; old?.Invalidate(); }
    internal static OutpostAcceptanceContext? Capture(OutpostScopeMessage scope, string operation, string kind, string objectId)
        => Observer is null ? null : new(Current.Game, RimlinkSession.Connection!, RimlinkSession.Connection!.Login!, scope.Clone(),
            operation, kind, objectId, RimlinkSession.RecoveryToken);
    internal static void RequireCurrent(OutpostAcceptanceContext context)
    {
        context.Lifetime.ThrowIfCancellationRequested();
        if (Current.ProgramState != ProgramState.Playing || Current.Game != context.Game || !PawnCustodyRegistry.IsOwnGame
            || RimlinkSession.Connection != context.Connection || !ReferenceEquals(context.Connection.Login, context.Login)
            || context.Connection.State != ConnectionState.Connected || !context.Login.OutpostIsolatedAcceptance)
            throw new OperationCanceledException("isolated acceptance operation no longer owns this game/session");
    }
    internal static async Task ReachAsync(string boundary, OutpostAcceptanceContext? context, string? snapshotId = null)
    {
        if (context is null || Observer is not { } observer) return;
        await observer.ReachAsync(boundary, context, snapshotId, context.Lifetime);
        await MainThread.Get(() => { RequireCurrent(context); return true; });
    }
    internal static Task VerifyDeliveryAsync(OutpostAcceptanceContext? context)
        => context is null || Observer is not { } observer ? Task.CompletedTask : observer.VerifyDeliveryAsync(context, context.Lifetime);
    internal static bool DefinitionAvailable(OutpostAcceptanceContext? context, Parcel parcel, string definition, bool actual)
        => actual && (context is null || Observer is not { } observer || observer.DefinitionAvailable(context, parcel, definition, actual));
    internal static IDisposable? OpenPlacement(OutpostAcceptanceContext? context, Parcel parcel, Map map, IReadOnlyList<Thing> things,
        IReadOnlyList<(Pawn Pawn, PawnRegistryEnvelope Envelope)> workers)
        => context is null || Observer is not { } observer ? null : observer.OpenPlacement(context, parcel, map, things, workers);
    internal static void DeliveryFailed(OutpostAcceptanceContext? context, Parcel parcel, bool spawnStarted, Exception error)
    { if (context is not null && Observer is { } observer) observer.DeliveryFailed(context, parcel, spawnStarted, error); }
}
