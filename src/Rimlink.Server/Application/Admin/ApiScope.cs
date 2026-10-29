namespace Rimlink.Server.Application.Admin;

/// <summary>What a REST API token may do (admin panel spec §3). Every /api/admin route declares the scope it needs.</summary>
[Flags]
public enum ApiScope
{
    None = 0,
    Read = 1,
    Chat = 2,
    Moderate = 4,
    Admin = 8,
    All = Read | Chat | Moderate | Admin,
}

public static class ApiScopes
{
    /// <summary><c>admin</c> includes everything, <c>moderate</c> includes <c>read</c>, <c>chat</c> includes nothing else.</summary>
    public static ApiScope Expand(ApiScope held)
    {
        if (held.HasFlag(ApiScope.Admin)) return ApiScope.All;
        if (held.HasFlag(ApiScope.Moderate)) held |= ApiScope.Read;
        return held;
    }

    public static bool Grants(ApiScope held, ApiScope needed) => (Expand(held) & needed) == needed;

    /// <summary>Parses one lowercase scope name as written in <c>Admin:Tokens[].Scopes</c>.</summary>
    public static bool TryParse(string name, out ApiScope scope)
    {
        scope = name switch
        {
            "read" => ApiScope.Read,
            "chat" => ApiScope.Chat,
            "moderate" => ApiScope.Moderate,
            "admin" => ApiScope.Admin,
            _ => ApiScope.None,
        };
        return scope != ApiScope.None;
    }

    public static string Name(ApiScope scope) => scope.ToString().ToLowerInvariant();
}
