using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

public sealed record PasswordLogin(string Login, string Password, bool Register, string PlayerName);

/// <summary>Login/password form used when Steam is not available or the player prefers an account.</summary>
public sealed class Dialog_Login : Window
{
    private readonly TaskCompletionSource<PasswordLogin?> answer;
    private string login = "";
    private string password = "";
    private string playerName = "";
    private bool register;

    public Dialog_Login(TaskCompletionSource<PasswordLogin?> answer, string? error = null)
    {
        this.answer = answer;
        Error = error;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public string? Error { get; }

    public override Vector2 InitialSize => new(420f, 320f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Rimlink.Login.Title".Translate());
        Text.Font = GameFont.Small;
        float y = 44f;
        if (Error is not null)
        {
            GUI.color = Color.red;
            Widgets.Label(new Rect(0f, y, inRect.width, 40f), Error);
            GUI.color = Color.white;
            y += 44f;
        }
        Widgets.Label(new Rect(0f, y, 120f, 28f), "Rimlink.Login.Login".Translate());
        login = Widgets.TextField(new Rect(124f, y, inRect.width - 124f, 28f), login, 32);
        y += 34f;
        Widgets.Label(new Rect(0f, y, 120f, 28f), "Rimlink.Login.Password".Translate());
        password = Widgets.TextField(new Rect(124f, y, inRect.width - 124f, 28f), password, 128);
        y += 34f;
        Widgets.CheckboxLabeled(new Rect(0f, y, inRect.width, 28f), "Rimlink.Login.Register".Translate(), ref register);
        y += 34f;
        if (register)
        {
            Widgets.Label(new Rect(0f, y, 120f, 28f), "Rimlink.Login.PlayerName".Translate());
            playerName = Widgets.TextField(new Rect(124f, y, inRect.width - 124f, 28f), playerName, 32);
        }
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, 180f, 32f), "Rimlink.Login.Ok".Translate()))
        {
            answer.TrySetResult(new PasswordLogin(login.Trim(), password, register, register ? playerName.Trim() : ""));
            Close();
        }
        if (Widgets.ButtonText(new Rect(inRect.width - 180f, inRect.height - 40f, 180f, 32f), "Rimlink.Cancel".Translate()))
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
