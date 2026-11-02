using MathNet.Numerics.Distributions;

namespace Pricer.Models;

public readonly record struct DigitalSensitivities(double Price, double Delta, double Gamma, double Vega);

/// <summary>Cash-or-nothing, per unit of cash paid.</summary>
public static class Digital
{
    public static DigitalSensitivities Evaluate(BlackScholesInputs x, bool isCall)
    {
        double sign = isCall ? 1 : -1;
        double df = Math.Exp(-x.DiscountRate * x.T);
        double totalVol = x.Vol * Math.Sqrt(x.T);
        double forward = x.Spot * Math.Exp(-x.DividendYield * x.T) / df;
        double d1 = Math.Log(forward / x.Strike) / totalVol + 0.5 * totalVol;
        double d2 = d1 - totalVol;
        double pdf = Normal.PDF(0, 1, d2);

        return new(
            df * Normal.CDF(0, 1, sign * d2),
            sign * df * pdf / (x.Spot * totalVol),
            -sign * df * pdf * d1 / (x.Spot * x.Spot * totalVol * totalVol),
            -sign * df * pdf * d1 / x.Vol);
    }
}

/// <summary>Reiner-Rubinstein single barrier options, continuously monitored and without rebate.</summary>
public static class ReinerRubinstein
{
    public static double Price(BlackScholesInputs x, double barrier, bool isCall, bool isUp, bool isOut)
    {
        double h = barrier;
        double phi = isCall ? 1 : -1;
        double eta = isUp ? -1 : 1;
        if (isUp ? x.Spot >= h : x.Spot <= h)
        {
            return isOut ? 0 : BlackScholes.Evaluate(x, isCall).Price;
        }

        double dfRate = Math.Exp(-x.DiscountRate * x.T);
        double dfDiv = Math.Exp(-x.DividendYield * x.T);
        double totalVol = x.Vol * Math.Sqrt(x.T);
        double mu = (x.DiscountRate - x.DividendYield - 0.5 * x.Vol * x.Vol) / (x.Vol * x.Vol);
        double shift = (1 + mu) * totalVol;

        double reflection = Math.Pow(h / x.Spot, 2 * mu);

        double Direct(double y)
        {
            return phi * x.Spot * dfDiv * Normal.CDF(0, 1, phi * y) - phi * x.Strike * dfRate * Normal.CDF(0, 1, phi * (y - totalVol));
        }

        double Reflected(double y)
        {
            return reflection * (phi * x.Spot * dfDiv * Math.Pow(h / x.Spot, 2) * Normal.CDF(0, 1, eta * y)
                - phi * x.Strike * dfRate * Normal.CDF(0, 1, eta * (y - totalVol)));
        }

        double a = Direct(Math.Log(x.Spot / x.Strike) / totalVol + shift);
        double b = Direct(Math.Log(x.Spot / h) / totalVol + shift);
        double c = Reflected(Math.Log(h * h / (x.Spot * x.Strike)) / totalVol + shift);
        double d = Reflected(Math.Log(h / x.Spot) / totalVol + shift);

        bool strikeAbove = x.Strike >= h;
        return (isOut, isUp, isCall, strikeAbove) switch
        {
            (false, false, true, true) => c,
            (false, false, true, false) => a - b + d,
            (false, true, true, true) => a,
            (false, true, true, false) => b - c + d,
            (false, false, false, true) => b - c + d,
            (false, false, false, false) => a,
            (false, true, false, true) => a - b + d,
            (false, true, false, false) => c,
            (true, false, true, true) => a - c,
            (true, false, true, false) => b - d,
            (true, true, true, true) => 0,
            (true, true, true, false) => a - b + c - d,
            (true, false, false, true) => a - b + c - d,
            (true, false, false, false) => 0,
            (true, true, false, true) => b - d,
            (true, true, false, false) => a - c,
        };
    }
}
