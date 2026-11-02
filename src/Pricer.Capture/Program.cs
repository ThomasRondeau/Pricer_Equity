using Pricer.Data;
using Pricer.Data.Ibkr;
using Pricer.Data.Yahoo;

// Daily capture, meant for Windows Task Scheduler. Exit code 1 means nothing was stored.
var settings = CaptureSettings.Load();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    using var yahoo = new YahooOptionChainSource();
    IOptionChainSource source = new IbkrOptionChainSource(settings);
    if (args.Contains("--synthetic"))
    {
        source = new SyntheticOptionChainSource();
        settings = settings with { Watchlist = [new ChainRequest("SYNTH")] };
    }
    else if (args.Contains("--yahoo"))
    {
        source = yahoo;
        settings = await yahoo.PrepareAsync(settings, cancel.Token);
    }

    var store = new SnapshotStore(CaptureSettings.DatabasePath);
    var capture = new SnapshotCapture(source, store);
    var progress = new Progress<string>(Console.WriteLine);
    var outcome = await capture.RunAsync(settings, DateOnly.FromDateTime(DateTime.Today), progress, cancel.Token);

    Console.WriteLine($"Snapshot {outcome.SnapshotId} enregistré dans {CaptureSettings.DatabasePath}");
    foreach (var report in outcome.Reports)
    {
        Console.WriteLine($"{report.Underlying} : {report.Slices.Length} maturités");
        foreach (var slice in report.Slices)
        {
            Console.WriteLine($"  {slice.Expiry:yyyy-MM-dd}  F={slice.Slice.Forward,10:F2}  {slice.Points.Length,3} pts  RMSE {slice.RmseVol * 100:F2} pts de vol");
        }
        foreach (string warning in report.Warnings) Console.WriteLine($"  ! {warning}");
    }
    foreach (string failure in outcome.Failures) Console.WriteLine($"ÉCHEC {failure}");
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
