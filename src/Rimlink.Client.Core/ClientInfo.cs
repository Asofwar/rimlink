namespace Rimlink.Client;

public static class ClientInfo
{
    public const string ModPackageId = "asofwar.rimlink";
    public const string Version = "0.8.1";
    public static int ProtocolVersion => Protocol.ProtocolVersion.Current;
}
