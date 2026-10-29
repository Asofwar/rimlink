using System.Text.Json;
using System.Text.RegularExpressions;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Panel;

/// <summary>Static checks on the panel's front end: no HTML injection sinks, no inline code, complete dictionaries.</summary>
public partial class FrontendGuardTests
{
    private static IEnumerable<string> Files(string pattern) => Directory.EnumerateFiles(RepoPaths.WwwRoot, pattern, SearchOption.AllDirectories);

    private static Dictionary<string, string> Dictionary(string lang)
    {
        var result = new Dictionary<string, string>();
        void Walk(JsonElement e, string prefix)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}");
            else result[prefix] = e.GetString() ?? "";
        }
        Walk(JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.WwwRoot, "i18n", $"{lang}.json"))).RootElement, "");
        return result;
    }

    [Fact]
    public void NoHtmlInjectionSinks()
    {
        var offenders = Files("*.js").Where(f => Sinks().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(Files("*.js"));
        Assert.Empty(offenders);
    }

    [Fact]
    public void ViewsUseTheNullSafeHelpers()
    {
        // Native Element.append/replaceChildren print null and arrays as text ("nullnull" in the header); shared/dom.js
        // append()/replace() skip null and flatten arrays.
        var offenders = Files("*.js")
            .Where(f => Path.GetFileName(f) != "dom.js")
            .Where(f => NativeAppend().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void HtmlHasNoInlineCode()
    {
        foreach (string file in Files("*.html"))
        {
            string html = File.ReadAllText(file);
            Assert.DoesNotMatch(InlineScript(), html);
            Assert.DoesNotMatch(InlineHandler(), html);
            Assert.DoesNotContain(" style=", html);
        }
    }

    [Fact]
    public void DictionariesHaveTheSameKeys()
    {
        var en = Dictionary("en");
        var ru = Dictionary("ru");

        Assert.Equal(en.Keys.Order(), ru.Keys.Order());
        Assert.DoesNotContain(en, p => p.Value.Length == 0);
        Assert.DoesNotContain(ru, p => p.Value.Length == 0);
    }

    [Fact]
    public void EveryUsedKeyExists()
    {
        var en = Dictionary("en");
        var missing = Files("*.js")
            .SelectMany(f => UsedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(k => !en.ContainsKey(k))
            .ToList();

        Assert.Empty(missing);
    }

    [GeneratedRegex(@"\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|\beval\s*\(|new\s+Function\s*\(")]
    private static partial Regex Sinks();

    [GeneratedRegex(@"\.(append|replaceChildren|prepend)\(")]
    private static partial Regex NativeAppend();

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\son[a-z]+=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();

    [GeneratedRegex(@"\bt\(\s*'([A-Za-z0-9_.]+)'")]
    private static partial Regex UsedKey();
}
