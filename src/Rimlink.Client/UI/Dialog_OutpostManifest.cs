using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Game;
using Rimlink.Client.Game.Outposts;
using Rimlink.Client.Outposts;
using Rimlink.Protocol.V1;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Administrator review is a separate explicit step; capturing/staging never silently confirms geography.</summary>
public sealed class Dialog_OutpostManifest : Window
{
    private ManifestPreview? staged;
    private bool busy;
    private string status = "Rimlink.Outposts.ManifestHelp";
    private string? details;
    private Vector2 scroll;
    public override Vector2 InitialSize => new(760, 590);
    public override void PreOpen() { base.PreOpen(); forcePause = true; doCloseX = true; absorbInputAroundWindow = true; }
    public override void DoWindowContents(Rect rect)
    {
        Text.Font = GameFont.Medium; Widgets.Label(new(0, 0, rect.width, 40), "Rimlink.Outposts.ManifestTitle".Translate()); Text.Font = GameFont.Small;
        Widgets.Label(new(0, 44, rect.width, 100), status.Translate());
        if (details is not null)
        {
            var area = new Rect(0, 148, rect.width, rect.height - 238); float height = Math.Max(area.height, Text.CalcHeight(details, area.width - 20));
            Widgets.BeginScrollView(area, ref scroll, new(0, 0, area.width - 20, height)); Widgets.Label(new(0, 0, area.width - 20, height), details); Widgets.EndScrollView();
        }
        if (Widgets.ButtonText(new(0, rect.height - 80, rect.width, 34), "Rimlink.Outposts.ManifestStage".Translate(), !busy)) _ = Stage();
        if (Widgets.ButtonText(new(0, rect.height - 40, rect.width, 34), "Rimlink.Outposts.ManifestConfirm".Translate(), !busy && staged is not null)) _ = Confirm();
    }
    public ManifestPreview? Staged => staged?.Clone();
    public bool Busy => busy;
    public string StatusKey => status;
    public Task StageAsync() => Stage();
    public Task ConfirmAsync() => Confirm();
    private async Task Stage()
    {
        var connection = RimlinkSession.Connection; var login = connection?.Login; var game = Current.Game;
        if (connection is null || login?.Role != Role.Admin) return;
        busy = true; staged = null; status = "Rimlink.Outposts.ManifestWorking";
        try
        {
            var api = new OutpostManifestClient(connection); var binding = await api.Binding(RimlinkSession.RecoveryToken);
            var document = await MainThread.Get(() =>
            {
                Check(); var export = OutpostManifestCapture.Capture(Find.World);
                if (binding.WorldInstanceId != export.Binding.WorldInstanceId || !binding.WorldParamsSha256.Equals(export.Binding.WorldParamsSha256))
                    throw new InvalidDataException("administrator world parameters differ from the server");
                export.Binding = binding.Clone(); return export;
            });
            var preview = await api.Stage(document, RimlinkSession.RecoveryToken);
            await MainThread.Get(() =>
            {
                Check(); staged = preview.Clone(); status = "Rimlink.Outposts.ManifestReview";
                details = "Rimlink.Outposts.ManifestDetails".Translate(preview.ManifestId, preview.Revision, preview.Digest,
                    document.TileCount, document.GameVersion, Convert.ToHexStringLower(document.GameDefsSha256.Span), string.Join(", ", document.ActiveDlc)); return true;
            });
        }
        catch (Exception error) { await MainThread.Get(() => { status = "Rimlink.Outposts.ManifestFailed"; details = Rimlink.Client.Net.RpcErrorDetail.Read(error) is { } refusal ? RefusalText.Of(refusal) : "Rimlink.Outposts.ManifestFailed".Translate(); return true; }); }
        finally { await MainThread.Get(() => { busy = false; return true; }); }
        void Check() { if (Current.Game != game || RimlinkSession.Connection != connection || !ReferenceEquals(connection.Login, login)) throw new OperationCanceledException(); }
    }
    private async Task Confirm()
    {
        var connection = RimlinkSession.Connection; var login = connection?.Login; var preview = staged?.Clone();
        if (connection is null || login?.Role != Role.Admin || preview is null) return;
        busy = true;
        try
        {
            await new OutpostManifestClient(connection).Confirm(preview, RimlinkSession.RecoveryToken);
            await MainThread.Get(() => { if (RimlinkSession.Connection == connection && ReferenceEquals(connection.Login, login)) { status = "Rimlink.Outposts.ManifestConfirmed"; staged = null; } return true; });
        }
        catch (Exception error) { await MainThread.Get(() => { status = "Rimlink.Outposts.ManifestFailed"; details = Rimlink.Client.Net.RpcErrorDetail.Read(error) is { } refusal ? RefusalText.Of(refusal) : "Rimlink.Outposts.ManifestFailed".Translate(); return true; }); }
        finally { await MainThread.Get(() => { busy = false; return true; }); }
    }
}
