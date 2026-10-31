using System.Collections.Immutable;

namespace Pricer.Market;

/// <summary>Raw SVI slice: w(k) = a + b(rho(k - m) + sqrt((k - m)^2 + sigma^2)), with k = ln(K / F).</summary>
public sealed record SviSlice(double T, double Forward, double A, double B, double Rho, double M, double Sigma)
{
    public double TotalVariance(double k)
    {
        double x = k - M;
        return A + B * (Rho * x + Math.Sqrt(x * x + Sigma * Sigma));
    }

    /// <summary>Gatheral's density function; negative values mean butterfly arbitrage.</summary>
    public double ButterflyDensity(double k)
    {
        double x = k - M;
        double root = Math.Sqrt(x * x + Sigma * Sigma);
        double w = TotalVariance(k);
        double dw = B * (Rho + x / root);
        double ddw = B * Sigma * Sigma / (root * root * root);
        double term = 1 - k * dw / (2 * w);
        return term * term - dw * dw / 4 * (1 / w + 0.25) + ddw / 2;
    }
}

/// <summary>Slices are keyed by the forward at calibration, so implied vol stays fixed by strike
/// when spot moves (sticky strike). Total variance is linear in time at constant forward moneyness.</summary>
public sealed record SviVolSurface(ImmutableArray<SviSlice> Slices, double Shift = 0) : VolSurface
{
    public override double Vol(double strike, double t)
    {
        var first = Slices[0];
        var last = Slices[^1];
        if (t <= first.T) return FlatExtrapolation(first, strike);
        if (t >= last.T) return FlatExtrapolation(last, strike);

        int i = 1;
        while (Slices[i].T < t) i++;
        var left = Slices[i - 1];
        var right = Slices[i];
        double weight = (t - left.T) / (right.T - left.T);
        double forward = Math.Exp(Math.Log(left.Forward) + weight * (Math.Log(right.Forward) - Math.Log(left.Forward)));
        double k = Math.Log(strike / forward);
        double w = left.TotalVariance(k) + weight * (right.TotalVariance(k) - left.TotalVariance(k));
        return Math.Sqrt(w / t) + Shift;
    }

    public override VolSurface Shifted(double shift) => this with { Shift = Shift + shift };

    private double FlatExtrapolation(SviSlice slice, double strike) =>
        Math.Sqrt(slice.TotalVariance(Math.Log(strike / slice.Forward)) / slice.T) + Shift;
}
