namespace Pricer.Data;

/// <summary>The part of a chain worth capturing, shared by the live sources.</summary>
public static class ChainFilter
{
    private const int MinDaysToExpiry = 7;
    private static readonly int[] TargetDays = [14, 30, 60, 90, 180, 270, 365, 540, 730];

    /// <summary>The listed expiry nearest to each target tenor.</summary>
    public static IReadOnlyList<DateOnly> Expiries(IEnumerable<DateOnly> listed, DateOnly asOf)
    {
        var usable = listed.Where(d => d.DayNumber - asOf.DayNumber >= MinDaysToExpiry).ToArray();
        if (usable.Length == 0) return [];
        return [.. TargetDays
            .Select(days => usable.OrderBy(d => Math.Abs(d.DayNumber - asOf.DayNumber - days)).First())
            .Distinct()];
    }

    public static bool IsNearSpot(double strike, double spot, double t) =>
        Math.Abs(strike / spot - 1) <= 0.1 + 0.25 * Math.Sqrt(t);
}
