namespace Rimlink.Server.Application.Feed;

/// <summary>One admin feed event. <see cref="Id"/> is <c>"&lt;epoch&gt;:&lt;seq&gt;"</c>; <see cref="DataJson"/> is serialized once, at publish.</summary>
public sealed record FeedEvent(string Id, long Seq, DateTime At, string Type, string DataJson);
