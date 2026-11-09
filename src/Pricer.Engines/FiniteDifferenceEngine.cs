using System.Collections.Immutable;
using Pricer.Core;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Native;

namespace Pricer.Engines;

/// <summary>Crank-Nicolson on a uniform log-spot grid, with Rannacher start-up against payoff kinks, under
/// Black-Scholes or Dupire local vol. Delta, gamma and theta are read from the grid; vega and rho are left
/// to the bump fallback.</summary>
public sealed class FiniteDifferenceEngine : IPricingEngine
{
    private const double DaysPerYear = 365.0;
    private const int PayoffSamples = 16;

    public string Name => "Pde";

    private readonly record struct GridValue(double Price, double Delta, double Gamma, double Theta)
    {
        public static GridValue operator -(GridValue a, GridValue b) =>
            new(a.Price - b.Price, a.Delta - b.Delta, a.Gamma - b.Gamma, a.Theta - b.Theta);
    }

    /// <param name="Payoff">Value at expiry.</param>
    /// <param name="Far">Value at time to expiry tau on a grid edge without a barrier.</param>
    /// <param name="Exercise">Early exercise value, for American options.</param>
    private sealed record Problem(
        Func<double, double> Payoff, Func<double, double, double> Far, Func<double, double>? Exercise,
        double? LowerBarrier, double? UpperBarrier);

    /// <summary>Rates to expiry and between two times; a local vol grid when the model has one.</summary>
    private sealed record Dynamics(
        BlackScholesInputs Inputs, Func<double, double> Discount, Func<double, double> Carry, LocalVolGrid? Local, double[] LogForwards);

    public PricingResult Price(PricingRequest request)
    {
        var option = (OptionInstrument)request.Trade.Instrument;
        var config = (PdeEngineConfig)request.Engine;
        double t = DayCount.Act365F.YearFraction(request.ValuationDate, option.Expiry);
        double spot = request.Market.Underlying(option.Underlying).Spot;
        string name = (request.Model is LocalVolConfig ? "LocalVol.Pde" : "BlackScholes.Pde") + BackendSuffix(config.Backend);

        if (t < 0) return Result(request, default, name);
        if (t == 0) return Result(request, new GridValue(PayoffCompiler.AtExpiry(option, spot), 0, 0, 0), name);

        var x = EngineInputs.For(option, request, t);
        if (x.Vol <= 0) throw new ArgumentException("The PDE requires a strictly positive vol.");
        return Result(request, Value(option, request, BuildDynamics(request, option, x, config), config), name);
    }

    private static Dynamics BuildDynamics(PricingRequest request, OptionInstrument option, BlackScholesInputs x, PdeEngineConfig config)
    {
        if (request.Model is not LocalVolConfig local)
        {
            return new(x, tau => Math.Exp(-x.DiscountRate * tau), tau => Math.Exp(-x.DividendYield * tau), null, []);
        }

        var market = request.Market.Underlying(option.Underlying);
        var discount = request.Market.DiscountCurve;
        var carry = market.DividendCurve;
        double T = x.T;
        double Forward(double t) => market.Spot * carry.Discount(t) / discount.Discount(t);

        double[] times = [.. Enumerable.Range(0, config.TimeSteps).Select(i => T * (1 - (i + 0.5) / config.TimeSteps))];
        var grid = new LocalVolGrid(market.Vol, Forward, times, local.MinVol, local.MaxVol, config.Backend == ComputeBackend.Native);
        return new(
            x, tau => discount.Discount(T) / discount.Discount(T - tau), tau => carry.Discount(T) / carry.Discount(T - tau),
            grid, [.. times.Select(t => Math.Log(Forward(t)))]);
    }

    private static GridValue Value(OptionInstrument option, PricingRequest request, Dynamics dynamics, PdeEngineConfig config)
    {
        var plain = Plain(option, dynamics);
        if (option is not BarrierOption barrier) return Solve(plain, dynamics, config);

        if (EngineInputs.IsBreached(barrier, request.Trade, dynamics.Inputs.Spot))
        {
            return barrier.IsOut ? default : Solve(plain, dynamics, config);
        }
        var knockOut = Solve(
            plain with { LowerBarrier = barrier.IsUp ? null : barrier.Barrier, UpperBarrier = barrier.IsUp ? barrier.Barrier : null },
            dynamics, config);
        return barrier.IsOut ? knockOut : Solve(plain, dynamics, config) - knockOut;
    }

    private static Problem Plain(OptionInstrument option, Dynamics dynamics)
    {
        double sign = option.Sign;
        double strike = option.Strike;
        double Intrinsic(double s) => Math.Max(sign * (s - strike), 0);
        double Forward(double s, double tau) =>
            Math.Max(sign * (s * dynamics.Carry(tau) - strike * dynamics.Discount(tau)), 0);

        return option switch
        {
            DigitalOption d => new(s => sign * (s - strike) >= 0 ? d.Cash : 0,
                (s, tau) => (sign * (s - strike) >= 0 ? d.Cash : 0) * dynamics.Discount(tau), null, null, null),
            AmericanOption => new(Intrinsic, (s, tau) => Math.Max(Forward(s, tau), Intrinsic(s)), Intrinsic, null, null),
            _ => new(Intrinsic, Forward, null, null, null),
        };
    }

    private static GridValue Solve(Problem problem, Dynamics dynamics, PdeEngineConfig config)
    {
        var grid = Discretise(problem, dynamics, config);
        return config.Backend == ComputeBackend.Native ? SolveNative(grid) : SolveManaged(grid, dynamics);
    }

    /// <summary>Everything the time loop reads, evaluated up front: payoff and exercise at the nodes, rates per
    /// step and edge values per sub-step. Both backends solve this same problem.</summary>
    private static PdeProblem Discretise(Problem problem, Dynamics dynamics, PdeEngineConfig config)
    {
        var x = dynamics.Inputs;
        int n = config.SpaceSteps;
        double logSpot = Math.Log(x.Spot);
        double reach = config.Width * x.Vol * Math.Sqrt(x.T);
        double low = problem.LowerBarrier is { } lower ? Math.Log(lower) : Math.Min(logSpot, Math.Log(x.Strike)) - reach;
        double high = problem.UpperBarrier is { } upper ? Math.Log(upper) : Math.Max(logSpot, Math.Log(x.Strike)) + reach;
        double h = (high - low) / n;
        if (problem.LowerBarrier is null && problem.UpperBarrier is null)
        {
            // With spot on a node, no interpolation error reaches the price.
            low = logSpot - Math.Round((logSpot - low) / h) * h;
        }

        double[] exercise = problem.Exercise is { } f ? [.. Enumerable.Range(0, n + 1).Select(j => f(Math.Exp(low + j * h)))] : [];

        // Averaging over each cell keeps second order when a payoff jump falls between nodes.
        double[] v = new double[n + 1];
        for (int j = 0; j <= n; j++)
        {
            double sum = 0;
            for (int k = 0; k < PayoffSamples; k++)
            {
                sum += problem.Payoff(Math.Exp(low + (j + (k + 0.5) / PayoffSamples - 0.5) * h));
            }
            v[j] = sum / PayoffSamples;
        }
        if (problem.LowerBarrier is not null) v[0] = 0;
        if (problem.UpperBarrier is not null) v[n] = 0;

        // The time loop unrolled: rates at the start of each step, edges after each sub-step.
        int steps = config.TimeSteps;
        double dt = x.T / steps;
        int subSteps = steps + Math.Min(config.RannacherSteps, steps);
        double[] rates = new double[steps], carries = new double[steps];
        double[] edgeLow = new double[subSteps], edgeHigh = new double[subSteps];
        double lowSpot = Math.Exp(low + 0 * h), highSpot = Math.Exp(low + n * h);
        double tau = 0;
        int sub = 0;

        void Advance(double step)
        {
            tau += step;
            edgeLow[sub] = problem.LowerBarrier is null ? problem.Far(lowSpot, tau) : 0;
            edgeHigh[sub] = problem.UpperBarrier is null ? problem.Far(highSpot, tau) : 0;
            sub++;
        }

        for (int i = 0; i < steps; i++)
        {
            rates[i] = -Math.Log(dynamics.Discount(tau + dt) / dynamics.Discount(tau)) / dt;
            carries[i] = -Math.Log(dynamics.Carry(tau + dt) / dynamics.Carry(tau)) / dt;
            if (i < config.RannacherSteps)
            {
                Advance(dt / 2);
                Advance(dt / 2);
            }
            else
            {
                Advance(dt);
            }
        }

        LocalVolRows? local = null;
        if (dynamics.Local is { } grid)
        {
            var (rows, points, gridLow, gridStep) = grid.Flat();
            local = new LocalVolRows(rows, points, gridLow, gridStep);
        }
        return new PdeProblem(
            low, h, logSpot, x.Spot, v, problem.Exercise is null ? null : exercise, steps, config.RannacherSteps, dt,
            rates, carries, edgeLow, edgeHigh, x.Vol, local, local is null ? null : dynamics.LogForwards);
    }

    private static GridValue SolveNative(PdeProblem grid)
    {
        var (price, delta, gamma, theta) = NativeCore.SolvePde(grid);
        return new GridValue(price, delta, gamma, theta);
    }

    /// <summary>The reference implementation of native/src/pde.cpp.</summary>
    private static GridValue SolveManaged(PdeProblem grid, Dynamics dynamics)
    {
        int n = grid.Payoff.Length - 1;
        double low = grid.Low, h = grid.H, logSpot = grid.LogSpot;
        double[] logNodes = [.. Enumerable.Range(0, n + 1).Select(j => low + j * h)];
        double[] exercise = grid.Exercise ?? [];
        double[] v = (double[])grid.Payoff.Clone();

        var scratch = new Scratch(n);
        double[] previous = new double[n + 1];
        double dt = grid.Dt;
        int sub = 0;

        void Coefficients(int index)
        {
            double rate = grid.Rates[index];
            double carry = grid.Carries[index];
            for (int j = 1; j < n; j++)
            {
                double vol = grid.Vol;
                if (dynamics.Local is { } local)
                {
                    vol = local.Lookup(local.Row(index), logNodes[j] - dynamics.LogForwards[index]);
                }
                double variance = vol * vol;
                double drift = rate - carry - 0.5 * variance;
                scratch.Below[j] = 0.5 * variance / (h * h) - 0.5 * drift / h;
                scratch.Above[j] = 0.5 * variance / (h * h) + 0.5 * drift / h;
                scratch.Centre[j] = -variance / (h * h) - rate;
            }
        }

        void Step(double step, double theta)
        {
            scratch.Advance(v, step, theta, grid.EdgeLow[sub], grid.EdgeHigh[sub]);
            sub++;
            for (int j = 0; j < exercise.Length; j++) v[j] = Math.Max(v[j], exercise[j]);
        }

        for (int i = 0; i < grid.TimeSteps; i++)
        {
            v.CopyTo(previous, 0);
            if (i == 0 || dynamics.Local is not null) Coefficients(i);
            if (i < grid.RannacherSteps)
            {
                Step(dt / 2, 1);
                Step(dt / 2, 1);
            }
            else
            {
                Step(dt, 0.5);
            }
        }

        int c = Math.Clamp((int)Math.Round((logSpot - low) / h), 1, n - 1);
        double u = logSpot - (low + c * h);
        double first = (v[c + 1] - v[c - 1]) / (2 * h);
        double second = (v[c + 1] - 2 * v[c] + v[c - 1]) / (h * h);
        double Interpolate(double[] w) =>
            w[c] + u * (w[c + 1] - w[c - 1]) / (2 * h) + 0.5 * u * u * (w[c + 1] - 2 * w[c] + w[c - 1]) / (h * h);

        double slope = first + u * second;
        double spot2 = grid.Spot * grid.Spot;
        return new GridValue(
            Interpolate(v), slope / grid.Spot, (second - slope) / spot2, (Interpolate(previous) - Interpolate(v)) / dt);
    }

    /// <summary>One theta-scheme step with Dirichlet edges, solved by the Thomas algorithm.</summary>
    private sealed class Scratch(int n)
    {
        private readonly double[] _rhs = new double[n + 1];
        private readonly double[] _upper = new double[n + 1];

        public double[] Below { get; } = new double[n + 1];
        public double[] Centre { get; } = new double[n + 1];
        public double[] Above { get; } = new double[n + 1];

        public void Advance(double[] v, double dt, double theta, double edgeLow, double edgeHigh)
        {
            double explicitWeight = (1 - theta) * dt;
            for (int j = 1; j < n; j++)
            {
                _rhs[j] = v[j] + explicitWeight * (Below[j] * v[j - 1] + Centre[j] * v[j] + Above[j] * v[j + 1]);
            }

            double implicitWeight = theta * dt;
            _rhs[1] += implicitWeight * Below[1] * edgeLow;
            _rhs[n - 1] += implicitWeight * Above[n - 1] * edgeHigh;

            double b = 1 - implicitWeight * Centre[1];
            _upper[1] = -implicitWeight * Above[1] / b;
            _rhs[1] /= b;
            for (int j = 2; j < n; j++)
            {
                double a = -implicitWeight * Below[j];
                double pivot = 1 - implicitWeight * Centre[j] - a * _upper[j - 1];
                _upper[j] = -implicitWeight * Above[j] / pivot;
                _rhs[j] = (_rhs[j] - a * _rhs[j - 1]) / pivot;
            }

            v[n - 1] = _rhs[n - 1];
            for (int j = n - 2; j >= 1; j--) v[j] = _rhs[j] - _upper[j] * v[j + 1];
            v[0] = edgeLow;
            v[n] = edgeHigh;
        }
    }

    internal static string BackendSuffix(ComputeBackend backend) => backend == ComputeBackend.Native ? ".Native" : "";

    private static PricingResult Result(PricingRequest request, GridValue grid, string name)
    {
        double quantity = request.Trade.Quantity;
        var greeks = ImmutableArray.CreateBuilder<GreekValue>();
        foreach (var measure in request.Measures)
        {
            double? value = measure switch
            {
                Measure.Delta => grid.Delta,
                Measure.Gamma => grid.Gamma,
                Measure.Theta => grid.Theta / DaysPerYear,
                _ => null,
            };
            if (value is { } v) greeks.Add(new GreekValue(measure, quantity * v, GreekMethod.PdeGrid, GreekConvention.Model));
        }
        return new PricingResult(quantity * grid.Price, null, name, greeks.ToImmutable());
    }
}
