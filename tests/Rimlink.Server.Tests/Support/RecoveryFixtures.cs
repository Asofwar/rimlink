using System.Text;
using System.Xml.Linq;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Tests.Support;

public static class RecoveryFixtures
{
    public static byte[] Save(long tick, string? id = null, long epoch = 0, long generation = 0, IEnumerable<long>? parcels = null,
        IEnumerable<string>? removed = null, IEnumerable<string>? returned = null, int padding = 0)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(SaveFixtures.Decompress(SaveFixtures.MakeSave(ServerFixture.WorldSeed, "Nova", tick, paddingBytes: padding))));
        var component = new XElement("li", new XAttribute("Class", SaveRecoveryData.ComponentClass),
            new XElement(SaveRecoveryData.VersionNode, 1), new XElement(SaveRecoveryData.SnapshotNode, id ?? Guid.NewGuid().ToString("N")),
            new XElement("rimlinkSaveGeneration", generation), new XElement(SaveRecoveryData.EpochNode, epoch),
            new XElement(SaveRecoveryData.ParcelsNode, (parcels ?? []).Select(value => new XElement("li", value))),
            new XElement(SaveRecoveryData.RemovedNode, (removed ?? []).Select(value => new XElement("li", value))),
            new XElement(SaveRecoveryData.ReturnedNode, (returned ?? []).Select(value => new XElement("li", value))));
        document.Root!.Element("game")!.Add(new XElement("components", component));
        return SaveFixtures.Compress(Encoding.UTF8.GetBytes(document.ToString()));
    }

    public static SaveHeader Header(byte[] bytes, long tick)
    {
        var header = SaveFixtures.HeaderFor(bytes, ServerFixture.WorldSeed, "Nova", tick);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(SaveFixtures.Decompress(bytes)), out _);
        header.RecoveryVersion = recovery.Version; header.SnapshotId = recovery.SnapshotId;
        header.SaveGeneration = recovery.Generation; header.SaveWriteEpoch = recovery.WriteEpoch;
        return header;
    }

    public static async Task<LoginReply> Login(ServerFixture fixture, string login = "nova")
    {
        var request = GrpcTestExtensions.PasswordLogin(login, "pw");
        request.RecoveryVersion = SaveRecoveryData.CurrentVersion;
        return await new Protocol.V1.Auth.AuthClient(fixture.CreateChannel()).LoginAsync(request);
    }
}
