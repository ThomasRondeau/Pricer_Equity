using System.Collections.Immutable;

namespace Pricer.Market;

/// <summary>Continuously compounded zero rates, linear between pillars and flat outside.</summary>
public sealed record YieldCurve(ImmutableArray<double> Times, ImmutableArray<double> ZeroRates)
{
    public static YieldCurve Flat(double rate) => new([0.0], [rate]);

    public double ZeroRate(double t)
    {
        if (t <= Times[0]) return ZeroRates[0];
        for (int i = 1; i < Times.Length; i++)
        {
            if (t <= Times[i])
            {
                double w = (t - Times[i - 1]) / (Times[i] - Times[i - 1]);
                return ZeroRates[i - 1] + w * (ZeroRates[i] - ZeroRates[i - 1]);
            }
        }
        return ZeroRates[^1];
    }

    public double Discount(double t) => Math.Exp(-ZeroRate(t) * t);

    public YieldCurve Shifted(double shift) => this with { ZeroRates = [.. ZeroRates.Select(r => r + shift)] };
}
