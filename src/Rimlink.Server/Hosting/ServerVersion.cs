using System.Reflection;

namespace Rimlink.Server.Hosting;

/// <summary>The release version (<c>Directory.Build.props</c>, equal to the mod's <c>modVersion</c>) without the source-revision suffix.</summary>
public static class ServerVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        string version = typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
