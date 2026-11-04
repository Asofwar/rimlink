using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed class OutpostLocalizationTests
{
    [Fact]
    public void Russian_and_English_keys_and_args_match()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rimlink.sln"))) root = root.Parent;
        Assert.NotNull(root);
        Dictionary<string, string> Load(string language) => XDocument.Load(Path.Combine(root!.FullName,
            "mod", "Languages", language, "Keyed", "Outposts.xml")).Root!.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
        var en = Load("English"); var ru = Load("Russian");
        Assert.Equal(en.Keys.Order(), ru.Keys.Order());
        foreach (string key in new[] { "Title", "Empty", "Summary", "Worker", "State.Active", "State.SafetyHold", "State.Archived", "State.Full",
            "Claim", "Recall", "Close", "Switch", "FractionWarning", "CloseWarning", "Pending", "DeliveryHold", "DeliveryComplete" })
            Assert.True(en.ContainsKey("Rimlink.Outposts." + key), "missing user-facing control: " + key);
        foreach (var (key, value) in en)
        {
            string[] Args(string text) => Regex.Matches(text, @"{([0-9]+)(?:[^}]*)}").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            Assert.Equal(Args(value), Args(ru[key]));
            object[] arguments = Enumerable.Range(0, 12).Select(n => (object)n).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, value, arguments);
            _ = string.Format(CultureInfo.GetCultureInfo("ru-RU"), ru[key], arguments);
        }
    }
}
