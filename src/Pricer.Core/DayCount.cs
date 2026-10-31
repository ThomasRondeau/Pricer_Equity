namespace Pricer.Core;

public enum DayCount
{
    Act365F,
    Act360,
}

public static class DayCountExtensions
{
    public static double YearFraction(this DayCount convention, DateOnly start, DateOnly end)
    {
        int days = end.DayNumber - start.DayNumber;
        return convention switch
        {
            DayCount.Act365F => days / 365.0,
            DayCount.Act360 => days / 360.0,
            _ => throw new ArgumentOutOfRangeException(nameof(convention)),
        };
    }
}
