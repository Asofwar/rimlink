using Rimlink.Protocol.V1;
namespace Rimlink.Client.Procurement;

/// <summary>Main-thread board cache; revision prevents a change during a refresh from being swallowed.</summary>
public sealed class ProcurementState
{
    public List<ProcurementEntry> Entries { get; } = [];
    public long Revision { get; private set; }
    public void Invalidate() => Revision++;
    public void Load(ProcurementList list) { Entries.Clear(); Entries.AddRange(list.Entries); }
    public void Reset() { Entries.Clear(); Revision++; }
}
