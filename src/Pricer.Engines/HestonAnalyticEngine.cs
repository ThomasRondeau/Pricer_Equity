using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Instruments;
using Pricer.Models;

namespace Pricer.Engines;

/// <summary>Semi-analytic European options. Vega is taken on sqrt(v0): a model greek, not a surface vega.
/// Theta and rho are left to the bump fallback.</summary>
public sealed class HestonAnalyticEngine : IPricingEngine
{
    private const double VolPoint = 0.01;

    public string Name => "Heston.Analytic";

    public PricingResult Price(PricingRequest request)
    {
        var option = (EuropeanOption)request.Trade.Instrument;
        var market = request.Market.Underlying(option.Underlying);
        double quantity = request.Trade.Quantity;
        double t = DayCount.Act365F.YearFraction(request.ValuationDate, option.Expiry);

        if (t < 0) return new PricingResult(0, null, Name, []);
        if (t == 0) return new PricingResult(quantity * PayoffCompiler.AtExpiry(option, market.Spot), null, Name, []);

        double discount = request.Market.DiscountCurve.Discount(t);
        double forward = market.Spot * market.DividendCurve.Discount(t) / discount;
        var s = new HestonSlice((HestonConfig)request.Model, t)
            .Evaluate(market.Spot, forward, option.Strike, discount, option.Right == OptionRight.Call);

        var greeks = ImmutableArray.CreateBuilder<GreekValue>();
        foreach (var measure in request.Measures)
        {
            double? value = measure switch
            {
                Measure.Delta => s.Delta,
                Measure.Gamma => s.Gamma,
                Measure.Vega => s.VegaV0 * VolPoint,
                _ => null,
            };
            if (value is { } v) greeks.Add(new GreekValue(measure, quantity * v, GreekMethod.SemiAnalytic, GreekConvention.Model));
        }
        return new PricingResult(quantity * s.Price, null, Name, greeks.ToImmutable());
    }
}
