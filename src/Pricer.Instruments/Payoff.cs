using System.Collections.Immutable;

namespace Pricer.Instruments;

public enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Max,
    Min,
}

/// <summary>An expression evaluated on each path at an event date.</summary>
public abstract record PayoffExpr
{
    public abstract bool IsContinuous { get; }

    public static implicit operator PayoffExpr(double value) => new ConstantExpr(value);
    public static PayoffExpr operator +(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Add, a, b);
    public static PayoffExpr operator -(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Subtract, a, b);
    public static PayoffExpr operator *(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Multiply, a, b);
    public static PayoffExpr Max(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Max, a, b);
    public static PayoffExpr Min(PayoffExpr a, PayoffExpr b) => new BinaryExpr(BinaryOp.Min, a, b);
}

public sealed record ConstantExpr(double Value) : PayoffExpr
{
    public override bool IsContinuous => true;
}

public sealed record SpotExpr : PayoffExpr
{
    public override bool IsContinuous => true;
}

/// <summary>A per-path variable kept between events: running average, knock-in flag, memorised coupon.</summary>
public sealed record SlotExpr(int Index) : PayoffExpr
{
    public override bool IsContinuous => true;
}

public sealed record BinaryExpr(BinaryOp Op, PayoffExpr Left, PayoffExpr Right) : PayoffExpr
{
    public override bool IsContinuous => Left.IsContinuous && Right.IsContinuous;
}

/// <summary>1 when Argument is positive or zero, else 0. Scale sizes the smoothing used for pathwise greeks.</summary>
public sealed record StepExpr(PayoffExpr Argument, double Scale) : PayoffExpr
{
    public override bool IsContinuous => false;
}

/// <summary>A payoff is a dated list of events. Structured products need no engine of their own:
/// an autocall is Pay and Terminate on the same condition at each observation.</summary>
public abstract record PayoffEvent(DateOnly Date)
{
    public abstract bool IsContinuous { get; }
}

public sealed record StoreEvent(DateOnly Date, int Slot, PayoffExpr Value) : PayoffEvent(Date)
{
    public override bool IsContinuous => Value.IsContinuous;
}

public sealed record PayEvent(DateOnly Date, PayoffExpr Amount) : PayoffEvent(Date)
{
    public override bool IsContinuous => Amount.IsContinuous;
}

/// <summary>Ends the paths where Condition is 1.</summary>
public sealed record TerminateEvent(DateOnly Date, PayoffExpr Condition) : PayoffEvent(Date)
{
    public override bool IsContinuous => Condition.IsContinuous;
}

public sealed record PayoffProgram(ImmutableArray<PayoffEvent> Events, int Slots)
{
    public bool IsContinuous => Events.All(e => e.IsContinuous);
}

public static class PayoffCompiler
{
    /// <summary>Broadie-Glasserman-Kou: a daily check misses crossings, so the barrier moves towards spot to mimic continuous monitoring.</summary>
    private const double MonitoringShift = 0.5826;
    private const double DaysPerYear = 365.0;

    /// <param name="vol">Lognormal vol, used only to correct the barrier for discrete monitoring.</param>
    public static PayoffProgram Compile(Instrument instrument, DateOnly valuation, double spot, double vol) => instrument switch
    {
        EuropeanOption o => new([new PayEvent(o.Expiry, Vanilla(o))], 0),
        DigitalOption o => new([new PayEvent(o.Expiry, Digital(o))], 0),
        AsianOption o => Asian(o, valuation),
        BarrierOption o => Barrier(o, valuation, spot, vol),
        _ => throw new NotSupportedException($"Aucun payoff de trajectoire pour {instrument.GetType().Name}."),
    };

    /// <summary>Value on the expiry date itself, where no simulation or grid is needed.</summary>
    public static double AtExpiry(OptionInstrument option, double spot) => option switch
    {
        DigitalOption d => d.Sign * (spot - d.Strike) >= 0 ? d.Cash : 0,
        BarrierOption b when b.IsOut == b.IsBreachedAt(spot) => 0,
        AsianOption => throw new NotSupportedException("Fixings passés : pris en charge avec l'état de vie (phase 4)."),
        _ => Math.Max(option.Sign * (spot - option.Strike), 0),
    };

    private static PayoffExpr Vanilla(OptionInstrument o) => PayoffExpr.Max(o.Sign * (new SpotExpr() - o.Strike), 0.0);

    private static PayoffExpr Digital(DigitalOption o) =>
        (PayoffExpr)o.Cash * new StepExpr((PayoffExpr)o.Sign * (new SpotExpr() - o.Strike), o.Strike);

    private static PayoffProgram Asian(AsianOption o, DateOnly valuation)
    {
        if (o.Fixings < 1) throw new ArgumentException("Il faut au moins un fixing.");
        if (o.AveragingStart <= valuation)
        {
            throw new NotSupportedException("Fixings passés : pris en charge avec l'état de vie (phase 4).");
        }

        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        var slot = new SlotExpr(0);
        double span = o.Expiry.DayNumber - o.AveragingStart.DayNumber;
        for (int i = 0; i < o.Fixings; i++)
        {
            double offset = o.Fixings == 1 ? span : span * i / (o.Fixings - 1);
            var date = o.AveragingStart.AddDays((int)Math.Round(offset));
            events.Add(new StoreEvent(date, 0, slot + new SpotExpr()));
        }
        var average = (PayoffExpr)(1.0 / o.Fixings) * slot;
        events.Add(new PayEvent(o.Expiry, PayoffExpr.Max(o.Sign * (average - o.Strike), 0.0)));
        return new(events.ToImmutable(), 1);
    }

    private static PayoffProgram Barrier(BarrierOption o, DateOnly valuation, double spot, double vol)
    {
        if (o.IsBreachedAt(spot))
        {
            return o.IsOut ? new([], 0) : new([new PayEvent(o.Expiry, Vanilla(o))], 0);
        }

        double shift = Math.Exp((o.IsUp ? -1 : 1) * MonitoringShift * vol * Math.Sqrt(1 / DaysPerYear));
        double level = o.Barrier * shift;
        var distance = o.IsUp ? new SpotExpr() - level : (PayoffExpr)level - new SpotExpr();
        var hit = new StepExpr(distance, o.Barrier);

        var events = ImmutableArray.CreateBuilder<PayoffEvent>();
        var flag = new SlotExpr(0);
        for (var date = valuation.AddDays(1); date <= o.Expiry; date = date.AddDays(1))
        {
            events.Add(o.IsOut ? new TerminateEvent(date, hit) : new StoreEvent(date, 0, PayoffExpr.Max(flag, hit)));
        }
        events.Add(new PayEvent(o.Expiry, o.IsOut ? Vanilla(o) : flag * Vanilla(o)));
        return new(events.ToImmutable(), o.IsOut ? 0 : 1);
    }
}
