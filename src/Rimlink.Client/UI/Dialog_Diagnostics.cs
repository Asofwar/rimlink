using RimWorld;
using Rimlink.Client.Diagnostics;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Live;
using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>The preview and ZIP are the same frozen snapshot; only Save writes anything, and nothing is sent.</summary>
public sealed class Dialog_Diagnostics : Window
{
    private readonly DiagnosticSnapshot snapshot;
    private readonly DiagnosticBundle bundle;
    private Vector2 scroll;

    public Dialog_Diagnostics()
    {
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        var connection = RimlinkSession.Connection;
        var live = MirrorMode.Session ?? LiveHost.Session;
        var role = MirrorMode.Active ? DiagnosticLiveRole.Guest :
            live is not null && live.Host?.PlayerId == RimlinkSession.MyPlayerId ? DiagnosticLiveRole.Host :
            Game.Visits.VisitSession.Active ? DiagnosticLiveRole.Visitor : DiagnosticLiveRole.None;
        snapshot = new(connection?.State ?? ConnectionState.Disconnected, connection?.UploadStatus.Snapshot,
            role, MirrorMode.Active ? MirrorMode.SnapshotVersion : live?.SnapshotVersion ?? 0,
            RimlinkSession.Outbox?.Entries.Count ?? 0, PendingSave(), live?.State,
            connection?.ReconnectingSince is { } since ? (int)Math.Clamp((DateTime.UtcNow - since).TotalSeconds, 0, 31_536_000) : 0,
            role == DiagnosticLiveRole.Host ? (int)LiveTempo.Effective : MirrorMode.Active ? (int)(Find.TickManager?.CurTimeSpeed ?? 0) : 0,
            role == DiagnosticLiveRole.Host ? LiveTempo.PausedBy : MirrorMode.Active ? MirrorMode.PausedBy : 0);
        bundle = DiagnosticBundle.Create(snapshot,
            LoadedModManager.RunningMods.Select(m => new DiagnosticMod(ModpackActions.Normalize(m.PackageId), m.ModMetaData?.ModVersion ?? "")),
            RimlinkLog.Diagnostics);
    }

    private static bool PendingSave()
    {
        try { return RimlinkSession.Active && SaveSync.Pending.IsSet; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public override Vector2 InitialSize => new(760f, 680f);

    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, rect.width, 36f), "Rimlink.Diagnostics.Title".Translate());
        Text.Font = GameFont.Small;
        var upload = snapshot.Upload ?? new UploadSnapshot(null, 0, UploadFailure.None);
        string time = upload.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "Rimlink.Diagnostics.NoUpload".Translate().ToString();
        string summary = "Rimlink.Diagnostics.Summary".Translate(ClientInfo.Version, ClientInfo.ProtocolVersion,
            ("Rimlink.Diagnostics.Connection." + snapshot.Connection).Translate(), time, upload.Version,
            ("Rimlink.Diagnostics.Failure." + upload.Failure).Translate(), ("Rimlink.Diagnostics.Role." + snapshot.LiveRole).Translate());
        float summaryHeight = Text.CalcHeight(summary, rect.width);
        Widgets.Label(new Rect(0f, 42f, rect.width, summaryHeight), summary);
        string notice = "Rimlink.Diagnostics.Privacy".Translate();
        float noticeY = 50f + summaryHeight;
        float noticeHeight = Text.CalcHeight(notice, rect.width);
        Widgets.Label(new Rect(0f, noticeY, rect.width, noticeHeight), notice);
        float top = noticeY + noticeHeight + 10f;
        var list = new Rect(0f, top, rect.width, Math.Max(40f, rect.height - top - 100f));
        Text.Font = GameFont.Tiny;
        float height = Text.CalcHeight(bundle.Preview, list.width - 24f) + 8f;
        Widgets.BeginScrollView(list, ref scroll, new Rect(0f, 0f, list.width - 20f, height));
        Widgets.Label(new Rect(0f, 0f, list.width - 24f, height), bundle.Preview);
        Widgets.EndScrollView();
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, rect.height - 94f, rect.width, 48f), "Rimlink.Diagnostics.Destination".Translate(ExportDirectory));
        if (Widgets.ButtonText(new Rect(0f, rect.height - 38f, 240f, 32f), "Rimlink.Diagnostics.Save".Translate())) Save();
        if (Widgets.ButtonText(new Rect(rect.width - 140f, rect.height - 38f, 140f, 32f), "Close".Translate())) Close();
    }

    private static string ExportDirectory => Path.Combine(RimlinkSession.DataDir, "Diagnostics");

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            string path = Path.Combine(ExportDirectory, $"rimlink-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bundle.ZipBytes);
            Find.WindowStack.Add(new Dialog_MessageBox("Rimlink.Diagnostics.Saved".Translate(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Messages.Message("Rimlink.Diagnostics.SaveFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
