namespace Rimlink.Client;

public static class ClientInfo
{
    public const string ModPackageId = "asofwar.rimlink";
    public const string Version = "0.6.0";
    public static int ProtocolVersion => Protocol.ProtocolVersion.Current;
}
