using System.Collections.Immutable;
using Pricer.Book;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;

namespace Pricer.Tests;

public class PnlTests
{
    private const string A = "A";
    private const string B = "B";
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly RunConfig Light = new() { MonteCarlo = new MonteCarloEngineConfig { Paths = 1 << 13, Batches = 16 } };

    private static MarketSnapshot Market(
        DateOnly? asOf = null, double spotA = 100, double spotB = 200, double vol = 0.2, double rate = 0.03, double carry = 0.01) => new(
        asOf ?? Today, YieldCurve.Flat(rate),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(A, new UnderlyingMarket(spotA, YieldCurve.Flat(carry), new FlatVolSurface(vol)))
            .Add(B, new UnderlyingMarket(spotB, YieldCurve.Flat(carry), new FlatVolSurface(vol + 0.05))));

    private static Position Call(string id, double quantity = 10, string underlying = A, double strike = 100) => new(
        id, new EuropeanOption { Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = Today.AddYears(1) }, quantity);

    private static Position Note(string id, Instrument instrument, TradeState? state = null) => new(id, instrument, 1, State: state);

    private static Autocall Autocall(DateOnly? start = null) => new()
    {
        Underlying = A, InitialFixing = 100, Start = start ?? Today, Expiry = Today.AddYears(2),
    };

    private static async Task<(RunResult, RunResult)> Runs(
        IReadOnlyList<Position> first, MarketSnapshot m0, IReadOnlyList<Position> second, MarketSnapshot m1,
        RunConfig? config0 = null, RunConfig? config1 = null)
    {
        var engine = new RunEngine(EngineRegistry.Default(), new MemoryRunCache());
        return (await engine.ExecuteAsync(first, m0, 1, config0 ?? Light, null, default),
            await engine.ExecuteAsync(second, m1, 2, config1 ?? Light, null, default));
    }

    private static async Task<PnlExplain> Explain(
        IReadOnlyList<Position> first, MarketSnapshot m0, IReadOnlyList<Position> second, MarketSnapshot m1,
        PnlConfig? config = null, RunConfig? config0 = null, RunConfig? config1 = null)
    {
        var (a, b) = await Runs(first, m0, second, m1, config0, config1);
        return await new PnlExplainer(EngineRegistry.Default()).ExplainAsync(a, m0, b, m1, config ?? new PnlConfig(), null, default);
    }

    private static PnlRow Total(PnlExplain explain) => PnlAggregator.Row("Book", explain.Trades);

    [Fact]
    public async Task StepsAddUpToTheTotalPnl()
    {
        var book = DemoBook.For(Market(), A);
        var explain = await Explain(book, Market(), book, Market(Today.AddDays(5), spotA: 104, vol: 0.22, rate: 0.032, carry: 0.012));

        var total = Total(explain);
        Assert.All(explain.Trades, t => Assert.Null(t.Error));
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
        Assert.Equal(total.Total, total.Greeks.Sum(), 6);
        Assert.True(Math.Abs(total[PnlFactor.Residual]) < 1e-6, $"residual {total[PnlFactor.Residual]}");
        Assert.True(Math.Abs(total[PnlFactor.Spot]) > 1);
    }

    [Fact]
    public async Task UnchangedMarketOnTheSameDayExplainsNothing()
    {
        var book = DemoBook.For(Market(), A);
        var explain = await Explain(book, Market(), book, Market());
        Assert.All(Total(explain).Waterfall, amount => Assert.Equal(0, amount, 8));
        Assert.Equal(0, Total(explain).Total, 8);
    }

    [Fact]
    public async Task MarketThatDeliversItsForwardsLeavesOnlyTime()
    {
        Position[] book = [Call("c")];
        var m0 = Market();
        var rolled = PnlExplainer.Roll(m0, Today.AddDays(10), PnlTimeRoll.Forward);
        var explain = await Explain(book, m0, book, rolled);

        var total = Total(explain);
        Assert.Equal(total.Total, total[PnlFactor.Time], 8);
        foreach (var factor in new[] { PnlFactor.Spot, PnlFactor.Dividends, PnlFactor.VolLevel, PnlFactor.VolShape, PnlFactor.Rates })
        {
            Assert.Equal(0, total[factor], 8);
        }
    }

    [Fact]
    public async Task ConstantSpotRollMatchesTheBumpedTheta()
    {
        Position[] book = [Call("c")];
        var (run, _) = await Runs(book, Market(), book, Market());
        double theta = run.Trades[0].Result!.Greek(Measure.Theta)!.Value;

        var explain = await Explain(book, Market(), book, Market(Today.AddDays(1)), new PnlConfig { Roll = PnlTimeRoll.ConstantSpot });
        Assert.Equal(theta, Total(explain)[PnlFactor.Time], 8);
        Assert.Equal(0, Total(explain)[PnlFactor.Spot], 8);
    }

    [Fact]
    public void RollKeepsForwardsAtFixedExpiries()
    {
        var m0 = Market() with { DiscountCurve = new YieldCurve([0.5, 1, 2], [0.02, 0.03, 0.04]) };
        var rolled = PnlExplainer.Roll(m0, Today.AddDays(73), PnlTimeRoll.Forward);
        const double dt = 0.2;
        double expected = m0.DiscountCurve.Discount(1 + dt) / m0.DiscountCurve.Discount(dt);
        Assert.Equal(expected, rolled.DiscountCurve.Discount(1), 10);

        double forward0 = m0.Underlying(A).Spot * m0.Underlying(A).DividendCurve.Discount(1.2) / m0.DiscountCurve.Discount(1.2);
        double forward1 = rolled.Underlying(A).Spot * rolled.Underlying(A).DividendCurve.Discount(1) / rolled.DiscountCurve.Discount(1);
        Assert.Equal(forward0, forward1, 8);
    }

    [Fact]
    public async Task EachMarketMoveLandsOnItsOwnFactor()
    {
        Position[] book = [Call("c")];
        var spot = Total(await Explain(book, Market(), book, Market(spotA: 105)));
        Assert.Equal(spot.Total, spot[PnlFactor.Spot], 8);

        var vol = Total(await Explain(book, Market(), book, Market(vol: 0.25)));
        Assert.Equal(vol.Total, vol[PnlFactor.VolLevel], 8);
        Assert.Equal(0, vol[PnlFactor.VolShape], 8);

        var rate = Total(await Explain(book, Market(), book, Market(rate: 0.04)));
        Assert.Equal(rate.Total, rate[PnlFactor.Rates], 8);

        var carry = Total(await Explain(book, Market(), book, Market(carry: 0.02)));
        Assert.Equal(carry.Total, carry[PnlFactor.Dividends], 8);
    }

    [Fact]
    public async Task OrderChangesTheAttributionNotTheTotal()
    {
        Position[] book = [Call("c")];
        var m1 = Market(spotA: 115, vol: 0.3);
        var spotFirst = Total(await Explain(book, Market(), book, m1));
        var volFirst = Total(await Explain(book, Market(), book, m1, new PnlConfig { Ordering = PnlOrdering.VolSpotRates }));

        Assert.Equal(spotFirst.Total, volFirst.Total, 8);
        Assert.True(Math.Abs(spotFirst[PnlFactor.Spot] - volFirst[PnlFactor.Spot]) > 0.05);
    }

    [Fact]
    public async Task MonteCarloNoiseStaysOutOfTheFactors()
    {
        Position[] book = [Note("n", Autocall())];
        var explain = await Explain(book, Market(), book, Market(spotA: 101));
        var total = Total(explain);
        Assert.Equal(0, total[PnlFactor.Dividends], 10);
        Assert.Equal(0, total[PnlFactor.VolLevel], 10);
        Assert.Equal(0, total[PnlFactor.Rates], 10);
    }

    [Fact]
    public async Task GreekMethodExplainsSmallMovesAndMissesLargeOnes()
    {
        Position[] book = [Call("c")];
        var small = Total(await Explain(book, Market(), book, Market(spotA: 100.5, vol: 0.202)));
        var large = Total(await Explain(book, Market(), book, Market(spotA: 130, vol: 0.32)));

        Assert.True(Math.Abs(small.Greeks[(int)PnlFactor.Residual]) < 0.01 * Math.Abs(small.Total), $"small {small.Greeks[(int)PnlFactor.Residual]} of {small.Total}");
        Assert.True(Math.Abs(large.Greeks[(int)PnlFactor.Residual]) > 0.01 * Math.Abs(large.Total), $"large {large.Greeks[(int)PnlFactor.Residual]} of {large.Total}");
    }

    [Fact]
    public async Task CallBetweenTheDatesIsAnEventAndItsCashCounts()
    {
        // The barrier is above the forward at the fixing: only the actual fixing calls the note, so this is a surprise.
        var note = Autocall(Today.AddMonths(-3)) with { CallBarrier = 1.05 };
        var firstDate = note.Observations()[1];
        var before = Note("n", note, new TradeState { Fixings = [new FixingRecord(A, note.Observations()[0], 90)] });
        var after = before with
        {
            State = new TradeState
            {
                Fixings = [new FixingRecord(A, note.Observations()[0], 90), new FixingRecord(A, firstDate, 110)],
            },
        };

        var explain = await Explain([before], Market(), [after], Market(firstDate.AddDays(1), spotA: 110));
        var total = Total(explain);
        double cash = note.Notional * (1 + 2 * note.Coupon);

        Assert.Null(explain.Trades[0].Error);
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
        Assert.True(Math.Abs(total[PnlFactor.Events]) > 0.5, $"events {total[PnlFactor.Events]}");
        // Called: nothing is left for the market to move.
        Assert.Equal(0, total[PnlFactor.Spot], 8);

        var (start, _) = await Runs([before], Market(), [after], Market(firstDate.AddDays(1), spotA: 110));
        Assert.Equal(cash - start.Trades[0].Result!.Pv, total.Total, 6);
    }

    [Fact]
    public async Task BarrierBreachIsAnEvent()
    {
        var option = new Position("b", new BarrierOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = Today.AddYears(1), Barrier = 80, Kind = BarrierKind.DownOut,
        }, 1);
        var breached = option with { State = new TradeState { BarrierBreached = true } };
        var total = Total(await Explain([option], Market(), [breached], Market(Today.AddDays(1))));
        Assert.True(total[PnlFactor.Events] < -1);
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
    }

    [Fact]
    public async Task NewAndClosedTradesAreComposition()
    {
        var kept = Call("kept");
        var closed = Call("closed", strike: 95);
        var opened = Call("opened", strike: 110);
        var explain = await Explain([kept, closed], Market(), [kept, opened], Market());

        TradeExplain Of(string id) => explain.Trades.First(t => t.Id == id);
        Assert.True(Of("closed").Total < 0);
        Assert.Equal(Of("closed").Total, Of("closed").Waterfall[(int)PnlFactor.Composition]);
        Assert.True(Of("opened").Total > 0);
        Assert.Equal(Of("opened").Total, Of("opened").Waterfall[(int)PnlFactor.Composition]);
        Assert.Equal(0, Of("kept").Total, 8);
    }

    [Fact]
    public async Task QuantityChangeIsCompositionAndModifiedTermsAreCloseAndNew()
    {
        var position = Call("c", quantity: 10);
        var more = Total(await Explain([position], Market(), [position with { Quantity = 15 }], Market(spotA: 102)));
        Assert.Equal(more.Total, more.Waterfall.Sum(), 6);
        Assert.True(more[PnlFactor.Composition] > 0);
        Assert.True(Math.Abs(more[PnlFactor.Residual]) < 1e-6);

        var changed = Call("c", strike: 120);
        var explain = await Explain([position], Market(), [changed], Market());
        Assert.Equal(0, Total(explain)[PnlFactor.Spot]);
        Assert.Equal(Total(explain).Total, Total(explain)[PnlFactor.Composition], 8);
    }

    [Fact]
    public async Task WorstOfExplainsEachLegThroughItsOwnSpot()
    {
        var worst = new WorstOfPhoenix
        {
            Underlying = A, InitialFixing = 100, Start = Today, Expiry = Today.AddYears(1),
            Basket = [new BasketLeg(A, 100), new BasketLeg(B, 200)],
        };
        Position[] book = [Note("w", worst)];
        var total = Total(await Explain(book, Market(), book, Market(spotA: 90, spotB: 190)));
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
        Assert.True(total[PnlFactor.Spot] < 0);
        Assert.True(Math.Abs(total[PnlFactor.Residual]) < 1e-6);
    }

    [Fact]
    public async Task HestonFollowsTheSurfaceOnlyFromTheVolSteps()
    {
        Position[] book = [Call("h") with { Model = ModelChoice.Heston }];
        var m1 = Market(spotA: 102, vol: 0.23);
        var total = Total(await Explain(book, Market(), book, m1));
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
        Assert.True(Math.Abs(total[PnlFactor.Spot]) > 0.5);
        Assert.True(total[PnlFactor.VolLevel] + total[PnlFactor.VolShape] > 0.5);
        Assert.True(Math.Abs(total[PnlFactor.Residual]) < 0.2, $"residual {total[PnlFactor.Residual]}");
    }

    [Fact]
    public async Task ScenarioRunsExplainOnTheSameSnapshot()
    {
        Position[] book = [Call("c")];
        var explain = await Explain(book, Market(), book, Market(), config1: Light with { SpotShock = 0.05 });
        var total = Total(explain);
        Assert.Equal(total.Total, total[PnlFactor.Spot], 8);
        Assert.Empty(explain.Warnings);
    }

    [Fact]
    public async Task DifferentConfigsAreFlaggedAndGoToTheResidual()
    {
        Position[] book = [Note("n", Autocall())];
        var explain = await Explain(book, Market(), book, Market(),
            config1: Light with { MonteCarlo = Light.MonteCarlo with { Seed = 7 } });
        Assert.Single(explain.Warnings);
        var total = Total(explain);
        Assert.Equal(total.Total, total[PnlFactor.Residual], 8);
    }

    [Fact]
    public async Task FailedTradeIsReportedAndLeftOutOfTheTotals()
    {
        Position[] first = [Call("good"), new Position("bad", new Stock { Underlying = "NOPE" }, 1)];
        var explain = await Explain(first, Market(), first, Market(spotA: 101));
        var total = Total(explain);
        Assert.Equal(1, total.Failed);
        Assert.Single(explain.Warnings);
        Assert.NotNull(explain.Trades.First(t => t.Id == "bad").Error);
        Assert.Equal(total.Total, total.Waterfall.Sum(), 6);
    }

    [Fact]
    public async Task EndBeforeStartIsRefused()
    {
        var (a, b) = await Runs([Call("c")], Market(Today.AddDays(1)), [Call("c")], Market());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PnlExplainer(EngineRegistry.Default()).ExplainAsync(a, Market(Today.AddDays(1)), b, Market(), new PnlConfig(), null, default));
    }

    [Fact]
    public async Task GroupsAddUpToTheBook()
    {
        var book = DemoBook.For(Market(), A);
        var explain = await Explain(book, Market(), book, Market(Today.AddDays(2), spotA: 103, spotB: 195));
        var total = Total(explain);
        var byProduct = PnlAggregator.Group(explain.Trades, BookGrouping.Product);
        Assert.Equal(total.Total, byProduct.Sum(r => r.Total), 6);
        Assert.Equal(total[PnlFactor.Spot], byProduct.Sum(r => r[PnlFactor.Spot]), 6);
    }
}
