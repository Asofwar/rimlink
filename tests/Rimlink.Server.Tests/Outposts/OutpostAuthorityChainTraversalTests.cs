using System.Diagnostics;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Xunit;
using Authority = Rimlink.Server.Application.Outposts.OutpostAuthority;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAuthorityChainTraversalTests
{
    [Fact]
    public void Long_unordered_history_is_walked_within_a_linear_work_budget()
    {
        const int count = 50_000;
        var transitions = Enumerable.Range(0, count).Select(i => new OutpostTransition
        {
            Id = "transition-" + i,
            OldHeadId = i == 0 ? Guid.Empty.ToString("D") : "head-" + i,
            NextHeadId = "head-" + (i + 1)
        }).Reverse().ToArray();
        var watch = Stopwatch.StartNew();

        var ordered = Authority.OrderTransitions(transitions).ToArray();

        Assert.Equal(count, ordered.Length);
        Assert.Equal("transition-0", ordered[0].Id);
        Assert.Equal("transition-49999", ordered[^1].Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Chain traversal took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("disconnected")]
    [InlineData("cycle-with-tail")]
    public void Forks_and_unreachable_history_still_refuse(string fault)
    {
        string genesis = Guid.Empty.ToString("D");
        var transitions = new List<OutpostTransition>
        {
            new() { Id = "first", OldHeadId = genesis, NextHeadId = "next" },
            new() { Id = "second", OldHeadId = "next", NextHeadId = "tip" }
        };
        if (fault == "fork") transitions.Add(new() { Id = "fork", OldHeadId = genesis, NextHeadId = "other" });
        if (fault == "disconnected") transitions.Add(new() { Id = "orphan", OldHeadId = "missing", NextHeadId = "other" });
        if (fault == "cycle-with-tail")
        {
            transitions[1].NextHeadId = genesis;
            transitions.Add(new() { Id = "orphan", OldHeadId = "missing", NextHeadId = "other" });
        }

        Assert.Throws<OutpostEvidenceUnavailableException>(() => Authority.OrderTransitions(transitions).ToArray());
    }
}
