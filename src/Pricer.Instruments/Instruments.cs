using System.Text.Json.Serialization;
using Pricer.Core;

namespace Pricer.Instruments;

public enum OptionRight
{
    Call,
    Put,
}

public enum BarrierKind
{
    DownOut,
    DownIn,
    UpOut,
    UpIn,
}

[JsonPolymorphic]
[JsonDerivedType(typeof(Stock), "stock")]
[JsonDerivedType(typeof(EquityForward), "forward")]
[JsonDerivedType(typeof(EuropeanOption), "european")]
[JsonDerivedType(typeof(AmericanOption), "american")]
[JsonDerivedType(typeof(DigitalOption), "digital")]
[JsonDerivedType(typeof(BarrierOption), "barrier")]
[JsonDerivedType(typeof(AsianOption), "asian")]
public abstract record Instrument
{
    public required string Underlying { get; init; }
}

/// <summary>An option with a single strike and expiry.</summary>
public abstract record OptionInstrument : Instrument
{
    [Parameter("Type", Group = "Produit")]
    public required OptionRight Right { get; init; }

    [Parameter("Strike", Group = "Produit", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Échéance", Group = "Produit")]
    public required DateOnly Expiry { get; init; }

    public double Sign => Right == OptionRight.Call ? 1 : -1;
}

public sealed record Stock : Instrument;

public sealed record EquityForward : Instrument
{
    [Parameter("Strike", Group = "Produit", Min = 0.2, Max = 3, Relative = true)]
    public required double Strike { get; init; }

    [Parameter("Échéance", Group = "Produit")]
    public required DateOnly Expiry { get; init; }
}

public sealed record EuropeanOption : OptionInstrument;

public sealed record AmericanOption : OptionInstrument;

/// <summary>Pays Cash at expiry if the option finishes in the money.</summary>
public sealed record DigitalOption : OptionInstrument
{
    [Parameter("Montant payé", Group = "Produit", Min = 0, Max = 1000)]
    public double Cash { get; init; } = 1;
}

/// <summary>Continuously monitored, without rebate.</summary>
public sealed record BarrierOption : OptionInstrument
{
    [Parameter("Barrière", Group = "Produit", Min = 0.2, Max = 3, Relative = true)]
    public required double Barrier { get; init; }

    [Parameter("Type de barrière", Group = "Produit")]
    public required BarrierKind Kind { get; init; }

    public bool IsUp => Kind is BarrierKind.UpOut or BarrierKind.UpIn;
    public bool IsOut => Kind is BarrierKind.DownOut or BarrierKind.UpOut;
    public bool IsBreachedAt(double spot) => IsUp ? spot >= Barrier : spot <= Barrier;
}

/// <summary>Arithmetic average of equally spaced fixings, from AveragingStart to Expiry included.</summary>
public sealed record AsianOption : OptionInstrument
{
    [Parameter("Début de moyenne", Group = "Produit")]
    public required DateOnly AveragingStart { get; init; }

    [Parameter("Nombre de fixings", Group = "Produit", Min = 1, Max = 260)]
    public int Fixings { get; init; } = 12;
}

public sealed record Trade(string Id, Instrument Instrument, double Quantity);
