using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Book;
using Pricer.Data;
using Pricer.Data.Yahoo;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Presentation;

namespace Pricer.Tests;

/// <summary>The PnL explain between two captures: daily closes, fixings and barriers between the dates, the forced
/// engine of a position, and the web page's view model end to end.</summary>
public class PnlScenarioTests
{
    private const string A = "A";
    private static readonly DateOnly Today = new(2026, 1, 2);

    private static MarketSnapshot Market(DateOnly asOf, double spot = 100) => new(
        asOf, YieldCurve.Flat(0.03),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(A, new UnderlyingMarket(spot, YieldCurve.Flat(0.01), new FlatVolSurface(0.2))));

    private static PriceHistory History(params (DateOnly Date, double Close)[] closes) => new(
        ImmutableSortedDictionary<string, ImmutableArray<DailyClose>>.Empty.Add(A, [.. closes.Select(c => new DailyClose(c.Date, c.Close))]));

    [Fact]
    public void YahooClosesAreDatedInTheExchangeTimeZoneAndSkipMissingDays()
    {
        // 2026-01-02 14:30 UTC and 2026-01-05 14:30 UTC, New York at UTC-5; the middle bar has no close.
        const string json = """
            {"chart":{"result":[{"meta":{"gmtoffset":-18000},
              "timestamp":[1767364200,1767450600,1767623400],
              "indicators":{"quote":[{"close":[101.5,null,103.25]}]}}]}}
            """;
        using var document = JsonDocument.Parse(json);
        var closes = YahooOptionChainSource.ParseCloses(document.RootElement);

        Assert.Equal([new DailyClose(Today, 101.5), new DailyClose(new DateOnly(2026, 1, 5), 103.25)], closes.ToArray());
    }

    [Fact]
    public void IntegerFieldsCommitIntegers()
    {
        var form = new ParameterForm("Engine", new MonteCarloEngineConfig());
        form.Fields.OfType<NumberField>().Single(f => f.Label == "Paths").Value = 5000.4;
        Assert.Equal(5000, ((MonteCarloEngineConfig)form.Record).Paths);
    }

    [Fact]
    public void HistoryGivesTheLastCloseOnOrBeforeADate()
    {
        var history = History((Today, 100), (Today.AddDays(3), 104));
        Assert.Equal(100, history.On(A, Today.AddDays(2))!.Close);
        Assert.Equal(104, history.On(A, Today.AddDays(10))!.Close);
        Assert.Null(history.On(A, Today.AddDays(-1)));
        Assert.Null(history.On("B", Today));
    }

    [Fact]
    public void FixingsComeFromClosesThenFromThePathToTheScenarioSpot()
    {
        var note = new Autocall { Underlying = A, InitialFixing = 100, Start = Today, Expiry = Today.AddYears(2), PeriodMonths = 1 };
        var observations = note.Observations();
        var captured = Market(observations[0].AddDays(5), spot: 96);
        var end = Market(observations[1].AddDays(10), spot: 120);
        // The first observation falls on a day without a close: the close before it stands.
        var history = History((Today, 100), (observations[0].AddDays(-1), 95), (captured.AsOf, 96));

        var settled = ScenarioFixings.Settle(new Position("n", note, 1), Market(Today), captured, end, history);

        var first = settled.Fixings.Single(f => f.Date == observations[0]);
        Assert.Equal((95.0, FixingSource.Close), (first.Level, first.Source));
        var second = settled.Fixings.Single(f => f.Date == observations[1]);
        Assert.Equal(FixingSource.Path, second.Source);
        Assert.InRange(second.Level, 96, 120);
        Assert.Equal(settled.Fixings.Length, settled.Position.State!.Fixings.Length);
    }

    [Fact]
    public void BarrierBreachedByACloseBetweenTheDatesStaysBreached()
    {
        var option = new BarrierOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = Today.AddYears(1), Barrier = 85, Kind = BarrierKind.DownOut,
        };
        var captured = Market(Today.AddDays(10), spot: 101);
        var through = History((Today, 100), (Today.AddDays(4), 84), (captured.AsOf, 101));
        var above = History((Today, 100), (Today.AddDays(4), 90), (captured.AsOf, 101));

        Assert.True(ScenarioFixings.Settle(new Position("b", option, 1), Market(Today), captured, captured, through).BarrierBreached);
        Assert.False(ScenarioFixings.Settle(new Position("b", option, 1), Market(Today), captured, captured, above).BarrierBreached);
    }

    [Fact]
    public async Task PositionEngineOverridesTheMostExactOneAndIsLeftOutWhenAbsent()
    {
        var option = new EuropeanOption { Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = Today.AddYears(1) };
        var engine = new MonteCarloEngineConfig { Paths = 1 << 12, Batches = 8 };
        var runs = new RunEngine(EngineRegistry.Default(), new MemoryRunCache());
        var run = await runs.ExecuteAsync(
            [new Position("mc", option, 1, Engine: engine), new Position("best", option, 1)], Market(Today), 1, new RunConfig(), null, default);

        Assert.Equal(engine, run.Trades[0].Engine);
        Assert.IsType<AnalyticEngineConfig>(run.Trades[1].Engine);
        Assert.DoesNotContain("Engine", JsonSerializer.Serialize(new Position("best", option, 1)));
        var read = JsonSerializer.Deserialize<Position>(JsonSerializer.Serialize(new Position("mc", option, 1, Engine: engine)));
        Assert.Equal(engine, read!.Engine);
    }

    private static async Task<PnlExplainViewModel> Page(int laterDays = 14)
    {
        var first = await WebExport.SyntheticAsync(Today, default);
        var second = await WebExport.SyntheticAsync(Today.AddDays(laterDays), default);
        var captures = new Dictionary<string, StoredCapture> { ["a.json"] = first, ["b.json"] = second };
        CaptureIndexEntry Entry(string file, StoredCapture c) =>
            new(file, file, c.Snapshot.AsOf, "Test", [.. c.Snapshot.Underlyings.Keys]);

        var page = new PnlExplainViewModel(new PricerOptions { MonteCarlo = new MonteCarloEngineConfig { Paths = 1 << 12, Batches = 8 } });
        await page.UseAsync([Entry("a.json", first), Entry("b.json", second)], e => Task.FromResult(captures[e.File]), PriceHistory.Empty,
            SyntheticMarketDataProvider.DefaultUnderlying);
        return page;
    }

    private static void Set(ParameterForm form, string label, double value) =>
        form.Fields.OfType<NumberField>().Single(f => f.Label.StartsWith(label, StringComparison.Ordinal)).Value = value;

    private static double Factor(PnlExplainViewModel page, string name) => page.Factors.Single(f => f.Factor == name).Waterfall;

    [Fact]
    public async Task SameCaptureWithoutScenarioExplainsNothing()
    {
        var page = await Page();
        page.To = page.From;
        await page.ExplainCommand.ExecuteAsync(null);

        Assert.NotNull(page.Waterfall);
        Assert.Equal(0, page.Waterfall!.Total, 10);
        Assert.All(page.Factors, f => Assert.Equal(0, f.Waterfall, 10));
    }

    [Fact]
    public async Task SpotShockAloneLandsOnTheSpotFactor()
    {
        var page = await Page();
        page.To = page.From;
        Set(page.ScenarioForm, "Spot shock", 0.05);
        await page.ExplainCommand.ExecuteAsync(null);

        var row = page.Waterfall!;
        Assert.True(row.Total > 0);
        Assert.Equal(row.Total, Factor(page, "Spot"), 8);
        Assert.Contains(page.Moves, m => m.SpotTo > m.SpotFrom);
    }

    /// <summary>The time step expects the observation at the forward, above the call barrier: a rally confirms the
    /// call, so its cash is no surprise; a fall cancels it, and the missed call is the event.</summary>
    [Theory]
    [InlineData(0.1, true)]
    [InlineData(-0.1, false)]
    public async Task AutocallObservedOverTheHorizonSettlesOnThePath(double shock, bool called)
    {
        var page = await Page();
        page.ProductKind = ProductKind.Autocall;
        Set(page.ScenarioForm, "Extra days", 100);
        Set(page.ScenarioForm, "Spot shock", shock);
        await page.ExplainCommand.ExecuteAsync(null);

        Assert.True(page.Waterfall is not null, page.Status);
        Assert.Contains(page.Fixings, f => f.Source == "Interpolated path");
        Assert.Equal(called, page.Summary.Contains("cash received"));
        Assert.Equal(called, Factor(page, "Events") == 0);
        Assert.Equal(page.Waterfall!.Total, page.Factors.Sum(f => f.Waterfall), 6);
    }
}
