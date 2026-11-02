using System.Numerics;
using Pricer.Instruments;

namespace Pricer.Engines.MonteCarlo;

/// <summary>Runs a payoff program over one batch, column by column. Each value carries its tangents
/// (forward-mode differentiation), so pathwise greeks need no code specific to a product.</summary>
internal sealed class PayoffEvaluator
{
    private sealed record Column(double[] Value, double[][] Tangents);

    private readonly int _length;
    private readonly int _tangents;
    private readonly double _smoothing;
    private readonly Stack<Column> _free = new();
    private readonly Column?[] _slots;
    private readonly Column _alive;
    private readonly Column _spot;

    public PayoffEvaluator(int slots, int length, int tangents, double smoothing)
    {
        _length = length;
        _tangents = tangents;
        _smoothing = smoothing;
        _slots = new Column?[slots];
        _alive = Rent();
        Array.Fill(_alive.Value, 1.0);
        Cash = Rent();
        _spot = Rent();
        for (int i = 0; i < slots; i++) _slots[i] = Rent();
    }

    private Column Cash { get; }
    public double[] Value => Cash.Value;
    public double[] Tangent(int index) => Cash.Tangents[index];

    /// <summary>Hands over the spot column of the current date; tangents are filled by the caller.</summary>
    public (double[] Spot, double[][] Tangents) SpotBuffers() => (_spot.Value, _spot.Tangents);

    public void Run(PayoffEvent @event, double discount)
    {
        switch (@event)
        {
            case StoreEvent store:
                var stored = Evaluate(store.Value);
                Release(_slots[store.Slot]!);
                _slots[store.Slot] = stored;
                break;

            case PayEvent pay:
                var amount = Evaluate(pay.Amount);
                for (int p = 0; p < _tangents; p++)
                {
                    double[] d = Cash.Tangents[p], da = amount.Tangents[p], dl = _alive.Tangents[p];
                    for (int i = 0; i < _length; i++) d[i] += discount * (_alive.Value[i] * da[i] + dl[i] * amount.Value[i]);
                }
                for (int i = 0; i < _length; i++) Cash.Value[i] += discount * _alive.Value[i] * amount.Value[i];
                Release(amount);
                break;

            case TerminateEvent terminate:
                var condition = Evaluate(terminate.Condition);
                for (int p = 0; p < _tangents; p++)
                {
                    double[] dl = _alive.Tangents[p], dc = condition.Tangents[p];
                    for (int i = 0; i < _length; i++) dl[i] = dl[i] * (1 - condition.Value[i]) - _alive.Value[i] * dc[i];
                }
                for (int i = 0; i < _length; i++) _alive.Value[i] *= 1 - condition.Value[i];
                Release(condition);
                break;
        }
    }

    private Column Evaluate(PayoffExpr expr) => expr switch
    {
        ConstantExpr c => Filled(c.Value),
        SpotExpr => Copy(_spot),
        SlotExpr s => Copy(_slots[s.Index]!),
        BinaryExpr b => Apply(b.Op, Evaluate(b.Left), Evaluate(b.Right)),
        StepExpr s => Step(Evaluate(s.Argument), s.Scale),
        _ => throw new NotSupportedException(expr.GetType().Name),
    };

    private Column Apply(BinaryOp op, Column left, Column right)
    {
        double[] a = left.Value, b = right.Value;
        for (int p = 0; p < _tangents; p++)
        {
            double[] da = left.Tangents[p], db = right.Tangents[p];
            switch (op)
            {
                case BinaryOp.Add: Combine(da, db, 1); break;
                case BinaryOp.Subtract: Combine(da, db, -1); break;
                case BinaryOp.Multiply:
                    for (int i = 0; i < _length; i++) da[i] = da[i] * b[i] + a[i] * db[i];
                    break;
                case BinaryOp.Max:
                    for (int i = 0; i < _length; i++) if (b[i] > a[i]) da[i] = db[i];
                    break;
                case BinaryOp.Min:
                    for (int i = 0; i < _length; i++) if (b[i] < a[i]) da[i] = db[i];
                    break;
            }
        }

        switch (op)
        {
            case BinaryOp.Add: Combine(a, b, 1); break;
            case BinaryOp.Subtract: Combine(a, b, -1); break;
            case BinaryOp.Multiply: Multiply(a, b); break;
            case BinaryOp.Max: Extreme(a, b, Vector.Max); break;
            case BinaryOp.Min: Extreme(a, b, Vector.Min); break;
        }
        Release(right);
        return left;
    }

    /// <summary>The value stays a hard step; only its derivative is smoothed, so the PV is unbiased.</summary>
    private Column Step(Column argument, double scale)
    {
        double[] x = argument.Value;
        double width = _smoothing * scale;
        for (int p = 0; p < _tangents; p++)
        {
            double[] d = argument.Tangents[p];
            for (int i = 0; i < _length; i++)
            {
                d[i] = width > 0 && Math.Abs(x[i]) < width ? d[i] / (2 * width) : 0;
            }
        }
        for (int i = 0; i < _length; i++) x[i] = x[i] >= 0 ? 1 : 0;
        return argument;
    }

    private static void Combine(double[] a, double[] b, double sign)
    {
        var scale = new Vector<double>(sign);
        int i = 0;
        for (; i <= a.Length - Vector<double>.Count; i += Vector<double>.Count)
        {
            (new Vector<double>(a, i) + scale * new Vector<double>(b, i)).CopyTo(a, i);
        }
        for (; i < a.Length; i++) a[i] += sign * b[i];
    }

    private static void Multiply(double[] a, double[] b)
    {
        int i = 0;
        for (; i <= a.Length - Vector<double>.Count; i += Vector<double>.Count)
        {
            (new Vector<double>(a, i) * new Vector<double>(b, i)).CopyTo(a, i);
        }
        for (; i < a.Length; i++) a[i] *= b[i];
    }

    private static void Extreme(double[] a, double[] b, Func<Vector<double>, Vector<double>, Vector<double>> pick)
    {
        int i = 0;
        for (; i <= a.Length - Vector<double>.Count; i += Vector<double>.Count)
        {
            pick(new Vector<double>(a, i), new Vector<double>(b, i)).CopyTo(a, i);
        }
        for (; i < a.Length; i++) a[i] = pick(new Vector<double>(a[i]), new Vector<double>(b[i]))[0];
    }

    private Column Filled(double value)
    {
        var column = Rent();
        Array.Fill(column.Value, value);
        foreach (double[] tangent in column.Tangents) Array.Clear(tangent);
        return column;
    }

    private Column Copy(Column source)
    {
        var column = Rent();
        source.Value.CopyTo(column.Value, 0);
        for (int p = 0; p < _tangents; p++) source.Tangents[p].CopyTo(column.Tangents[p], 0);
        return column;
    }

    private Column Rent()
    {
        if (_free.TryPop(out var column)) return column;
        var tangents = new double[_tangents][];
        for (int p = 0; p < _tangents; p++) tangents[p] = new double[_length];
        return new Column(new double[_length], tangents);
    }

    private void Release(Column column) => _free.Push(column);
}
