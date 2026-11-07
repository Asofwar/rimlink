using Rimlink.OutpostAcceptanceHost;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAcceptanceOfflineInvocationTests
{
    [Fact]
    public void Offline_actions_keep_distinct_explicit_request_and_continuation_shapes()
    {
        string id = Guid.NewGuid().ToString("D"), digest = new('1', 64);
        AcceptanceOffline.ValidateInvocation(new(1, "execute", "request.json", digest));
        AcceptanceOffline.ValidateInvocation(new(1, "backout", "request.json", digest));
        AcceptanceOffline.ValidateInvocation(new(1, "resume", RestoreId: id));
        foreach (var wrong in new[] {
            new AcceptanceOfflineInvocation(1, "execute", "request.json"), new(1, "execute", "request.json", digest, id),
            new(1, "resume", "request.json", digest, id), new(1, "resume", RestoreId: "unknown"),
            new(1, "repair", "request.json", digest), new(2, "resume", RestoreId: id), new(1, "backout", "request.json", new('z',64)) })
            Assert.Throws<InvalidDataException>(() => AcceptanceOffline.ValidateInvocation(wrong));
    }
}
