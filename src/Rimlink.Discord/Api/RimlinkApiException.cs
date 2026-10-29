namespace Rimlink.Discord.Api;

/// <summary>A refused or failed admin API call. <see cref="Status"/> 0 means the server did not answer (timeout, no connection).</summary>
public sealed class RimlinkApiException(int status, string detail) : Exception($"{status}: {detail}")
{
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}
