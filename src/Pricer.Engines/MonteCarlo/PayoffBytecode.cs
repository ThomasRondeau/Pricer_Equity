using Pricer.Instruments;
using Pricer.Native;

namespace Pricer.Engines.MonteCarlo;

/// <summary>Flattens a payoff program to the postfix code the native evaluator runs (native/src/payoff_vm.hpp):
/// per event a header, the expression with operands before their operator, then End.</summary>
internal static class PayoffBytecode
{
    private const int End = 0;
    private const int Constant = 1;
    private const int Spot = 2;
    private const int Slot = 3;
    private const int Step = 9;
    private const int Store = 20;
    private const int Pay = 21;
    private const int Terminate = 22;

    /// <param name="events">In date order, each with the index of its date.</param>
    public static PayoffCode Compile(IEnumerable<(PayoffEvent Event, int Date)> events, double[] slots, int assets, int dates)
    {
        var code = new List<int>();
        var constants = new List<double>();

        int ConstantIndex(double value)
        {
            constants.Add(value);
            return constants.Count - 1;
        }

        void Emit(PayoffExpr expr)
        {
            switch (expr)
            {
                case ConstantExpr c:
                    code.AddRange([Constant, ConstantIndex(c.Value)]);
                    break;
                case SpotExpr s:
                    code.AddRange([Spot, s.Asset]);
                    break;
                case SlotExpr s:
                    code.AddRange([Slot, s.Index]);
                    break;
                case BinaryExpr b:
                    Emit(b.Left);
                    Emit(b.Right);
                    code.Add(Operator(b.Op));
                    break;
                case StepExpr s:
                    Emit(s.Argument);
                    code.AddRange([Step, ConstantIndex(s.Scale)]);
                    break;
                default:
                    throw new NotSupportedException(expr.GetType().Name);
            }
        }

        foreach (var (payoffEvent, date) in events)
        {
            switch (payoffEvent)
            {
                case StoreEvent store:
                    code.AddRange([Store, date, store.Slot]);
                    Emit(store.Value);
                    break;
                case PayEvent pay:
                    code.AddRange([Pay, date]);
                    Emit(pay.Amount);
                    break;
                case TerminateEvent terminate:
                    code.AddRange([Terminate, date]);
                    Emit(terminate.Condition);
                    break;
                default:
                    throw new NotSupportedException(payoffEvent.GetType().Name);
            }
            code.Add(End);
        }
        return new PayoffCode([.. code], [.. constants], slots, assets, dates);
    }

    private static int Operator(BinaryOp op) => op switch
    {
        BinaryOp.Add => 4,
        BinaryOp.Subtract => 5,
        BinaryOp.Multiply => 6,
        BinaryOp.Max => 7,
        BinaryOp.Min => 8,
        _ => throw new NotSupportedException(op.ToString()),
    };

    public static NativeBridgeStep[] Bridge(BridgeStep[] steps) =>
        [.. steps.Select(s => new NativeBridgeStep(s.Target, s.Left, s.Right, s.LeftWeight, s.RightWeight, s.Noise))];

    public static BatchPlan Plan(MonteCarloEngineConfig config, int paths) =>
        new(paths, [.. Enumerable.Range(0, config.Batches).Select(b => McSizing.BatchSeed(config.Seed, b))], Sobol.FlatDirections);
}
