using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Engines;

/// <summary>Closed-form greeks for options. Stock and forward leave greeks to the bump fallback,
/// which is exact for payoffs linear in spot. Barriers have a closed-form price only.</summary>
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

            case OptionInstrument option:
            {
                double t = DayCount.Act365F.YearFraction(request.ValuationDate, option.Expiry);
                if (t < 0) return Result(0, [.. request.Measures.Select(m => Greek(m, 0))]);
                var inputs = EngineInputs.For(option, request, t);

                return option switch
                {
                    EuropeanOption => European(request, inputs, option, quantity),
                    DigitalOption digital when t > 0 => DigitalResult(request, inputs, digital, quantity),
                    BarrierOption barrier when t > 0 => Result(quantity * ReinerRubinstein.Price(
                        inputs, barrier.Barrier, option.Right == OptionRight.Call, barrier.IsUp, barrier.IsOut), []),
                    _ => Result(quantity * PayoffCompiler.AtExpiry(option, market.Spot), []),
                };
            }

            default:
                throw new NotSupportedException(request.Trade.Instrument.GetType().Name);
        }
    }

    private PricingResult European(PricingRequest request, BlackScholesInputs inputs, OptionInstrument option, double quantity)
    {
        var s = BlackScholes.Evaluate(inputs, option.Right == OptionRight.Call);
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

    private PricingResult DigitalResult(PricingRequest request, BlackScholesInputs inputs, DigitalOption option, double quantity)
    {
        var s = Digital.Evaluate(inputs, option.Right == OptionRight.Call);
        double size = quantity * option.Cash;
        var greeks = new List<GreekValue>();
        foreach (var measure in request.Measures)
        {
            if (measure == Measure.Delta) greeks.Add(Greek(measure, size * s.Delta));
            if (measure == Measure.Gamma) greeks.Add(Greek(measure, size * s.Gamma));
            if (measure == Measure.Vega) greeks.Add(Greek(measure, size * s.Vega * VolPoint));
        }
        return Result(size * s.Price, [.. greeks]);
    }

    private PricingResult Result(double pv, ImmutableArray<GreekValue> greeks) => new(pv, null, Name, greeks);

    private static GreekValue Greek(Measure measure, double value) =>
        new(measure, value, GreekMethod.ClosedForm, GreekConvention.Model);
}

public static class EngineInputs
{
    /// <summary>Black-Scholes inputs for an option: zero rates and implied vol at its strike and expiry.
    /// A single vol ignores the smile along a path-dependent payoff; local vol comes with phase 4.</summary>
    public static BlackScholesInputs For(OptionInstrument option, PricingRequest request, double t)
    {
        var market = request.Market.Underlying(option.Underlying);
        double vol = market.Vol.Vol(option.Strike, t) + ((BlackScholesConfig)request.Model).VolAdjustment;
        return new(market.Spot, option.Strike, t, vol, request.Market.DiscountCurve.ZeroRate(t), market.DividendCurve.ZeroRate(t));
    }
}
