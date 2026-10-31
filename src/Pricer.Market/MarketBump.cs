namespace Pricer.Market;

/// <summary>A transformation in market space. Greeks, stress and PnL steps all build on these.</summary>
public abstract record MarketBump
{
    public abstract MarketSnapshot Apply(MarketSnapshot snapshot);
}

public sealed record SpotBump(string Underlying, double Relative) : MarketBump
{
    public override MarketSnapshot Apply(MarketSnapshot snapshot)
    {
        var market = snapshot.Underlying(Underlying);
        return snapshot.WithUnderlying(Underlying, market with { Spot = market.Spot * (1 + Relative) });
    }
}

public sealed record VolBump(string Underlying, double Absolute) : MarketBump
{
    public override MarketSnapshot Apply(MarketSnapshot snapshot)
    {
        var market = snapshot.Underlying(Underlying);
        return snapshot.WithUnderlying(Underlying, market with { Vol = market.Vol.Shifted(Absolute) });
    }
}

public sealed record RateBump(double Absolute) : MarketBump
{
    public override MarketSnapshot Apply(MarketSnapshot snapshot) =>
        snapshot with { DiscountCurve = snapshot.DiscountCurve.Shifted(Absolute) };
}
