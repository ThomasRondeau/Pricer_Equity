using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pricer.Book;
using Pricer.Engines;

namespace Pricer.Data;

public sealed record RunInfo(long Id, string Label, DateTime CreatedAt, DateOnly Valuation, long SnapshotId, int Trades, double Pv);

/// <summary>Positions and past runs. The results of past runs double as the persistent cache:
/// a hash covers all the inputs of a trade, so an old result stays valid for as long as its hash is asked for.</summary>
public sealed class BookStore : IRunCache
{
    private readonly string _connection;

    public BookStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open();
        db.Execute("""
            CREATE TABLE IF NOT EXISTS positions (id TEXT PRIMARY KEY, position_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, label TEXT NOT NULL, created_at TEXT NOT NULL,
                valuation TEXT NOT NULL, snapshot_id INTEGER NOT NULL, code_version TEXT NOT NULL,
                config_json TEXT NOT NULL, trades INTEGER NOT NULL, pv REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS run_trades (
                run_id INTEGER NOT NULL REFERENCES runs(id), position_id TEXT NOT NULL, hash TEXT,
                trade_json TEXT NOT NULL, PRIMARY KEY (run_id, position_id));
            CREATE INDEX IF NOT EXISTS run_trades_hash ON run_trades(hash);
            """);
    }

    public IReadOnlyList<Position> Positions()
    {
        using var db = Open();
        return [.. db.Query<string>("SELECT position_json FROM positions ORDER BY id").Select(Deserialize<Position>)];
    }

    public void Save(IEnumerable<Position> positions)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var position in positions)
        {
            db.Execute(
                "INSERT OR REPLACE INTO positions (id, position_json) VALUES (@Id, @Json)",
                new { position.Id, Json = JsonSerializer.Serialize(position) }, transaction);
        }
        transaction.Commit();
    }

    /// <summary>Makes the stored book exactly this list, for a reconciliation that may close positions.</summary>
    public void Replace(IEnumerable<Position> positions)
    {
        using (var db = Open()) db.Execute("DELETE FROM positions");
        Save(positions);
    }

    public void Delete(string id)
    {
        using var db = Open();
        db.Execute("DELETE FROM positions WHERE id = @id", new { id });
    }

    public long Save(RunResult run, string label)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        long id = db.ExecuteScalar<long>("""
            INSERT INTO runs (label, created_at, valuation, snapshot_id, code_version, config_json, trades, pv)
            VALUES (@Label, @CreatedAt, @Valuation, @SnapshotId, @CodeVersion, @Config, @Trades, @Pv);
            SELECT last_insert_rowid();
            """, new
        {
            Label = label,
            CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Valuation = run.Valuation.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            run.SnapshotId,
            run.CodeVersion,
            Config = JsonSerializer.Serialize(run.Config),
            Trades = run.Trades.Length,
            Pv = run.Trades.Sum(t => t.Result?.Pv ?? 0),
        }, transaction);

        foreach (var trade in run.Trades)
        {
            db.Execute(
                "INSERT INTO run_trades (run_id, position_id, hash, trade_json) VALUES (@Run, @Position, @Hash, @Json)",
                new { Run = id, Position = trade.Position.Id, trade.Hash, Json = JsonSerializer.Serialize(trade) }, transaction);
        }
        transaction.Commit();
        return id;
    }

    public IReadOnlyList<RunInfo> Runs()
    {
        using var db = Open();
        return db.Query<(long Id, string Label, string CreatedAt, string Valuation, long SnapshotId, long Trades, double Pv)>(
                "SELECT id, label, created_at, valuation, snapshot_id, trades, pv FROM runs ORDER BY id DESC")
            .Select(r => new RunInfo(
                r.Id, r.Label, DateTime.Parse(r.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateOnly.Parse(r.Valuation, CultureInfo.InvariantCulture), r.SnapshotId, (int)r.Trades, r.Pv))
            .ToList();
    }

    public RunResult Load(long id)
    {
        using var db = Open();
        var run = db.QuerySingle<(string Valuation, long SnapshotId, string CodeVersion, string Config)>(
            "SELECT valuation, snapshot_id, code_version, config_json FROM runs WHERE id = @id", new { id });
        var trades = db.Query<string>("SELECT trade_json FROM run_trades WHERE run_id = @id ORDER BY position_id", new { id })
            .Select(Deserialize<TradeResult>);
        return new RunResult(
            DateOnly.Parse(run.Valuation, CultureInfo.InvariantCulture), run.SnapshotId, run.CodeVersion,
            Deserialize<RunConfig>(run.Config), [.. trades]);
    }

    public PricingResult? Find(string hash)
    {
        using var db = Open();
        string? json = db.QueryFirstOrDefault<string>("SELECT trade_json FROM run_trades WHERE hash = @hash LIMIT 1", new { hash });
        return json is null ? null : Deserialize<TradeResult>(json).Result;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"Empty {typeof(T).Name}.");
}
