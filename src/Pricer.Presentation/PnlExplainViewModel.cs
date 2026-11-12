using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Book;
using Pricer.Core;
using Pricer.Engines;
using Pricer.Instruments;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.Presentation;

/// <summary>Optional moves on top of the second capture: a later date, then shocks on every underlying.</summary>
public sealed record PnlScenario
{
    /// <summary>The surface stays a function of strike and time to expiry, as in the explain's own time roll.</summary>
    [Parameter("Extra days after the capture", Group = "Scenario", Min = 0, Max = 365)]
    public int Days { get; init; }

    [Parameter("Spot shock", Group = "Scenario", Unit = "rel", Min = -0.5, Max = 0.5)]
    public double SpotShock { get; init; }

    [Parameter("Vol shock", Group = "Scenario", Unit = "abs", Min = -0.1, Max = 0.3)]
    public double VolShock { get; init; }

    [Parameter("Rate shock", Group = "Scenario", Unit = "abs", Min = -0.03, Max = 0.03)]
    public double RateShock { get; init; }

    public bool IsEmpty => this == new PnlScenario();

    public MarketSnapshot Apply(MarketSnapshot captured)
    {
        var market = captured with { AsOf = captured.AsOf.AddDays(Days) };
        foreach (string name in market.Underlyings.Keys)
        {
            if (SpotShock != 0) market = new SpotBump(name, SpotShock).Apply(market);
            if (VolShock != 0) market = new VolBump(name, VolShock).Apply(market);
        }
        return RateShock == 0 ? market : new RateBump(RateShock).Apply(market);
    }
}

/// <summary>Where an underlying stood at each end of the explain.</summary>
public sealed record MoveRow(string Underlying, double SpotFrom, double SpotTo, double VolFrom, double VolTo, double RateFrom, double RateTo);

public sealed record FixingRow(string Date, string Underlying, double Level, string Source);

/// <summary>One trade explained from one market date to another, through the book's full-revaluation explain:
/// a run on each date, then the factor-by-factor walk of <see cref="PnlExplainer"/>.</summary>
public sealed partial class PnlExplainViewModel : ObservableObject
{
    private const int ProductForm = 0;
    private const int PositionForm = 1;
    private const int ModelForm = 2;
    private const int EngineForm = 3;
    private const int RiskForm = 4;
    private const int LifeForm = 5;
    private const string TradeId = "PNL";

    private readonly EngineRegistry _registry = EngineRegistry.Default();
    private readonly PricerOptions _options;
    private Func<CaptureIndexEntry, Task<StoredCapture>> _load = _ => throw new InvalidOperationException("No market data.");
    private IReadOnlyList<CaptureIndexEntry> _captures = [];
    private PriceHistory _history = PriceHistory.Empty;
    private MarketSnapshot _snapshot;
    private string _underlying = SyntheticMarketDataProvider.DefaultUnderlying;

    [ObservableProperty] private ProductKind _productKind = ProductKind.EuropeanOption;
    [ObservableProperty] private IReadOnlyList<ModelKind> _availableModels = [ModelKind.BlackScholes, ModelKind.LocalVol, ModelKind.Heston];
    [ObservableProperty] private ModelKind _selectedModel = ModelKind.BlackScholes;
    [ObservableProperty] private IReadOnlyList<EngineKind> _availableEngines = [EngineKind.Analytic, EngineKind.Pde, EngineKind.MonteCarlo];
    [ObservableProperty] private EngineKind _selectedEngine = EngineKind.Analytic;
    [ObservableProperty] private IReadOnlyList<CaptureIndexEntry> _starts = [];
    [ObservableProperty] private IReadOnlyList<CaptureIndexEntry> _ends = [];
    [ObservableProperty] private CaptureIndexEntry? _from;
    [ObservableProperty] private CaptureIndexEntry? _to;
    [ObservableProperty] private PnlRow? _waterfall;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public PnlExplainViewModel(PricerOptions? options = null)
    {
        _options = options ?? new PricerOptions();
        _snapshot = new SyntheticMarketDataProvider().GetSnapshot(DateOnly.FromDateTime(DateTime.Today));
        Forms = [];
        for (int i = 0; i <= LifeForm; i++) Forms.Add(NewForm(i));
        foreach (var form in Forms) form.Changed += Clear;
    }

    public Array ProductKinds { get; } = Enum.GetValues<ProductKind>();

    /// <summary>The trade, as on the Pricer page.</summary>
    public ObservableCollection<ParameterForm> Forms { get; }

    public ParameterForm ScenarioForm { get; } = new("Scenario (optional)", new PnlScenario());
    public ParameterForm ExplainForm { get; } = new("Explain", new PnlConfig());
    public ObservableCollection<FactorLine> Factors { get; } = [];
    public ObservableCollection<MoveRow> Moves { get; } = [];
    public ObservableCollection<FixingRow> Fixings { get; } = [];

    /// <summary>Only local vol reads its parameters from the form: the explain prices on the run's model choice.</summary>
    public string ModelNote => SelectedModel switch
    {
        ModelKind.Heston => "Heston is calibrated to the surface of each date, then to each vol step of the explain.",
        ModelKind.BlackScholes => "Black-Scholes reads the implied vol of each date's surface at the trade's strike.",
        _ => "",
    };

    public string MarketLabel => From is null ? "No capture loaded." : $"{_underlying} · trade struck on {From.AsOf:yyyy-MM-dd}";

    /// <summary>The captures that hold the underlying, oldest first; the explain goes from the oldest real date to the latest.</summary>
    public async Task UseAsync(
        IReadOnlyList<CaptureIndexEntry> captures, Func<CaptureIndexEntry, Task<StoredCapture>> load, PriceHistory history, string underlying)
    {
        _load = load;
        _history = history;
        _underlying = underlying;
        // On a date, the capture with the most underlyings first: a worst-of needs its whole basket on both dates.
        _captures = [.. captures.Where(c => c.Underlyings.Contains(underlying)).OrderBy(c => c.AsOf).ThenByDescending(c => c.Underlyings.Length)];
        Starts = _captures;
        var real = _captures.Where(c => c.Source != "Synthetic").ToList();
        var pool = real.Count > 0 ? real : [.. _captures];
        var first = pool.FirstOrDefault();
        var last = pool.LastOrDefault() is { } latest ? pool.First(c => c.AsOf == latest.AsOf) : null;
        From = first;
        To = last is not null && first is not null && last.AsOf >= first.AsOf ? last : first;
        await LoadStartAsync();
    }

    partial void OnFromChanged(CaptureIndexEntry? value)
    {
        Ends = value is null ? [] : [.. _captures.Where(c => c.AsOf >= value.AsOf && c.Source == value.Source)];
        if (To is null || !Ends.Contains(To)) To = Ends.LastOrDefault();
        OnPropertyChanged(nameof(MarketLabel));
    }

    partial void OnToChanged(CaptureIndexEntry? value) => Clear();

    /// <summary>The trade is struck on the first market: its default strike and initial fixing are that day's spot.</summary>
    public async Task SelectFromAsync(CaptureIndexEntry entry)
    {
        From = entry;
        await LoadStartAsync();
    }

    private async Task LoadStartAsync()
    {
        if (From is not { } from) return;
        try
        {
            _snapshot = (await _load(from)).Snapshot;
            Replace(ProductForm);
            Clear();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            Status = $"Could not load {from.Label}: {e.Message}";
        }
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
        OnPropertyChanged(nameof(ModelNote));
        RefreshEngines();
    }

    private void RefreshEngines()
    {
        AvailableEngines = [.. EnginesFor(SelectedModel)];
        if (!AvailableEngines.Contains(SelectedEngine)) SelectedEngine = AvailableEngines[0];
        Clear();
    }

    partial void OnSelectedEngineChanged(EngineKind value) => Replace(EngineForm);

    private void Replace(int index)
    {
        Forms[index].Changed -= Clear;
        Forms[index] = NewForm(index);
        Forms[index].Changed += Clear;
        Clear();
    }

    private ParameterForm NewForm(int index) => index switch
    {
        ProductForm => new("Product", TradeCatalog.NewInstrument(ProductKind, _snapshot, _underlying)),
        PositionForm => new("Position", new PositionInputs()),
        // Only local vol has settings that the run reads: its bounds and correlation.
        ModelForm => SelectedModel == ModelKind.LocalVol
            ? new("Model: LocalVol", new LocalVolConfig())
            : new($"Model: {SelectedModel}", new NoSettings()),
        EngineForm => new($"Engine: {SelectedEngine}", TradeCatalog.NewEngine(SelectedEngine, _options)),
        RiskForm => new("Risk (greeks explain, always bumped)", TradeCatalog.NewRisk(ProductKind)),
        LifeForm => new("Trade lifecycle", new LifeInputs()),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>A model whose parameters come from the market, not from the form.</summary>
    private sealed record NoSettings;

    /// <summary>A result belongs to the inputs that produced it.</summary>
    private void Clear()
    {
        if (IsBusy) return;
        Waterfall = null;
        Summary = "";
        Warnings = "";
        Factors.Clear();
        Moves.Clear();
        Fixings.Clear();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Explain(CancellationToken token)
    {
        if (From is not { } from || To is not { } to)
        {
            Status = "Two captures of the underlying are required.";
            return;
        }

        Clear();
        IsBusy = true;
        var watch = Stopwatch.StartNew();
        try
        {
            Status = "Loading the markets…";
            var start = (await _load(from)).Snapshot;
            var captured = (await _load(to)).Snapshot;
            var scenario = (PnlScenario)ScenarioForm.Record;
            var end = scenario.Apply(captured);

            var instrument = (Instrument)Forms[ProductForm].Record;
            var engine = (EngineConfig)Forms[EngineForm].Record;
            var state = ((LifeInputs)Forms[LifeForm].Record).State(instrument, start.AsOf, name => start.Underlying(name).Spot);
            var position = new Position(
                TradeId, instrument, ((PositionInputs)Forms[PositionForm].Record).Quantity, Tag: "PnL", State: state,
                Model: TradeCatalog.Choice(SelectedModel), Engine: engine);
            var settled = ScenarioFixings.Settle(position, start, captured, end, _history);

            var config = new RunConfig
            {
                LocalVol = Forms[ModelForm].Record as LocalVolConfig ?? new LocalVolConfig(),
                Risk = (RiskConfig)Forms[RiskForm].Record,
                Pde = engine as PdeEngineConfig ?? (PdeEngineConfig)TradeCatalog.NewEngine(EngineKind.Pde, _options),
                MonteCarlo = engine as MonteCarloEngineConfig ?? (MonteCarloEngineConfig)TradeCatalog.NewEngine(EngineKind.MonteCarlo, _options),
            };
            var runs = new RunEngine(_registry, new MemoryRunCache());

            // One step at a time, each after a yield: a single-threaded browser repaints the status in between.
            var left = await Step($"Pricing on {start.AsOf:yyyy-MM-dd}…",
                () => runs.ExecuteAsync([position], start, 0, config, null, token), token);
            var right = await Step($"Pricing on {end.AsOf:yyyy-MM-dd}…",
                () => runs.ExecuteAsync([settled.Position], end, 1, config, null, token), token);
            if ((left.Trades[0].Error ?? right.Trades[0].Error) is { } error)
            {
                Status = error;
                return;
            }

            var explain = await Step("Explaining factor by factor…",
                () => new PnlExplainer(_registry).ExplainAsync(left, start, right, end, (PnlConfig)ExplainForm.Record, null, token), token);
            if (explain.Trades[0].Error is { } failure)
            {
                Status = failure;
                return;
            }

            Show(explain, left.Trades[0], right.Trades[0], settled, scenario);
            Status = $"{right.Trades[0].Result!.Engine} · {watch.Elapsed.TotalSeconds:F1} s for two runs and the explain.";
        }
        catch (OperationCanceledException)
        {
            Status = "Explain cancelled.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException
            or HttpRequestException)
        {
            Status = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<T> Step<T>(string status, Func<Task<T>> work, CancellationToken token)
    {
        Status = status;
        await Task.Delay(1, token);
        var result = await Task.Run(work, token);
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        return result;
    }

    private void Show(PnlExplain explain, TradeResult left, TradeResult right, SettledPosition settled, PnlScenario scenario)
    {
        var row = PnlAggregator.Row("Trade", explain.Trades);
        var (first, last) = (left.Result!, right.Result!);
        double cash = last.Realised - first.Realised;
        string paid = cash == 0 ? "" : $", cash received {cash:N2}";
        string breach = settled.BarrierBreached && left.Position.State?.BarrierBreached != true ? " The barrier was breached between the dates." : "";
        string shocked = scenario.IsEmpty ? "" : " (scenario applied)";
        Summary = $"PnL {row.Total:N2} from {explain.From:yyyy-MM-dd} to {explain.To:yyyy-MM-dd}{shocked}: "
            + $"PV {first.Pv:N2} → {last.Pv:N2}{paid}. Residual {row[PnlFactor.Residual]:N2} in full revaluation, "
            + $"{row.Greeks[(int)PnlFactor.Residual]:N2} from greeks.{breach}";
        Warnings = string.Join(Environment.NewLine, explain.Warnings);

        foreach (var factor in Enum.GetValues<PnlFactor>())
        {
            double waterfall = row.Waterfall[(int)factor];
            double greeks = row.Greeks[(int)factor];
            Factors.Add(new FactorLine(Spaced(factor.ToString()), waterfall, greeks, waterfall - greeks));
        }
        foreach (var point in left.Market)
        {
            var later = right.Market.FirstOrDefault(p => p.Underlying == point.Underlying) ?? point;
            Moves.Add(new MoveRow(point.Underlying, point.Spot, later.Spot, point.Vol, later.Vol, point.Rate, later.Rate));
        }
        foreach (var fixing in settled.Fixings)
        {
            Fixings.Add(new FixingRow(
                fixing.Date.ToString("yyyy-MM-dd"), fixing.Underlying, fixing.Level,
                fixing.Source == FixingSource.Close ? "Daily close" : "Interpolated path"));
        }
        Waterfall = row;
    }

    private static string Spaced(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString()));
}
