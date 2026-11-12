using System.Collections.Immutable;
using Pricer.Market;

namespace Pricer.Models;

/// <summary>A captured market with the calibration report of each underlying.</summary>
public sealed record StoredCapture(MarketSnapshot Snapshot, ImmutableArray<CalibrationReport> Reports);

/// <summary>One line of a static capture catalogue, as shipped with the web demo.</summary>
public sealed record CaptureIndexEntry(string File, string Label, DateOnly AsOf, string Source, ImmutableArray<string> Underlyings);

public sealed record DailyClose(DateOnly Date, double Close);

/// <summary>Daily closes of each underlying between capture dates: the fixings and barrier checks of the days
/// between two snapshots, which no snapshot holds.</summary>
public sealed record PriceHistory(ImmutableSortedDictionary<string, ImmutableArray<DailyClose>> Closes)
{
    public static PriceHistory Empty { get; } = new(ImmutableSortedDictionary<string, ImmutableArray<DailyClose>>.Empty);

    /// <summary>The close of that day, or of the last trading day before it; null outside the history.</summary>
    public DailyClose? On(string underlying, DateOnly date)
    {
        if (!Closes.TryGetValue(underlying, out var closes) || closes.IsDefaultOrEmpty || date < closes[0].Date) return null;
        DailyClose? found = null;
        foreach (var close in closes)
        {
            if (close.Date > date) break;
            found = close;
        }
        return found;
    }

    /// <summary>The closes after <paramref name="from"/> up to <paramref name="to"/>, both by date.</summary>
    public IEnumerable<DailyClose> Between(string underlying, DateOnly from, DateOnly to) =>
        Closes.TryGetValue(underlying, out var closes) ? closes.Where(c => c.Date > from && c.Date <= to) : [];

    public DateOnly? Last(string underlying) =>
        Closes.TryGetValue(underlying, out var closes) && !closes.IsDefaultOrEmpty ? closes[^1].Date : null;
}
