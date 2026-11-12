using System.Collections.Immutable;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Book;

public enum FixingSource
{
    /// <summary>The underlying's daily close on that date, or on the last trading day before it.</summary>
    Close,

    /// <summary>No close is known: geometric interpolation between the two nearest known spots.</summary>
    Path,
}

public sealed record FixingUsed(string Underlying, DateOnly Date, double Level, FixingSource Source);

/// <param name="Position">The trade as it stands at the end date: its fixings and barrier breach between the dates.</param>
public sealed record SettledPosition(Position Position, ImmutableArray<FixingUsed> Fixings, bool BarrierBreached);

/// <summary>What happened to a trade between two market dates. Up to the last capture the daily closes tell; past it,
/// over an optional horizon, the spot moves geometrically from the captured level to the scenario's.</summary>
public static class ScenarioFixings
{
    /// <param name="start">The market of the first run.</param>
    /// <param name="captured">The second capture, before any scenario: its date ends the closes.</param>
    /// <param name="end">The market of the second run, scenario included; its date may lie after the capture.</param>
    public static SettledPosition Settle(
        Position position, MarketSnapshot start, MarketSnapshot captured, MarketSnapshot end, PriceHistory history)
    {
        var instrument = position.Instrument;
        var state = position.State ?? new TradeState();
        double Level(string name, DateOnly date, out FixingSource source) =>
            LevelOn(name, date, start, captured, end, history, out source);

        var used = ImmutableArray.CreateBuilder<FixingUsed>();
        if (instrument is StructuredNote or AsianOption)
        {
            var known = state.Fixings.Select(f => (f.Underlying, f.Date)).ToHashSet();
            var dates = PayoffCompiler.Compile(instrument, start.AsOf, 0, 0).Events.Select(e => e.Date).Distinct().Order();
            // The convention of PnlExplainer.Expected: a fixing on the end date itself is still the spot.
            foreach (var date in dates.Where(d => d >= start.AsOf && d < end.AsOf))
            {
                foreach (string name in instrument.Underlyings.Where(n => !known.Contains((n, date))))
                {
                    used.Add(new FixingUsed(name, date, Level(name, date, out var source), source));
                }
            }
        }

        bool breached = state.BarrierBreached;
        if (instrument is BarrierOption barrier && !breached)
        {
            string name = barrier.Underlying;
            var levels = history.Between(name, start.AsOf, captured.AsOf).Select(c => c.Close)
                .Append(captured.Underlying(name).Spot).Append(end.Underlying(name).Spot);
            breached = levels.Any(barrier.IsBreachedAt);
        }

        var fixings = used.ToImmutable();
        var settled = position with
        {
            State = state with
            {
                Fixings = [.. state.Fixings, .. fixings.Select(f => new FixingRecord(f.Underlying, f.Date, f.Level))],
                BarrierBreached = breached,
            },
        };
        return new SettledPosition(settled, fixings, breached);
    }

    private static double LevelOn(
        string name, DateOnly date, MarketSnapshot start, MarketSnapshot captured, MarketSnapshot end, PriceHistory history,
        out FixingSource source)
    {
        if (date <= captured.AsOf && history.Last(name) is { } last && date <= last && history.On(name, date) is { } close)
        {
            source = FixingSource.Close;
            return close.Close;
        }

        source = FixingSource.Path;
        double startSpot = start.Underlying(name).Spot;
        double capturedSpot = captured.Underlying(name).Spot;
        double endSpot = end.Underlying(name).Spot;
        return date <= captured.AsOf
            ? Geometric(start.AsOf, startSpot, captured.AsOf, capturedSpot, date)
            : Geometric(captured.AsOf, capturedSpot, end.AsOf, endSpot, date);
    }

    private static double Geometric(DateOnly from, double a, DateOnly to, double b, DateOnly date)
    {
        int span = to.DayNumber - from.DayNumber;
        if (span <= 0) return b;
        double weight = (double)(date.DayNumber - from.DayNumber) / span;
        return a * Math.Pow(b / a, weight);
    }
}
