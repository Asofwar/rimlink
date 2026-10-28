using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rimlink.Tools.Commands;

/// <summary>Thin wrapper over the server's /api/admin REST API.</summary>
public sealed class RestClient : IDisposable
{
    private readonly HttpClient http;

    public RestClient(string server, string token, bool insecure)
    {
        var handler = new HttpClientHandler();
        if (insecure) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        http = new HttpClient(handler) { BaseAddress = new Uri(server.TrimEnd('/') + "/api/admin/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<string> Get(string path) => await Read(await http.GetAsync(path));

    public async Task<string> Post(string path, object? body) => await Read(await http.PostAsJsonAsync(path, body ?? new { }));

    public async Task<string> Put(string path, object body) => await Read(await http.PutAsJsonAsync(path, body));

    public async Task<string> Delete(string path) => await Read(await http.DeleteAsync(path));

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
