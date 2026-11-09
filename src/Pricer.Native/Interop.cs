using System.Runtime.InteropServices;

namespace Pricer.Native;

// Twins of the structs of native/include/pricer_core.h, field for field: change both together and bump
// PC_ABI_VERSION. Pointers are filled from pinned managed arrays for the duration of one call.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PdeProblemStruct
{
    public int Nodes;
    public double Low, H, LogSpot, Spot;
    public double* Payoff;
    public double* Exercise;
    public int TimeSteps, RannacherSteps;
    public double Dt;
    public double* Rates;
    public double* Carries;
    public double* EdgeLow;
    public double* EdgeHigh;
    public double Vol;
    public double* LocalRows;
    public int LocalPoints;
    public double LocalLow, LocalStep;
    public double* LogForwards;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VolSurfaceStruct
{
    public SviSliceData* Slices;
    public int SliceCount;
    public int Padding;
    public double FlatVol;
    public double Shift;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LocalVolRequestStruct
{
    public int Rows, Points;
    public double Low, Step, MinVol, MaxVol;
    public double* Times;
    public double* UpTimes;
    public double* DownTimes;
    public double* Forwards;
    public double* UpForwards;
    public double* DownForwards;
    public int Threads;
    public int Padding;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ProgramStruct
{
    public int* Code;
    public int CodeLength;
    public double* Constants;
    public int Slots;
    public double* InitialSlots;
    public int Assets;
    public int Dates;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SobolStruct
{
    public uint* Directions;
    public int Dimensions;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BatchesStruct
{
    public int Paths, Batches;
    public ulong* BatchSeeds;
    public int Threads;
    public int Padding;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BlackScholesStruct
{
    public double* Times;
    public double* Forwards;
    public double* Discounts;
    public NativeBridgeStep* Bridge;
    public int BridgeLength;
    public int WantTangents, WantScores;
    public int Padding;
    public double Spot, Vol, Smoothing;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ScheduleStruct
{
    public double* Times;
    public int Steps;
    public int Padding;
    public int* StepOfDate;
    public double* Discounts;
    public double* Forwards;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LocalVolStruct
{
    public double* Rows;
    public double* Lows;
    public double* StepSizes;
    public int Points;
    public int BridgeLength;
    public NativeBridgeStep* Bridge;
    public double Correlation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HestonStruct
{
    public double V0, Kappa, Theta, Xi, Rho;
}

internal static unsafe partial class NativeMethods
{
    private const string Library = "pricer_core";

    [LibraryImport(Library, EntryPoint = "pc_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Library, EntryPoint = "pc_last_error")]
    public static partial nint LastError();

    [LibraryImport(Library, EntryPoint = "pc_pde_solve")]
    public static partial int PdeSolve(PdeProblemStruct* problem, double* output);

    [LibraryImport(Library, EntryPoint = "pc_local_vol_grid")]
    public static partial int LocalVolGrid(VolSurfaceStruct* surface, LocalVolRequestStruct* request, double* rows, long* clamped);

    [LibraryImport(Library, EntryPoint = "pc_mc_black_scholes")]
    public static partial int BlackScholes(
        ProgramStruct* program, SobolStruct* sobol, BlackScholesStruct* model, BatchesStruct* batches, double* stats);

    [LibraryImport(Library, EntryPoint = "pc_mc_local_vol")]
    public static partial int LocalVol(
        ProgramStruct* program, SobolStruct* sobol, ScheduleStruct* schedule, LocalVolStruct* model, BatchesStruct* batches,
        double* means);

    [LibraryImport(Library, EntryPoint = "pc_mc_heston")]
    public static partial int Heston(
        ProgramStruct* program, ScheduleStruct* schedule, HestonStruct* model, BatchesStruct* batches, double* means);
}
