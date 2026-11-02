using System.Text.Json.Serialization;
using Pricer.Core;

namespace Pricer.Models;

[JsonPolymorphic]
[JsonDerivedType(typeof(BlackScholesConfig), "black-scholes")]
[JsonDerivedType(typeof(LocalVolConfig), "local-vol")]
[JsonDerivedType(typeof(HestonConfig), "heston")]
public abstract record ModelConfig;

public sealed record BlackScholesConfig : ModelConfig
{
    [Parameter("Ajustement de vol", Group = "Modèle", Unit = "abs", Min = -0.2, Max = 0.2)]
    public double VolAdjustment { get; init; }
}

/// <summary>Dupire local vol from the market surface. Baskets join several of them with a Gaussian copula.</summary>
public sealed record LocalVolConfig : ModelConfig
{
    [Parameter("Vol locale minimale", Group = "Modèle", Unit = "abs", Min = 0.005, Max = 0.2)]
    public double MinVol { get; init; } = 0.02;

    [Parameter("Vol locale maximale", Group = "Modèle", Unit = "abs", Min = 0.5, Max = 5)]
    public double MaxVol { get; init; } = 2;

    /// <summary>Flat between all pairs. Not negative: the common factor construction needs its square root.</summary>
    [Parameter("Corrélation", Group = "Modèle", Min = 0, Max = 1)]
    public double Correlation { get; init; } = 0.6;
}

public sealed record HestonConfig : ModelConfig
{
    [Parameter("Variance initiale v0", Group = "Modèle", Min = 0.0005, Max = 1)]
    public double V0 { get; init; } = 0.04;

    [Parameter("Vitesse de retour kappa", Group = "Modèle", Min = 0.05, Max = 15)]
    public double Kappa { get; init; } = 2;

    [Parameter("Variance long terme theta", Group = "Modèle", Min = 0.0005, Max = 1)]
    public double Theta { get; init; } = 0.04;

    [Parameter("Vol de vol xi", Group = "Modèle", Min = 0.02, Max = 3)]
    public double Xi { get; init; } = 0.5;

    [Parameter("Corrélation rho", Group = "Modèle", Min = -0.99, Max = 0.99)]
    public double Rho { get; init; } = -0.6;

    /// <summary>A market bump has no effect on fixed parameters, so market greeks need the fit redone.</summary>
    [Parameter("Recalibrer sur les bumps", Group = "Modèle")]
    public bool RecalibrateOnBump { get; init; } = true;
}
