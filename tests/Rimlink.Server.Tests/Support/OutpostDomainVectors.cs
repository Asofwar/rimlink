namespace Rimlink.Server.Tests.Support;

/// <summary>Golden values captured from the unchanged v1 writer and actual supported-platform migrations.</summary>
internal static class OutpostDomainVectors
{
    public static string EmptyV1ForLocalMigrations => OperatingSystem.IsWindows()
        ? "d997fc5e6acce1f9381ae0945fdea701cbaddc715431ec0b14c9b99ce33df03a"
        : OperatingSystem.IsLinux()
            ? "796b149daea451b6a9cbbf32704198be720274a6df3aef5ffaecadb65bdb5bf5"
            : throw new PlatformNotSupportedException("Unqualified v1 migration platform.");
}
