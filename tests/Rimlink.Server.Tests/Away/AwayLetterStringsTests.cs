using System.Text.RegularExpressions;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Away;

/// <summary>The mod's away letter names every parcel kind and raid state the summary can send, in both languages.</summary>
public partial class AwayLetterStringsTests
{
    public static TheoryData<string> Languages => new() { "English", "Russian" };

    private static string Keyed(string language) =>
        File.ReadAllText(Path.Combine(RepoPaths.Root, "mod", "Languages", language, "Keyed", "Rimlink.xml"));

    private static string Snake(string pascal) => Capital().Replace(pascal, "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex Capital();

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryParcelKindHasALetterString(string language)
    {
        string keyed = Keyed(language);

        var missing = Enum.GetNames<ParcelKindDb>().Select(Snake).Where(kind => !keyed.Contains($"<Rimlink.Away.Parcel.{kind}>")).ToList();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryRaidStateHasALetterString(string language)
    {
        string keyed = Keyed(language);

        var missing = Enum.GetNames<RaidStateDb>().Select(s => s.ToLowerInvariant()).Where(state => !keyed.Contains($"<Rimlink.Away.RaidState.{state}>")).ToList();

        Assert.Empty(missing);
    }
}
