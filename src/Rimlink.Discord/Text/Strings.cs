using System.Globalization;
using System.Text.Json;

namespace Rimlink.Discord.Text;

/// <summary>The bot's texts in one language (<c>Strings/Strings.&lt;language&gt;.json</c>, embedded). A missing key shows as the key.</summary>
public sealed class Strings
{
    private readonly Dictionary<string, string> texts;

    public Strings(string language)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream($"Rimlink.Discord.Strings.Strings.{language}.json")
            ?? throw new ArgumentException($"no texts for language '{language}'", nameof(language));
        texts = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    public IReadOnlyCollection<string> Keys => texts.Keys;

    public string T(string key, params object?[] args) =>
        texts.TryGetValue(key, out var text) ? string.Format(CultureInfo.InvariantCulture, text, args) : key;

    /// <summary>The text for <paramref name="key"/> when it exists, else null (for optional variants like a reason's wording).</summary>
    public string? Maybe(string key) => texts.GetValueOrDefault(key);
}
