namespace Rimlink.Server.Hosting;

public sealed class GuildCaptureOptions
{
    public const string Section = "GuildCapture";
    public bool Enabled { get; set; }
    public string ExecutorId { get; set; } = "";
    public string ExecutorTokenSha256 { get; set; } = "";
    public string ApprovedProfileDigest { get; set; } = "";

    internal static bool Valid(GuildCaptureOptions options) => !options.Enabled || Configured(options);
    internal static bool Configured(GuildCaptureOptions options) =>
        !string.IsNullOrWhiteSpace(options.ExecutorId) && options.ExecutorId.Length <= 128
        && options.ExecutorId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && Hex(options.ExecutorTokenSha256) && (options.ApprovedProfileDigest == "" || Hex(options.ApprovedProfileDigest));
    private static bool Hex(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
