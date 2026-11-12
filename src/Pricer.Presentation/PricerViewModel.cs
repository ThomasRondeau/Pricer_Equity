using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Book;
using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Native;
using Pricer.Risk;

namespace Pricer.Presentation;

public enum ProductKind
{
    EuropeanOption,
    AmericanOption,
    Digital,
    Barrier,
    Asian,
    ReverseConvertible,
    Autocall,
    Phoenix,
    WorstOf,
    Forward,
    Stock,
}

public enum ModelKind
{
    BlackScholes,
    LocalVol,
    Heston,
}

public enum EngineKind
{
    Analytic,
    Pde,
    MonteCarlo,
}

/// <summary>Manual overrides, layered over the selected snapshot.</summary>
public sealed record MarketInputs
{
    [Parameter("Spot", Group = "Market", Min = 0.2, Max = 3, Relative = true)]
    public double Spot { get; init; }

    [Parameter("Vol shift", Group = "Market", Unit = "abs", Min = -0.15, Max = 0.3)]
    public double VolShift { get; init; }

    [Parameter("Dividend shift", Group = "Market", Unit = "abs", Min = -0.05, Max = 0.05)]
    public double DividendShift { get; init; }

    [Parameter("Curve shift", Group = "Market", Unit = "abs", Min = -0.03, Max = 0.03)]
    public double RateShift { get; init; }

    public MarketSnapshot ApplyTo(MarketSnapshot snapshot, string underlying)
    {
        var market = snapshot.Underlying(underlying);
        return snapshot.WithUnderlying(underlying, new UnderlyingMarket(
            Spot, market.DividendCurve.Shifted(DividendShift), market.Vol.Shifted(VolShift))) with
        {
            DiscountCurve = snapshot.DiscountCurve.Shifted(RateShift),
        };
    }
}

public sealed record PositionInputs
{
    [Parameter("Quantity", Group = "Position", Min = -1000, Max = 1000)]
    public double Quantity { get; init; } = 1;
}

/// <summary>A simplified past for a trade in life: all past fixings at one level.</summary>
public sealed record LifeInputs
{
    [Parameter("Past fixings level", Group = "Trade lifecycle", Unit = "× initial", Min = 0.2, Max = 2)]
    public double PastLevel { get; init; } = 1;

    [Parameter("Barrier already breached", Group = "Trade lifecycle")]
    public bool BarrierBreached { get; init; }

    public TradeState State(Instrument instrument, DateOnly asOf, Func<string, double> spot)
    {
        if (instrument is not (StructuredNote or AsianOption)) return new TradeState { BarrierBreached = BarrierBreached };

        var dates = PayoffCompiler.Compile(instrument, asOf, 0, 0).Events.Select(e => e.Date).Where(d => d < asOf).Distinct();
        return new TradeState
        {
            BarrierBreached = BarrierBreached,
            Fixings = [.. dates.SelectMany(date => instrument.Underlyings.Select(
                name => new FixingRecord(name, date, PastLevel * Initial(instrument, name, spot))))],
        };
    }

    private static double Initial(Instrument instrument, string name, Func<string, double> spot) => instrument switch
    {
        WorstOfPhoenix worst => worst.Basket.First(l => l.Underlying == name).InitialFixing,
        StructuredNote note => note.InitialFixing,
        _ => spot(name),
    };
}

/// <summary>Calculation defaults chosen by the host: a single-threaded browser needs lighter settings than the desktop.</summary>
public sealed record PricerOptions
{
    public MonteCarloEngineConfig MonteCarlo { get; init; } = new();
    public int ProfilePoints { get; init; } = 81;

    /// <summary>Where new PDE and Monte Carlo engines run; the engine form can change it. The native kernels give the
    /// managed results to the last bits and run faster; without the library, engines stay managed.</summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Native;
}

/// <summary>A greek next to its bump-and-reprice control. When the two conventions differ (a local vol grid delta
/// is a model greek, the bump a sticky-strike market greek), the gap measures smile dynamics, not an error.</summary>
public sealed record GreekRow(
    string Measure, double Value, double? Error, string Method, string Convention, double BumpValue, string BumpConvention, double Gap)
{
    public const string ConventionNote = "Different conventions: the gap is the effect of smile dynamics, not a numerical error.";

    public bool SameConvention => Convention == BumpConvention;

    public string GapNote => SameConvention ? "" : $"{Convention} vs {BumpConvention}";
}

public sealed record EngineRow(string Engine, double Pv, double? Error, double Milliseconds);

public sealed record ProfileData(double[] Spots, double[] Values);

public sealed partial class PricerViewModel : ObservableObject
{
    private const int ProductForm = 0;
    private const int PositionForm = 1;
    private const int MarketForm = 2;
    private const int ModelForm = 3;
    private const int EngineForm = 4;
    private const int RiskForm = 5;
    private const int LifeForm = 6;
    private const int PreviewDivisor = 16;
    private const int MinPreviewPaths = 1024;
    private const int PreviewBatches = 8;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);
    private static readonly ImmutableArray<Measure> AllMeasures = [.. Enum.GetValues<Measure>()];

    private readonly EngineRegistry _registry = EngineRegistry.Default();
    private readonly PricerOptions _options;
    private readonly RiskCalculator _risk;
    private HestonConfig _heston = new();
    private MarketSnapshot _snapshot;
    private string _underlying = SyntheticMarketDataProvider.DefaultUnderlying;
    private CancellationTokenSource? _pending;

    [ObservableProperty] private ProductKind _productKind = ProductKind.EuropeanOption;
    [ObservableProperty] private IReadOnlyList<ModelKind> _availableModels = [ModelKind.BlackScholes, ModelKind.LocalVol, ModelKind.Heston];
    [ObservableProperty] private ModelKind _selectedModel = ModelKind.BlackScholes;
    [ObservableProperty] private IReadOnlyList<EngineKind> _availableEngines = [EngineKind.Analytic, EngineKind.Pde, EngineKind.MonteCarlo];
    [ObservableProperty] private EngineKind _selectedEngine = EngineKind.Analytic;
    [ObservableProperty] private string _pvText = "";
    [ObservableProperty] private string _marketLabel = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isBusy;

    public PricerViewModel(PricerOptions? options = null)
    {
        _options = options ?? new PricerOptions();
        _risk = new RiskCalculator(_registry);
        _snapshot = new SyntheticMarketDataProvider().GetSnapshot(DateOnly.FromDateTime(DateTime.Today));
        Forms = [];
        for (int i = 0; i <= LifeForm; i++) Forms.Add(NewForm(i));
        foreach (var form in Forms) form.Changed += Schedule;
        UpdateMarketLabel();
        Schedule();
    }

    public event Action<ProfileData>? ProfileChanged;
    public event Action<Position>? PositionRequested;

    public Array ProductKinds { get; } = Enum.GetValues<ProductKind>();
    public ObservableCollection<ParameterForm> Forms { get; }
    public ObservableCollection<GreekRow> Greeks { get; } = [];
    public ObservableCollection<EngineRow> Engines { get; } = [];

    public void UseMarket(MarketSnapshot snapshot, string underlying)
    {
        _snapshot = snapshot;
        _underlying = underlying;
        Replace(ProductForm);
        Replace(MarketForm);
        UpdateMarketLabel();
        Schedule();
    }

    [RelayCommand]
    private void AddToBook()
    {
        var instrument = (Instrument)Forms[ProductForm].Record;
        var state = ((LifeInputs)Forms[LifeForm].Record).State(instrument, _snapshot.AsOf, name => _snapshot.Underlying(name).Spot);
        var model = TradeCatalog.Choice(SelectedModel);
        PositionRequested?.Invoke(new Position(
            $"L-{Guid.NewGuid():N}"[..10], instrument, ((PositionInputs)Forms[PositionForm].Record).Quantity,
            Tag: "Pricer", State: state, Model: model));
        Status = "Added to the book.";
    }

    /// <summary>The calibrated parameters take over from the defaults for the Heston model.</summary>
    public void UseHeston(HestonConfig config)
    {
        _heston = config;
        if (SelectedModel != ModelKind.Heston) return;
        Replace(ModelForm);
        Schedule();
    }

    private Type InstrumentType => Forms[ProductForm].Record.GetType();

    private IEnumerable<EngineKind> EnginesFor(ModelKind model) => TradeCatalog.EnginesFor(_registry, InstrumentType, model);

    partial void OnProductKindChanged(ProductKind value)
    {
        Replace(ProductForm);
        Replace(RiskForm);
        AvailableModels = [.. Enum.GetValues<ModelKind>().Where(m => EnginesFor(m).Any())];
        if (AvailableModels.Contains(SelectedModel)) RefreshEngines();
        else SelectedModel = AvailableModels[0];
    }

    partial void OnSelectedModelChanged(ModelKind value)
    {
        Replace(ModelForm);
        RefreshEngines();
    }

    private void RefreshEngines()
    {
        AvailableEngines = [.. EnginesFor(SelectedModel)];
        if (AvailableEngines.Contains(SelectedEngine)) Schedule();
        else SelectedEngine = AvailableEngines[0];
    }

    partial void OnSelectedEngineChanged(EngineKind value)
    {
        Replace(EngineForm);
        Schedule();
    }

    private void UpdateMarketLabel() => MarketLabel = $"{_underlying} as of {_snapshot.AsOf:yyyy-MM-dd}";

    private void Replace(int index)
    {
        Forms[index].Changed -= Schedule;
        Forms[index] = NewForm(index);
        Forms[index].Changed += Schedule;
    }

    private ParameterForm NewForm(int index) => index switch
    {
        ProductForm => new("Product", NewInstrument()),
        PositionForm => new("Position", new PositionInputs()),
        MarketForm => new("Market (overrides)", new MarketInputs { Spot = _snapshot.Underlying(_underlying).Spot }),
        ModelForm => new($"Model: {SelectedModel}", NewModel(SelectedModel)),
        EngineForm => new($"Engine: {SelectedEngine}", NewEngine(SelectedEngine)),
        RiskForm => new("Risk", TradeCatalog.NewRisk(ProductKind)),
        LifeForm => new("Trade lifecycle", new LifeInputs()),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private ModelConfig NewModel(ModelKind kind) => TradeCatalog.NewModel(kind, _heston);

    private EngineConfig NewEngine(EngineKind kind) => TradeCatalog.NewEngine(kind, _options);

    private static EngineConfig WithBackend(EngineConfig config, ComputeBackend backend) => config switch
    {
        PdeEngineConfig pde => pde with { Backend = backend },
        MonteCarloEngineConfig mc => mc with { Backend = backend },
        _ => config,
    };

    private static ComputeBackend? BackendOf(EngineConfig config) => config switch
    {
        PdeEngineConfig pde => pde.Backend,
        MonteCarloEngineConfig mc => mc.Backend,
        _ => null,
    };

    /// <summary>The selected engine on the other backend first, then the other engines on the selected backend.</summary>
    private EngineConfig[] Comparisons(EngineConfig selected)
    {
        var backend = BackendOf(selected) ?? ComputeBackend.Managed;
        var others = AvailableEngines.Where(e => e != SelectedEngine).Select(e => WithBackend(NewEngine(e), backend));
        if (BackendOf(selected) is { } own && NativeCore.IsAvailable)
        {
            var other = own == ComputeBackend.Native ? ComputeBackend.Managed : ComputeBackend.Native;
            others = others.Prepend(WithBackend(selected, other));
        }
        return [.. others];
    }

    /// <summary>The profile reprices many times, so Monte Carlo runs lighter there.</summary>
    private static EngineConfig Preview(EngineConfig config) => config is MonteCarloEngineConfig mc
        ? mc with { Paths = Math.Max(mc.Paths / PreviewDivisor, MinPreviewPaths), Batches = PreviewBatches }
        : config;

    private Instrument NewInstrument() => TradeCatalog.NewInstrument(ProductKind, _snapshot, _underlying);

    private sealed record Output(
        PricingResult Best, PricingResult Bumped, ProfileData Profile, ImmutableArray<EngineRow> Engines, string Hash, TimeSpan Elapsed);

    private async void Schedule()
    {
        _pending?.Cancel();
        var pending = _pending = new CancellationTokenSource();
        var token = pending.Token;

        var market = ((MarketInputs)Forms[MarketForm].Record).ApplyTo(_snapshot, _underlying);
        var instrument = (Instrument)Forms[ProductForm].Record;
        var life = (LifeInputs)Forms[LifeForm].Record;
        var state = life.State(instrument, market.AsOf, name => market.Underlying(name).Spot);
        var trade = new Trade("UI", instrument, ((PositionInputs)Forms[PositionForm].Record).Quantity, state);
        var request = new PricingRequest(
            trade, market, (ModelConfig)Forms[ModelForm].Record, (EngineConfig)Forms[EngineForm].Record, market.AsOf, AllMeasures);
        var config = (RiskConfig)Forms[RiskForm].Record;
        string underlying = _underlying;
        var others = Comparisons(request.Engine);

        IsBusy = true;
        try
        {
            await Task.Delay(Debounce, token);
            var output = await Task.Run(() => CalculateAsync(request, config, underlying, others, token), token);
            Show(output);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            HasError = true;
            Status = e.Message;
        }
        finally
        {
            // A newer edit owns the flag once it has started.
            if (_pending == pending) IsBusy = false;
        }
    }

    /// <summary>Yields between reprices: in a single-threaded browser this lets the page redraw and a new edit cancel the run.</summary>
    private async Task<Output> CalculateAsync(
        PricingRequest request, RiskConfig config, string underlying, EngineConfig[] others, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var best = _risk.Compute(request, config);
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        // When every greek already comes from bumping, the control would only repeat the calculation.
        var bumped = best.Greeks.All(g => g.Method == GreekMethod.BumpReprice)
            ? best
            : _risk.Compute(request, config with { Preference = GreekPreference.ForceBump });

        double spot = request.Market.Underlying(underlying).Spot;
        int points = _options.ProfilePoints;
        var spots = new double[points];
        var values = new double[points];
        var pvOnly = request with { Measures = [] };
        var preview = pvOnly with { Engine = Preview(request.Engine) };
        for (int i = 0; i < points; i++)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            double relative = -0.5 + i / (points - 1.0);
            spots[i] = spot * (1 + relative);
            var shifted = new SpotBump(underlying, relative).Apply(request.Market);
            values[i] = _risk.Compute(preview with { Market = shifted }, config).Pv;
        }

        // Every row is timed on the PV alone, so that engines and backends compare like for like.
        var engines = ImmutableArray.CreateBuilder<EngineRow>();
        foreach (var other in others.Prepend(request.Engine))
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = _risk.Compute(pvOnly with { Engine = other }, config);
            engines.Add(new EngineRow(result.Engine, result.Pv, result.PvStandardError, timer.Elapsed.TotalMilliseconds));
        }

        return new Output(
            best, bumped, new ProfileData(spots, values), engines.ToImmutable(), ContentHash.Of(new { request, config }), watch.Elapsed);
    }

    private void Show(Output output)
    {
        PvText = output.Best.PvStandardError is > 0 and var error
            ? $"{output.Best.Pv:N4} ± {error:N4}"
            : output.Best.Pv.ToString("N4");
        HasError = false;
        string realised = output.Best.Realised == 0 ? "" : $" · already paid {output.Best.Realised:N2}";
        Status = $"{output.Best.Engine} · {output.Elapsed.TotalMilliseconds:F1} ms · hash {output.Hash[..12]}{realised}";

        Greeks.Clear();
        foreach (var greek in output.Best.Greeks)
        {
            var bump = output.Bumped.Greek(greek.Measure, greek.Underlying)!;
            Greeks.Add(new GreekRow(
                $"{greek.Measure} {greek.Underlying}".TrimEnd(), greek.Value, greek.StandardError, greek.Method.ToString(), greek.Convention.ToString(),
                bump.Value, bump.Convention.ToString(), greek.Value - bump.Value));
        }

        Engines.Clear();
        foreach (var row in output.Engines) Engines.Add(row);
        ProfileChanged?.Invoke(output.Profile);
    }
}
