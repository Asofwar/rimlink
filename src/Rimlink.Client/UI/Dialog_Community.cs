using RimWorld;
using Rimlink.Client.Economy;
using Rimlink.Client.Game;
using Rimlink.Client.Net;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Consent, membership, treasury and public profiles from the Players window.</summary>
public sealed class Dialog_Community : Window
{
    private int tab;
    private Vector2 scroll;
    private PublicProfiles directory = new();
    private PublicProfile? selected;
    private string groupName = "", message = "", amount = "100", cap = "100", bio = "", playerName = "";
    private bool invitationsOpen = true, notifications = true, busy, editingProfile;
    private readonly long initialPlayer;
    private bool loaded;

    public Dialog_Community(long playerId = 0)
    {
        initialPlayer = playerId;
        doCloseX = true; draggable = true; forcePause = false;
        absorbInputAroundWindow = false; preventCameraMotion = false; closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(920f, 710f);
    private static string T(string key) => ("Rimlink.Community." + key).Translate();
    private static CommunityState State => RimlinkSession.State.Community.Current;
    private bool Pending => RimlinkSession.Outbox?.Entries.Any(e => e.Kind == OutboxKind.Community && e.Refusal is null) == true;

    public override void DoWindowContents(Rect rect)
    {
        CommunitySync.Tick();
        if (!loaded) { loaded = true; LoadDirectory(); }
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0, 0, rect.width - 160, 34), T("Title"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(rect.width - 130, 0, 116, 28), T("Refresh"))) { RimlinkSession.State.Community.Invalidate(); LoadDirectory(); }
        string[] tabs = ["People", "Alliance", "Invitations", "Profile"];
        for (int i = 0; i < tabs.Length; i++)
            if (Widgets.ButtonText(new Rect(i * 185, 42, 178, 30), T(tabs[i]))) { tab = i; scroll = Vector2.zero; }
        bool stale = RimlinkSession.State.Community.Stale;
        if (busy || Pending || stale) Widgets.Label(new Rect(0, 78, rect.width, 28), T(Pending ? "Pending" : "Loading"));
        var area = new Rect(0, 110, rect.width, rect.height - 110);
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && !busy && !Pending && !stale;
        switch (tab)
        {
            case 0: People(area); break;
            case 1: Alliance(area); break;
            case 2: Invitations(area); break;
            case 3: Profile(area); break;
        }
        GUI.enabled = wasEnabled;
    }

    private void People(Rect area)
    {
        var known = directory.Players.ToDictionary(p => p.PlayerId);
        foreach (var friend in State.Friends)
            known.TryAdd(friend.PlayerId, new PublicProfile { PlayerId = friend.PlayerId, Name = friend.Name });
        float width = area.width - 16;
        Widgets.BeginScrollView(area, ref scroll, new Rect(0, 0, width, Math.Max(area.height, known.Count * 64 + 32)));
        float y = 0;
        foreach (var player in known.Values.OrderBy(p => p.Name))
        {
            bool self = player.PlayerId == RimlinkSession.MyPlayerId;
            bool friend = State.Friends.Any(p => p.PlayerId == player.PlayerId);
            string label = player.Name + (friend ? "  " + T("Friend") : "") + (RimlinkSession.State.Community.IsAlly(player.PlayerId) ? "  " + T("Ally") : "");
            Widgets.Label(new Rect(0, y, width - 400, 28), label);
            if (Button(width - 390, y, 100, "Profile")) ShowProfile(player.PlayerId);
            if (!self && Button(width - 284, y, 130, friend ? "Unfriend" : "AddFriend"))
                Send(new CommunityRequest { Action = friend ? CommunityAction.FriendRemove : CommunityAction.FriendInvite, PlayerId = player.PlayerId });
            if (!self && State.Alliance?.OwnerId == RimlinkSession.MyPlayerId && Button(width - 148, y, 148, "InviteAlly"))
                Send(new CommunityRequest { Action = CommunityAction.AllianceInvite, AllianceId = State.Alliance.Id, PlayerId = player.PlayerId });
            Widgets.Label(new Rect(0, y + 29, width, 28), player.Bio.Replace('\n', ' '));
            y += 64;
        }
        Widgets.EndScrollView();
    }

    private void Alliance(Rect area)
    {
        if (State.Alliance is not { } alliance)
        {
            Widgets.Label(new Rect(area.x, area.y, area.width, 30), T("NoAlliance"));
            groupName = Widgets.TextField(new Rect(0, area.y + 38, 400, 30), groupName, 32);
            if (Button(410, area.y + 38, 200, "Create")) Send(new CommunityRequest { Action = CommunityAction.AllianceCreate, Text = groupName });
            return;
        }
        float width = area.width - 16;
        var chatLines = State.Chat.Select(line => (Text: $"{line.From.Name}: {line.Text}", Height: Math.Max(28, Text.CalcHeight($"{line.From.Name}: {line.Text}", width) + 6))).ToList();
        float height = 330 + alliance.Members.Count * 40 + chatLines.Sum(line => line.Height) + State.Audit.Count * 28;
        Widgets.BeginScrollView(area, ref scroll, new Rect(0, 0, width, Math.Max(area.height, height)));
        float y = 0;
        Widgets.Label(new Rect(0, y, width - 300, 30), "Rimlink.Community.GroupSummary".Translate(alliance.Name, alliance.Silver));
        bool owner = alliance.OwnerId == RimlinkSession.MyPlayerId;
        if (Button(width - 280, y, 270, owner ? "Disband" : "Leave")) Confirm(owner ? "DisbandConfirm" : "LeaveConfirm", new CommunityRequest { Action = owner ? CommunityAction.AllianceDisband : CommunityAction.AllianceLeave, AllianceId = alliance.Id });
        y += 38;
        Widgets.Label(new Rect(0, y, 90, 28), T("Silver"));
        amount = Widgets.TextField(new Rect(94, y, 150, 28), amount, 12);
        if (Button(254, y, 150, "Deposit")) Silver(CommunityAction.TreasuryDeposit, alliance.Id);
        if (Button(414, y, 150, "Withdraw")) Silver(CommunityAction.TreasuryWithdraw, alliance.Id);
        y += 36;
        Widgets.Label(new Rect(0, y, 145, 28), T("DailyCap"));
        cap = Widgets.TextField(new Rect(150, y, 160, 28), cap, 12);
        y += 38;
        foreach (var member in alliance.Members)
        {
            string name = member.Player.Name + (member.Player.PlayerId == alliance.OwnerId ? "  " + T("Owner") : "");
            Widgets.Label(new Rect(0, y, width - 425, 32), name);
            Widgets.Label(new Rect(width - 420, y, 120, 32), member.MayWithdraw ? $"{member.WithdrawnToday} / {member.DailyCap}" : T("NoWithdraw"));
            if (owner)
            {
                if (Button(width - 300, y, 70, "SetCap"))
                {
                    if (long.TryParse(cap, out long limit) && limit is >= 0 and <= 1_000_000_000)
                        Send(new CommunityRequest { Action = CommunityAction.TreasuryPermission, AllianceId = alliance.Id, PlayerId = member.Player.PlayerId, MayWithdraw = member.MayWithdraw, DailyCap = limit });
                    else InvalidNumber();
                }
                if (Button(width - 225, y, 110, member.MayWithdraw ? "Revoke" : "Grant"))
                {
                    if (long.TryParse(cap, out long limit) && limit is >= 0 and <= 1_000_000_000)
                        Send(new CommunityRequest { Action = CommunityAction.TreasuryPermission, AllianceId = alliance.Id, PlayerId = member.Player.PlayerId, MayWithdraw = !member.MayWithdraw, DailyCap = limit });
                    else InvalidNumber();
                }
                if (member.Player.PlayerId != alliance.OwnerId && Button(width - 110, y, 110, "Transfer"))
                    Confirm("TransferConfirm", new CommunityRequest { Action = CommunityAction.AllianceTransfer, AllianceId = alliance.Id, PlayerId = member.Player.PlayerId });
            }
            y += 40;
        }
        Widgets.Label(new Rect(0, y, width, 28), T("Chat")); y += 30;
        message = Widgets.TextField(new Rect(0, y, width - 135, 30), message, 500);
        if (Button(width - 125, y, 125, "Send")) Send(new CommunityRequest { Action = CommunityAction.AllianceChat, AllianceId = alliance.Id, Text = message }, () => message = "");
        y += 38;
        foreach (var line in chatLines)
        {
            Widgets.Label(new Rect(0, y, width, line.Height), line.Text); y += line.Height;
        }
        Widgets.Label(new Rect(0, y, width, 28), T("Audit")); y += 30;
        foreach (var line in State.Audit)
        {
            string actor = alliance.Members.FirstOrDefault(m => m.Player.PlayerId == line.ActorId)?.Player.Name ?? line.ActorId.ToString();
            string subject = alliance.Members.FirstOrDefault(m => m.Player.PlayerId == line.SubjectId)?.Player.Name ?? line.SubjectId.ToString();
            Widgets.Label(new Rect(0, y, width, 26), $"{line.At.ToDateTime():u}  {actor} → {subject}  {T("Audit_" + line.Kind)}  {line.Delta:+0;-0;0}  [{line.Balance}]  {line.DailyCap}"); y += 28;
        }
        Widgets.EndScrollView();
    }

    private void Invitations(Rect area)
    {
        float width = area.width - 16;
        Widgets.BeginScrollView(area, ref scroll, new Rect(0, 0, width, Math.Max(area.height, State.Invitations.Count * 68)));
        float y = 0;
        foreach (var invite in State.Invitations)
        {
            Widgets.Label(new Rect(0, y, width - 260, 58), $"{invite.From.Name} → {invite.To.Name}: {(invite.AllianceId == 0 ? T("Friend") : invite.AllianceName)}");
            bool mine = invite.To.PlayerId == RimlinkSession.MyPlayerId;
            if (mine && Button(width - 250, y, 120, "Accept")) Send(new CommunityRequest { Action = CommunityAction.InviteAccept, InviteId = invite.Id });
            if (Button(width - 125, y, 125, mine ? "Refuse" : "Revoke")) Send(new CommunityRequest { Action = mine ? CommunityAction.InviteRefuse : CommunityAction.InviteRevoke, InviteId = invite.Id });
            y += 68;
        }
        Widgets.EndScrollView();
    }

    private void Profile(Rect area)
    {
        selected ??= State.Profile;
        if (selected is null) return;
        bool self = selected.PlayerId == RimlinkSession.MyPlayerId;
        Widgets.Label(new Rect(0, area.y, area.width, 28), selected.Name);
        Widgets.Label(new Rect(0, area.y + 32, area.width, 28), "Rimlink.Community.LastSeen".Translate(selected.LastSeen?.ToDateTime().ToString("u") ?? T("Unknown")));
        if (self)
        {
            if (!editingProfile) { bio = selected.Bio; playerName = selected.Name; invitationsOpen = selected.InvitationsOpen; notifications = selected.Notifications; editingProfile = true; }
            bio = Widgets.TextArea(new Rect(0, area.y + 70, area.width, 200), bio);
            if (bio.Length > 500) bio = bio[..500];
            Widgets.CheckboxLabeled(new Rect(0, area.y + 282, 420, 30), T("InvitationsOpen"), ref invitationsOpen);
            Widgets.CheckboxLabeled(new Rect(0, area.y + 320, 420, 30), T("Notifications"), ref notifications);
            if (Button(0, area.y + 365, 220, "Save")) Send(new CommunityRequest { Action = CommunityAction.ProfileUpdate, Text = bio, InvitationsOpen = invitationsOpen, Notifications = notifications }, () => { selected = null; LoadDirectory(); });
            playerName = Widgets.TextField(new Rect(0, area.y + 410, 320, 30), playerName, 32);
            if (Button(330, area.y + 410, 190, "Rename")) Send(new CommunityRequest { Action = CommunityAction.ProfileRename, Text = playerName }, () => { selected = null; editingProfile = false; LoadDirectory(); });
        }
        else
        {
            var biography = new Rect(0, area.y + 70, area.width, 240);
            float height = Math.Max(biography.height, Text.CalcHeight(selected.Bio, biography.width - 16));
            Widgets.BeginScrollView(biography, ref scroll, new Rect(0, 0, biography.width - 16, height));
            Widgets.Label(new Rect(0, 0, biography.width - 16, height), selected.Bio);
            Widgets.EndScrollView();
            Widgets.Label(new Rect(0, area.y + 350, area.width, 30), T(selected.InvitationsOpen ? "InvitationsOpen" : "InvitationsClosed"));
            bool friend = State.Friends.Any(p => p.PlayerId == selected.PlayerId);
            if (Button(0, area.y + 312, 200, friend ? "Unfriend" : "AddFriend"))
                Send(new CommunityRequest { Action = friend ? CommunityAction.FriendRemove : CommunityAction.FriendInvite, PlayerId = selected.PlayerId });
            if (State.Alliance?.OwnerId == RimlinkSession.MyPlayerId && Button(210, area.y + 312, 200, "InviteAlly"))
                Send(new CommunityRequest { Action = CommunityAction.AllianceInvite, AllianceId = State.Alliance.Id, PlayerId = selected.PlayerId });
        }
        if (Button(area.width - 220, area.y + 365, 220, "MyProfile")) { selected = State.Profile; editingProfile = false; }
    }

    private static bool Button(float x, float y, float width, string key) => Widgets.ButtonText(new Rect(x, y, width, 30), T(key));
    private static void InvalidNumber() => Messages.Message(T("InvalidNumber"), MessageTypeDefOf.RejectInput, historical: false);
    private void Silver(CommunityAction action, long groupId)
    {
        if (long.TryParse(amount, out long silver) && silver is >= 1 and <= 1_000_000_000) Send(new CommunityRequest { Action = action, AllianceId = groupId, Silver = silver });
        else InvalidNumber();
    }
    private void Confirm(string text, CommunityRequest request) => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T(text), () => Send(request), destructive: true));

    private void Send(CommunityRequest request, Action? success = null)
    {
        if (busy || Pending || RimlinkSession.Community is not { } client || RimlinkSession.Outbox is not { } outbox || RimlinkSession.Connection is not { } connection) return;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.Submit(request, outbox, CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (!ReferenceEquals(connection, RimlinkSession.Connection)) return;
                    RimlinkSession.State.Community.Invalidate(); success?.Invoke();
                });
            }
            catch (Exception ex)
            {
                var refusal = RpcErrorDetail.Read(ex);
                MainThread.Post(() => { if (ReferenceEquals(connection, RimlinkSession.Connection)) Messages.Message(RefusalText.Of(refusal), MessageTypeDefOf.RejectInput, historical: false); });
            }
            finally { MainThread.Post(() => busy = false); }
        });
    }

    private void LoadDirectory()
    {
        if (RimlinkSession.Community is not { } client || RimlinkSession.Connection is not { } connection) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await client.Directory(CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (!ReferenceEquals(connection, RimlinkSession.Connection)) return;
                    directory = list;
                    if (initialPlayer != 0 && selected is null) ShowProfile(initialPlayer);
                });
            }
            catch (Exception ex) { RimlinkLog.Instance.Warn("community directory: " + ex.Message); }
        });
    }

    private void ShowProfile(long playerId)
    {
        if (RimlinkSession.Community is not { } client || RimlinkSession.Connection is not { } connection) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var profile = await client.Profile(playerId, CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (!ReferenceEquals(connection, RimlinkSession.Connection)) return;
                    selected = profile; editingProfile = false; tab = 3; scroll = Vector2.zero;
                });
            }
            catch (Exception ex) { RimlinkLog.Instance.Warn("community profile: " + ex.Message); }
        });
    }
}
