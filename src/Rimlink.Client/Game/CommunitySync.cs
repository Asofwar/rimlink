using Rimlink.Client.Net;
using Rimlink.Client.UI;
using RimWorld;
using Verse;

namespace Rimlink.Client.Game;

/// <summary>Main-thread invalidation, background fetch, main-thread apply. A former session's response is never applied.</summary>
public static class CommunitySync
{
    private static RimlinkConnection? fetching;
    private static DateTime retryAt;
    private static RimlinkConnection? notifiedConnection;
    private static readonly HashSet<long> notified = [];

    public static void Tick()
    {
        if (RimlinkSession.Connection is not { } connection || RimlinkSession.Community is not { } client
            || !RimlinkSession.State.Community.Stale || ReferenceEquals(fetching, connection) || DateTime.UtcNow < retryAt) return;
        fetching = connection;
        long revision = RimlinkSession.State.Community.Revision;
        _ = Task.Run(async () =>
        {
            try
            {
                var state = await client.Get(CancellationToken.None);
                MainThread.Post(() =>
                {
                    if (!ReferenceEquals(RimlinkSession.Connection, connection) || !RimlinkSession.State.Community.Load(state, revision)) return;
                    if (!ReferenceEquals(notifiedConnection, connection)) { notifiedConnection = connection; notified.Clear(); }
                    notified.IntersectWith(state.Invitations.Select(i => i.Id));
                    foreach (var invite in state.Invitations.Where(i => i.To.PlayerId == RimlinkSession.MyPlayerId))
                        if (notified.Add(invite.Id) && state.Profile.Notifications)
                            Messages.Message("Rimlink.Community.Invited".Translate(invite.From.Name, invite.AllianceId == 0 ? "Rimlink.Community.Friend".Translate().ToString() : invite.AllianceName), MessageTypeDefOf.NeutralEvent, historical: false);
                });
            }
            catch (Exception ex) { RimlinkLog.Instance.Warn("community refresh: " + ex.Message); }
            finally { MainThread.Post(() => { if (ReferenceEquals(fetching, connection)) fetching = null; retryAt = DateTime.UtcNow.AddSeconds(2); }); }
        });
    }
}
