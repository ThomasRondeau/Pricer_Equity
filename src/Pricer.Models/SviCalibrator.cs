using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Pricer.Market;

namespace Pricer.Models;

/// <summary>Fits one SVI slice in vol space. The minimum variance is a free parameter bounded at zero,
/// which keeps w(k) non-negative by construction; b at most 2 enforces Lee's wing bound.</summary>
public static class SviCalibrator
{
    public const int MinPoints = 6;

    private static readonly double[] RhoStarts = [-0.7, -0.3, 0.0];
    private static readonly double[] SigmaStarts = [0.05, 0.25];

    public static SviSlice Fit(double t, double forward, double[] ks, double[] vols, double[] weights)
    {
        if (ks.Length < MinPoints) throw new ArgumentException($"SVI needs at least {MinPoints} points.");

        double maxVariance = vols.Max(v => v * v * t);
        var x = Vector<double>.Build.DenseOfArray(ks);
        var y = Vector<double>.Build.DenseOfArray(vols);
        var w = Vector<double>.Build.DenseOfArray(weights);
        var lower = Vector<double>.Build.DenseOfArray([0, 1e-5, -0.999, -1, 1e-3]);
        var upper = Vector<double>.Build.DenseOfArray([maxVariance, 2, 0.999, 1, 2]);

        Vector<double> Model(Vector<double> p, Vector<double> k) =>
            k.Map(ki => Math.Sqrt(Math.Max(ToSlice(t, forward, p).TotalVariance(ki), 1e-12) / t));

        Vector<double>? best = null;
        double bestError = double.PositiveInfinity;
        var minimizer = new LevenbergMarquardtMinimizer(maximumIterations: 300);

        foreach (double rho in RhoStarts)
        {
            foreach (double sigma in SigmaStarts)
            {
                var start = Vector<double>.Build.DenseOfArray([vols.Min() * vols.Min() * t * 0.5, 0.1 * Math.Sqrt(t), rho, 0, sigma]);
                var objective = ObjectiveFunction.NonlinearModel(Model, x, y, w);
                var fit = minimizer.FindMinimum(objective, start, lower, upper);
                var residual = Model(fit.MinimizingPoint, x) - y;
                double error = residual.PointwiseMultiply(residual).DotProduct(w);
                if (error < bestError)
                {
                    bestError = error;
                    best = fit.MinimizingPoint;
                }
            }
        }

        return ToSlice(t, forward, best!);
    }

    private static SviSlice ToSlice(double t, double forward, Vector<double> p)
    {
        double b = p[1], rho = p[2], sigma = p[4];
        double a = p[0] - b * sigma * Math.Sqrt(1 - rho * rho);
        return new SviSlice(t, forward, a, b, rho, p[3], sigma);
    }
}
