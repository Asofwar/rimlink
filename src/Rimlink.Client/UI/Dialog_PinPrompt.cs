using Rimlink.Client.Net;
using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>"Trust this server certificate?" — first contact or a changed fingerprint.</summary>
public sealed class Dialog_PinPrompt : Window
{
    private readonly ServerEndpoint endpoint;
    private readonly string fingerprint;
    private readonly string? previous;
    private readonly TaskCompletionSource<bool> answer;

    public Dialog_PinPrompt(ServerEndpoint endpoint, string fingerprint, string? previous, TaskCompletionSource<bool> answer)
    {
        this.endpoint = endpoint;
        this.fingerprint = fingerprint;
        this.previous = previous;
        this.answer = answer;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(620f, 300f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), (previous is null ? "Rimlink.Pin.NewTitle" : "Rimlink.Pin.ChangedTitle").Translate());
        Text.Font = GameFont.Small;
        string body = previous is null
            ? "Rimlink.Pin.NewBody".Translate(endpoint.Display, Pretty(fingerprint))
            : "Rimlink.Pin.ChangedBody".Translate(endpoint.Display, Pretty(previous), Pretty(fingerprint));
        Widgets.Label(new Rect(0f, 44f, inRect.width, inRect.height - 100f), body);
        if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, 200f, 32f), "Rimlink.Pin.Trust".Translate())) Finish(true);
        if (Widgets.ButtonText(new Rect(inRect.width - 200f, inRect.height - 40f, 200f, 32f), "Rimlink.Cancel".Translate())) Finish(false);
    }

    public override void PreClose()
    {
        answer.TrySetResult(false);
        base.PreClose();
    }

    private void Finish(bool trust)
    {
        answer.TrySetResult(trust);
        Close();
    }

    private static string Pretty(string hex) => string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}

/// <summary>IPinPrompt that opens the dialog on the main thread and waits for the answer.</summary>
public sealed class GamePinPrompt : IPinPrompt
{
    public Task<bool> Confirm(ServerEndpoint endpoint, string fingerprint, string? previous) =>
        MainThread.Ask<bool>(tcs => new Dialog_PinPrompt(endpoint, fingerprint, previous, tcs));
}
