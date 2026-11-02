using Complex = System.Numerics.Complex;
using Vec = MathNet.Numerics.LinearAlgebra.Vector<double>;
using MathNet.Numerics.Integration;
using MathNet.Numerics.Optimization;
using Pricer.Market;

namespace Pricer.Models;

/// <summary>Per unit of forward and spot as given; VegaV0 is the sensitivity to the instantaneous vol sqrt(v0),
/// a model greek and not a market vega.</summary>
public readonly record struct HestonSensitivities(double Price, double Delta, double Gamma, double VegaV0);

/// <summary>Lewis's single integral with the "little trap" characteristic function. All the greeks
/// come from one pass over the same integrand, whose dependence on spot and v0 is explicit.</summary>
public sealed class HestonSlice
{
    private const int Nodes = 192;
    private const double TailLog = 30;
    private const double MinRange = 40;
    private const double MaxRange = 3000;
    private static readonly GaussLegendreRule Rule = new(0, 1, Nodes);

    private readonly double _t;
    private readonly double[] _u = new double[Nodes];
    private readonly double[] _weight = new double[Nodes];
    private readonly Complex[] _phi = new Complex[Nodes];
    private readonly Complex[] _dv0 = new Complex[Nodes];
    private readonly double _rootV0;

    public HestonSlice(HestonConfig p, double t)
    {
        _t = t;
        _rootV0 = Math.Sqrt(p.V0);
        // The integrand decays as exp(-c u), with c from Lord and Kahl.
        double decay = Math.Sqrt(1 - p.Rho * p.Rho) / p.Xi * (p.V0 + p.Kappa * p.Theta * t);
        double range = Math.Clamp(TailLog / decay, MinRange, MaxRange);

        for (int i = 0; i < Nodes; i++)
        {
            double u = Rule.GetAbscissa(i) * range;
            _u[i] = u;
            _weight[i] = Rule.GetWeight(i) * range / (u * u + 0.25);
            (_phi[i], _dv0[i]) = Characteristic(p, new Complex(u, -0.5), t);
        }
    }

    /// <summary>E[exp(iu ln(S_T / F))] and its derivative in v0.</summary>
    private static (Complex Value, Complex DV0) Characteristic(HestonConfig p, Complex u, double t)
    {
        var iu = Complex.ImaginaryOne * u;
        var beta = p.Kappa - p.Rho * p.Xi * iu;
        var d = Complex.Sqrt(beta * beta + p.Xi * p.Xi * (iu + u * u));
        var g = (beta - d) / (beta + d);
        var decay = Complex.Exp(-d * t);
        var fraction = 1 - g * decay;

        var a = p.Kappa * p.Theta / (p.Xi * p.Xi) * ((beta - d) * t - 2 * Complex.Log(fraction / (1 - g)));
        var b = (beta - d) / (p.Xi * p.Xi) * (1 - decay) / fraction;
        var value = Complex.Exp(a + b * p.V0);
        return (value, b * value);
    }

    public HestonSensitivities Evaluate(double spot, double forward, double strike, double discount, bool isCall)
    {
        double x = Math.Log(forward / strike);
        double i0 = 0, i1 = 0, i2 = 0, iv = 0;
        for (int i = 0; i < Nodes; i++)
        {
            double u = _u[i];
            var phase = new Complex(Math.Cos(u * x), Math.Sin(u * x));
            var value = phase * _phi[i];
            i0 += _weight[i] * value.Real;
            i1 -= _weight[i] * u * value.Imaginary;
            i2 -= _weight[i] * u * u * value.Real;
            iv += _weight[i] * (phase * _dv0[i]).Real;
        }

        double root = Math.Sqrt(forward * strike);
        double scale = Math.Sqrt(strike / forward) / Math.PI;
        double call = discount * (forward - root * i0 / Math.PI);
        double slope = scale * (i0 / 2 + i1);
        double dF = discount * (1 - slope);
        double ddF = -discount * (-0.5 * slope + scale * (i1 / 2 + i2)) / forward;
        double dV0 = -discount * root * iv / Math.PI;

        double price = isCall ? call : call - discount * (forward - strike);
        double delta = (isCall ? dF : dF - discount) * forward / spot;
        double gamma = ddF * forward * forward / (spot * spot);
        return new(price, delta, gamma, 2 * _rootV0 * dV0);
    }
}

public sealed record HestonFit(HestonConfig Config, double RmseVol);

/// <summary>Least squares on the vol-equivalent error (price error over vega) against the market surface.</summary>
public static class HestonCalibrator
{
    private const int ColdIterations = 80;
    private const int WarmIterations = 12;
    private const double MinVega = 1e-6;
    private static readonly double[] Times = [0.1, 0.25, 0.5, 1, 2];
    private static readonly double[] Moneyness = [-2, -1, -0.5, 0, 0.5, 1];

    private sealed record Quote(double Strike, bool IsCall, double Market, double Vega);

    private sealed record Maturity(double T, double Forward, double Discount, Quote[] Quotes);

    /// <param name="start">Previous fit: a warm start after a small market change.</param>
    public static HestonFit Fit(MarketSnapshot snapshot, string underlying, HestonConfig? start = null)
    {
        var market = snapshot.Underlying(underlying);
        var maturities = Targets(snapshot, market);
        int count = maturities.Sum(m => m.Quotes.Length);
        var indices = Vec.Build.Dense(count, i => i);
        var zeros = Vec.Build.Dense(count);

        double atm = market.Vol.Vol(market.Spot, 1);
        var first = start ?? new HestonConfig { V0 = atm * atm, Theta = atm * atm };
        Vec Pack(HestonConfig c) => Vec.Build.DenseOfArray([c.V0, c.Kappa, c.Theta, c.Xi, c.Rho]);
        HestonConfig Unpack(Vec p) => first with { V0 = p[0], Kappa = p[1], Theta = p[2], Xi = p[3], Rho = p[4] };

        Vec Errors(Vec p, Vec _)
        {
            var config = Unpack(p);
            var errors = Vec.Build.Dense(count);
            int n = 0;
            foreach (var maturity in maturities)
            {
                var slice = new HestonSlice(config, maturity.T);
                foreach (var q in maturity.Quotes)
                {
                    double price = slice.Evaluate(market.Spot, maturity.Forward, q.Strike, maturity.Discount, q.IsCall).Price;
                    errors[n++] = (price - q.Market) / q.Vega;
                }
            }
            return errors;
        }

        var lower = Vec.Build.DenseOfArray([0.0005, 0.05, 0.0005, 0.02, -0.99]);
        var upper = Vec.Build.DenseOfArray([1, 15, 1, 3, 0.99]);
        var minimizer = new LevenbergMarquardtMinimizer(maximumIterations: start is null ? ColdIterations : WarmIterations);
        var objective = ObjectiveFunction.NonlinearModel(Errors, indices, zeros);
        var best = minimizer.FindMinimum(objective, Pack(first), lower, upper).MinimizingPoint;

        var residuals = Errors(best, indices);
        return new HestonFit(Unpack(best), Math.Sqrt(residuals.DotProduct(residuals) / count));
    }

    private static List<Maturity> Targets(MarketSnapshot snapshot, UnderlyingMarket market)
    {
        double[] times = market.Vol is SviVolSurface svi
            ? [.. Times.Select(t => Math.Clamp(t, svi.Slices[0].T, svi.Slices[^1].T)).Distinct()]
            : Times;

        var maturities = new List<Maturity>();
        foreach (double t in times)
        {
            double discount = snapshot.DiscountCurve.Discount(t);
            double forward = market.Spot * market.DividendCurve.Discount(t) / discount;
            double rate = -Math.Log(discount) / t;
            double atm = market.Vol.Vol(forward, t);
            var quotes = new List<Quote>();
            foreach (double z in Moneyness)
            {
                double strike = forward * Math.Exp(z * atm * Math.Sqrt(t));
                bool isCall = strike >= forward;
                var s = BlackScholes.Evaluate(new(forward, strike, t, market.Vol.Vol(strike, t), rate, rate), isCall);
                quotes.Add(new Quote(strike, isCall, s.Price, Math.Max(s.Vega, MinVega)));
            }
            maturities.Add(new Maturity(t, forward, discount, [.. quotes]));
        }
        return maturities;
    }
}
