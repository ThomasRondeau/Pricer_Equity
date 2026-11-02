using System.Collections.Immutable;
using Pricer.Engines;
using Pricer.Market;

namespace Pricer.Risk;

/// <summary>Entry point: keeps the greeks the engine computed itself and bumps the rest.</summary>
public sealed class RiskCalculator(EngineRegistry registry)
{
    private const double VolPoint = 0.01;
    private const double RatePoint = 0.01;

    public PricingResult Compute(PricingRequest request, RiskConfig config)
    {
        var engine = registry.Resolve(request.Trade.Instrument, request.Model, request.Engine);
        var result = engine.Price(request);

        var own = config.Preference == GreekPreference.ForceBump ? ImmutableArray<GreekValue>.Empty : result.Greeks;
        var missing = request.Measures.Where(m => own.All(g => g.Measure != m));
        var bumped = missing.Select(m => Bump(engine, request, result.Pv, m, config));
        return result with { Greeks = [.. own, .. bumped] };
    }

    private static GreekValue Bump(
        IPricingEngine engine, PricingRequest request, double pv, Measure measure, RiskConfig config)
    {
        string underlying = request.Trade.Instrument.Underlying;
        double ds = request.Market.Underlying(underlying).Spot * config.SpotBump;

        double Pv(MarketBump bump) => Reprice(engine, request with { Market = bump.Apply(request.Market) });
        double SpotUp() => Pv(new SpotBump(underlying, config.SpotBump));
        double SpotDown() => Pv(new SpotBump(underlying, -config.SpotBump));

        double value = measure switch
        {
            Measure.Delta => (SpotUp() - SpotDown()) / (2 * ds),
            Measure.Gamma => (SpotUp() - 2 * pv + SpotDown()) / (ds * ds),
            Measure.Vega => (Pv(new VolBump(underlying, config.VolBump)) - Pv(new VolBump(underlying, -config.VolBump)))
                / (2 * config.VolBump) * VolPoint,
            Measure.Rho => (Pv(new RateBump(config.RateBump)) - Pv(new RateBump(-config.RateBump)))
                / (2 * config.RateBump) * RatePoint,
            Measure.Theta => Reprice(engine, request with { ValuationDate = request.ValuationDate.AddDays(1) }) - pv,
            _ => throw new ArgumentOutOfRangeException(nameof(measure)),
        };
        return new GreekValue(measure, value, GreekMethod.BumpReprice, GreekConvention.MarketStickyStrike);
    }

    private static double Reprice(IPricingEngine engine, PricingRequest request) =>
        engine.Price(request with { Measures = ImmutableArray<Measure>.Empty }).Pv;
}
