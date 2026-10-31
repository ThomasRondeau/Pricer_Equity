using System.Text.Json.Serialization;
using Pricer.Core;

namespace Pricer.Instruments;

public enum OptionRight
{
    Call,
    Put,
}

[JsonPolymorphic]
[JsonDerivedType(typeof(Stock), "stock")]
[JsonDerivedType(typeof(EquityForward), "forward")]
[JsonDerivedType(typeof(EuropeanOption), "european")]
public abstract record Instrument
{
    public required string Underlying { get; init; }
}

public sealed record Stock : Instrument;

public sealed record EquityForward : Instrument
{
    [Parameter("Strike", Group = "Produit", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Échéance", Group = "Produit")]
    public required DateOnly Expiry { get; init; }
}

public sealed record EuropeanOption : Instrument
{
    [Parameter("Type", Group = "Produit")]
    public required OptionRight Right { get; init; }

    [Parameter("Strike", Group = "Produit", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Échéance", Group = "Produit")]
    public required DateOnly Expiry { get; init; }
}

public sealed record Trade(string Id, Instrument Instrument, double Quantity);
