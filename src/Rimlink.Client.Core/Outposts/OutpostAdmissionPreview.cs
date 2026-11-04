using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

public sealed record WorkerProductionPreview(int Plants, int Mining, float Consciousness, float Manipulation, float Moving);
public sealed record OutpostAdmissionPreview(double UnitsPerHour, int Capacity, long Cost, long WalletAfter);
public static class OutpostAdmissionPreviews
{
    public static OutpostAdmissionPreview Calculate(OutpostProfile profile, IReadOnlyList<WorkerProductionPreview> workers, bool founding, long wallet)
    {
        if (profile is not (OutpostProfile.Wood or OutpostProfile.Steel) || workers.Count > 4) throw new ArgumentException("invalid production preview");
        double units = 0;
        foreach (var worker in workers)
        {
            if (!float.IsFinite(worker.Consciousness) || !float.IsFinite(worker.Manipulation) || !float.IsFinite(worker.Moving))
                throw new ArgumentException("invalid worker capacity");
            double health = Math.Clamp(Math.Min(worker.Consciousness, Math.Min(worker.Manipulation, worker.Moving)), 0, 1);
            if (health == 0) throw new ArgumentException("worker capacity must be positive");
            int skill = Math.Clamp(profile == OutpostProfile.Wood ? worker.Plants : worker.Mining, 0, 20);
            units += (profile == OutpostProfile.Wood ? 20 : 10) * (0.5 + 0.05 * skill) * health;
        }
        long cost = founding ? 500 : 0;
        return new(units, 1000, cost, wallet < long.MinValue + cost ? long.MinValue : wallet - cost);
    }
}
