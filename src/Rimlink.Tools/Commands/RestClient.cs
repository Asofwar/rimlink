using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rimlink.Tools.Commands;

/// <summary>Thin wrapper over the server's /api/admin REST API.</summary>
public sealed class RestClient : IDisposable
{
    private readonly HttpClient http;

    public RestClient(string server, string token, bool insecure)
        : this(CreateHttp(server, token, insecure))
    {
    }

    /// <summary>Over a ready client whose BaseAddress ends in /api/admin/ and which already carries the auth header (tests).</summary>
    public RestClient(HttpClient http) => this.http = http;

    private static HttpClient CreateHttp(string server, string token, bool insecure)
    {
        var handler = new HttpClientHandler();
        if (insecure) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var client = new HttpClient(handler) { BaseAddress = new Uri(server.TrimEnd('/') + "/api/admin/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<string> Get(string path) => await Read(await http.GetAsync(path));

    public async Task<string> Post(string path, object? body) => await Read(await http.PostAsJsonAsync(path, body ?? new { }));

    public async Task<string> Put(string path, object body) => await Read(await http.PutAsJsonAsync(path, body));

    public async Task<string> Delete(string path) => await Read(await http.DeleteAsync(path));

    /// <summary>Streams a binary response (a backup archive) into <paramref name="file"/>.</summary>
    public async Task Download(string path, string file)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) await Read(response);
        await using var target = File.Create(file);
        await response.Content.CopyToAsync(target);
    }

    private static async Task<string> Read(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {text}");
        if (text.Length == 0) return "ok";
        try { return JsonSerializer.Serialize(JsonDocument.Parse(text), new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return text; }
    }

    public void Dispose() => http.Dispose();
}
