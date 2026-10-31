using System.Collections.Immutable;

namespace Pricer.Market;

public enum ExerciseStyle
{
    European,
    American,
}

public sealed record OptionQuote(DateOnly Expiry, double Strike, bool IsCall, double Bid, double Ask)
{
    public double Mid => (Bid + Ask) / 2;
    public bool IsTwoSided => Bid > 0 && Ask > Bid;
}

/// <summary>Raw quotes as captured. Kept alongside the snapshot: they cannot be fetched again later.</summary>
public sealed record OptionChain(
    string Underlying, DateOnly AsOf, double Spot, ExerciseStyle Exercise, ImmutableArray<OptionQuote> Quotes);
