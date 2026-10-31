using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Tests;

public class PricingTests
{
    private const string Underlying = SyntheticMarketDataProvider.DefaultUnderlying;
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];
    private readonly RiskCalculator _risk = new(EngineRegistry.Default());

    private static PricingRequest Request(Instrument instrument, double quantity = 1.0) => new(
        new Trade("T1", instrument, quantity),
        new SyntheticMarketDataProvider().GetSnapshot(Today),
        new BlackScholesConfig(), new AnalyticEngineConfig(), Today, AllMeasures);

    private static EuropeanOption Option(OptionRight right, double strike = 105, int days = 365) => new()
    {
        Underlying = Underlying, Right = right, Strike = strike, Expiry = Today.AddDays(days),
    };

    [Theory]
    [InlineData(100, 100, 1.0, 0.20, 0.05, 0.0, true, 10.4506)]
    [InlineData(100, 100, 1.0, 0.20, 0.05, 0.0, false, 5.5735)]
    [InlineData(42, 40, 0.5, 0.20, 0.10, 0.0, true, 4.7594)]
    [InlineData(42, 40, 0.5, 0.20, 0.10, 0.0, false, 0.8086)]
    public void BlackScholesMatchesReferenceValues(
        double s, double k, double t, double vol, double r, double q, bool isCall, double expected)
    {
        Assert.Equal(expected, BlackScholes.Evaluate(new(s, k, t, vol, r, q), isCall).Price, 3);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(130)]
    public void PutCallParityHolds(double strike)
    {
        var inputs = new BlackScholesInputs(100, strike, 1.3, 0.25, 0.03, 0.02);
        double call = BlackScholes.Evaluate(inputs, true).Price;
        double put = BlackScholes.Evaluate(inputs, false).Price;
        double forwardValue = 100 * Math.Exp(-0.02 * 1.3) - strike * Math.Exp(-0.03 * 1.3);
        Assert.Equal(forwardValue, call - put, 10);
    }

    [Theory]
    [InlineData(OptionRight.Call, 105)]
    [InlineData(OptionRight.Put, 105)]
    [InlineData(OptionRight.Call, 80)]
    [InlineData(OptionRight.Put, 130)]
    public void ClosedFormGreeksAgreeWithBumpOnFlatSurface(OptionRight right, double strike)
    {
        var request = Request(Option(right, strike), quantity: 3);
        var closed = _risk.Compute(request, new RiskConfig());
        var bumped = _risk.Compute(request, new RiskConfig { Preference = GreekPreference.ForceBump });

        Assert.Equal(closed.Pv, bumped.Pv);
        foreach (var measure in AllMeasures)
        {
            var c = closed.Greek(measure)!;
            var b = bumped.Greek(measure)!;
            Assert.Equal(GreekMethod.ClosedForm, c.Method);
            Assert.Equal(GreekMethod.BumpReprice, b.Method);
            // Theta bump is a one-day finite difference, the closed form an instantaneous rate.
            double tolerance = measure == Measure.Theta ? 2e-3 : 1e-3;
            Assert.True(Math.Abs(c.Value - b.Value) < tolerance, $"{measure}: {c.Value} vs {b.Value}");
        }
    }

    [Fact]
    public void ForwardGreeksFallBackToBumpWithExpectedDelta()
    {
        var forward = new EquityForward { Underlying = Underlying, Strike = 100, Expiry = Today.AddDays(365) };
        var result = _risk.Compute(Request(forward), new RiskConfig());

        var delta = result.Greek(Measure.Delta)!;
        Assert.Equal(GreekMethod.BumpReprice, delta.Method);
        Assert.Equal(Math.Exp(-0.02), delta.Value, 6);
        Assert.Equal(0, result.Greek(Measure.Gamma)!.Value, 5);
        Assert.Equal(0, result.Greek(Measure.Vega)!.Value, 10);
    }

    [Fact]
    public void StockPvIsSpotTimesQuantity()
    {
        var stock = new Stock { Underlying = Underlying };
        Assert.Equal(1000, _risk.Compute(Request(stock, 10), new RiskConfig()).Pv, 10);
    }

    [Fact]
    public void OptionAtExpiryIsWorthIntrinsicAndAfterExpiryNothing()
    {
        Assert.Equal(15.0, _risk.Compute(Request(Option(OptionRight.Call, 85, 0)), new RiskConfig()).Pv);
        var expired = _risk.Compute(Request(Option(OptionRight.Call, 85, -1)), new RiskConfig());
        Assert.Equal(0.0, expired.Pv);
        Assert.All(expired.Greeks, g => Assert.Equal(0.0, g.Value));
    }

    [Fact]
    public void ZeroVolOptionIsDiscountedForwardIntrinsic()
    {
        var s = BlackScholes.Evaluate(new(100, 90, 1, 0, 0.03, 0.02), true);
        Assert.Equal(100 * Math.Exp(-0.02) - 90 * Math.Exp(-0.03), s.Price, 10);
        Assert.Equal(0.0, BlackScholes.Evaluate(new(100, 150, 1, 0, 0.03, 0.02), true).Price);
    }

    [Fact]
    public void BumpsLeaveOriginalSnapshotUntouched()
    {
        var snapshot = new SyntheticMarketDataProvider().GetSnapshot(Today);
        var bumped = new SpotBump(Underlying, 0.1).Apply(snapshot);
        Assert.Equal(100.0, snapshot.Underlying(Underlying).Spot);
        Assert.Equal(110.0, bumped.Underlying(Underlying).Spot, 10);
    }

    [Fact]
    public void HashIsStableAndSensitiveToInputs()
    {
        var a = Request(Option(OptionRight.Call));
        var b = Request(Option(OptionRight.Call));
        var c = Request(Option(OptionRight.Call, strike: 106));
        Assert.Equal(ContentHash.Of(a), ContentHash.Of(b));
        Assert.NotEqual(ContentHash.Of(a), ContentHash.Of(c));
    }

    [Fact]
    public void SameRequestGivesIdenticalResult()
    {
        var request = Request(Option(OptionRight.Put));
        var first = _risk.Compute(request, new RiskConfig());
        var second = _risk.Compute(request, new RiskConfig());
        Assert.Equal(first.Pv, second.Pv);
        Assert.Equal(first.Greeks.Select(g => g.Value), second.Greeks.Select(g => g.Value));
    }

    [Fact]
    public void ParameterSchemaExposesAndCopiesRecords()
    {
        var option = Option(OptionRight.Call);
        var names = ParameterSchema.Of(typeof(EuropeanOption)).Select(d => d.Name).ToArray();
        Assert.Equal(new[] { "Right", "Strike", "Expiry" }, names);

        var changed = ParameterSchema.With(option, nameof(EuropeanOption.Strike), 120.0);
        Assert.Equal(105.0, option.Strike);
        Assert.Equal(120.0, changed.Strike);

        var diff = Assert.Single(ParameterSchema.Diff(option, changed));
        Assert.Equal("Strike", diff.Parameter.Name);
    }
}
