using System.Collections.Immutable;
using System.Text.Json;
using Pricer.Data;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Native;

namespace Pricer.Tests;

/// <summary>The C++ kernels against the managed engines: same algorithm, same random numbers, so the results
/// agree up to the rounding of exp and log. The library is built with the tests (Pricer.Native.csproj).</summary>
public class NativeParityTests
{
    private const string A = "A";
    private const string B = "B";
    private const double Spot = 100;
    private static readonly DateOnly Today = new(2026, 1, 2);
    private static readonly DateOnly OneYear = Today.AddDays(365);
    private static readonly DateOnly TwoYears = Today.AddYears(2);
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];
    private static readonly EngineRegistry Registry = EngineRegistry.Default();
    private static readonly Lazy<MarketSnapshot> SmileMarket = new(Smile);

    private static MarketSnapshot Flat() => new(
        Today, YieldCurve.Flat(0.03),
        ImmutableSortedDictionary<string, UnderlyingMarket>.Empty
            .Add(A, new UnderlyingMarket(Spot, YieldCurve.Flat(0.01), new FlatVolSurface(0.2)))
            .Add(B, new UnderlyingMarket(Spot, YieldCurve.Flat(0.02), new FlatVolSurface(0.25))));

    private static MarketSnapshot Smile()
    {
        var chain = new SyntheticOptionChainSource().FetchAsync(new ChainRequest(A), Today, null, default).Result;
        var built = SurfaceBuilder.Build(chain, SyntheticMarketDataProvider.DiscountCurve);
        return new MarketSnapshot(
            Today, SyntheticMarketDataProvider.DiscountCurve,
            ImmutableSortedDictionary<string, UnderlyingMarket>.Empty.Add(A, built.Market));
    }

    private static Instrument Product(string name) => name switch
    {
        "european call" => new EuropeanOption { Underlying = A, Right = OptionRight.Call, Strike = 105, Expiry = OneYear },
        "european put" => new EuropeanOption { Underlying = A, Right = OptionRight.Put, Strike = 95, Expiry = TwoYears },
        "american put" => new AmericanOption { Underlying = A, Right = OptionRight.Put, Strike = 100, Expiry = OneYear },
        "digital call" => new DigitalOption { Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = OneYear, Cash = 10 },
        "down-and-out call" => new BarrierOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = OneYear, Barrier = 85, Kind = BarrierKind.DownOut,
        },
        "up-and-in put" => new BarrierOption
        {
            Underlying = A, Right = OptionRight.Put, Strike = 100, Expiry = OneYear, Barrier = 115, Kind = BarrierKind.UpIn,
        },
        "asian call" => new AsianOption
        {
            Underlying = A, Right = OptionRight.Call, Strike = 100, Expiry = OneYear, AveragingStart = Today.AddMonths(1), Fixings = 12,
        },
        "reverse convertible" => new ReverseConvertible
        {
            Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears, Protection = 0.7,
        },
        "autocall" => new Autocall { Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears },
        "phoenix" => new Phoenix { Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears, CouponBarrier = 0.8 },
        "worst-of phoenix" => new WorstOfPhoenix
        {
            Underlying = A, InitialFixing = Spot, Start = Today, Expiry = TwoYears,
            Basket = [new BasketLeg(A, Spot), new BasketLeg(B, Spot)],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static ModelConfig Model(string name) => name switch
    {
        "black-scholes" => new BlackScholesConfig(),
        "local vol" => new LocalVolConfig(),
        "heston" => new HestonConfig(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string, string> PdeCases => new()
    {
        { "european call", "black-scholes" },
        { "american put", "black-scholes" },
        { "digital call", "black-scholes" },
        { "down-and-out call", "black-scholes" },
        { "up-and-in put", "black-scholes" },
        { "european put", "local vol" },
        { "american put", "local vol" },
        { "down-and-out call", "local vol" },
    };

    public static TheoryData<string, string, bool> MonteCarloCases => new()
    {
        { "european call", "black-scholes", false },
        { "digital call", "black-scholes", false },
        { "down-and-out call", "black-scholes", false },
        { "asian call", "black-scholes", false },
        { "reverse convertible", "black-scholes", false },
        { "autocall", "black-scholes", false },
        { "phoenix", "black-scholes", false },
        { "autocall", "black-scholes", true },
        { "european put", "local vol", false },
        { "asian call", "local vol", false },
        { "autocall", "local vol", false },
        { "phoenix", "local vol", false },
        { "worst-of phoenix", "local vol", false },
        { "european call", "heston", false },
        { "autocall", "heston", false },
    };

    [Theory]
    [MemberData(nameof(PdeCases))]
    public void PdeMatchesManaged(string product, string model)
    {
        var engine = new PdeEngineConfig { SpaceSteps = 400, TimeSteps = 200 };
        var (managed, native) = Both(Product(product), Model(model), engine, engine with { Backend = ComputeBackend.Native });

        // Same operations on the same inputs: only the compilers' rounding can differ.
        Same(managed, native, relative: 1e-12, errors: 0);
    }

    [Theory]
    [MemberData(nameof(MonteCarloCases))]
    public void MonteCarloMatchesManaged(string product, string model, bool likelihoodRatio)
    {
        var engine = new MonteCarloEngineConfig
        {
            Paths = 1 << 14, Batches = 8, Seed = 7, StepsPerYear = 26, Smoothing = 0.02,
            Greeks = likelihoodRatio ? McGreekChoice.LikelihoodRatio : McGreekChoice.Auto,
        };
        var (managed, native) = Both(Product(product), Model(model), engine, engine with { Backend = ComputeBackend.Native });

        // The paths agree to a few ulps (exp, log); a path ending exactly on a barrier could still fall on the
        // other side, hence a margin of a small fraction of the standard error.
        Same(managed, native, relative: 1e-9, errors: 0.01);
    }

    [Theory]
    [InlineData("svi", 0.0)]
    [InlineData("svi", 0.01)]
    [InlineData("flat", 0.0)]
    public void LocalVolGridMatchesManaged(string surface, double shift)
    {
        var market = surface == "svi" ? SmileMarket.Value : Flat();
        var underlying = market.Underlying(A);
        var vol = underlying.Vol.Shifted(shift);
        double Forward(double t) => underlying.Spot * underlying.DividendCurve.Discount(t) / market.DiscountCurve.Discount(t);
        double[] times = [.. Enumerable.Range(1, 60).Select(i => i / 24.0)];

        var managed = new LocalVolGrid(vol, Forward, times, 0.02, 2);
        var native = new LocalVolGrid(vol, Forward, times, 0.02, 2, native: true);

        Assert.Equal(managed.ClampedShare, native.ClampedShare);
        for (int i = 0; i < times.Length; i++)
        {
            foreach (var (m, n) in managed.Row(i).Zip(native.Row(i))) Close(m, n, 1e-12, 0, $"row {i}");
        }
    }

    [Fact]
    public void NativeEnginesSayWhereTheyRan()
    {
        var (managed, native) = Both(
            Product("european call"), new BlackScholesConfig(), new PdeEngineConfig(), new PdeEngineConfig { Backend = ComputeBackend.Native });
        Assert.Equal("BlackScholes.Pde", managed.Engine);
        Assert.Equal("BlackScholes.Pde.Native", native.Engine);
    }

    [Fact]
    public void MalformedProgramIsRefused()
    {
        var code = new PayoffCode([21, 0, 4, 0], [], [], 1, 1);
        var paths = new BlackScholesPaths([1.0], [100.0], [1.0], [new NativeBridgeStep(0, -1, -1, 0, 0, 1)], 100, 0.2, 0, false, false);
        var error = Assert.Throws<ArgumentException>(() => NativeCore.BlackScholes(code, paths, new BatchPlan(16, [1UL], [])));
        Assert.Contains("Malformed payoff program", error.Message);
    }

    [Fact]
    public void ManagedBackendLeavesTheSavedConfigurationUnchanged()
    {
        string json = JsonSerializer.Serialize<EngineConfig>(new PdeEngineConfig());
        Assert.DoesNotContain("Backend", json);
        var native = JsonSerializer.Deserialize<EngineConfig>(
            JsonSerializer.Serialize<EngineConfig>(new MonteCarloEngineConfig { Backend = ComputeBackend.Native }));
        Assert.Equal(ComputeBackend.Native, ((MonteCarloEngineConfig)native!).Backend);
    }

    private static (PricingResult Managed, PricingResult Native) Both(
        Instrument instrument, ModelConfig model, EngineConfig managed, EngineConfig native)
    {
        var market = model is BlackScholesConfig || instrument is WorstOfPhoenix ? Flat() : SmileMarket.Value;
        var request = new PricingRequest(new Trade("T", instrument, 3), market, model, managed, Today, AllMeasures);
        var engine = Registry.Resolve(instrument, model, managed);
        return (engine.Price(request), engine.Price(request with { Engine = native }));
    }

    private static void Same(PricingResult managed, PricingResult native, double relative, double errors)
    {
        double margin = errors * (managed.PvStandardError ?? 0);
        Close(managed.Pv, native.Pv, relative, margin, "PV");
        Close(managed.PvStandardError ?? 0, native.PvStandardError ?? 0, relative, margin, "standard error");
        Assert.Equal(managed.Greeks.Length, native.Greeks.Length);
        foreach (var (m, n) in managed.Greeks.Zip(native.Greeks))
        {
            Assert.Equal((m.Measure, m.Method, m.Convention), (n.Measure, n.Method, n.Convention));
            Close(m.Value, n.Value, relative, errors * (m.StandardError ?? 0), m.Measure.ToString());
        }
        Assert.Equal(managed.Realised, native.Realised);
    }

    private static void Close(double expected, double actual, double relative, double margin, string what) =>
        Assert.True(
            Math.Abs(expected - actual) <= relative * Math.Max(1, Math.Abs(expected)) + margin,
            $"{what}: managed {expected:R}, native {actual:R}, gap {actual - expected:E2}");
}
