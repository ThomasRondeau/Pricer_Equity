using MathNet.Numerics.RootFinding;

namespace Pricer.Models;

public static class ImpliedVol
{
    private const double MinVol = 1e-4;
    private const double MaxVol = 5.0;

    /// <summary>European implied vol from a forward. Null when the price is outside no-arbitrage bounds.</summary>
    public static double? Solve(double price, double forward, double strike, double t, double discount, bool isCall)
    {
        double rate = -Math.Log(discount) / t;
        double Value(double vol) =>
            BlackScholes.Evaluate(new(forward, strike, t, vol, rate, rate), isCall).Price;

        if (price <= Value(MinVol) || price >= Value(MaxVol)) return null;
        return Brent.TryFindRoot(v => Value(v) - price, MinVol, MaxVol, 1e-8, 100, out double vol) ? vol : null;
    }
}
