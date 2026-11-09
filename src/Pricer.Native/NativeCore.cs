using System.Runtime.InteropServices;

namespace Pricer.Native;

/// <summary>A Brownian bridge step, laid out as pc_bridge_step.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeBridgeStep(int target, int left, int right, double leftWeight, double rightWeight, double noise)
{
    public readonly int Target = target, Left = left, Right = right;
    private readonly int _padding;
    public readonly double LeftWeight = leftWeight, RightWeight = rightWeight, Noise = noise;
}

/// <summary>An SVI slice, laid out as pc_svi_slice.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SviSliceData(double T, double Forward, double A, double B, double Rho, double M, double Sigma);

/// <summary>An implied surface: SVI slices in increasing time, or a flat vol when there are none.</summary>
public sealed record ImpliedSurface(SviSliceData[] Slices, double FlatVol, double Shift);

/// <summary>A local vol table to fill: one row per time, read at its time and at the two times of the time
/// derivative, each with its forward.</summary>
public sealed record LocalVolRequest(
    int Points, double Low, double Step, double MinVol, double MaxVol,
    double[] Times, double[] UpTimes, double[] DownTimes, double[] Forwards, double[] UpForwards, double[] DownForwards);

/// <summary>A uniform log-spot grid and everything the time loop reads, already evaluated: payoff and exercise
/// at the nodes, rates per step, edge values per sub-step (Rannacher steps count twice).</summary>
public sealed record PdeProblem(
    double Low, double H, double LogSpot, double Spot, double[] Payoff, double[]? Exercise,
    int TimeSteps, int RannacherSteps, double Dt, double[] Rates, double[] Carries, double[] EdgeLow, double[] EdgeHigh,
    double Vol, LocalVolRows? Local, double[]? LogForwards);

/// <summary>A local vol table: Rows[step][point] flattened, in y = ln(S / F).</summary>
public sealed record LocalVolRows(double[] Rows, int Points, double Low, double Step);

/// <summary>A payoff program flattened to postfix code (PayoffBytecode).</summary>
public sealed record PayoffCode(int[] Code, double[] Constants, double[] InitialSlots, int Assets, int Dates);

/// <param name="SobolDirections">Direction numbers, 32 per dimension; past them, dimensions use the batch generator.</param>
public sealed record BatchPlan(int Paths, ulong[] Seeds, uint[] SobolDirections)
{
    public int Batches => Seeds.Length;
}

public sealed record BlackScholesPaths(
    double[] Times, double[] Forwards, double[] Discounts, NativeBridgeStep[] Bridge, double Spot, double Vol, double Smoothing,
    bool WantTangents, bool WantScores);

/// <param name="Forwards">Dates x assets.</param>
public sealed record SteppedSchedule(double[] Times, int[] StepOfDate, double[] Discounts, double[] Forwards);

/// <param name="Grids">One table per asset, each with a row per step.</param>
public sealed record LocalVolPaths(LocalVolRows[] Grids, NativeBridgeStep[] Bridge, double Correlation);

public sealed record HestonPaths(double V0, double Kappa, double Theta, double Xi, double Rho);

/// <summary>Entry point to the C++ kernels. Each call pins the arrays it is given and returns plain results;
/// the native side keeps no state between calls.</summary>
public static unsafe class NativeCore
{
    private const int AbiVersion = 2;
    public const int BlackScholesStats = 7;

    private static readonly Lazy<string?> LoadError = new(Probe);

    public static bool IsAvailable => LoadError.Value is null;

    /// <summary>Why the library cannot be used, or null.</summary>
    public static string? Unavailable => LoadError.Value;

    private static string? Probe()
    {
        try
        {
            int version = NativeMethods.AbiVersion();
            return version == AbiVersion ? null : $"pricer_core speaks ABI {version}, this build expects {AbiVersion}.";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"pricer_core could not be loaded: {e.Message}";
        }
    }

    private static void EnsureAvailable()
    {
        if (LoadError.Value is { } error) throw new InvalidOperationException(error);
    }

    private static void Check(int status)
    {
        if (status == 0) return;
        string message = Marshal.PtrToStringUTF8(NativeMethods.LastError()) ?? "";
        throw status == 1 ? new ArgumentException($"Native core: {message}") : new InvalidOperationException($"Native core: {message}");
    }

    /// <summary>Price, delta, gamma and theta per year at spot.</summary>
    public static (double Price, double Delta, double Gamma, double Theta) SolvePde(PdeProblem p)
    {
        EnsureAvailable();
        if (p.Rates.Length < p.TimeSteps || p.Carries.Length < p.TimeSteps) throw new ArgumentException("One rate per time step.");
        int subSteps = p.TimeSteps + Math.Min(p.RannacherSteps, p.TimeSteps);
        if (p.EdgeLow.Length < subSteps || p.EdgeHigh.Length < subSteps) throw new ArgumentException("One edge value per sub-step.");
        if (p.Exercise is { } e && e.Length != p.Payoff.Length) throw new ArgumentException("One exercise value per node.");
        if (p.Local is { } l && (l.Rows.Length < p.TimeSteps * l.Points || p.LogForwards is not { } f || f.Length < p.TimeSteps))
        {
            throw new ArgumentException("One local vol row and forward per time step.");
        }

        var output = stackalloc double[4];
        fixed (double* payoff = p.Payoff, exercise = p.Exercise, rates = p.Rates, carries = p.Carries)
        fixed (double* low = p.EdgeLow, high = p.EdgeHigh, rows = p.Local?.Rows, forwards = p.LogForwards)
        {
            var problem = new PdeProblemStruct
            {
                Nodes = p.Payoff.Length, Low = p.Low, H = p.H, LogSpot = p.LogSpot, Spot = p.Spot,
                Payoff = payoff, Exercise = exercise, TimeSteps = p.TimeSteps, RannacherSteps = p.RannacherSteps, Dt = p.Dt,
                Rates = rates, Carries = carries, EdgeLow = low, EdgeHigh = high, Vol = p.Vol,
                LocalRows = rows, LocalPoints = p.Local?.Points ?? 0, LocalLow = p.Local?.Low ?? 0, LocalStep = p.Local?.Step ?? 0,
                LogForwards = forwards,
            };
            Check(NativeMethods.PdeSolve(&problem, output));
        }
        return (output[0], output[1], output[2], output[3]);
    }

    /// <summary>Local vols, rows one after the other, and the number of nodes that fell back on implied variance.</summary>
    public static (double[] Rows, long Clamped) LocalVolGrid(ImpliedSurface surface, LocalVolRequest r)
    {
        EnsureAvailable();
        int count = r.Times.Length;
        double[][] perRow = [r.UpTimes, r.DownTimes, r.Forwards, r.UpForwards, r.DownForwards];
        if (count < 1 || perRow.Any(a => a.Length != count)) throw new ArgumentException("One time and forward triple per row.");
        var rows = new double[count * r.Points];
        long clamped = 0;
        fixed (SviSliceData* slices = surface.Slices)
        fixed (double* times = r.Times, upTimes = r.UpTimes, downTimes = r.DownTimes)
        fixed (double* forwards = r.Forwards, upForwards = r.UpForwards, downForwards = r.DownForwards, output = rows)
        {
            var s = new VolSurfaceStruct { Slices = slices, SliceCount = surface.Slices.Length, FlatVol = surface.FlatVol, Shift = surface.Shift };
            var request = new LocalVolRequestStruct
            {
                Rows = count, Points = r.Points, Low = r.Low, Step = r.Step, MinVol = r.MinVol, MaxVol = r.MaxVol,
                Times = times, UpTimes = upTimes, DownTimes = downTimes,
                Forwards = forwards, UpForwards = upForwards, DownForwards = downForwards,
            };
            Check(NativeMethods.LocalVolGrid(&s, &request, output, &clamped));
        }
        return (rows, clamped);
    }

    /// <summary>Statistics per batch, BlackScholesStats each, in the order of the engine's Stat enum.</summary>
    public static double[][] BlackScholes(PayoffCode code, BlackScholesPaths model, BatchPlan plan)
    {
        EnsureAvailable();
        CheckPlan(plan);
        if (model.Times.Length != code.Dates || model.Forwards.Length != code.Dates || model.Discounts.Length != code.Dates)
        {
            throw new ArgumentException("One time, forward and discount per date.");
        }
        var stats = new double[plan.Batches * BlackScholesStats];
        fixed (int* program = code.Code)
        fixed (double* constants = code.Constants, slots = code.InitialSlots, output = stats)
        fixed (double* times = model.Times, forwards = model.Forwards, discounts = model.Discounts)
        fixed (uint* directions = plan.SobolDirections)
        fixed (ulong* seeds = plan.Seeds)
        fixed (NativeBridgeStep* bridge = model.Bridge)
        {
            var p = Program(code, program, constants, slots);
            var sobol = Sobol(plan, directions);
            var batches = Batches(plan, seeds);
            var paths = new BlackScholesStruct
            {
                Times = times, Forwards = forwards, Discounts = discounts, Bridge = bridge, BridgeLength = model.Bridge.Length,
                WantTangents = model.WantTangents ? 1 : 0, WantScores = model.WantScores ? 1 : 0,
                Spot = model.Spot, Vol = model.Vol, Smoothing = model.Smoothing,
            };
            Check(NativeMethods.BlackScholes(&p, &sobol, &paths, &batches, output));
        }
        return [.. Enumerable.Range(0, plan.Batches).Select(b => stats.AsSpan(b * BlackScholesStats, BlackScholesStats).ToArray())];
    }

    /// <summary>Mean discounted payoff per batch.</summary>
    public static double[] LocalVol(PayoffCode code, SteppedSchedule schedule, LocalVolPaths model, BatchPlan plan)
    {
        EnsureAvailable();
        CheckPlan(plan);
        CheckSchedule(code, schedule);
        int steps = schedule.Times.Length;
        if (model.Grids.Length != code.Assets) throw new ArgumentException("One local vol grid per asset.");
        int points = model.Grids[0].Points;
        if (model.Grids.Any(g => g.Points != points || g.Rows.Length != steps * points))
        {
            throw new ArgumentException("Every grid needs one row of the same width per step.");
        }

        double[] rows = [.. model.Grids.SelectMany(g => g.Rows)];
        double[] lows = [.. model.Grids.Select(g => g.Low)];
        double[] widths = [.. model.Grids.Select(g => g.Step)];
        var means = new double[plan.Batches];
        fixed (int* program = code.Code, stepOfDate = schedule.StepOfDate)
        fixed (double* constants = code.Constants, slots = code.InitialSlots, output = means)
        fixed (double* times = schedule.Times, discounts = schedule.Discounts, forwards = schedule.Forwards)
        fixed (double* r = rows, l = lows, w = widths)
        fixed (uint* directions = plan.SobolDirections)
        fixed (ulong* seeds = plan.Seeds)
        fixed (NativeBridgeStep* bridge = model.Bridge)
        {
            var p = Program(code, program, constants, slots);
            var sobol = Sobol(plan, directions);
            var batches = Batches(plan, seeds);
            var s = Schedule(schedule, times, stepOfDate, discounts, forwards);
            var local = new LocalVolStruct
            {
                Rows = r, Lows = l, StepSizes = w, Points = points, Bridge = bridge, BridgeLength = model.Bridge.Length,
                Correlation = model.Correlation,
            };
            Check(NativeMethods.LocalVol(&p, &sobol, &s, &local, &batches, output));
        }
        return means;
    }

    /// <summary>Mean discounted payoff per batch.</summary>
    public static double[] Heston(PayoffCode code, SteppedSchedule schedule, HestonPaths model, BatchPlan plan)
    {
        EnsureAvailable();
        CheckPlan(plan);
        CheckSchedule(code, schedule);
        var means = new double[plan.Batches];
        fixed (int* program = code.Code, stepOfDate = schedule.StepOfDate)
        fixed (double* constants = code.Constants, slots = code.InitialSlots, output = means)
        fixed (double* times = schedule.Times, discounts = schedule.Discounts, forwards = schedule.Forwards)
        fixed (ulong* seeds = plan.Seeds)
        {
            var p = Program(code, program, constants, slots);
            var batches = Batches(plan, seeds);
            var s = Schedule(schedule, times, stepOfDate, discounts, forwards);
            var heston = new HestonStruct { V0 = model.V0, Kappa = model.Kappa, Theta = model.Theta, Xi = model.Xi, Rho = model.Rho };
            Check(NativeMethods.Heston(&p, &s, &heston, &batches, output));
        }
        return means;
    }

    private static void CheckPlan(BatchPlan plan)
    {
        if (plan.Batches < 1 || plan.Paths < 1) throw new ArgumentException("Paths and batches must be positive.");
    }

    private static void CheckSchedule(PayoffCode code, SteppedSchedule schedule)
    {
        if (schedule.StepOfDate.Length != code.Dates || schedule.Discounts.Length != code.Dates
            || schedule.Forwards.Length != code.Dates * code.Assets)
        {
            throw new ArgumentException("One step, discount and forward per asset for each date.");
        }
    }

    private static ProgramStruct Program(PayoffCode code, int* program, double* constants, double* slots) => new()
    {
        Code = program, CodeLength = code.Code.Length, Constants = constants, Slots = code.InitialSlots.Length,
        InitialSlots = slots, Assets = code.Assets, Dates = code.Dates,
    };

    private static SobolStruct Sobol(BatchPlan plan, uint* directions) =>
        new() { Directions = directions, Dimensions = plan.SobolDirections.Length / 32 };

    /// <summary>All hardware threads on the desktop; the browser build has a single one.</summary>
    private static BatchesStruct Batches(BatchPlan plan, ulong* seeds) =>
        new() { Paths = plan.Paths, Batches = plan.Batches, BatchSeeds = seeds, Threads = 0 };

    private static ScheduleStruct Schedule(SteppedSchedule schedule, double* times, int* stepOfDate, double* discounts, double* forwards) =>
        new() { Times = times, Steps = schedule.Times.Length, StepOfDate = stepOfDate, Discounts = discounts, Forwards = forwards };
}
