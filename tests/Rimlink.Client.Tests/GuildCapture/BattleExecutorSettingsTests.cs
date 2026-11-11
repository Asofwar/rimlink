using System.Text.Json;
using Rimlink.Client.GuildCapture;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class BattleExecutorSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"rimlink-executor-"+Guid.NewGuid().ToString("N"));
    public BattleExecutorSettingsTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory,true);
    private string PathOf(string name) => Path.Combine(directory,name);
    private Dictionary<string,object> Settings() => new()
    {
        ["Version"]=1,["ExecutorId"]="arbiter-1",["WorldInstanceId"]="world",["RestoreBoundary"]="4fcd206c-2bc0-4dcb-9c88-a2f9e79c660f",["Endpoint"]="https://localhost:27555",
        ["CertificateSha256"]=new string('a',64),["ProfilePath"]=PathOf("profile.artifact"),["ProfileSha256"]=new string('b',64),
        ["ProfileDigest"]=new string('c',64),["ArchiveRoot"]=PathOf("archive"),["CredentialPath"]=PathOf("secret"),["StateDirectory"]=PathOf("state"),
        ["Roots"]=new[]{new{Kind=1,Id="game",Path=PathOf("game")}}
    };
    private string Write(Dictionary<string,object>? values=null)
    {string path=PathOf("executor.json");File.WriteAllText(path,JsonSerializer.Serialize(values??Settings()));return path;}

    [Fact]
    public void Ordinary_startup_never_opens_an_executor_configuration_or_credential()
    {
        Assert.Null(BattleExecutorSettings.ConfigurationArgument(["RimWorld.exe","-savedatafolder="+directory]));
        string config=Write(); var value=BattleExecutorSettings.Read(config);
        Assert.Equal("arbiter-1",value.ExecutorId); Assert.False(File.Exists(value.CredentialPath));
        Assert.Equal(config,BattleExecutorSettings.ConfigurationArgument(["RimWorld.exe","-rimlinkExecutorConfig="+config]));
    }
    [Theory]
    [InlineData("Version",2)][InlineData("Endpoint","http://localhost:27555")]
    [InlineData("Endpoint","https://user:password@localhost")][InlineData("Endpoint","https://localhost/path")]
    [InlineData("RestoreBoundary", "")][InlineData("RestoreBoundary", "00000000-0000-0000-0000-000000000000")]
    [InlineData("CertificateSha256","unknown")][InlineData("ProfileDigest","abc")]
    [InlineData("CredentialPath","relative-secret")][InlineData("ExecutorId","bad id")]
    public void Invalid_operator_settings_fail_closed_before_opening_a_secret(string key,object replacement)
    {
        var settings=Settings();settings[key]=replacement;
        Assert.Throws<InvalidDataException>(()=>BattleExecutorSettings.Read(Write(settings)));
    }
    [Fact]
    public void Unknown_duplicate_or_oversized_configuration_is_refused()
    {
        string path=Write();string valid=File.ReadAllText(path);
        foreach(string invalid in new[]{valid.Replace("{","{\"Unapproved\":1,",StringComparison.Ordinal),valid.Replace("\"Version\":1","\"Version\":1,\"Version\":1"),new string(' ',16385)})
        {File.WriteAllText(path,invalid);Assert.Throws<InvalidDataException>(()=>BattleExecutorSettings.Read(path));}
        Assert.Throws<InvalidDataException>(()=>BattleExecutorSettings.ConfigurationArgument(["-rimlinkExecutorConfig="+path,"-rimlinkExecutorConfig="+path]));
    }
    [Fact]
    public void Restore_cleanup_selects_one_historical_assignment_and_a_distinct_boundary()
    {
        var value = BattleExecutorSettings.Read(Write());
        Directory.CreateDirectory(value.StateDirectory);
        var target = Guid.NewGuid().ToString("D");
        var battle = Guid.NewGuid().ToString("D");
        var json = JsonSerializer.Serialize(new { Version = 1, BattleId = battle, Attempt = 1, Fence = 2,
            OriginalBoundary = value.RestoreBoundary, TargetBoundary = target });
        File.WriteAllText(value.RestoreCleanupPath(), json);
        var selection = value.ReadRestoreCleanup();
        Assert.Equal(battle, selection.Original.BattleId);
        Assert.Equal(2, selection.Original.Fence);
        Assert.Equal(value.RestoreBoundary, selection.Original.RestoreBoundary);
        Assert.Equal(target, selection.TargetBoundary);
        Assert.Null(selection.ReplacementAssignmentId);
        Assert.Null(selection.ReplacementStartDigest);
        File.WriteAllText(value.RestoreCleanupPath(), json.Replace(target, value.RestoreBoundary, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => value.ReadRestoreCleanup());
        string assignment = Guid.NewGuid().ToString("D");
        string replacement = json[..^1] + $",\"ReplacementAssignmentId\":\"{assignment}\"";
        File.WriteAllText(value.RestoreCleanupPath(), replacement + "}");
        Assert.Throws<InvalidDataException>(() => value.ReadRestoreCleanup());
        foreach (string digest in new[] { "", new string('a', 64) })
        {
            File.WriteAllText(value.RestoreCleanupPath(), replacement + $",\"ReplacementStartDigest\":\"{digest}\"}}");
            selection = value.ReadRestoreCleanup();
            Assert.Equal(assignment, selection.ReplacementAssignmentId);
            Assert.Equal(digest, selection.ReplacementStartDigest);
        }
    }
    [Fact]
    public void Secret_is_read_only_explicitly_and_refuses_whitespace_instead_of_silently_changing_identity()
    {
        var value=BattleExecutorSettings.Read(Write());
        File.WriteAllText(value.CredentialPath,new string('s',32));Assert.Equal(new string('s',32),value.ReadCredential());
        foreach(string invalid in new[]{"short",new string('s',32)+"\n",new string('s',257)})
        {File.WriteAllText(value.CredentialPath,invalid);Assert.Throws<InvalidDataException>(()=>value.ReadCredential());}
    }
}
