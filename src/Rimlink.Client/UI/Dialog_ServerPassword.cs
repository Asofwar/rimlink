using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Asked once when the server is protected by a join password (not the account password).</summary>
public sealed class Dialog_ServerPassword : Window
{
    private readonly TaskCompletionSource<string?> answer;
    private readonly string serverName;
    private string password = "";

    public Dialog_ServerPassword(string serverName, TaskCompletionSource<string?> answer)
    {
        this.serverName = serverName;
        this.answer = answer;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(420f, 190f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.ServerPassword.Title".Translate(serverName));
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, 44f, inRect.width, 44f), "Rimlink.ServerPassword.Hint".Translate());
        password = PasswordWidget.Draw(new Rect(0f, 92f, inRect.width, 28f), password);

        float w = (inRect.width - 8f) / 2f;
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 36f, w, 32f), "Rimlink.Login.Ok".Translate()))
        {
            answer.TrySetResult(password);
            Close();
        }
        if (Widgets.ButtonText(new Rect(w + 8f, inRect.height - 36f, w, 32f), "Rimlink.Cancel".Translate()))
        {
            answer.TrySetResult(null);
            Close();
        }
    }

    public override void PreClose()
    {
        answer.TrySetResult(null);
        base.PreClose();
    }
}
