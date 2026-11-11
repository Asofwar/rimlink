using Rimlink.Client.Game;
using Rimlink.Client.Game.GuildCapture;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using RimWorld;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI.Outposts;

internal sealed class Dialog_GuildOutposts : Window
{
    private GuildOutpostList directory = new();
    private Vector2 scroll;
    private bool busy;
    private int claimCount = 1;
    private string claimBuffer = "1";
    private string status = "";

    public override Vector2 InitialSize => new(760, 560);

    public override void PreOpen()
    {
        base.PreOpen();
        doCloseX = true;
        absorbInputAroundWindow = true;
        Refresh();
    }

    public override void DoWindowContents(Rect rect)
    {
        var community = RimlinkSession.State.Community;
        var alliance = community.Stale ? null : community.Current.Alliance;
        bool leader = alliance is not null && alliance.OwnerId == RimlinkSession.MyPlayerId;
        Text.Font = GameFont.Medium;
        Widgets.Label(new(0, 0, rect.width - 324, 32), T("Title"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new(rect.width - 120, 0, 120, 30), "Rimlink.Outposts.Refresh".Translate(), active: !busy))
            Refresh();
        if (Widgets.ButtonText(new(rect.width - 316, 0, 188, 30), T("JoinBattle"), active: !busy))
            JoinBattle();
        Widgets.Label(new(0, 38, rect.width - 90, 28), T("ClaimCount"));
        Widgets.TextFieldNumeric(new(rect.width - 84, 38, 84, 28), ref claimCount, ref claimBuffer, 1, 1000);

        var area = new Rect(0, 74, rect.width, rect.height - 130);
        float width = area.width - 22;
        int rowCount = directory.Campaigns.Count + directory.Outposts.Count + directory.Obligations.Count;
        Widgets.BeginScrollView(area, ref scroll, new(0, 0, width, Math.Max(area.height, rowCount * 46)));
        float y = 0;
        foreach (var campaign in directory.Campaigns)
        {
            string scheduled = campaign.ScheduledAtUtcTicks > 0 && campaign.ScheduledAtUtcTicks <= DateTime.MaxValue.Ticks
                ? new DateTime(campaign.ScheduledAtUtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm") + " UTC"
                : T("ScheduleUnavailable");
            string commander = campaign.CommanderId == 0 ? T("NoCommander")
                : alliance?.Members.FirstOrDefault(member => member.Player.PlayerId == campaign.CommanderId)?.Player.Name
                    ?? campaign.CommanderId.ToString();
            Widgets.Label(new(0, y, width - 160, 42),
                T(campaign.Defending ? "DefenceCampaign" : "AttackCampaign", campaign.Tile, scheduled, commander));
            if (leader && alliance!.Id == campaign.AllianceId && campaign.Defending && campaign.CanAssignDefender
                && Widgets.ButtonText(new(width - 154, y, 154, 32), T("AssignDefender"), active: !busy))
                ChooseCommander(alliance, player => ConfirmDefender(campaign, player));
            y += 46;
        }
        foreach (var row in directory.Outposts)
        {
            string resource = Resource(row.ResourceDef);
            Widgets.Label(new(0, y, width - 160, 42),
                row.StockVisible ? T("OwnStock", row.Tile, resource, row.Stock) : T("Outpost", row.Tile, resource));
            var button = new Rect(width - 154, y, 154, 32);
            if (leader && !busy)
            {
                if (row.AllianceId == 0 && Widgets.ButtonText(button, T("Acquire"))) ChooseWindow(row, alliance!.Id);
                if (row.StockVisible && Widgets.ButtonText(button, T("Claim"), active: claimCount > 0 && claimCount <= row.Stock && row.Phase == "Active"))
                    ConfirmClaim("Outpost", row.OutpostId, row.OwnerRevision, alliance!.Id, resource);
                if (row.AllianceId != 0 && !row.StockVisible && Widgets.ButtonText(button, T("Attack")))
                    ChooseCommander(alliance!, player => ConfirmAttack(row, alliance!.Id, player));
            }
            y += 46;
        }
        foreach (var row in directory.Obligations)
        {
            string resource = Resource(row.ResourceDef);
            Widgets.Label(new(0, y, width - 160, 42), T("OldStock", resource, row.AvailableCount));
            if (leader && Widgets.ButtonText(new(width - 154, y, 154, 32), T("Claim"),
                active: !busy && claimCount > 0 && claimCount <= row.AvailableCount))
                ConfirmClaim("Obligation", row.ObligationId, row.OwnerRevision, row.AllianceId, resource);
            y += 46;
        }
        if (rowCount == 0 && !busy) Widgets.Label(new(0, 0, width, 32), T("Empty"));
        Widgets.EndScrollView();
        Widgets.Label(new(0, rect.height - 48, rect.width, 48), busy ? T("Loading") : status);
    }

    private void Refresh() => Run(async (api, home, token) =>
    {
        var result = await api.ListOutposts(Scope(home), token);
        await MainThread.Get(() => { directory = result; return true; });
    });

    private void JoinBattle() => Run(async (api, home, token) =>
    {
        var entry = await api.PreparedEntry(Scope(home), token);
        if (entry is not { } battle)
        {
            await MainThread.Get(() => { status = T("NoPreparedBattle"); return true; });
            return;
        }
        var roots = await MainThread.Get(() =>
        {
            if (!IsOpen) throw new OperationCanceledException();
            BattleGameContext.RequireIdleViewerHome(home);
            return BattleRuntimeCapture.ViewerRoots(battle.Profile);
        });
        await BattlePreparedViewer.Enter(battle.Scope, battle.Profile, roots, token);
    });

    private void ChooseWindow(GuildOutpostSummary row, long allianceId) =>
        Find.WindowStack.Add(new FloatMenu(Enumerable.Range(0, 24).Select(hour =>
            new FloatMenuOption(T("DefenceHour", hour.ToString("00")), () => Preview(row, allianceId, hour))).ToList()));

    private void Preview(GuildOutpostSummary row, long allianceId, int hour) => Run(async (api, home, token) =>
    {
        var request = new GuildConsentRequest { Scope = Scope(home), AllianceId = allianceId, OutpostId = row.OutpostId, DefenceHourUtc = hour };
        var terms = await api.PreviewConsent(request, token);
        request.ExpectedTermsDigest = terms.TermsDigest;
        await MainThread.Get(() =>
        {
            if (IsOpen && !token.IsCancellationRequested)
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("Terms", row.Tile, Resource(terms.ResourceDef),
                    terms.HourlyYield, terms.Capacity, terms.ProtectionHours, terms.MaximumOutposts, terms.DefenceHourUtc.ToString("00")),
                    () => Run(async (client, current, ct) =>
                    {
                        if (!current.Equals(home)) throw new InvalidOperationException();
                        await client.SubmitConsent(request, RimlinkSession.Outbox!, ct);
                        await MainThread.Get(() => { status = T("Acquired"); return true; });
                    }, refresh: true)));
            return true;
        });
    });

    private static void ChooseCommander(AllianceInfo alliance, Action<PlayerRef> select) =>
        Find.WindowStack.Add(new FloatMenu(alliance.Members.Select(member =>
            new FloatMenuOption(member.Player.Name, () => select(member.Player))).ToList()));

    private void ConfirmDefender(GuildCampaignSummary campaign, PlayerRef commander)
    {
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("DefenderConfirm", campaign.Tile, commander.Name),
            () => Run(async (api, home, token) =>
            {
                await api.SubmitDefenderCommander(new() { Scope = Scope(home), CampaignId = campaign.CampaignId, CommanderId = commander.PlayerId },
                    RimlinkSession.Outbox!, token);
                await MainThread.Get(() => { status = T("DefenderAssigned", commander.Name); return true; });
            }, refresh: true)));
    }

    private void ConfirmAttack(GuildOutpostSummary row, long allianceId, PlayerRef commander)
    {
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("AttackConfirm", row.Tile, commander.Name),
            () => Run(async (api, home, token) =>
            {
                var campaign = await api.SubmitCampaign(new() { Scope = Scope(home), AllianceId = allianceId, OutpostId = row.OutpostId,
                    OwnerRevision = row.OwnerRevision, CommanderId = commander.PlayerId }, RimlinkSession.Outbox!, token);
                var scheduled = new DateTime(campaign.ScheduledAtUtcTicks, DateTimeKind.Utc);
                await MainThread.Get(() => { status = T("Scheduled", scheduled.ToString("yyyy-MM-dd HH:mm")); return true; });
            }, refresh: true)));
    }

    private void ConfirmClaim(string origin, string id, long revision, long allianceId, string resource)
    {
        int count = claimCount;
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("ClaimConfirm", count, resource), () => Run(async (api, home, token) =>
        {
            await api.SubmitClaim(new() { Scope = Scope(home), AllianceId = allianceId, OriginKind = origin, OriginId = id, OwnerRevision = revision,
                RecipientId = home.PlayerId, RecipientGeneration = home.SaveGeneration, RecipientEpoch = home.SaveWriteEpoch, Count = count },
                RimlinkSession.Outbox!, token);
            await MainThread.Get(() => { status = T("ClaimQueued"); return true; });
        }, refresh: true)));
    }

    private async void Run(Func<GuildCaptureClient, GuildColonyScope, CancellationToken, Task> action, bool refresh = false)
    {
        if (busy) return;
        busy = true;
        status = "";
        try
        {
            var login = RimlinkSession.Login ?? throw new InvalidOperationException();
            var home = new GuildColonyScope { WorldInstanceId = RimlinkSession.World?.WorldInstanceId ?? "", RestoreBoundary = login.OutpostRestoreBoundary,
                PlayerId = login.Player.PlayerId, SaveGeneration = login.SaveGeneration, SaveWriteEpoch = login.SaveWriteEpoch };
            BattleGameContext.RequireIdleViewerHome(home);
            var api = new GuildCaptureClient(RimlinkSession.Connection!);
            var token = RimlinkSession.RecoveryToken;
            await action(api, home, token);
            if (refresh)
            {
                var result = await api.ListOutposts(Scope(home), token);
                await MainThread.Get(() => { directory = result; return true; });
            }
        }
        catch (global::Grpc.Core.RpcException error)
        {
            await MainThread.Get(() => { status = RefusalText.Of(RpcErrorDetail.Read(error)); return true; });
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await MainThread.Get(() => { status = T("Unavailable"); return true; });
        }
        finally { await MainThread.Get(() => { busy = false; return true; }); }
    }

    private static GuildWorldScope Scope(GuildColonyScope home) => new() { WorldInstanceId = home.WorldInstanceId, RestoreBoundary = home.RestoreBoundary };
    private static string Resource(string def) => (def == "WoodLog" ? "Rimlink.Outposts.Wood" : "Rimlink.Outposts.Steel").Translate();
    private static string T(string key, params NamedArgument[] args) => ("Rimlink.Guild.Outposts." + key).Translate(args);
}
