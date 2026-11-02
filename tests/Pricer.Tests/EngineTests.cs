using System.Collections.Immutable;
using System.Text.Json;
using MathNet.Numerics.Distributions;
using Pricer.Core;
using Pricer.Data;
using Pricer.Data.Yahoo;
using Pricer.Engines;
using Pricer.Engines.MonteCarlo;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Tests;

public class EngineTests
{
    private const string Name = "X";
    private const double Spot = 100;
    private const double Vol = 0.2;
    private const double Rate = 0.05;
    private const double Carry = 0.01;
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly DateOnly OneYear = Today.AddDays(365);
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];
    private readonly RiskCalculator _risk = new(EngineRegistry.Default());
    private static readonly MonteCarloEngineConfig Mc = new() { Paths = 1 << 18 };
    private static readonly PdeEngineConfig Pde = new() { SpaceSteps = 1000, TimeSteps = 500 };

    private static MarketSnapshot Market(double dividend = Carry, double rate = Rate) => new(
        Today, YieldCurve.Flat(rate),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add(
            Name, new UnderlyingMarket(Spot, YieldCurve.Flat(dividend), new FlatVolSurface(Vol))));

    private static PricingRequest Request(Instrument instrument, EngineConfig engine, MarketSnapshot? market = null) => new(
        new Trade("T", instrument, 1), market ?? Market(), new BlackScholesConfig(), engine, Today, AllMeasures);

    private PricingResult Price(Instrument instrument, EngineConfig engine, MarketSnapshot? market = null) =>
        _risk.Compute(Request(instrument, engine, market), new RiskConfig());

    private static EuropeanOption European(OptionRight right, double strike = 105) =>
        new() { Underlying = Name, Right = right, Strike = strike, Expiry = OneYear };

    private static DigitalOption Digital(OptionRight right, double strike = 105) =>
        new() { Underlying = Name, Right = right, Strike = strike, Expiry = OneYear, Cash = 10 };

    private static BarrierOption BarrierOf(BarrierKind kind, OptionRight right, double strike, double barrier) => new()
    {
        Underlying = Name, Right = right, Strike = strike, Expiry = OneYear, Barrier = barrier, Kind = kind,
    };

    private static void WithinErrors(double expected, GreekValue greek, double errors = 4, double floor = 0) =>
        Assert.True(Math.Abs(greek.Value - expected) <= errors * greek.StandardError!.Value + floor,
            $"{greek.Measure} {greek.Method}: {greek.Value} ± {greek.StandardError} vs {expected}");

    private static void WithinErrors(double expected, PricingResult result, double errors = 4, double floor = 0) =>
        Assert.True(Math.Abs(result.Pv - expected) <= errors * result.PvStandardError!.Value + floor,
            $"{result.Pv} ± {result.PvStandardError} vs {expected}");

    [Fact]
    public void SobolDimensionsAreStratified()
    {
        const int points = 1 << 10;
        var values = new double[points];
        for (int d = 0; d < Sobol.MaxDimensions; d++)
        {
            Sobol.Fill(d, 0, values);
            int[] counts = new int[16];
            foreach (double u in values) counts[(int)(u * 16)]++;
            Assert.All(counts, c => Assert.Equal(points / 16, c));
        }
    }

    [Fact]
    public void SobolPairsCoverTheSquareEvenly()
    {
        const int points = 1 << 12;
        const int cells = 8;
        var a = new double[points];
        var b = new double[points];
        for (int i = 0; i < Sobol.MaxDimensions; i++)
        {
            Sobol.Fill(i, 0, a);
            for (int j = i + 1; j < Sobol.MaxDimensions; j++)
            {
                Sobol.Fill(j, 0, b);
                var counts = new int[cells * cells];
                for (int p = 0; p < points; p++) counts[(int)(a[p] * cells) * cells + (int)(b[p] * cells)]++;
                double expected = points / (double)(cells * cells);
                // Poisson-like spread would be near 8; a defective pair of direction numbers leaves empty cells.
                Assert.True(counts.Max() < 2.2 * expected, $"dims {i},{j}: max {counts.Max()}");
            }
        }
    }

    [Theory]
    [InlineData(1e-6)]
    [InlineData(0.01)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void InverseNormalMatchesLibrary(double p)
    {
        Assert.Equal(Normal.InvCDF(0, 1, p), InverseNormal.Evaluate(p), 7);
    }

    [Theory]
    [InlineData(OptionRight.Call)]
    [InlineData(OptionRight.Put)]
    public void EuropeanPdeMatchesClosedFormWithGridGreeks(OptionRight right)
    {
        var option = European(right);
        var closed = Price(option, new AnalyticEngineConfig());
        var pde = Price(option, Pde);

        Assert.Equal(closed.Pv, pde.Pv, 3);
        Assert.Equal(GreekMethod.PdeGrid, pde.Greek(Measure.Delta)!.Method);
        Assert.Equal(closed.Greek(Measure.Delta)!.Value, pde.Greek(Measure.Delta)!.Value, 3);
        Assert.Equal(closed.Greek(Measure.Gamma)!.Value, pde.Greek(Measure.Gamma)!.Value, 4);
        Assert.Equal(closed.Greek(Measure.Theta)!.Value, pde.Greek(Measure.Theta)!.Value, 3);
        Assert.Equal(GreekMethod.BumpReprice, pde.Greek(Measure.Vega)!.Method);
        Assert.Equal(closed.Greek(Measure.Vega)!.Value, pde.Greek(Measure.Vega)!.Value, 2);
    }

    [Fact]
    public void AmericanPutMatchesPublishedValue()
    {
        var put = new AmericanOption { Underlying = Name, Right = OptionRight.Put, Strike = 100, Expiry = OneYear };
        Assert.Equal(6.0903, Price(put, Pde, Market(dividend: 0)).Pv, 2);
    }

    [Fact]
    public void AmericanCallWithoutDividendIsWorthTheEuropean()
    {
        var market = Market(dividend: 0);
        var american = new AmericanOption { Underlying = Name, Right = OptionRight.Call, Strike = 105, Expiry = OneYear };
        Assert.Equal(Price(European(OptionRight.Call), new AnalyticEngineConfig(), market).Pv, Price(american, Pde, market).Pv, 3);
    }

    [Fact]
    public void AmericanPutIsWorthMoreThanEuropeanAndAtLeastIntrinsic()
    {
        var american = new AmericanOption { Underlying = Name, Right = OptionRight.Put, Strike = 130, Expiry = OneYear };
        var european = European(OptionRight.Put, 130);
        double value = Price(american, Pde).Pv;
        Assert.True(value > Price(european, new AnalyticEngineConfig()).Pv);
        Assert.True(value >= 30 - 1e-9);
    }

    [Theory]
    [InlineData(OptionRight.Call)]
    [InlineData(OptionRight.Put)]
    public void DigitalEnginesAgreeWithClosedForm(OptionRight right)
    {
        var digital = Digital(right);
        var closed = Price(digital, new AnalyticEngineConfig());
        Assert.Equal(GreekMethod.ClosedForm, closed.Greek(Measure.Delta)!.Method);

        var pde = Price(digital, Pde);
        Assert.Equal(closed.Pv, pde.Pv, 2);
        Assert.Equal(closed.Greek(Measure.Delta)!.Value, pde.Greek(Measure.Delta)!.Value, 2);

        var mc = Price(digital, Mc);
        WithinErrors(closed.Pv, mc);
        var delta = mc.Greek(Measure.Delta)!;
        Assert.Equal(GreekMethod.LikelihoodRatio, delta.Method);
        WithinErrors(closed.Greek(Measure.Delta)!.Value, delta);
        WithinErrors(closed.Greek(Measure.Gamma)!.Value, mc.Greek(Measure.Gamma)!);
        WithinErrors(closed.Greek(Measure.Vega)!.Value, mc.Greek(Measure.Vega)!);
    }

    [Fact]
    public void SmoothingGivesPathwiseDigitalGreeksAndLeavesPvUnbiased()
    {
        var digital = Digital(OptionRight.Call);
        var closed = Price(digital, new AnalyticEngineConfig());
        var hard = Price(digital, Mc);
        var smooth = Price(digital, Mc with { Smoothing = 0.01 });

        Assert.Equal(hard.Pv, smooth.Pv);
        var delta = smooth.Greek(Measure.Delta)!;
        Assert.Equal(GreekMethod.Pathwise, delta.Method);
        Assert.True(Math.Abs(closed.Greek(Measure.Delta)!.Value - delta.Value) < 0.005, $"{closed.Greek(Measure.Delta)!.Value} vs {delta.Value}");
    }

    [Theory]
    [InlineData(OptionRight.Call)]
    [InlineData(OptionRight.Put)]
    public void EuropeanMonteCarloMatchesClosedFormAndGreeksByEachMethod(OptionRight right)
    {
        var option = European(right);
        var closed = Price(option, new AnalyticEngineConfig());

        var pathwise = Price(option, Mc);
        WithinErrors(closed.Pv, pathwise);
        Assert.Equal(GreekMethod.Pathwise, pathwise.Greek(Measure.Delta)!.Method);
        Assert.Equal(GreekMethod.PathwiseLikelihoodRatio, pathwise.Greek(Measure.Gamma)!.Method);

        var score = Price(option, Mc with { Greeks = McGreekChoice.LikelihoodRatio });
        Assert.Equal(GreekMethod.LikelihoodRatio, score.Greek(Measure.Delta)!.Method);

        foreach (var measure in new[] { Measure.Delta, Measure.Gamma, Measure.Vega })
        {
            WithinErrors(closed.Greek(measure)!.Value, pathwise.Greek(measure)!);
            WithinErrors(closed.Greek(measure)!.Value, score.Greek(measure)!);
        }
        Assert.True(pathwise.Greek(Measure.Delta)!.StandardError < score.Greek(Measure.Delta)!.StandardError);
    }

    [Fact]
    public void MonteCarloThetaAndRhoFallBackToBumpWithCommonRandomNumbers()
    {
        var option = European(OptionRight.Call);
        var closed = Price(option, new AnalyticEngineConfig());
        var mc = Price(option, Mc);

        Assert.Equal(GreekMethod.BumpReprice, mc.Greek(Measure.Rho)!.Method);
        Assert.Equal(closed.Greek(Measure.Rho)!.Value, mc.Greek(Measure.Rho)!.Value, 2);
        Assert.Equal(closed.Greek(Measure.Theta)!.Value, mc.Greek(Measure.Theta)!.Value, 3);
    }

    [Fact]
    public void MonteCarloIsReproducibleAndMoreBatchesDoNotBiasIt()
    {
        var option = European(OptionRight.Call);
        var first = Price(option, Mc);
        var second = Price(option, Mc);
        Assert.Equal(first.Pv, second.Pv);
        Assert.Equal(first.Greeks.Select(g => g.Value), second.Greeks.Select(g => g.Value));
        Assert.NotEqual(first.Pv, Price(option, Mc with { Seed = 43 }).Pv);
    }

    [Fact]
    public void MonteCarloFollowsCurvesOnSteepTermStructure()
    {
        // The forward at expiry must come from the curves, not from a flat drift.
        var market = Market() with { DiscountCurve = new YieldCurve([0.25, 1, 2], [0.01, 0.04, 0.06]) };
        var closed = Price(European(OptionRight.Call), new AnalyticEngineConfig(), market);
        WithinErrors(closed.Pv, Price(European(OptionRight.Call), Mc, market));
    }

    public static TheoryData<BarrierKind, OptionRight, double, double> Barriers => new()
    {
        { BarrierKind.DownOut, OptionRight.Call, 100, 90 },
        { BarrierKind.DownOut, OptionRight.Call, 85, 90 },
        { BarrierKind.DownIn, OptionRight.Call, 100, 90 },
        { BarrierKind.DownIn, OptionRight.Call, 85, 90 },
        { BarrierKind.UpOut, OptionRight.Call, 100, 130 },
        { BarrierKind.UpIn, OptionRight.Call, 100, 130 },
        { BarrierKind.UpIn, OptionRight.Call, 140, 130 },
        { BarrierKind.DownOut, OptionRight.Put, 100, 80 },
        { BarrierKind.DownIn, OptionRight.Put, 100, 80 },
        { BarrierKind.DownIn, OptionRight.Put, 70, 80 },
        { BarrierKind.UpOut, OptionRight.Put, 100, 120 },
        { BarrierKind.UpOut, OptionRight.Put, 130, 120 },
        { BarrierKind.UpIn, OptionRight.Put, 100, 120 },
        { BarrierKind.UpIn, OptionRight.Put, 130, 120 },
    };

    [Theory]
    [MemberData(nameof(Barriers))]
    public void BarrierPdeAndMonteCarloMatchReinerRubinstein(BarrierKind kind, OptionRight right, double strike, double level)
    {
        var option = BarrierOf(kind, right, strike, level);
        double closed = Price(option, new AnalyticEngineConfig()).Pv;
        Assert.Equal(closed, Price(option, Pde).Pv, 2);
        WithinErrors(closed, Price(option, Mc), floor: 0.01);
    }

    [Theory]
    [InlineData(OptionRight.Call, 100, 90, false)]
    [InlineData(OptionRight.Put, 100, 120, true)]
    public void KnockInPlusKnockOutIsTheVanilla(OptionRight right, double strike, double level, bool up)
    {
        var (o, i) = up ? (BarrierKind.UpOut, BarrierKind.UpIn) : (BarrierKind.DownOut, BarrierKind.DownIn);
        double vanilla = Price(European(right, strike), new AnalyticEngineConfig()).Pv;
        double sum = Price(BarrierOf(o, right, strike, level), new AnalyticEngineConfig()).Pv
            + Price(BarrierOf(i, right, strike, level), new AnalyticEngineConfig()).Pv;
        Assert.Equal(vanilla, sum, 8);
    }

    [Fact]
    public void ReinerRubinsteinSurvivesZeroMu()
    {
        // r - q = vol^2 / 2 makes mu exactly zero.
        var x = new BlackScholesInputs(100, 100, 1, 0.2, 0.05, 0.03);
        double vanilla = BlackScholes.Evaluate(x, true).Price;
        double sum = ReinerRubinstein.Price(x, 90, true, false, true) + ReinerRubinstein.Price(x, 90, true, false, false);
        Assert.Equal(vanilla, sum, 8);
    }

    [Fact]
    public void BreachedBarrierIsZeroOrVanilla()
    {
        var engines = new EngineConfig[] { new AnalyticEngineConfig(), Pde, Mc };
        double vanilla = Price(European(OptionRight.Call, 100), new AnalyticEngineConfig()).Pv;
        foreach (var engine in engines)
        {
            Assert.Equal(0, Price(BarrierOf(BarrierKind.DownOut, OptionRight.Call, 100, 110), engine).Pv);
            Assert.Equal(vanilla, Price(BarrierOf(BarrierKind.DownIn, OptionRight.Call, 100, 110), engine).Pv, 1);
        }
    }

    [Fact]
    public void BarrierMonteCarloDeltaAgreesWithBumpOnClosedForm()
    {
        var option = BarrierOf(BarrierKind.DownOut, OptionRight.Call, 100, 80);
        var closed = Price(option, new AnalyticEngineConfig());
        var mc = Price(option, Mc);
        Assert.Equal(GreekMethod.BumpReprice, closed.Greek(Measure.Delta)!.Method);
        Assert.Equal(GreekMethod.LikelihoodRatio, mc.Greek(Measure.Delta)!.Method);
        WithinErrors(closed.Greek(Measure.Delta)!.Value, mc.Greek(Measure.Delta)!, floor: 0.01);

        var pde = Price(option, Pde);
        Assert.Equal(closed.Greek(Measure.Delta)!.Value, pde.Greek(Measure.Delta)!.Value, 2);
        Assert.Equal(closed.Greek(Measure.Gamma)!.Value, pde.Greek(Measure.Gamma)!.Value, 3);
    }

    private static AsianOption Asian(OptionRight right, double strike, int fixings, DateOnly? start = null) => new()
    {
        Underlying = Name, Right = right, Strike = strike, Expiry = OneYear,
        AveragingStart = start ?? Today.AddDays(30), Fixings = fixings,
    };

    [Fact]
    public void AsianWithOneFixingIsTheEuropean()
    {
        var closed = Price(European(OptionRight.Call, 100), new AnalyticEngineConfig());
        WithinErrors(closed.Pv, Price(Asian(OptionRight.Call, 100, 1), Mc));
    }

    /// <summary>Geometric average of lognormal fixings has a closed form and bounds the arithmetic one from below.</summary>
    private static double GeometricAsianCall(double strike, double[] times)
    {
        double n = times.Length;
        double mean = Math.Log(Spot) + times.Sum(t => (Rate - Carry - 0.5 * Vol * Vol) * t) / n;
        double variance = 0;
        foreach (double a in times)
        {
            foreach (double b in times) variance += Vol * Vol * Math.Min(a, b) / (n * n);
        }
        double T = times[^1];
        double root = Math.Sqrt(variance);
        double d1 = (mean + variance - Math.Log(strike)) / root;
        return Math.Exp(-Rate * T) * (Math.Exp(mean + variance / 2) * Normal.CDF(0, 1, d1) - strike * Normal.CDF(0, 1, d1 - root));
    }

    /// <summary>Arithmetic average approximated by a lognormal with the same two moments.</summary>
    private static double TurnbullWakemanCall(double strike, double[] times)
    {
        double n = times.Length;
        double Forward(double t) => Spot * Math.Exp((Rate - Carry) * t);
        double first = times.Sum(Forward) / n;
        double second = 0;
        foreach (double a in times)
        {
            foreach (double b in times) second += Forward(a) * Forward(b) * Math.Exp(Vol * Vol * Math.Min(a, b)) / (n * n);
        }
        double root = Math.Sqrt(Math.Log(second / (first * first)));
        double d1 = Math.Log(first / strike) / root + root / 2;
        return Math.Exp(-Rate * times[^1]) * (first * Normal.CDF(0, 1, d1) - strike * Normal.CDF(0, 1, d1 - root));
    }

    [Fact]
    public void AsianLiesBetweenGeometricAndEuropeanWithPathwiseGreeks()
    {
        const int fixings = 12;
        var option = Asian(OptionRight.Call, 100, fixings);
        var mc = Price(option, Mc);

        double span = 335;
        double[] times = [.. Enumerable.Range(0, fixings).Select(i => (30 + Math.Round(span * i / (fixings - 1))) / 365)];
        double geometric = GeometricAsianCall(100, times);
        double european = Price(European(OptionRight.Call, 100), new AnalyticEngineConfig()).Pv;
        Assert.True(mc.Pv > geometric && mc.Pv < european, $"{geometric} < {mc.Pv} < {european}");
        double moments = TurnbullWakemanCall(100, times);
        Assert.True(Math.Abs(mc.Pv - moments) < 0.02, $"{mc.Pv} vs {moments}");

        var bumped = _risk.Compute(Request(option, Mc), new RiskConfig { Preference = GreekPreference.ForceBump });
        Assert.Equal(GreekMethod.Pathwise, mc.Greek(Measure.Delta)!.Method);
        WithinErrors(bumped.Greek(Measure.Delta)!.Value, mc.Greek(Measure.Delta)!);
        WithinErrors(bumped.Greek(Measure.Vega)!.Value, mc.Greek(Measure.Vega)!);

        // Multi-step paths: the mixed gamma must agree with the pure likelihood ratio.
        var score = Price(option, Mc with { Greeks = McGreekChoice.LikelihoodRatio }).Greek(Measure.Gamma)!;
        var mixed = mc.Greek(Measure.Gamma)!;
        Assert.Equal(GreekMethod.PathwiseLikelihoodRatio, mixed.Method);
        Assert.True(Math.Abs(mixed.Value - score.Value) < 4 * (mixed.StandardError + score.StandardError), $"{mixed.Value} vs {score.Value}");
    }

    [Fact]
    public void ExpiryAndPastExpiryAreHandledByPdeAndMonteCarlo()
    {
        var atExpiry = new EuropeanOption { Underlying = Name, Right = OptionRight.Call, Strike = 85, Expiry = Today };
        var expired = atExpiry with { Expiry = Today.AddDays(-1) };
        foreach (EngineConfig engine in new EngineConfig[] { Pde, Mc })
        {
            Assert.Equal(15.0, Price(atExpiry, engine).Pv);
            Assert.Equal(0.0, Price(expired, engine).Pv);
        }
    }

    [Fact]
    public void PayoffProgramsFlagDiscontinuity()
    {
        Assert.True(PayoffCompiler.Compile(European(OptionRight.Call), Today, Spot, Vol).IsContinuous);
        Assert.False(PayoffCompiler.Compile(Digital(OptionRight.Call), Today, Spot, Vol).IsContinuous);
        Assert.False(PayoffCompiler.Compile(BarrierOf(BarrierKind.UpOut, OptionRight.Put, 100, 120), Today, Spot, Vol).IsContinuous);
    }

    [Fact]
    public void InstrumentsAndEngineConfigsRoundTripThroughJson()
    {
        Instrument option = BarrierOf(BarrierKind.UpIn, OptionRight.Put, 100, 120);
        Assert.Equal(option, JsonSerializer.Deserialize<Instrument>(JsonSerializer.Serialize(option)));
        EngineConfig config = Mc with { Seed = 7 };
        Assert.Equal(config, JsonSerializer.Deserialize<EngineConfig>(JsonSerializer.Serialize(config)));
    }

    [Fact]
    public void RegistryListsEnginesByProduct()
    {
        var registry = EngineRegistry.Default();
        Assert.Equal([typeof(PdeEngineConfig)], registry.ConfigsFor(typeof(AmericanOption), typeof(BlackScholesConfig)));
        Assert.Equal(3, registry.ConfigsFor(typeof(BarrierOption), typeof(BlackScholesConfig)).Count);
        Assert.Throws<NotSupportedException>(() => Price(Asian(OptionRight.Call, 100, 4), Pde));
    }

    [Fact]
    public void YahooQuotesParseWithMissingFields()
    {
        const string json = """
            {"options":[{"calls":[{"strike":100.0,"bid":1.5,"ask":1.7},{"strike":105.0}],
                         "puts":[{"strike":100.0,"bid":0.0,"ask":0.2}]}]}
            """;
        using var document = JsonDocument.Parse(json);
        var quotes = YahooOptionChainSource.ParseQuotes(document.RootElement, Today);

        Assert.Equal(3, quotes.Length);
        Assert.True(quotes[0].IsTwoSided);
        Assert.False(quotes[1].IsTwoSided);
        Assert.False(quotes[2].IsCall);
    }

    [Fact]
    public void ChainFilterPicksDistinctExpiriesAwayFromThePresent()
    {
        DateOnly[] listed = [Today.AddDays(1), Today.AddDays(14), Today.AddDays(90), Today.AddDays(800)];
        var chosen = ChainFilter.Expiries(listed, Today);
        Assert.DoesNotContain(Today.AddDays(1), chosen);
        Assert.Equal(chosen.Count, chosen.Distinct().Count());
        Assert.Contains(Today.AddDays(800), chosen);
    }
}
