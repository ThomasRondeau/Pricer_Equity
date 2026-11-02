using System.Numerics;

namespace Pricer.Engines.MonteCarlo;

/// <summary>Sobol points with Joe-Kuo direction numbers (new-joe-kuo-6). Randomised by a digital shift,
/// so each batch is an independent copy of the same well-spread set.</summary>
public static class Sobol
{
    private const int Bits = 32;
    private const double Scale = 1.0 / (1L << Bits);

    /// <summary>Degree s, polynomial a and initial numbers m for dimensions 2 and up.</summary>
    private static readonly (int S, uint A, uint[] M)[] Primitives =
    [
        (1, 0, [1]),
        (2, 1, [1, 3]),
        (3, 1, [1, 3, 1]),
        (3, 2, [1, 1, 1]),
        (4, 1, [1, 1, 3, 3]),
        (4, 4, [1, 3, 5, 13]),
        (5, 2, [1, 1, 5, 5, 17]),
        (5, 4, [1, 1, 5, 5, 5]),
        (5, 7, [1, 1, 7, 11, 19]),
        (5, 11, [1, 1, 5, 1, 1]),
        (5, 13, [1, 1, 1, 3, 11]),
        (5, 14, [1, 3, 5, 5, 31]),
        (6, 1, [1, 3, 3, 9, 7, 49]),
        (6, 13, [1, 1, 1, 15, 21, 21]),
        (6, 16, [1, 3, 1, 13, 27, 49]),
        (6, 19, [1, 1, 1, 15, 7, 5]),
        (6, 22, [1, 3, 1, 15, 13, 25]),
        (6, 25, [1, 1, 5, 5, 19, 61]),
        (7, 1, [1, 3, 7, 11, 23, 15, 103]),
        (7, 4, [1, 3, 7, 13, 13, 15, 69]),
    ];

    private static readonly uint[][] Directions = BuildDirections();

    public static int MaxDimensions => Directions.Length;

    /// <summary>Writes the first points of one dimension as uniforms strictly inside (0, 1).</summary>
    public static void Fill(int dimension, uint shift, Span<double> uniforms)
    {
        uint[] v = Directions[dimension];
        uint x = 0;
        for (int i = 0; i < uniforms.Length; i++)
        {
            uniforms[i] = ((x ^ shift) + 0.5) * Scale;
            x ^= v[BitOperations.TrailingZeroCount(i + 1)];
        }
    }

    private static uint[][] BuildDirections()
    {
        var all = new uint[Primitives.Length + 1][];
        all[0] = new uint[Bits];
        for (int i = 0; i < Bits; i++) all[0][i] = 1u << (Bits - 1 - i);

        for (int d = 0; d < Primitives.Length; d++)
        {
            var (s, a, m) = Primitives[d];
            var v = new uint[Bits];
            for (int i = 0; i < s; i++) v[i] = m[i] << (Bits - 1 - i);
            for (int i = s; i < Bits; i++)
            {
                v[i] = v[i - s] ^ (v[i - s] >> s);
                for (int k = 1; k < s; k++)
                {
                    v[i] ^= ((a >> (s - 1 - k)) & 1) * v[i - k];
                }
            }
            all[d + 1] = v;
        }
        return all;
    }
}

/// <summary>Deterministic generator, so that a run replays bit for bit whatever the thread count.</summary>
public struct SplitMix(ulong state)
{
    private ulong _state = state;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform strictly inside (0, 1).</summary>
    public double NextUniform() => ((Next() >> 11) + 0.5) / (1L << 53);
}

public static class InverseNormal
{
    private const double LowTail = 0.02425;
    private static readonly double[] A =
        [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
    private static readonly double[] B =
        [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
    private static readonly double[] C =
        [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
    private static readonly double[] D =
        [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

    /// <summary>Acklam's rational approximation, relative error about 1e-9.</summary>
    public static double Evaluate(double p)
    {
        if (p < LowTail) return Tail(Math.Sqrt(-2 * Math.Log(p)));
        if (p > 1 - LowTail) return -Tail(Math.Sqrt(-2 * Math.Log(1 - p)));

        double q = p - 0.5;
        double r = q * q;
        return (((((A[0] * r + A[1]) * r + A[2]) * r + A[3]) * r + A[4]) * r + A[5]) * q
            / (((((B[0] * r + B[1]) * r + B[2]) * r + B[3]) * r + B[4]) * r + 1);
    }

    private static double Tail(double q) =>
        (((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5])
        / ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1);
}
