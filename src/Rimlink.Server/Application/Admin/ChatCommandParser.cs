using System.Text;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Admin;

/// <summary>Turns a chat line like <c>/ban "Player 1" 7 griefing</c> into an <see cref="AdminCommand"/>. Null when the line is not a command.</summary>
public static class ChatCommandParser
{
    public static AdminCommand? Parse(string line)
    {
        if (line.Length < 2 || line[0] != '/') return null;

        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line[1..])
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { parts.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) parts.Add(current.ToString());
        if (parts.Count == 0) return null;

        var command = new AdminCommand { Name = parts[0].ToLowerInvariant() };
        command.Args.AddRange(parts.Skip(1));
        return command;
    }
}
