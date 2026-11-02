using System.Collections.Immutable;
using Pricer.Engines;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Risk;

/// <summary>Entry point: keeps the greeks the engine computed itself and bumps the rest. A basket has one
/// delta, gamma and vega per underlying, tagged with its name.</summary>
public sealed class RiskCalculator(EngineRegistry registry)
{
    private const double VolPoint = 0.01;
    private const double RatePoint = 0.01;
    private static readonly Measure[] PerUnderlying = [Measure.Delta, Measure.Gamma, Measure.Vega];

    public PricingResult Compute(PricingRequest request, RiskConfig config)
    {
        var engine = registry.Resolve(request.Trade.Instrument, request.Model, request.Engine);
        var result = engine.Price(request);

        var own = config.Preference == GreekPreference.ForceBump ? ImmutableArray<GreekValue>.Empty : result.Greeks;
        var names = request.Trade.Instrument.Underlyings;
        var bumped = new List<GreekValue>();
        foreach (var measure in request.Measures.Where(m => own.All(g => g.Measure != m)))
        {
            if (names.Length > 1 && PerUnderlying.Contains(measure))
            {
                bumped.AddRange(names.Select(name => Bump(engine, request, result.Pv, measure, name, config) with { Underlying = name }));
            }
            else
            {
                bumped.Add(Bump(engine, request, result.Pv, measure, names[0], config));
            }
        }
        return result with { Greeks = [.. own, .. bumped] };
    }

    private static GreekValue Bump(
        IPricingEngine engine, PricingRequest request, double pv, Measure measure, string underlying, RiskConfig config)
    {
        double ds = request.Market.Underlying(underlying).Spot * config.SpotBump;
        bool followsSurface = measure is Measure.Vega or Measure.Rho;

        double Pv(MarketBump bump)
        {
            var market = bump.Apply(request.Market);
            var model = followsSurface ? Recalibrated(request.Model, market, underlying) : request.Model;
            return Reprice(engine, request with { Market = market, Model = model });
        }

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
        // Refitting on spot bumps would feed calibration noise into a second difference, so Heston keeps its
        // parameters there: its delta and gamma are model greeks.
        var convention = request.Model is HestonConfig && !followsSurface ? GreekConvention.Model : GreekConvention.MarketStickyStrike;
        return new GreekValue(measure, value, GreekMethod.BumpReprice, convention);
    }

    /// <summary>Bumps live in market space, so a model that does not read the surface has to be refitted to it.</summary>
    private static ModelConfig Recalibrated(ModelConfig model, MarketSnapshot market, string underlying) =>
        model is HestonConfig { RecalibrateOnBump: true } heston
            ? HestonCalibrator.Fit(market, underlying, heston).Config
            : model;

    private static double Reprice(IPricingEngine engine, PricingRequest request) =>
        engine.Price(request with { Measures = ImmutableArray<Measure>.Empty }).Pv;
}
