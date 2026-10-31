using Pricer.Instruments;
using Pricer.Models;

namespace Pricer.Engines;

public sealed class EngineRegistry
{
    private readonly Dictionary<(Type Instrument, Type Model), IPricingEngine> _engines = [];

    public static EngineRegistry Default()
    {
        var analytic = new BlackScholesAnalyticEngine();
        return new EngineRegistry()
            .Register<Stock, BlackScholesConfig>(analytic)
            .Register<EquityForward, BlackScholesConfig>(analytic)
            .Register<EuropeanOption, BlackScholesConfig>(analytic);
    }

    public EngineRegistry Register<TInstrument, TModel>(IPricingEngine engine)
        where TInstrument : Instrument
        where TModel : ModelConfig
    {
        _engines[(typeof(TInstrument), typeof(TModel))] = engine;
        return this;
    }

    public IPricingEngine Resolve(Instrument instrument, ModelConfig model) =>
        _engines.TryGetValue((instrument.GetType(), model.GetType()), out var engine)
            ? engine
            : throw new NotSupportedException(
                $"No engine for {instrument.GetType().Name} under {model.GetType().Name}.");
}
