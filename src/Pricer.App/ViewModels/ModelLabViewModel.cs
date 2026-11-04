using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pricer.Market;
using Pricer.Models;
using Pricer.Risk;

namespace Pricer.App.ViewModels;

public sealed record ModelFitRow(string Expiry, double T, double Svi, double Heston, double LocalVol, SliceFit Fit);

public sealed partial class ModelLabViewModel : ObservableObject
{
    private const double VolPoints = 100;

    private MarketSnapshot? _snapshot;
    private CalibrationReport? _report;
    private string _underlying = "";

    [ObservableProperty] private ParameterForm _hestonForm = new("Heston", new HestonConfig());
    [ObservableProperty] private ParameterForm _localVolForm = new("Vol locale", new LocalVolConfig());
    [ObservableProperty] private ModelFitRow? _selectedSlice;
    [ObservableProperty] private string _marketLabel = "Sélectionnez un snapshot dans l'onglet Market.";
    [ObservableProperty] private string _status = "";

    public event Action<HestonConfig>? HestonFitted;
    public event Action<SliceFit>? FitChanged;

    public ObservableCollection<ModelFitRow> Slices { get; } = [];

    public void Use(MarketSnapshot snapshot, string underlying, CalibrationReport report)
    {
        _snapshot = snapshot;
        _underlying = underlying;
        _report = report;
        MarketLabel = $"{underlying} au {snapshot.AsOf:dd/MM/yyyy}";
        Slices.Clear();
        Status = "";
    }

    [RelayCommand]
    private async Task Calibrate()
    {
        if (_snapshot is not { } snapshot) return;
        Status = "Calibration de Heston…";
        string underlying = _underlying;
        var fit = await Task.Run(() => HestonCalibrator.Fit(snapshot, underlying));
        HestonForm = new ParameterForm("Heston", fit.Config);
        HestonFitted?.Invoke(fit.Config);
        Status = $"Heston calibré : RMSE {fit.RmseVol * VolPoints:F2} pts de vol, repris par l'onglet Pricer.";
        await Compare();
    }

    [RelayCommand]
    private async Task Compare()
    {
        if (_snapshot is not { } snapshot || _report is not { } report) return;
        Status = "Comparaison des modèles…";
        var heston = (HestonConfig)HestonForm.Record;
        var local = (LocalVolConfig)LocalVolForm.Record;
        string underlying = _underlying;
        var fits = await Task.Run(() => ModelComparison.Run(snapshot, underlying, report, heston, local));

        Slices.Clear();
        foreach (var fit in fits)
        {
            Slices.Add(new ModelFitRow(
                fit.Expiry.ToString("yyyy-MM-dd"), fit.T, fit.RmseSvi * VolPoints, fit.RmseHeston * VolPoints, fit.RmseLocalVol * VolPoints, fit));
        }
        SelectedSlice = Slices.FirstOrDefault();
        Status = $"{Slices.Count} maturités comparées.";
    }

    partial void OnSelectedSliceChanged(ModelFitRow? value)
    {
        if (value is not null) FitChanged?.Invoke(value.Fit);
    }
}
