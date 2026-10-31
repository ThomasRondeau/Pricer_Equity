using System.Collections.Immutable;
using System.Text.Json.Serialization;
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
    BumpReprice,
}

public enum GreekConvention
{
    /// <summary>Model state held fixed; not comparable across models.</summary>
    Model,

    /// <summary>Spot moved with the implied vol surface fixed by strike.</summary>
    MarketStickyStrike,
}

[JsonPolymorphic]
[JsonDerivedType(typeof(AnalyticEngineConfig), "analytic")]
public abstract record EngineConfig;

public sealed record AnalyticEngineConfig : EngineConfig;

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
