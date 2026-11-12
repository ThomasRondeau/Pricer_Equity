using Pricer.Book;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Native;
using Pricer.Risk;

namespace Pricer.Presentation;

/// <summary>What the product, model and engine pickers offer, shared by the Pricer and PnL Explain pages:
/// the default term sheet of each product on a market, and the engines each pair of product and model has.</summary>
public static class TradeCatalog
{
    /// <summary>A small bump on a payoff with jumps measures noise, not a sensitivity.</summary>
    private const double NoteSpotBump = 0.01;

    private static readonly Dictionary<Type, EngineKind> EngineKindOf = new()
    {
        [typeof(AnalyticEngineConfig)] = EngineKind.Analytic,
        [typeof(PdeEngineConfig)] = EngineKind.Pde,
        [typeof(MonteCarloEngineConfig)] = EngineKind.MonteCarlo,
    };

    public static IEnumerable<EngineKind> EnginesFor(EngineRegistry registry, Type instrument, ModelKind model) =>
        registry.ConfigsFor(instrument, ModelType(model)).Select(c => EngineKindOf[c]).Order();

    public static Type ModelType(ModelKind kind) => kind switch
    {
        ModelKind.LocalVol => typeof(LocalVolConfig),
        ModelKind.Heston => typeof(HestonConfig),
        _ => typeof(BlackScholesConfig),
    };

    public static ModelChoice Choice(ModelKind kind) => kind switch
    {
        ModelKind.LocalVol => ModelChoice.LocalVol,
        ModelKind.Heston => ModelChoice.Heston,
        _ => ModelChoice.BlackScholes,
    };

    public static ModelConfig NewModel(ModelKind kind, HestonConfig heston) => kind switch
    {
        ModelKind.LocalVol => new LocalVolConfig(),
        ModelKind.Heston => heston,
        _ => new BlackScholesConfig(),
    };

    public static EngineConfig NewEngine(EngineKind kind, PricerOptions options) => kind switch
    {
        EngineKind.Pde => new PdeEngineConfig { Backend = Backend(options.Backend) },
        EngineKind.MonteCarlo => options.MonteCarlo with { Backend = Backend(options.Backend) },
        _ => new AnalyticEngineConfig(),
    };

    /// <summary>Native only where the library loaded, so that a missing build degrades to the managed engines.</summary>
    public static ComputeBackend Backend(ComputeBackend wanted) => NativeCore.IsAvailable ? wanted : ComputeBackend.Managed;

    public static RiskConfig NewRisk(ProductKind kind) =>
        kind >= ProductKind.ReverseConvertible && kind <= ProductKind.WorstOf ? new RiskConfig { SpotBump = NoteSpotBump } : new RiskConfig();

    /// <summary>The default term sheet: at the money, one year, or three for the autocallable notes.</summary>
    public static Instrument NewInstrument(ProductKind kind, MarketSnapshot snapshot, string underlying)
    {
        double spot = snapshot.Underlying(underlying).Spot;
        double strike = Math.Round(spot);
        var start = snapshot.AsOf;
        var expiry = start.AddYears(1);
        return kind switch
        {
            ProductKind.EuropeanOption => new EuropeanOption
            {
                Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
            },
            ProductKind.AmericanOption => new AmericanOption
            {
                Underlying = underlying, Right = OptionRight.Put, Strike = strike, Expiry = expiry,
            },
            ProductKind.Digital => new DigitalOption
            {
                Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
            },
            ProductKind.Barrier => new BarrierOption
            {
                Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
                Barrier = Math.Round(0.8 * strike), Kind = BarrierKind.DownOut,
            },
            ProductKind.Asian => new AsianOption
            {
                Underlying = underlying, Right = OptionRight.Call, Strike = strike, Expiry = expiry,
                AveragingStart = start.AddMonths(1),
            },
            ProductKind.ReverseConvertible => new ReverseConvertible
            {
                Underlying = underlying, InitialFixing = spot, Start = start, Expiry = expiry, Protection = 0.8,
            },
            ProductKind.Autocall => new Autocall
            {
                Underlying = underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
            },
            ProductKind.Phoenix => new Phoenix
            {
                Underlying = underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
            },
            ProductKind.WorstOf => new WorstOfPhoenix
            {
                Underlying = underlying, InitialFixing = spot, Start = start, Expiry = start.AddYears(3),
                Basket = [.. snapshot.Underlyings.Keys.OrderBy(n => n != underlying)
                    .Select(n => new BasketLeg(n, snapshot.Underlying(n).Spot))],
            },
            ProductKind.Forward => new EquityForward { Underlying = underlying, Strike = strike, Expiry = expiry },
            ProductKind.Stock => new Stock { Underlying = underlying },
            _ => throw new InvalidOperationException(),
        };
    }
}
