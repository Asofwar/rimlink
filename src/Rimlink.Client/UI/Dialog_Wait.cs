using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>Progress text with a Cancel button that cancels the token.</summary>
public sealed class Dialog_Wait : Window
{
    private readonly CancellationTokenSource cts;

    public string Text { get; set; }

    public Dialog_Wait(string text, CancellationTokenSource cts)
    {
        Text = text;
        this.cts = cts;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnCancel = false;
        closeOnClickedOutside = false;
    }

    public override Vector2 InitialSize => new(420f, 150f);

    public override void DoWindowContents(Rect inRect)
    {
        Verse.Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 60f), Text);
        if (Widgets.ButtonText(new Rect(inRect.width / 2f - 70f, inRect.height - 40f, 140f, 32f), "Rimlink.Cancel".Translate()))
        {
            cts.Cancel();
            Close();
        }
    }
}
