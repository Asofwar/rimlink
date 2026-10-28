using RimWorld;
using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Saved servers: pick one to connect, add or remove entries.</summary>
public sealed class Dialog_ServerList : Window
{
    private string newName = "";
    private string newHost = "";
    private string newPort = "25560";
    private Vector2 scroll;

    public Dialog_ServerList()
    {
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
    }

    public override Vector2 InitialSize => new(560f, 480f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Servers.Title".Translate());
        Text.Font = GameFont.Small;

        var servers = RimlinkMod.Settings.Servers.Select(ServerEndpoint.FromSettingsLine).Where(e => e is not null).Cast<ServerEndpoint>().ToList();
        var listRect = new Rect(0f, 44f, inRect.width, inRect.height - 44f - 110f);
        var viewRect = new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, servers.Count * 32f));
        Widgets.BeginScrollView(listRect, ref scroll, viewRect);
        float y = 0f;
        foreach (var server in servers)
        {
            var row = new Rect(0f, y, viewRect.width, 30f);
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 200f, 24f), $"{server.Display}  ({server.Host}:{server.Port})");
            if (Widgets.ButtonText(new Rect(row.xMax - 196f, row.y, 100f, 28f), "Rimlink.Servers.Connect".Translate()))
            {
                Close();
                LoginFlow.Run(server);
            }
            if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y, 90f, 28f), "Rimlink.Servers.Remove".Translate()))
            {
                RimlinkMod.Settings.Servers.Remove(server.ToSettingsLine());
                RimlinkMod.Settings.Write();
            }
            y += 32f;
        }
        if (servers.Count == 0) Widgets.Label(new Rect(0f, 0f, viewRect.width, 30f), "Rimlink.Servers.Empty".Translate());
        Widgets.EndScrollView();

        float formY = inRect.height - 100f;
        Widgets.Label(new Rect(0f, formY, 60f, 28f), "Rimlink.Servers.Name".Translate());
        newName = Widgets.TextField(new Rect(64f, formY, 160f, 28f), newName, 32);
        Widgets.Label(new Rect(236f, formY, 50f, 28f), "Rimlink.Servers.Host".Translate());
        newHost = Widgets.TextField(new Rect(290f, formY, 170f, 28f), newHost, 253);
        newPort = Widgets.TextField(new Rect(470f, formY, 60f, 28f), newPort, 5);
        if (Widgets.ButtonText(new Rect(0f, formY + 36f, 160f, 32f), "Rimlink.Servers.Add".Translate()))
        {
            if (int.TryParse(newPort, out int port) && port is > 0 and < 65536 && !string.IsNullOrWhiteSpace(newHost))
            {
                var endpoint = new ServerEndpoint(newHost.Trim(), port, newName.Trim());
                if (!RimlinkMod.Settings.Servers.Contains(endpoint.ToSettingsLine()))
                {
                    RimlinkMod.Settings.Servers.Add(endpoint.ToSettingsLine());
                    RimlinkMod.Settings.Write();
                }
                newName = ""; newHost = "";
            }
            else
            {
                Messages.Message("Rimlink.Servers.Invalid".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            }
        }
    }
}
