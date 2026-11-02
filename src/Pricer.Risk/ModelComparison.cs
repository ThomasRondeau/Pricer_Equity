using System.Collections.Immutable;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Risk;

public sealed record SmileFit(double Strike, double Market, double Svi, double Heston, double LocalVol);

public sealed record SliceFit(
    DateOnly Expiry, double T, double RmseSvi, double RmseHeston, double RmseLocalVol, ImmutableArray<SmileFit> Points);

/// <summary>Reprices the quoted smile under each model and reads the implied vols back, so that all fits are
/// judged in the same units. The local vol goes through the PDE: this is its actual repricing error.</summary>
public static class ModelComparison
{
    private const int StrikesPerSlice = 9;
    private static readonly PdeEngineConfig Pde = new();
    private static readonly ImmutableArray<Measure> NoMeasures = [];

    public static ImmutableArray<SliceFit> Run(
        MarketSnapshot snapshot, string underlying, CalibrationReport report, HestonConfig heston, LocalVolConfig localVol)
    {
        var fits = new SliceFit[report.Slices.Length];
        var risk = new RiskCalculator(EngineRegistry.Default());
        Parallel.For(0, fits.Length, i => fits[i] = Fit(snapshot, underlying, report.Slices[i], heston, localVol, risk));
        return [.. fits];
    }

    private static SliceFit Fit(
        MarketSnapshot snapshot, string underlying, SliceReport report, HestonConfig heston, LocalVolConfig localVol, RiskCalculator risk)
    {
        var market = snapshot.Underlying(underlying);
        double t = report.Slice.T;
        double discount = snapshot.DiscountCurve.Discount(t);
        double forward = market.Spot * market.DividendCurve.Discount(t) / discount;
        var slice = new HestonSlice(heston, t);

        int stride = Math.Max(1, report.Points.Length / StrikesPerSlice);
        var points = ImmutableArray.CreateBuilder<SmileFit>();
        foreach (var point in report.Points.Where((_, i) => i % stride == 0))
        {
            bool isCall = point.Strike >= forward;
            double hestonPrice = slice.Evaluate(market.Spot, forward, point.Strike, discount, isCall).Price;
            var option = new EuropeanOption
            {
                Underlying = underlying, Right = isCall ? OptionRight.Call : OptionRight.Put, Strike = point.Strike, Expiry = report.Expiry,
            };
            var request = new PricingRequest(new Trade("fit", option, 1), snapshot, localVol, Pde, snapshot.AsOf, NoMeasures);
            double localPrice = risk.Compute(request, new RiskConfig()).Pv;

            double Implied(double price) => ImpliedVol.Solve(price, forward, point.Strike, t, discount, isCall) ?? double.NaN;
            double svi = Math.Sqrt(report.Slice.TotalVariance(Math.Log(point.Strike / report.Slice.Forward)) / t);
            points.Add(new SmileFit(point.Strike, point.Vol, svi, Implied(hestonPrice), Implied(localPrice)));
        }

        var fits = points.ToImmutable();
        double Rmse(Func<SmileFit, double> model)
        {
            double[] errors = [.. fits.Select(p => model(p) - p.Market).Where(e => !double.IsNaN(e))];
            return errors.Length == 0 ? double.NaN : Math.Sqrt(errors.Average(e => e * e));
        }
        return new SliceFit(report.Expiry, t, Rmse(p => p.Svi), Rmse(p => p.Heston), Rmse(p => p.LocalVol), fits);
    }
}
