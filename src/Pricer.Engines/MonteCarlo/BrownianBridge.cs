using System.Numerics;

namespace Pricer.Engines.MonteCarlo;

internal readonly record struct BridgeStep(int Target, int Left, int Right, double LeftWeight, double RightWeight, double Noise);

/// <summary>Builds Brownian paths from the coarsest move to the finest, so that the first, best
/// distributed dimensions of a Sobol sequence set the shape of the path.</summary>
internal static class BrownianBridge
{
    /// <summary>Terminal point first, then midpoints breadth-first.</summary>
    public static BridgeStep[] Build(double[] times)
    {
        int m = times.Length;
        var steps = new List<BridgeStep> { new(m - 1, -1, -1, 0, 0, Math.Sqrt(times[m - 1])) };
        var queue = new Queue<(int Left, int Right)>();
        queue.Enqueue((-1, m - 1));
        while (queue.TryDequeue(out var span))
        {
            if (span.Right - span.Left < 2) continue;
            int middle = (span.Left + span.Right) / 2;
            double tl = span.Left < 0 ? 0 : times[span.Left];
            double tr = times[span.Right];
            double tm = times[middle];
            double width = tr - tl;
            steps.Add(new BridgeStep(
                middle, span.Left, span.Right, (tr - tm) / width, (tm - tl) / width, Math.Sqrt((tm - tl) * (tr - tm) / width)));
            queue.Enqueue((span.Left, middle));
            queue.Enqueue((middle, span.Right));
        }
        return [.. steps];
    }

    public static void Apply(in BridgeStep step, double[][] brownian, double[] normals)
    {
        double[] target = brownian[step.Target];
        var noise = new Vector<double>(step.Noise);
        int width = Vector<double>.Count;
        int i = 0;
        if (step.Left < 0 && step.Right < 0)
        {
            for (; i <= target.Length - width; i += width) (noise * new Vector<double>(normals, i)).CopyTo(target, i);
            for (; i < target.Length; i++) target[i] = step.Noise * normals[i];
            return;
        }

        double[] right = brownian[step.Right];
        var rightWeight = new Vector<double>(step.RightWeight);
        if (step.Left < 0)
        {
            for (; i <= target.Length - width; i += width)
            {
                (rightWeight * new Vector<double>(right, i) + noise * new Vector<double>(normals, i)).CopyTo(target, i);
            }
            for (; i < target.Length; i++) target[i] = step.RightWeight * right[i] + step.Noise * normals[i];
            return;
        }

        double[] left = brownian[step.Left];
        var leftWeight = new Vector<double>(step.LeftWeight);
        for (; i <= target.Length - width; i += width)
        {
            (leftWeight * new Vector<double>(left, i) + rightWeight * new Vector<double>(right, i)
                + noise * new Vector<double>(normals, i)).CopyTo(target, i);
        }
        for (; i < target.Length; i++)
        {
            target[i] = step.LeftWeight * left[i] + step.RightWeight * right[i] + step.Noise * normals[i];
        }
    }
}

internal static class McSizing
{
    /// <summary>Powers of two keep the Sobol set balanced.</summary>
    public static int PathsPerBatch(MonteCarloEngineConfig config) =>
        1 << BitOperations.Log2((uint)Math.Max(config.Paths / config.Batches * 2 - 1, 1));

    public static ulong BatchSeed(int seed, int batch) =>
        new SplitMix((ulong)seed * 0xD1342543DE82EF95 + (ulong)batch).Next();
}
