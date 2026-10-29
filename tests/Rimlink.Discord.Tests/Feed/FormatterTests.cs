using Rimlink.Discord.Feed;
using Rimlink.Discord.Text;
using Xunit;
using static Rimlink.Discord.Tests.Support.Events;

namespace Rimlink.Discord.Tests.Feed;

public class FormatterTests
{
    private static readonly Formatter En = new(new Strings("en"));

    /// <summary>One sample per event type of the bot spec §2 table (except chat.message).</summary>
    public static TheoryData<string, object> Samples => new()
    {
        { FeedTypes.ServerStarted, new { version = "0.5.0" } },
        { FeedTypes.PlayerJoined, new { player = P(1, "Nova"), faction = "Nova's" } },
        { FeedTypes.PlayerLeft, new { player = P(1, "Nova"), faction = "" } },
        { FeedTypes.ColonyFounded, new { player = P(1, "Nova"), colony = "Home", tile = 100 } },
        { FeedTypes.ColonyRemoved, new { player = P(1, "Nova"), colony = "Home", tile = (int?)null } },
        { FeedTypes.ColonyArchived, new { player = P(12, "Nova"), colony = "Nova Home", tile = 100, reason = "auto", actor = "system:hygiene" } },
        { FeedTypes.ColonyRestored, new { player = P(12, "Nova"), colony = "Nova Home", tile = 100, actor = "player" } },
        { FeedTypes.Notice, new { text = "hello" } },
        { FeedTypes.ServerShutdown, new { inSeconds = 60, reason = "update" } },
        { FeedTypes.EconomyFrozen, new { frozen = true, reason = "audit" } },
        { FeedTypes.RaidsFrozen, new { frozen = false, reason = (string?)null } },
        { FeedTypes.RaidLanded, new { raidId = 5, attacker = P(1, "Nova"), target = P(2, "Bob") } },
        { FeedTypes.RaidResolved, new { raidId = 5, attacker = P(1, "Nova"), target = P(2, "Bob"), dead = 2, captured = 1, fled = 0, alive = 0, lootValue = 300, captives = 0 } },
        { FeedTypes.VisitStarted, new { visitor = P(1, "Nova"), host = P(2, "Bob") } },
        { FeedTypes.LiveStarted, new { sessionId = 3, kind = "raid", host = P(2, "Bob"), guest = P(1, "Nova"), reason = (string?)null } },
        { FeedTypes.LiveEnded, new { sessionId = 3, kind = "visit", host = P(2, "Bob"), guest = P(1, "Nova"), reason = "guest-lost" } },
        { FeedTypes.LotPosted, new { lotId = 7, seller = P(1, "Nova"), buyer = (object?)null, price = 300, summary = "Steel ×50" } },
        { FeedTypes.LotSold, new { lotId = 7, seller = P(1, "Nova"), buyer = P(2, "Bob"), price = 300, summary = "Steel ×50" } },
        { FeedTypes.ShopSold, new { host = P(2, "Bob"), buyer = P(1, "Nova"), item = "steel", count = 2, price = 200 } },
        { FeedTypes.AdminAction, new { action = "kick", actor = "api:discord-bot:discord:Mod (1)", target = P(2, "Bob"), details = "spam" } },
        { FeedTypes.IntegrityAlert, new { player = P(1, "Nova"), kind = "devmode", version = 4, net = (long?)null, days = (double?)null, rate = (long?)null, frozen = true } },
    };

    [Fact]
    public void IntegrityAlertsNameThePlayerTheNumbersAndTheFreeze()
    {
        string warn = En.Format(Ev(FeedTypes.IntegrityAlert, new { player = P(1, "Nova"), kind = "wealth-warn", version = 7, net = 30000, days = 1.0, rate = 30000, frozen = false }))!;
        string freeze = En.Format(Ev(FeedTypes.IntegrityAlert, new { player = P(1, "Nova"), kind = "wealth-freeze", version = 8, net = 300000, days = 2.0, rate = 150000, frozen = true }))!;
        string dev = En.Format(Ev(FeedTypes.IntegrityAlert, new { player = P(1, "Nova"), kind = "devmode", version = 9, frozen = true }))!;

        Assert.Contains("Nova", warn);
        Assert.Contains("30000", warn);
        Assert.Contains("v7", warn);
        Assert.Contains("150000", freeze);
        Assert.Contains("frozen", freeze);
        Assert.Contains("dev mode", dev);
        Assert.Contains("frozen", dev);
        Assert.Equal(FeedGroup.Moderation, FeedTypes.GroupOf(FeedTypes.IntegrityAlert));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryFeedTypeIsFormatted(string type, object data)
    {
        string? text = En.Format(Ev(type, data));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("feed.", text);
        Assert.Equal(type, Assert.Single(FeedTypes.All, t => t == type));
    }

    [Theory]
    [InlineData("colony.archived", "auto", "system:hygiene", "The colony **Nova Home** of **Nova** went to the archive (inactive)")]
    [InlineData("colony.archived", "admin", "discord:Mod (123)", "The colony **Nova Home** of **Nova** was archived by discord:Mod (123)")]
    [InlineData("colony.restored", null, "player", "The colony **Nova Home** of **Nova** is back: its player returned")]
    [InlineData("colony.restored", null, "panel", "The colony **Nova Home** of **Nova** was restored by panel")]
    public void ColonyArchiveVariantsAreFormatted(string type, string? reason, string actor, string expected)
    {
        var data = new { player = P(12, "Nova"), colony = "Nova Home", tile = 100, reason, actor };

        Assert.Equal(expected, En.Format(Ev(type, data)));
        Assert.Equal(FeedGroup.Server, FeedTypes.GroupOf(type));
    }

    [Theory]
    [InlineData("colony.archived", "auto", "system:hygiene")]
    [InlineData("colony.archived", "admin", "@everyone")]
    [InlineData("colony.restored", null, "player")]
    [InlineData("colony.restored", null, "@everyone")]
    public void ColonyArchiveNamesAndActorsAreEscaped(string type, string? reason, string actor)
    {
        string text = En.Format(Ev(type, new { player = P(12, "@everyone"), colony = "**@everyone**", tile = 100, reason, actor }))!;

        Assert.NotNull(text);
        Assert.DoesNotContain("@everyone", text, StringComparison.Ordinal);
        Assert.Contains("@\u200beveryone", text, StringComparison.Ordinal);
        Assert.Contains("\\*\\*@\u200beveryone\\*\\*", text, StringComparison.Ordinal);
        if (actor == "@everyone") Assert.EndsWith("@\u200beveryone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RaidResolvedNamesBothSidesAndTheCounts()
    {
        string text = En.Format(Ev(FeedTypes.RaidResolved, new { raidId = 5, attacker = P(1, "Nova"), target = P(2, "Bob"), dead = 2, captured = 1, fled = 3, alive = 4, lootValue = 300, captives = 0 }))!;

        Assert.Contains("Nova", text);
        Assert.Contains("Bob", text);
        Assert.Contains("2", text);
        Assert.Contains("300", text);
    }

    [Fact]
    public void LotSoldNamesSellerBuyerPriceAndSummary()
    {
        string text = En.Format(Ev(FeedTypes.LotSold, new { lotId = 7, seller = P(1, "Nova"), buyer = P(2, "Bob"), price = 300, summary = "Steel ×50" }))!;

        foreach (string part in new[] { "Nova", "Bob", "300", "Steel ×50" }) Assert.Contains(part, text);
    }

    [Fact]
    public void AdminActionShowsActorActionTargetAndDetails()
    {
        string text = En.Format(Ev(FeedTypes.AdminAction, new { action = "ban", actor = "api:discord-bot:discord:Mod (1)", target = P(2, "Bob"), details = "ban #4: griefing" }))!;

        foreach (string part in new[] { "ban", "discord:Mod (1)", "Bob", "griefing" }) Assert.Contains(part, text);
    }

    [Fact]
    public void NamesAreEscaped()
    {
        string text = En.Format(Ev(FeedTypes.PlayerJoined, new { player = P(1, "**x**"), faction = "" }))!;

        Assert.Contains(@"\*\*x\*\*", text);
    }

    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@123>")]
    [InlineData("<@&456>")]
    public void MentionsAreNeutralised(string mention)
    {
        // Ordinal: a culture-aware comparison ignores the zero-width space that breaks the mention; Discord does not.
        Assert.DoesNotContain(mention, En.Format(Ev(FeedTypes.PlayerJoined, new { player = P(1, mention), faction = "" }))!, StringComparison.Ordinal);
        Assert.DoesNotContain(mention, En.Format(Ev(FeedTypes.Notice, new { text = "hi " + mention }))!, StringComparison.Ordinal);
        Assert.DoesNotContain(mention, Formatter.Escape(mention), StringComparison.Ordinal);
    }

    [Fact]
    public void ChatAndUnknownTypesAreNotFormatted()
    {
        Assert.Null(En.Format(Ev(FeedTypes.ChatMessage, new { id = 1, kind = "player", from = P(1, "Nova"), author = (string?)null, text = "hi" })));
        Assert.Null(En.Format(Ev("something.new", new { })));
    }
}
