using System.Text.Json.Serialization;
using Pricer.Core;

namespace Pricer.Models;

[JsonPolymorphic]
[JsonDerivedType(typeof(BlackScholesConfig), "black-scholes")]
public abstract record ModelConfig;

public sealed record BlackScholesConfig : ModelConfig
{
    [Parameter("Ajustement de vol", Group = "Modèle", Unit = "abs", Min = -0.2, Max = 0.2)]
    public double VolAdjustment { get; init; }
}
