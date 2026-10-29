using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

/// <summary>A token that matched: <see cref="Name"/> is null for <c>Admin:ApiToken</c>.</summary>
public sealed record ApiTokenMatch(string? Name, ApiScope Scopes);

/// <summary>
/// <c>Admin:ApiToken</c> (unnamed, full admin) and the named, scoped <c>Admin:Tokens</c>. The constructor validates the list,
/// so a bad configuration stops the server at start instead of surfacing as 401s in an integration.
/// </summary>
public sealed partial class ApiTokens
{
    public const int MinTokenLength = 24;

    private readonly string? apiToken;
    private readonly List<(string Name, string Token, ApiScope Scopes)> named = [];

    public ApiTokens(IOptions<AdminOptions> options)
    {
        var admin = options.Value;
        apiToken = string.IsNullOrWhiteSpace(admin.ApiToken) ? null : admin.ApiToken;

        for (int i = 0; i < admin.Tokens.Count; i++)
        {
            var entry = admin.Tokens[i];
            string where = $"Admin:Tokens[{i}] (\"{entry.Name}\")";
            if (!NamePattern().IsMatch(entry.Name)) throw Invalid(where, "name must match ^[a-z0-9-]{1,32}$");
            if (named.Any(t => t.Name == entry.Name)) throw Invalid(where, "name is already used by another token");
            if (entry.Token.Length < MinTokenLength) throw Invalid(where, $"token must be at least {MinTokenLength} characters");
            if (entry.Token == apiToken || named.Any(t => t.Token == entry.Token)) throw Invalid(where, "token is the same as another token");
            if (entry.Scopes.Length == 0) throw Invalid(where, "scopes must list at least one of read, chat, moderate, admin");

            var scopes = ApiScope.None;
            foreach (string name in entry.Scopes)
            {
                if (!ApiScopes.TryParse(name, out var scope)) throw Invalid(where, $"unknown scope '{name}' (expected read, chat, moderate or admin)");
                scopes |= scope;
            }
            named.Add((entry.Name, entry.Token, scopes));
        }
    }

    /// <summary>The REST API answers at all only when some token is configured.</summary>
    public bool Enabled => apiToken is not null || named.Count > 0;

    /// <summary>Finds the token a Bearer header carries. Compares against every token, with no early exit.</summary>
    public ApiTokenMatch? Find(string? bearer)
    {
        ApiTokenMatch? match = null;
        if (AdminToken.Matches(apiToken, bearer)) match = new ApiTokenMatch(null, ApiScope.All);
        foreach (var token in named)
            if (AdminToken.Matches(token.Token, bearer)) match ??= new ApiTokenMatch(token.Name, token.Scopes);
        return match;
    }

    private static InvalidOperationException Invalid(string where, string problem) => new($"{where}: {problem}");

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex NamePattern();
}
