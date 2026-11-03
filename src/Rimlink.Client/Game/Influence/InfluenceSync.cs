using System.Diagnostics;
using Grpc.Core;
using Rimlink.Client.Influence;
using Rimlink.Client.Net;
using Rimlink.Client.UI;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Influence;

public static class InfluenceSync
{
    private static RimlinkConnection? connection;
    private static string session = "";
    private static bool fetching, trustedContext, unsupported;
    private static int generation;
    private static long retryAt;

    public static void Tick()
    {
        if (!RimlinkSession.Active || RimlinkSession.Connection is not { } current) { Reset(); return; }
        var cache = RimlinkSession.State.Influence;
        if (!ReferenceEquals(connection, current) || session != current.Login?.SessionToken)
        {
            Reset(); connection = current; session = current.Login?.SessionToken ?? "";
        }
        // World generation registers the first planet asynchronously; an empty pre-creation response is not an old server.
        if (RimlinkSession.World is not { Exists: true }) return;
        if (current.State != ConnectionState.Connected)
        {
            if (!cache.Stale) cache.Invalidate();
            trustedContext = false;
            InfluenceLayerController.Tick(); return;
        }
        InfluenceLayerController.Tick();
        if (fetching || unsupported || !InfluenceLayerController.Enabled || Stopwatch.GetTimestamp() < retryAt) return;
        var api = new InfluenceClient(current);
        int version = generation;
        if (!trustedContext || cache.RequiresWorldRefresh)
        {
            fetching = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await current.World.GetAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(15));
                    MainThread.Post(() =>
                    {
                        if (generation != version || !ReferenceEquals(connection, current)) return;
                        if (!info.Exists || info.WorldInstanceId.Length == 0 || info.ProcessInstanceId.Length == 0
                            || RimlinkSession.World?.WorldInstanceId != info.WorldInstanceId)
                        { unsupported = true; cache.MarkUnavailable(); return; }
                        cache.SetContext(info.WorldInstanceId, info.ProcessInstanceId); trustedContext = true;
                    });
                }
                catch (Exception ex) { MainThread.Post(() => { if (generation == version) Handle(ex, cache); }); }
                finally { MainThread.Post(() => Finish(version)); }
            });
            return;
        }
        if (!cache.Stale || cache.CandidateToken is not null) return;
        var token = cache.BeginFetch();
        fetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await api.Get(CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (generation != version || !ReferenceEquals(connection, current)) return;
                    if (!InfluenceValidation.IsValid(snapshot)) { cache.MarkUnavailable(); return; }
                    if (snapshot.WorldInstanceId != cache.WorldId || snapshot.ProcessInstanceId != cache.ProcessId)
                    {
                        cache.Invalidate(new InfluenceChanged { WorldInstanceId = snapshot.WorldInstanceId, ProcessInstanceId = snapshot.ProcessInstanceId });
                        return;
                    }
                    cache.TryAcceptCandidate(snapshot, token, out _);
                });
            }
            catch (Exception ex) { MainThread.Post(() => { if (generation == version) Handle(ex, cache); }); }
            finally { MainThread.Post(() => Finish(version)); }
        });
    }

    private static void Handle(Exception error, InfluenceCache cache)
    {
        if (error is RpcException { StatusCode: StatusCode.Unimplemented }) { unsupported = true; cache.MarkUnavailable(); }
        else if (error is RpcException rpc && Rimlink.Client.Economy.EconomyClient.CodeOf(rpc) == ErrorCode.InfluenceUnavailable) cache.MarkUnavailable();
        else { cache.Invalidate(); RimlinkLog.Instance.Warn("influence refresh: " + error.Message); }
    }

    private static void Finish(int expected)
    {
        if (generation != expected) return;
        fetching = false; retryAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2);
    }

    public static void Reset()
    {
        generation++; fetching = false; trustedContext = false; unsupported = false; retryAt = 0;
        connection = null; session = ""; InfluenceLayerController.Reset();
    }

    public static void ConfirmRegistered(long settlementId, InfluenceSurfaceProof proof, RimlinkConnection registeredOn)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var client = new InfluenceClient(registeredOn);
                var setting = await client.GetSetting(new InfluenceTarget { SettlementId = settlementId }, CancellationToken.None);
                if (setting.Context.WorldInstanceId != proof.WorldId || setting.Context.SaveGeneration != proof.Generation || setting.Tile != proof.Tile) return;
                if (RimlinkSession.Connection != registeredOn || RimlinkSession.Outbox is not { } outbox) return;
                await client.Submit(new InfluenceMutation { Surface = new InfluenceSurface { Context = setting.Context, ExpectedVersion = setting.Version, Tile = proof.Tile, RootSurface = proof.Root } }, outbox, CancellationToken.None);
            }
            catch (Exception ex) { RimlinkLog.Instance.Warn("influence surface not confirmed: " + ex.Message); }
        });
    }
}
