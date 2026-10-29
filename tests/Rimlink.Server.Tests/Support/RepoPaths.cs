namespace Rimlink.Server.Tests.Support;

/// <summary>Paths inside the repository checkout, found by walking up from the test binaries to <c>Rimlink.sln</c>.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string WwwRoot => Path.Combine(Root, "src", "Rimlink.Server", "wwwroot");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Rimlink.sln"))) return dir.FullName;
        throw new InvalidOperationException("Rimlink.sln not found above " + AppContext.BaseDirectory);
    }
}
