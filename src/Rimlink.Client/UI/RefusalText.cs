using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Verse;

namespace Rimlink.Client.UI;

/// <summary>
/// A server refusal in the player's language (live UX spec §5): the keyed string <c>Rimlink.Refusal.&lt;key&gt;</c> with the refusal's
/// arguments when this language has it, otherwise the server's English message (older servers send no key).
/// </summary>
public static class RefusalText
{
    public static string Of(RpcException ex) => Of(RpcErrorDetail.Read(ex));

    public static string Of(Exception ex) => ex is RpcException rpc ? Of(rpc) : ex.Message;

    public static string Of(OutboxEntry entry) =>
        Of(new Refusal(null, entry.Refusal ?? "", entry.RefusalKey, (IReadOnlyList<string>?)entry.RefusalArgs ?? []));

    public static string Of(Refusal refusal)
    {
        if (refusal.Key is not { } key) return refusal.Message;
        string name = "Rimlink.Refusal." + key;
        if (!name.CanTranslate()) return refusal.Message;
        try
        {
            return name.Translate(refusal.Args.Select(a => (NamedArgument)a).ToArray());
        }
        catch (FormatException)
        {
            return refusal.Message; // a translation expecting more arguments than the server sent
        }
    }
}
