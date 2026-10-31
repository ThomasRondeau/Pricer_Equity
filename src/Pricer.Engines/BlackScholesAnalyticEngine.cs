using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Instruments;
using Pricer.Models;

namespace Pricer.Engines;

/// <summary>Closed-form greeks for options. Stock and forward leave greeks to the bump fallback,
/// which is exact for payoffs linear in spot.</summary>
public sealed class BlackScholesAnalyticEngine : IPricingEngine
{
    private const double VolPoint = 0.01;
    private const double RatePoint = 0.01;
    private const double DaysPerYear = 365.0;

    public string Name => "BlackScholes.Analytic";

    public PricingResult Price(PricingRequest request)
    {
        var config = (BlackScholesConfig)request.Model;
        var market = request.Market.Underlying(request.Trade.Instrument.Underlying);
        double quantity = request.Trade.Quantity;

        switch (request.Trade.Instrument)
        {
            case Stock:
                return Result(quantity * market.Spot, []);

            case EquityForward forward:
            {
                double t = DayCount.Act365F.YearFraction(request.ValuationDate, forward.Expiry);
                if (t < 0) return Result(0, []);
                double df = request.Market.DiscountCurve.Discount(t);
                return Result(quantity * (market.Spot * market.DividendCurve.Discount(t) - forward.Strike * df), []);
            }

            case EuropeanOption option:
            {
                double t = DayCount.Act365F.YearFraction(request.ValuationDate, option.Expiry);
                if (t < 0) return Result(0, [.. request.Measures.Select(m => Greek(m, 0))]);
                double vol = market.Vol.Vol(option.Strike, t) + config.VolAdjustment;
                var s = BlackScholes.Evaluate(
                    new(market.Spot, option.Strike, t, vol, request.Market.DiscountCurve.ZeroRate(t), market.DividendCurve.ZeroRate(t)),
                    option.Right == OptionRight.Call);
                return Result(quantity * s.Price, [.. request.Measures.Select(m => Greek(m, quantity * m switch
                {
                    Measure.Delta => s.Delta,
                    Measure.Gamma => s.Gamma,
                    Measure.Vega => s.Vega * VolPoint,
                    Measure.Theta => s.Theta / DaysPerYear,
                    Measure.Rho => s.Rho * RatePoint,
                    _ => throw new ArgumentOutOfRangeException(nameof(m)),
                }))]);
            }

            default:
                throw new NotSupportedException(request.Trade.Instrument.GetType().Name);
        }
    }

    private PricingResult Result(double pv, ImmutableArray<GreekValue> greeks) => new(pv, null, Name, greeks);

    private static GreekValue Greek(Measure measure, double value) =>
        new(measure, value, GreekMethod.ClosedForm, GreekConvention.Model);
}
