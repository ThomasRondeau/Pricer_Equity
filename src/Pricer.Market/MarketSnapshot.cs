using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pricer.Market;

[JsonPolymorphic]
[JsonDerivedType(typeof(FlatVolSurface), "flat")]
[JsonDerivedType(typeof(SviVolSurface), "svi")]
public abstract record VolSurface
{
    public abstract double Vol(double strike, double t);
    public abstract VolSurface Shifted(double shift);
}

public sealed record FlatVolSurface(double Level) : VolSurface
{
    public override double Vol(double strike, double t) => Level;
    public override VolSurface Shifted(double shift) => new FlatVolSurface(Level + shift);
}

/// <param name="DividendCurve">Continuous carry; market-implied forwards are stored through it.</param>
public sealed record UnderlyingMarket(double Spot, YieldCurve DividendCurve, VolSurface Vol);

public sealed record MarketSnapshot(
    DateOnly AsOf,
    YieldCurve DiscountCurve,
    ImmutableSortedDictionary<string, UnderlyingMarket> Underlyings)
{
    public UnderlyingMarket Underlying(string name) =>
        Underlyings.TryGetValue(name, out var market)
            ? market
            : throw new KeyNotFoundException($"No market data for underlying '{name}'.");

    public MarketSnapshot WithUnderlying(string name, UnderlyingMarket market) =>
        this with { Underlyings = Underlyings.SetItem(name, market) };
}
