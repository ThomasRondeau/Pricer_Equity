using Pricer.Book;

namespace Pricer.Presentation;

/// <summary>One line of the explain table, for a group or a trade.</summary>
public sealed record PnlLine(string Key, PnlRow Row, string Status)
{
    public double Total => Row.Total;
    public double Time => Row[PnlFactor.Time];
    public double Events => Row[PnlFactor.Events];
    public double Spot => Row[PnlFactor.Spot];
    public double Dividends => Row[PnlFactor.Dividends];
    public double VolLevel => Row[PnlFactor.VolLevel];
    public double VolShape => Row[PnlFactor.VolShape];
    public double Rates => Row[PnlFactor.Rates];
    public double Composition => Row[PnlFactor.Composition];
    public double Residual => Row[PnlFactor.Residual];
    public double GreekResidual => Row.Greeks[(int)PnlFactor.Residual];
}

/// <summary>A factor under both methods, for the side-by-side table.</summary>
public sealed record FactorLine(string Factor, double Waterfall, double Greeks, double Gap);
