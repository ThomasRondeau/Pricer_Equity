using Pricer.Market;

namespace Pricer.Models;

/// <summary>Dupire local vol from Gatheral's formula in total variance, tabulated by time and forward
/// log-moneyness y = ln(S / F(t)). Rows are built for the engine's own time grid.</summary>
public sealed class LocalVolGrid
{
    private const int Points = 161;
    private const double MinReach = 1;
    private const double StdDevReach = 5;
    private const double MoneynessStep = 0.01;
    private const double MinTimeStep = 0.005;
    private const double RelativeTimeStep = 0.02;
    private const double MinDenominator = 1e-3;

    private readonly double _low;
    private readonly double _step;
    private readonly double[][] _rows;

    /// <param name="forward">Forward of the underlying by time.</param>
    /// <param name="times">Times to tabulate, strictly positive.</param>
    public LocalVolGrid(VolSurface surface, Func<double, double> forward, double[] times, double minVol, double maxVol)
    {
        double last = times.Max();
        double reach = Math.Max(MinReach, StdDevReach * surface.Vol(forward(last), last) * Math.Sqrt(last));
        _low = -reach;
        _step = 2 * reach / (Points - 1);
        _rows = new double[times.Length][];
        int clamped = 0;

        for (int i = 0; i < times.Length; i++)
        {
            double[] row = new double[Points];
            for (int j = 0; j < Points; j++)
            {
                double vol = Math.Sqrt(LocalVariance(surface, forward, times[i], _low + j * _step, ref clamped));
                row[j] = Math.Clamp(vol, minVol, maxVol);
            }
            _rows[i] = row;
        }
        ClampedShare = clamped / (double)(times.Length * Points);
    }

    /// <summary>Share of the grid where the surface gave no valid local variance, so implied vol was used.</summary>
    public double ClampedShare { get; }

    public double[] Row(int index) => _rows[index];

    /// <summary>Linear between nodes, flat outside the grid.</summary>
    public double Lookup(double[] row, double y)
    {
        double position = (y - _low) / _step;
        if (position <= 0) return row[0];
        if (position >= Points - 1) return row[Points - 1];
        int node = (int)position;
        double weight = position - node;
        return row[node] + weight * (row[node + 1] - row[node]);
    }

    private static double LocalVariance(VolSurface surface, Func<double, double> forward, double t, double y, ref int clamped)
    {
        double Total(double k, double time)
        {
            double vol = surface.Vol(forward(time) * Math.Exp(k), time);
            return vol * vol * time;
        }

        double h = Math.Max(MinTimeStep, RelativeTimeStep * t);
        double before = Math.Max(t - h, t / 2);
        double w = Total(y, t);
        double up = Total(y + MoneynessStep, t);
        double down = Total(y - MoneynessStep, t);
        double slope = (up - down) / (2 * MoneynessStep);
        double curvature = (up - 2 * w + down) / (MoneynessStep * MoneynessStep);
        double dwdt = (Total(y, t + h) - Total(y, before)) / (t + h - before);
        double denominator = 1 - y / w * slope + 0.25 * (-0.25 - 1 / w + y * y / (w * w)) * slope * slope + 0.5 * curvature;

        if (dwdt > 0 && denominator > MinDenominator) return dwdt / denominator;
        clamped++;
        return w / t;
    }
}
