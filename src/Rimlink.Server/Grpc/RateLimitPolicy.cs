namespace Rimlink.Server.Grpc;

/// <summary>Permits per fixed window, by full gRPC method name. Anonymous calls are keyed by IP, the rest by account.</summary>
public static class RateLimitPolicy
{
    public static readonly (int Permits, TimeSpan Window) Default = (30, TimeSpan.FromSeconds(10));

    private static readonly Dictionary<string, (int, TimeSpan)> Table = new()
    {
        ["/rimlink.v1.World/StageOutpostManifest"] = (128, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.World/ReadOutpostManifest"] = (128, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Outposts/Manifest"] = (128, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Outposts/Prepare"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Outposts/Commit"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Outposts/Cancel"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Outposts/Mutate"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/AttachControl"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/SubmitCommand"] = (40, TimeSpan.FromSeconds(1)),
        ["/rimlink.v1.GuildCapture/ControlHeartbeat"] = (4, TimeSpan.FromSeconds(1)),
        ["/rimlink.v1.GuildCapture/ReadRunningView"] = (8, TimeSpan.FromSeconds(1)),
        ["/rimlink.v1.GuildCapture/ReadCompletedView"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.BattleExecutor/RuntimeStarted"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.BattleExecutor/ReadPreparation"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.BattleExecutor/HelloBegin"] = (2, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.BattleExecutor/HelloComplete"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.BattleExecutor/PollRunning"] = (40, TimeSpan.FromSeconds(1)),
        ["/rimlink.v1.BattleExecutor/AcknowledgeCommands"] = (40, TimeSpan.FromSeconds(1)),
        ["/rimlink.v1.GuildCapture/ReadPreparedView"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/ReadPreparedEntry"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/ReadPreparedInitial"] = (128, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.GuildCapture/ReadReadyOffer"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/AcknowledgeReadyOffer"] = (4, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/ReconcileControl"] = (2, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/AttackerReady"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.GuildCapture/AssignDefenderCommander"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Outposts/BeginDelivery"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Chat/Send"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Chat/Ping"] = (12, TimeSpan.FromMinutes(1)),
        ["/rimlink.v1.Community/Mutate"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Community/Directory"] = (5, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Influence/Get"] = (5, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Influence/GetSetting"] = (5, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Influence/Mutate"] = (5, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Saves/Upload"] = (3, TimeSpan.FromSeconds(30)), // a transfer, a delivery and a leave can each save within half a minute
        ["/rimlink.v1.World/Settle"] = (1, TimeSpan.FromSeconds(10)),
        ["/rimlink.v1.Auth/Login"] = (10, TimeSpan.FromSeconds(60)),
        ["/rimlink.v1.Auth/Register"] = (5, TimeSpan.FromSeconds(60)),
        // Economy: one outgoing transfer per cooldown (Economy:SendCooldownSeconds is read by the interceptor for these two).
        ["/rimlink.v1.Transfer/Send"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Trades/Open"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Auctions/Post"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Auctions/Bid"] = (6, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Auctions/Cancel"] = (6, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Trades/SetSide"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Procurement/Create"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Procurement/Supply"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Market/Post"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Raids/Launch"] = (1, TimeSpan.FromSeconds(5)),
        ["/rimlink.v1.Shop/Buy"] = (1, TimeSpan.FromSeconds(5)),
    };

    public static (int Permits, TimeSpan Window) For(string method) => Table.TryGetValue(method, out var p) ? p : Default;
}
