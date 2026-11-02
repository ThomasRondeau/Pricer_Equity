using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Pricer.Core;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;

namespace Pricer.Engines;

public enum Measure
{
    Delta,
    Gamma,
    Vega,
    Theta,
    Rho,
}

public enum GreekMethod
{
    ClosedForm,
    PdeGrid,
    Pathwise,

    /// <summary>Pathwise delta differentiated once more through the likelihood ratio.</summary>
    PathwiseLikelihoodRatio,
    LikelihoodRatio,
    BumpReprice,
}

public enum GreekConvention
{
    /// <summary>Model state held fixed; not comparable across models.</summary>
    Model,

    /// <summary>Spot moved with the implied vol surface fixed by strike.</summary>
    MarketStickyStrike,
}

public enum McGreekChoice
{
    /// <summary>Pathwise where the payoff allows it, likelihood ratio otherwise.</summary>
    Auto,
    LikelihoodRatio,
}

[JsonPolymorphic]
[JsonDerivedType(typeof(AnalyticEngineConfig), "analytic")]
[JsonDerivedType(typeof(PdeEngineConfig), "pde")]
[JsonDerivedType(typeof(MonteCarloEngineConfig), "monte-carlo")]
public abstract record EngineConfig;

public sealed record AnalyticEngineConfig : EngineConfig;

public sealed record PdeEngineConfig : EngineConfig
{
    [Parameter("Pas d'espace", Group = "Moteur", Min = 50, Max = 3000)]
    public int SpaceSteps { get; init; } = 500;

    [Parameter("Pas de temps", Group = "Moteur", Min = 20, Max = 2000)]
    public int TimeSteps { get; init; } = 250;

    [Parameter("Pas Rannacher", Group = "Moteur", Min = 0, Max = 10)]
    public int RannacherSteps { get; init; } = 2;

    [Parameter("Largeur de grille", Group = "Moteur", Unit = "écarts-types", Min = 3, Max = 10)]
    public double Width { get; init; } = 5;
}

public sealed record MonteCarloEngineConfig : EngineConfig
{
    [Parameter("Trajectoires", Group = "Moteur", Min = 1024, Max = 1048576)]
    public int Paths { get; init; } = 1 << 16;

    /// <summary>Independently randomised copies of the Sobol set; their dispersion gives the standard error.</summary>
    [Parameter("Lots", Group = "Moteur", Min = 4, Max = 128)]
    public int Batches { get; init; } = 32;

    [Parameter("Graine", Group = "Moteur", Min = 0, Max = 100000)]
    public int Seed { get; init; } = 42;

    [Parameter("Grecques", Group = "Moteur")]
    public McGreekChoice Greeks { get; init; } = McGreekChoice.Auto;

    /// <summary>Half-width of the ramp replacing a step in pathwise derivatives, relative to its scale. The PV stays unsmoothed.</summary>
    [Parameter("Lissage des discontinuités", Group = "Moteur", Unit = "rel", Min = 0, Max = 0.1)]
    public double Smoothing { get; init; }
}

public sealed record PricingRequest(
    Trade Trade,
    MarketSnapshot Market,
    ModelConfig Model,
    EngineConfig Engine,
    DateOnly ValuationDate,
    ImmutableArray<Measure> Measures);

/// <summary>Position-level sensitivity. Units: delta per spot unit, gamma per spot unit squared,
/// vega per vol point, theta per calendar day, rho per rate point.</summary>
public sealed record GreekValue(
    Measure Measure, double Value, GreekMethod Method, GreekConvention Convention, double? StandardError = null);

public sealed record PricingResult(double Pv, double? PvStandardError, string Engine, ImmutableArray<GreekValue> Greeks)
{
    public GreekValue? Greek(Measure measure) => Greeks.FirstOrDefault(g => g.Measure == measure);
}

public interface IPricingEngine
{
    string Name { get; }

    /// <summary>The engine may omit requested measures; the risk layer fills them by bumping.</summary>
    PricingResult Price(PricingRequest request);
}
