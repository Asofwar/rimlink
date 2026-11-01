using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rimlink.Client.Diagnostics;
using Rimlink.Client.Net;
using Xunit;

namespace Rimlink.Client.Tests.Diagnostics;

public sealed class DiagnosticBundleTests
{
    [Fact]
    public void DirtyLogsAndModMetadataNeverReachZipOrPreview()
    {
        const string dirty = "SteamID=76561198012345678 login=MyLogin IP=192.168.0.1 endpoint=https://private.example:123 " +
                             "pin=ABCDEF password=Secret42 ticket=Ticket42 token=Token42 C:\\Users\\Private\\file chat=PrivateWords";
        var journal = new DiagnosticJournal();
        journal.RecordLog(DiagnosticSeverity.Warning, dirty);
        journal.RecordLog(DiagnosticSeverity.Error, "save uploaded as v1 " + dirty);
        var snapshot = new DiagnosticSnapshot(ConnectionState.Reconnecting, new UploadSnapshot(null, 0, UploadFailure.Transport),
            DiagnosticLiveRole.Guest, 2, 3, true);
        var bundle = DiagnosticBundle.Create(snapshot, [new DiagnosticMod(dirty, dirty), new DiagnosticMod("asofwar.rimlink", "0.6.2")], journal);
        Assert.DoesNotContain(dirty, bundle.Preview);
        foreach (string secret in new[] { "76561198", "MyLogin", "192.168", "private.example", "ABCDEF", "Secret42", "Ticket42", "Token42", "PrivateWords", "C:\\Users" })
            Assert.DoesNotContain(secret, bundle.Preview, StringComparison.OrdinalIgnoreCase);
        using var zip = new ZipArchive(new MemoryStream(bundle.ZipBytes));
        Assert.Equal(new[] { "diagnostics.json", "events.log" }, zip.Entries.Select(e => e.FullName));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.Contains(text, bundle.Preview);
            Assert.DoesNotContain(dirty, text);
        }
        using var report = JsonDocument.Parse(zip.GetEntry("diagnostics.json")!.Open());
        Assert.Equal("Reconnecting", report.RootElement.GetProperty("connection").GetString());
        Assert.Equal("Transport", report.RootElement.GetProperty("upload").GetProperty("failure").GetString());
        Assert.Equal("Guest", report.RootElement.GetProperty("liveRole").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("liveSnapshotVersion").GetInt32());
        Assert.Equal(3, report.RootElement.GetProperty("pendingRequests").GetInt32());
        Assert.True(report.RootElement.GetProperty("pendingSave").GetBoolean());
        Assert.Contains("asofwar.rimlink", bundle.Preview);
        Assert.Contains("details omitted", bundle.Preview);
    }

    [Fact]
    public void AdversarialLogsAndUnboundedModSequenceStayWithinCaps()
    {
        var journal = new DiagnosticJournal();
        string huge = new('x', 2_000_000);
        for (int i = 0; i < 1000; i++) journal.RecordLog(DiagnosticSeverity.Warning, huge);
        var bundle = DiagnosticBundle.Create(new DiagnosticSnapshot(), Enumerable.Repeat(new DiagnosticMod(huge, huge), int.MaxValue), journal);
        Assert.InRange(bundle.ZipBytes.Length, 1, DiagnosticBundle.MaxZipBytes);
        Assert.InRange(Encoding.UTF8.GetByteCount(bundle.Preview), 1, DiagnosticBundle.MaxPreviewBytes);
        using var zip = new ZipArchive(new MemoryStream(bundle.ZipBytes));
        Assert.All(zip.Entries, e => Assert.InRange(e.Length, 0, DiagnosticBundle.MaxEntryBytes));
        using var report = JsonDocument.Parse(zip.GetEntry("diagnostics.json")!.Open());
        Assert.Equal(DiagnosticBundle.MaxMods, report.RootElement.GetProperty("mods").GetArrayLength());
        Assert.True(report.RootElement.GetProperty("modsTruncated").GetBoolean());
        Assert.Equal(DiagnosticJournal.MaxEntries, journal.Snapshot().Count);
    }

    [Fact]
    public void NumericModVersionsCannotSmuggleNetworkAddressesIntoTheBundle()
    {
        var bundle = DiagnosticBundle.Create(new DiagnosticSnapshot(),
            [new DiagnosticMod("private.mod", "192.168.0.1"), new DiagnosticMod("brrainz.harmony", "10.0.0.2")], new DiagnosticJournal());
        Assert.DoesNotContain("192.168.0.1", bundle.Preview);
        Assert.DoesNotContain("10.0.0.2", bundle.Preview);
    }

    [Fact]
    public void UploadStatusPreservesLastSuccessOnFailureAndClearsErrorOnRecovery()
    {
        var status = new UploadStatus();
        Assert.Equal(new UploadSnapshot(null, 0, UploadFailure.None), status.Snapshot);
        status.Succeeded(7);
        var success = status.Snapshot;
        status.Failed(new IOException("C:\\secret path"));
        Assert.Equal(success.LastSuccessUtc, status.Snapshot.LastSuccessUtc);
        Assert.Equal(7, status.Snapshot.Version);
        Assert.Equal(UploadFailure.LocalIo, status.Snapshot.Failure);
        status.Succeeded(8);
        Assert.Equal(8, status.Snapshot.Version);
        Assert.Equal(UploadFailure.None, status.Snapshot.Failure);
    }
}
