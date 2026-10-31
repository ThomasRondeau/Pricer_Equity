using MathNet.Numerics.Distributions;

namespace Pricer.Models;

public readonly record struct BlackScholesInputs(
    double Spot, double Strike, double T, double Vol, double DiscountRate, double DividendYield);

/// <summary>Raw sensitivities: per unit of spot, vol and rate; theta per year of calendar time.</summary>
public readonly record struct BlackScholesSensitivities(
    double Price, double Delta, double Gamma, double Vega, double Theta, double Rho);

public static class BlackScholes
{
    private const double MinTotalVol = 1e-12;

    public static BlackScholesSensitivities Evaluate(BlackScholesInputs x, bool isCall)
    {
        double sign = isCall ? 1 : -1;
        if (x.T <= 0)
        {
            double intrinsic = Math.Max(sign * (x.Spot - x.Strike), 0);
            return new(intrinsic, intrinsic > 0 ? sign : 0, 0, 0, 0, 0);
        }

        double dfRate = Math.Exp(-x.DiscountRate * x.T);
        double dfDiv = Math.Exp(-x.DividendYield * x.T);
        double totalVol = x.Vol * Math.Sqrt(x.T);

        if (totalVol < MinTotalVol)
        {
            double value = sign * (x.Spot * dfDiv - x.Strike * dfRate);
            return value <= 0
                ? default
                : new(value, sign * dfDiv, 0, 0,
                    sign * (x.DividendYield * x.Spot * dfDiv - x.DiscountRate * x.Strike * dfRate),
                    sign * x.Strike * x.T * dfRate);
        }

        double forward = x.Spot * dfDiv / dfRate;
        double d1 = Math.Log(forward / x.Strike) / totalVol + 0.5 * totalVol;
        double d2 = d1 - totalVol;
        double nd1 = Normal.CDF(0, 1, sign * d1);
        double nd2 = Normal.CDF(0, 1, sign * d2);
        double pdf = Normal.PDF(0, 1, d1);

        double price = sign * (x.Spot * dfDiv * nd1 - x.Strike * dfRate * nd2);
        double delta = sign * dfDiv * nd1;
        double gamma = dfDiv * pdf / (x.Spot * totalVol);
        double vega = x.Spot * dfDiv * pdf * Math.Sqrt(x.T);
        double theta = -x.Spot * dfDiv * pdf * x.Vol / (2 * Math.Sqrt(x.T))
            + sign * (x.DividendYield * x.Spot * dfDiv * nd1 - x.DiscountRate * x.Strike * dfRate * nd2);
        double rho = sign * x.Strike * x.T * dfRate * nd2;

        return new(price, delta, gamma, vega, theta, rho);
    }
}
