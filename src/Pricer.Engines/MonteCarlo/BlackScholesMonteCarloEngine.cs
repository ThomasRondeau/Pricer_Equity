using System.Collections.Immutable;
using System.Numerics;
using Pricer.Core;
using Pricer.Engines.MonteCarlo;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Engines;

/// <summary>Randomised Sobol with Brownian bridge, then padded with a generator past the Sobol dimensions.
/// The bridge gives the Sobol dimensions the largest moves of the path. Paths are exact at event dates.
/// Delta, gamma and vega come from the simulation itself; theta and rho are left to the bump fallback,
/// which reuses the same numbers through the seed.</summary>
public sealed class BlackScholesMonteCarloEngine : IPricingEngine
{
    private const double VolPoint = 0.01;
    private const int Delta = 0;
    private const int Vega = 1;
    private const int Tangents = 2;

    private enum Stat
    {
        Pv,
        PathwiseDelta,
        PathwiseVega,
        MixedGamma,
        ScoreDelta,
        ScoreGamma,
        ScoreVega,
    }

    private static readonly int StatCount = Enum.GetValues<Stat>().Length;

    public string Name => "BlackScholes.MonteCarlo";

    private sealed record Setup(
        PayoffProgram Program, double[] Times, double[] Forwards, Dictionary<DateOnly, int> Index, double[] Discounts,
        BridgeStep[] Bridge, double Spot, double Vol, int Paths, MonteCarloEngineConfig Config, bool WantTangents, bool WantScores);

    private readonly record struct BridgeStep(int Target, int Left, int Right, double LeftWeight, double RightWeight, double Noise);

    public PricingResult Price(PricingRequest request)
    {
        var option = (OptionInstrument)request.Trade.Instrument;
        var config = (MonteCarloEngineConfig)request.Engine;
        var market = request.Market.Underlying(option.Underlying);
        double quantity = request.Trade.Quantity;
        var valuation = request.ValuationDate;
        double t = DayCount.Act365F.YearFraction(valuation, option.Expiry);

        if (t < 0) return Fixed(request, 0);
        if (t == 0) return Fixed(request, quantity * PayoffCompiler.AtExpiry(option, market.Spot));

        var x = EngineInputs.For(option, request, t);
        if (x.Vol <= 0) throw new ArgumentException("Le Monte Carlo demande une vol strictement positive.");
        var program = PayoffCompiler.Compile(option, valuation, market.Spot, x.Vol);
        if (program.Events.Length == 0) return Fixed(request, 0);

        bool pathwise = config.Greeks == McGreekChoice.Auto && (program.IsContinuous || config.Smoothing > 0);
        bool wantGreeks = request.Measures.Any(m => m is Measure.Delta or Measure.Gamma or Measure.Vega);
        var setup = Prepare(request, market, program, x.Vol, config, wantGreeks, pathwise);

        var stats = new double[config.Batches][];
        Parallel.For(0, config.Batches, batch => stats[batch] = RunBatch(setup, batch));

        double Mean(Stat s) => stats.Average(b => b[(int)s]);
        double Error(Stat s)
        {
            double mean = Mean(s);
            double variance = stats.Sum(b => Math.Pow(b[(int)s] - mean, 2)) / (config.Batches - 1);
            return Math.Sqrt(variance / config.Batches);
        }

        var greeks = ImmutableArray.CreateBuilder<GreekValue>();
        void Add(Measure measure, Stat stat, GreekMethod method, double scale)
        {
            if (request.Measures.Contains(measure))
            {
                greeks.Add(new GreekValue(
                    measure, scale * Mean(stat), method, GreekConvention.Model, Math.Abs(scale) * Error(stat)));
            }
        }

        if (pathwise)
        {
            Add(Measure.Delta, Stat.PathwiseDelta, GreekMethod.Pathwise, quantity);
            Add(Measure.Gamma, Stat.MixedGamma, GreekMethod.PathwiseLikelihoodRatio, quantity);
            Add(Measure.Vega, Stat.PathwiseVega, GreekMethod.Pathwise, quantity * VolPoint);
        }
        else
        {
            Add(Measure.Delta, Stat.ScoreDelta, GreekMethod.LikelihoodRatio, quantity);
            Add(Measure.Gamma, Stat.ScoreGamma, GreekMethod.LikelihoodRatio, quantity);
            Add(Measure.Vega, Stat.ScoreVega, GreekMethod.LikelihoodRatio, quantity * VolPoint);
        }
        return new PricingResult(quantity * Mean(Stat.Pv), Math.Abs(quantity) * Error(Stat.Pv), Name, greeks.ToImmutable());
    }

    private PricingResult Fixed(PricingRequest request, double pv) => new(pv, 0, Name, [.. request.Measures
        .Where(m => m is Measure.Delta or Measure.Gamma or Measure.Vega)
        .Select(m => new GreekValue(m, 0, GreekMethod.ClosedForm, GreekConvention.Model))]);

    private static Setup Prepare(
        PricingRequest request, UnderlyingMarket market, PayoffProgram program, double vol,
        MonteCarloEngineConfig config, bool wantGreeks, bool pathwise)
    {
        var dates = program.Events.Select(e => e.Date).Distinct().Order().ToArray();
        double[] times = [.. dates.Select(d => DayCount.Act365F.YearFraction(request.ValuationDate, d))];
        double[] discounts = [.. times.Select(request.Market.DiscountCurve.Discount)];
        double[] forwards = [.. times.Select((t, i) => market.Spot * market.DividendCurve.Discount(t) / discounts[i])];
        var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);

        // Powers of two keep the Sobol set balanced.
        int paths = 1 << BitOperations.Log2((uint)Math.Max(config.Paths / config.Batches * 2 - 1, 1));
        return new Setup(
            program, times, forwards, index, discounts, BuildBridge(times), market.Spot, vol, paths, config,
            wantGreeks && pathwise, wantGreeks && !pathwise);
    }

    /// <summary>Terminal point first, then midpoints breadth-first.</summary>
    private static BridgeStep[] BuildBridge(double[] times)
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

    private static double[] RunBatch(Setup s, int batch)
    {
        int n = s.Paths;
        int m = s.Times.Length;
        var random = new SplitMix(Mix((ulong)s.Config.Seed, (ulong)batch));
        var uniforms = new double[n];
        var normals = new double[n];
        var brownian = new double[m][];
        for (int i = 0; i < m; i++) brownian[i] = new double[n];

        for (int dimension = 0; dimension < s.Bridge.Length; dimension++)
        {
            uint shift = (uint)random.Next();
            if (dimension < Sobol.MaxDimensions)
            {
                Sobol.Fill(dimension, shift, uniforms);
            }
            else
            {
                for (int i = 0; i < n; i++) uniforms[i] = random.NextUniform();
            }
            for (int i = 0; i < n; i++) normals[i] = InverseNormal.Evaluate(uniforms[i]);
            ApplyBridge(s.Bridge[dimension], brownian, normals);
        }

        int tangents = s.WantTangents ? Tangents : 0;
        var evaluator = new PayoffEvaluator(s.Program.Slots, n, tangents, s.Config.Smoothing);
        var (spot, spotTangents) = evaluator.SpotBuffers();

        int current = -1;
        foreach (var payoffEvent in s.Program.Events.OrderBy(e => e.Date))
        {
            int date = s.Index[payoffEvent.Date];
            if (date != current)
            {
                current = date;
                FillSpot(s, date, brownian[date], spot);
                if (tangents > 0) FillTangents(s, date, brownian[date], spot, spotTangents);
            }
            evaluator.Run(payoffEvent, s.Discounts[date]);
        }

        var stats = new double[StatCount];
        double[] pv = evaluator.Value;
        stats[(int)Stat.Pv] = pv.Average();
        if (s.WantTangents) Pathwise(s, evaluator, brownian[0], stats);
        if (s.WantScores) Scores(s, pv, brownian, stats);
        return stats;
    }

    private static ulong Mix(ulong seed, ulong batch) => new SplitMix(seed * 0xD1342543DE82EF95 + batch).Next();

    private static void ApplyBridge(in BridgeStep step, double[][] brownian, double[] normals)
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

    /// <summary>S(t) = F(t) exp(vol W(t) - vol^2 t / 2).</summary>
    private static void FillSpot(Setup s, int date, double[] brownian, double[] spot)
    {
        double t = s.Times[date];
        double forward = s.Forwards[date];
        var drift = new Vector<double>(-0.5 * s.Vol * s.Vol * t);
        var vol = new Vector<double>(s.Vol);
        var level = new Vector<double>(forward);
        int width = Vector<double>.Count;
        int i = 0;
        for (; i <= spot.Length - width; i += width)
        {
            (level * Vector.Exp(vol * new Vector<double>(brownian, i) + drift)).CopyTo(spot, i);
        }
        for (; i < spot.Length; i++) spot[i] = forward * Math.Exp(s.Vol * brownian[i] - 0.5 * s.Vol * s.Vol * t);
    }

    private static void FillTangents(Setup s, int date, double[] brownian, double[] spot, double[][] tangents)
    {
        double t = s.Times[date];
        double[] delta = tangents[Delta], vega = tangents[Vega];
        for (int i = 0; i < spot.Length; i++)
        {
            delta[i] = spot[i] / s.Spot;
            vega[i] = spot[i] * (brownian[i] - s.Vol * t);
        }
    }

    private static void Pathwise(Setup s, PayoffEvaluator evaluator, double[] firstBrownian, double[] stats)
    {
        double root = Math.Sqrt(s.Times[0]);
        double score = 1 / (s.Spot * s.Vol * root * root);
        double[] delta = evaluator.Tangent(Delta);
        double crossed = 0;
        for (int i = 0; i < delta.Length; i++) crossed += delta[i] * firstBrownian[i];
        double meanDelta = delta.Average();

        stats[(int)Stat.PathwiseDelta] = meanDelta;
        stats[(int)Stat.PathwiseVega] = evaluator.Tangent(Vega).Average();
        // With S proportional to S0, gamma = E[pathwise delta * delta score] - delta / S0.
        stats[(int)Stat.MixedGamma] = crossed / delta.Length * score - meanDelta / s.Spot;
    }

    /// <summary>Weights from the lognormal density. Only the first step depends on the initial spot,
    /// so a short first step means noisy delta and gamma.</summary>
    private static void Scores(Setup s, double[] pv, double[][] brownian, double[] stats)
    {
        int n = pv.Length;
        double t1 = s.Times[0];
        double root = Math.Sqrt(t1);
        double vol = s.Vol;
        double delta = 0, gamma = 0, vega = 0;

        for (int i = 0; i < n; i++)
        {
            double z = brownian[0][i] / root;
            delta += pv[i] * z;
            gamma += pv[i] * (z * z - z * vol * root - 1);
        }

        var weights = new double[n];
        for (int step = 0; step < s.Times.Length; step++)
        {
            double previous = step == 0 ? 0 : s.Times[step - 1];
            double dt = Math.Sqrt(s.Times[step] - previous);
            double[] end = brownian[step];
            double[]? start = step == 0 ? null : brownian[step - 1];
            for (int i = 0; i < n; i++)
            {
                double z = (end[i] - (start?[i] ?? 0)) / dt;
                weights[i] += (z * z - 1) / vol - z * dt;
            }
        }
        for (int i = 0; i < n; i++) vega += pv[i] * weights[i];

        stats[(int)Stat.ScoreDelta] = delta / n / (s.Spot * vol * root);
        stats[(int)Stat.ScoreGamma] = gamma / n / (s.Spot * s.Spot * vol * vol * t1);
        stats[(int)Stat.ScoreVega] = vega / n;
    }
}
