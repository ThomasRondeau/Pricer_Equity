using System.Globalization;
using Pricer.Book;

namespace Pricer.Data.Ibkr;

/// <summary>Reads the account's positions. Read-only, like the rest of the connection.</summary>
public sealed class IbkrPositionProvider(CaptureSettings settings) : IPositionProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public string Name => BrokerImport.Source;

    public async Task<IReadOnlyList<BrokerPosition>> FetchAsync(CancellationToken token)
    {
        using var connection = await IbkrConnection.ConnectAsync(settings, token);
        var contracts = await connection.PositionsAsync(Timeout, token);
        return [.. contracts.Select(p => new BrokerPosition(
            p.Contract.ConId, p.Contract.Symbol, p.Contract.SecType, p.Contract.Right ?? "", p.Contract.Strike,
            p.Contract.LastTradeDateOrContractMonth ?? "",
            double.TryParse(p.Contract.Multiplier, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) ? multiplier : 1,
            p.Quantity))];
    }
}
