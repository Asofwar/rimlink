using UnityEngine;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// A masked text field. The game's UnityStubs <c>GUI.PasswordField</c> ignores the mask character, so the field shows
/// asterisks and the real text is reconciled from the edit: typing appends, Backspace removes; an edit in the middle
/// is treated as retyping from that point.
/// </summary>
public static class PasswordWidget
{
    public static string Draw(Rect rect, string password, int maxLength = 128)
    {
        string shown = new('*', password.Length);
        string edited = Widgets.TextField(rect, shown, maxLength);
        if (edited == shown) return password;
        int kept = 0;
        while (kept < edited.Length && kept < password.Length && edited[kept] == '*') kept++;
        return password[..kept] + edited[kept..].Replace("*", "");
    }
}
