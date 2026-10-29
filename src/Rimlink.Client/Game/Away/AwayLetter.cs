using System.Text;
using RimWorld;
using Rimlink.Client.UI;
using Rimlink.Protocol.V1;
using Verse;

namespace Rimlink.Client.Game.Away;

/// <summary>The "while you were away" letter: asked for once per login after the home colony loads, posted when not empty.</summary>
public static class AwayLetter
{
    /// <summary>Fetches the summary in the background and posts the letter on the main thread.</summary>
    public static void Request()
    {
        if (RimlinkSession.Connection is not { } connection) return;
        _ = Task.Run(async () =>
        {
            AwaySummary summary;
            try { summary = await connection.AwayApi.SummaryAsync(new Empty()); }
            catch (Exception ex) { RimlinkLog.Instance.Warn("away summary not fetched: " + ex.Message); return; }
            MainThread.Post(() => Post(summary));
        });
    }

    public static bool IsEmpty(AwaySummary s) =>
        s.ColonyRestore is null && s.RaidsOnMe.Count == 0 && s.MyRaids.Count == 0 && s.Visits.Count == 0 && s.Parcels.Count == 0
        && s.LotSales.Count == 0 && s.ShopItemsSold == 0 && s.ShopSilver == 0 && s.Mentions.Count == 0 && s.AdminActions.Count == 0;

    private static void Post(AwaySummary s)
    {
        if (IsEmpty(s) || Current.ProgramState != ProgramState.Playing) return;
        var text = new StringBuilder();

        if (s.ColonyRestore is { Restored: true } restored) Paragraph(text, "Rimlink.Away.Restored".Translate(restored.Name));

        Section(text, "Rimlink.Away.RaidsOnMe", s.RaidsOnMe.Select(r => $"{Who(r.Other)}: {State(r.State)}"), s.RaidsOnMeMore);
        Section(text, "Rimlink.Away.MyRaids", s.MyRaids.Select(r => $"{Who(r.Other)}: {State(r.State)}{Outcome(r)}"), s.MyRaidsMore);
        Section(text, "Rimlink.Away.Visits", s.Visits.Select(v => Who(v.Visitor)), s.VisitsMore);
        Section(text, "Rimlink.Away.Parcels", s.Parcels.Select(Parcel), s.ParcelsMore);
        Section(text, "Rimlink.Away.LotSales", s.LotSales.Select(l => "Rimlink.Away.LotSale".Translate(l.Summary, l.Price, Who(l.Buyer)).ToString()), s.LotSalesMore);
        if (s.ShopItemsSold > 0 || s.ShopSilver > 0) Paragraph(text, "Rimlink.Away.Shop".Translate(s.ShopItemsSold, s.ShopSilver));
        Section(text, "Rimlink.Away.Mentions", s.Mentions.Select(Line), s.MentionsMore);
        Section(text, "Rimlink.Away.Admin", s.AdminActions.Select(a => "Rimlink.Away.AdminLine".Translate(a.Action, a.Actor, a.Details).ToString()), s.AdminActionsMore);

        string label = s.Since is null ? "Rimlink.Away.TitleShort".Translate() : "Rimlink.Away.Title".Translate(Duration(DateTime.UtcNow - s.Since.ToDateTime()));
        Find.LetterStack.ReceiveLetter(label, text.ToString().TrimEnd(), LetterDefOf.NeutralEvent);
    }

    private static void Paragraph(StringBuilder text, string line) => text.AppendLine(line).AppendLine();

    private static void Section(StringBuilder text, string headerKey, IEnumerable<string> lines, int more)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        text.AppendLine(headerKey.Translate().Colorize(ColoredText.SubtleGrayColor));
        foreach (string line in list) text.AppendLine("  - " + line);
        if (more > 0) text.AppendLine("  " + "Rimlink.Away.More".Translate(more));
        text.AppendLine();
    }

    private static string Who(PlayerRef? player) => player is null || player.Name.Length == 0 ? "Rimlink.Away.Someone".Translate().ToString() : player.Name;

    private static string State(string state) => ("Rimlink.Away.RaidState." + state).Translate();

    private static string Outcome(AwayRaid r) => r.Summary is { } o
        ? " (" + "Rimlink.Away.RaidOutcome".Translate(o.Dead, o.Captured, o.Fled, o.LootValue) + ")"
        : "";

    private static string Parcel(AwayParcel p)
    {
        string line = ("Rimlink.Away.Parcel." + p.Kind).Translate();
        if (p.From is not null) line += " " + "Rimlink.Away.From".Translate(p.From.Name);
        if (p.Silver > 0) line += ", " + "Rimlink.Away.Silver".Translate(p.Silver);
        return line;
    }

    private static string Line(AwayLine m)
    {
        string author = m.From?.Name is { Length: > 0 } name ? name : m.Author.Length > 0 ? m.Author : "Rimlink.Away.Someone".Translate().ToString();
        return (m.Direct ? "Rimlink.Away.Direct".Translate(author) : (TaggedString)author) + ": " + m.Text;
    }

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? "Rimlink.Away.Days".Translate((int)span.TotalDays)
        : span.TotalHours >= 1 ? "Rimlink.Away.Hours".Translate((int)span.TotalHours)
        : "Rimlink.Away.Minutes".Translate(Math.Max(1, (int)span.TotalMinutes));
}
