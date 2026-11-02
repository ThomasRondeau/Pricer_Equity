using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Engines.MonteCarlo;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Engines;

/// <summary>Stepped simulation for models without exact transitions: local vol on one or several underlyings,
/// and Heston. The PV carries a standard error; greeks come from the bump fallback, with common random
/// numbers through the seed.</summary>
public sealed class PathSimulationEngine : IPricingEngine
{
    /// <summary>Euler's bias grows with the step relative to the horizon, so short trades need a finer frequency.</summary>
    private const double MinStepsPerPath = 64;

    public string Name => "Simulation";

    /// <summary>One batch of paths, advanced step by step in log-moneyness y = ln(S / F).</summary>
    private interface IPathBatch
    {
        void Advance(int step);
        void FillSpot(int asset, double forward, double[] spot);
    }

    private sealed record Schedule(double[] Times, int[] StepOfDate, PayoffEvent[][] EventsOfDate, double[] Discounts, double[][] Forwards);

    public PricingResult Price(PricingRequest request)
    {
        var instrument = request.Trade.Instrument;
        var product = (IDatedProduct)instrument;
        var config = (MonteCarloEngineConfig)request.Engine;
        var valuation = request.ValuationDate;
        var snapshot = request.Market;
        var names = instrument.Underlyings;
        string name = request.Model is LocalVolConfig ? "LocalVol.MonteCarlo" : "Heston.MonteCarlo";
        double quantity = request.Trade.Quantity;
        double t = DayCount.Act365F.YearFraction(valuation, product.Expiry);
        if (t < 0) return new PricingResult(0, 0, name, []);

        double spot = snapshot.Underlying(names[0]).Spot;
        double vol = EngineInputs.For(instrument, request, Math.Max(t, 1 / 365.0)).Vol;
        var program = PayoffCompiler.Compile(instrument, valuation, spot, vol, request.Trade.State);
        var replay = PayoffReplay.Run(program, names, valuation, request.Trade.State, u => snapshot.Underlying(u).Spot);
        if (replay.Remaining.Length == 0)
        {
            return new PricingResult(quantity * replay.DueToday, 0, name, [], quantity * replay.Realised);
        }

        var schedule = BuildSchedule(replay.Remaining, request, config);
        Func<int, int, IPathBatch> factory = request.Model switch
        {
            LocalVolConfig local => LocalVolFactory(request, local, schedule, config),
            HestonConfig heston => (batch, paths) => new HestonBatch(
                heston, schedule.Times, paths, McSizing.BatchSeed(config.Seed, batch)),
            _ => throw new NotSupportedException(request.Model.GetType().Name),
        };

        int paths = McSizing.PathsPerBatch(config);
        var means = new double[config.Batches];
        Parallel.For(0, config.Batches, batch =>
        {
            var evaluator = new PayoffEvaluator(program.Slots, names.Length, paths, 0, 0);
            evaluator.Seed(replay.Slots);
            var process = factory(batch, paths);
            int date = 0;
            for (int step = 0; step < schedule.Times.Length; step++)
            {
                process.Advance(step);
                if (schedule.StepOfDate[date] != step) continue;
                for (int asset = 0; asset < names.Length; asset++)
                {
                    process.FillSpot(asset, schedule.Forwards[date][asset], evaluator.SpotBuffers(asset).Spot);
                }
                foreach (var payoffEvent in schedule.EventsOfDate[date]) evaluator.Run(payoffEvent, schedule.Discounts[date]);
                date++;
                if (date == schedule.StepOfDate.Length) break;
            }
            means[batch] = evaluator.Value.Average();
        });

        double mean = means.Average();
        double variance = means.Sum(m => (m - mean) * (m - mean)) / (config.Batches - 1);
        return new PricingResult(
            quantity * (mean + replay.DueToday), Math.Abs(quantity) * Math.Sqrt(variance / config.Batches), name, [], quantity * replay.Realised);
    }

    /// <summary>Every event date is a step; longer gaps are cut to the configured frequency.</summary>
    private static Schedule BuildSchedule(ImmutableArray<PayoffEvent> events, PricingRequest request, MonteCarloEngineConfig config)
    {
        var groups = events.GroupBy(e => e.Date).OrderBy(g => g.Key).ToArray();
        var snapshot = request.Market;
        double[] dateTimes = [.. groups.Select(g => DayCount.Act365F.YearFraction(request.ValuationDate, g.Key))];
        var times = new List<double>();
        int[] stepOfDate = new int[groups.Length];
        double frequency = Math.Max(config.StepsPerYear, MinStepsPerPath / dateTimes[^1]);
        double previous = 0;
        for (int d = 0; d < groups.Length; d++)
        {
            int pieces = Math.Max(1, (int)Math.Ceiling((dateTimes[d] - previous) * frequency));
            for (int i = 1; i <= pieces; i++) times.Add(previous + (dateTimes[d] - previous) * i / pieces);
            stepOfDate[d] = times.Count - 1;
            previous = dateTimes[d];
        }

        var names = request.Trade.Instrument.Underlyings;
        double[][] forwards = [.. dateTimes.Select(t => names.Select(n => Forward(snapshot, n, t)).ToArray())];
        return new Schedule(
            [.. times], stepOfDate, [.. groups.Select(g => g.ToArray())],
            [.. dateTimes.Select(snapshot.DiscountCurve.Discount)], forwards);
    }

    private static double Forward(MarketSnapshot snapshot, string underlying, double t)
    {
        var market = snapshot.Underlying(underlying);
        return market.Spot * market.DividendCurve.Discount(t) / snapshot.DiscountCurve.Discount(t);
    }

    private static Func<int, int, IPathBatch> LocalVolFactory(
        PricingRequest request, LocalVolConfig local, Schedule schedule, MonteCarloEngineConfig config)
    {
        var snapshot = request.Market;
        var names = request.Trade.Instrument.Underlyings;
        double[] middles = new double[schedule.Times.Length];
        for (int i = 0; i < middles.Length; i++)
        {
            middles[i] = ((i == 0 ? 0 : schedule.Times[i - 1]) + schedule.Times[i]) / 2;
        }

        // Each surface is read through its own forwards; the grids are shared by all batches.
        LocalVolGrid[] grids = [.. names.Select(n => new LocalVolGrid(
            snapshot.Underlying(n).Vol, t => Forward(snapshot, n, t), middles, local.MinVol, local.MaxVol))];
        var bridge = BrownianBridge.Build(schedule.Times);
        return (batch, paths) => new LocalVolBatch(
            grids, schedule.Times, bridge, local.Correlation, paths, McSizing.BatchSeed(config.Seed, batch));
    }

    /// <summary>Euler on y with a common factor for the correlation. y needs no carry: the forward holds it.</summary>
    private sealed class LocalVolBatch : IPathBatch
    {
        private readonly LocalVolGrid[] _grids;
        private readonly double[] _times;
        private readonly double[][][] _brownian;
        private readonly double[][] _log;
        private readonly double _common;
        private readonly double _own;

        public LocalVolBatch(
            LocalVolGrid[] grids, double[] times, BridgeStep[] bridge, double correlation, int paths, ulong seed)
        {
            _grids = grids;
            _times = times;
            _common = Math.Sqrt(correlation);
            _own = Math.Sqrt(1 - correlation);
            int assets = grids.Length;
            int factors = assets == 1 ? 1 : assets + 1;
            _log = new double[assets][];
            for (int a = 0; a < assets; a++) _log[a] = new double[paths];

            _brownian = new double[factors][][];
            for (int f = 0; f < factors; f++)
            {
                _brownian[f] = new double[times.Length][];
                for (int i = 0; i < times.Length; i++) _brownian[f][i] = new double[paths];
            }

            var random = new SplitMix(seed);
            var uniforms = new double[paths];
            var normals = new double[paths];
            for (int rank = 0; rank < bridge.Length; rank++)
            {
                for (int f = 0; f < factors; f++)
                {
                    int dimension = rank * factors + f;
                    uint shift = (uint)random.Next();
                    if (dimension < Sobol.MaxDimensions)
                    {
                        Sobol.Fill(dimension, shift, uniforms);
                    }
                    else
                    {
                        for (int i = 0; i < paths; i++) uniforms[i] = random.NextUniform();
                    }
                    for (int i = 0; i < paths; i++) normals[i] = InverseNormal.Evaluate(uniforms[i]);
                    BrownianBridge.Apply(bridge[rank], _brownian[f], normals);
                }
            }
        }

        public void Advance(int step)
        {
            double dt = _times[step] - (step == 0 ? 0 : _times[step - 1]);
            bool single = _grids.Length == 1;
            double[] commonEnd = _brownian[^1][step];
            double[]? commonStart = step == 0 ? null : _brownian[^1][step - 1];

            for (int a = 0; a < _grids.Length; a++)
            {
                var grid = _grids[a];
                double[] row = grid.Row(step);
                double[] y = _log[a];
                double[] end = _brownian[a][step];
                double[]? start = step == 0 ? null : _brownian[a][step - 1];
                for (int i = 0; i < y.Length; i++)
                {
                    double own = end[i] - (start?[i] ?? 0);
                    double move = single ? own : _common * (commonEnd[i] - (commonStart?[i] ?? 0)) + _own * own;
                    double vol = grid.Lookup(row, y[i]);
                    y[i] += vol * move - 0.5 * vol * vol * dt;
                }
            }
        }

        public void FillSpot(int asset, double forward, double[] spot)
        {
            double[] y = _log[asset];
            for (int i = 0; i < y.Length; i++) spot[i] = forward * Math.Exp(y[i]);
        }
    }

    /// <summary>Andersen's quadratic-exponential scheme, which keeps variance positive with long steps.</summary>
    private sealed class HestonBatch : IPathBatch
    {
        private const double SwitchLevel = 1.5;

        private readonly HestonConfig _p;
        private readonly double[] _times;
        private readonly double[] _y;
        private readonly double[] _v;
        private SplitMix _random;

        public HestonBatch(HestonConfig p, double[] times, int paths, ulong seed)
        {
            _p = p;
            _times = times;
            _y = new double[paths];
            _v = new double[paths];
            Array.Fill(_v, p.V0);
            _random = new SplitMix(seed);
        }

        public void Advance(int step)
        {
            double dt = _times[step] - (step == 0 ? 0 : _times[step - 1]);
            double kappa = _p.Kappa, theta = _p.Theta, xi = _p.Xi, rho = _p.Rho;
            double decay = Math.Exp(-kappa * dt);
            double c1 = xi * xi * decay * (1 - decay) / kappa;
            double c2 = theta * xi * xi * (1 - decay) * (1 - decay) / (2 * kappa);
            double k0 = -rho * kappa * theta / xi * dt;
            double k1 = 0.5 * dt * (kappa * rho / xi - 0.5) - rho / xi;
            double k2 = 0.5 * dt * (kappa * rho / xi - 0.5) + rho / xi;
            double k3 = 0.5 * dt * (1 - rho * rho);

            for (int i = 0; i < _y.Length; i++)
            {
                double v = _v[i];
                double mean = theta + (v - theta) * decay;
                double variance = v * c1 + c2;
                double psi = variance / (mean * mean);
                double u = _random.NextUniform();
                double next;
                if (psi <= SwitchLevel)
                {
                    double inverse = 2 / psi;
                    double b2 = inverse - 1 + Math.Sqrt(inverse) * Math.Sqrt(inverse - 1);
                    double a = mean / (1 + b2);
                    double z = Math.Sqrt(b2) + InverseNormal.Evaluate(u);
                    next = a * z * z;
                }
                else
                {
                    double p0 = (psi - 1) / (psi + 1);
                    double beta = (1 - p0) / mean;
                    next = u <= p0 ? 0 : Math.Log((1 - p0) / (1 - u)) / beta;
                }

                double normal = InverseNormal.Evaluate(_random.NextUniform());
                _y[i] += k0 + k1 * v + k2 * next + Math.Sqrt(k3 * (v + next)) * normal;
                _v[i] = next;
            }
        }

        public void FillSpot(int asset, double forward, double[] spot)
        {
            for (int i = 0; i < spot.Length; i++) spot[i] = forward * Math.Exp(_y[i]);
        }
    }
}
