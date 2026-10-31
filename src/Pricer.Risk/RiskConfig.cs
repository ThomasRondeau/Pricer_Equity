using Pricer.Core;

namespace Pricer.Risk;

public enum GreekPreference
{
    /// <summary>Use the engine's own greeks, bump what is missing.</summary>
    BestAvailable,

    /// <summary>Bump everything, e.g. to check an engine against the reference method.</summary>
    ForceBump,
}

public sealed record RiskConfig
{
    [Parameter("Méthode des grecques", Group = "Risque")]
    public GreekPreference Preference { get; init; } = GreekPreference.BestAvailable;

    [Parameter("Bump spot", Group = "Risque", Unit = "rel", Min = 0.0001, Max = 0.05)]
    public double SpotBump { get; init; } = 0.001;

    [Parameter("Bump vol", Group = "Risque", Unit = "abs", Min = 0.0001, Max = 0.02)]
    public double VolBump { get; init; } = 0.001;

    [Parameter("Bump taux", Group = "Risque", Unit = "abs", Min = 0.00001, Max = 0.01)]
    public double RateBump { get; init; } = 0.0001;
}
