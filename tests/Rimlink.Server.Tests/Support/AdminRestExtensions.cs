using System.Net.Http.Headers;

namespace Rimlink.Server.Tests.Support;

/// <summary>REST clients and named API tokens for admin API tests.</summary>
public static class AdminRestExtensions
{
    public const string ApiToken = "test-admin-token";

    /// <summary>Adds <c>Admin:Tokens[index]</c> with the given name, token and scopes.</summary>
    public static ServerFixture WithToken(this ServerFixture fixture, int index, string name, string token, params string[] scopes)
    {
        fixture.WithSetting($"Admin:Tokens:{index}:Name", name).WithSetting($"Admin:Tokens:{index}:Token", token);
        for (int i = 0; i < scopes.Length; i++) fixture.WithSetting($"Admin:Tokens:{index}:Scopes:{i}", scopes[i]);
        return fixture;
    }

    /// <summary>A client sending <paramref name="token"/> as Bearer and, when given, an <c>X-Rimlink-Actor</c> label.</summary>
    public static HttpClient Rest(this ServerFixture fixture, string token = ApiToken, string? actor = null)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (actor is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Rimlink-Actor", actor);
        return client;
    }
}
