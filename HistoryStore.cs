using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProcLens;

internal sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly JsonSerializerOptions _jsonOptions;
    private DateTime _nextCleanupUtc = DateTime.MinValue;
    private readonly int _retentionDays;

    public string DatabasePath { get; }

    public HistoryStore(string dataDirectory, JsonSerializerOptions jsonOptions, int retentionDays)
    {
        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(dataDirectory, "proclens.db");
        _jsonOptions = jsonOptions;
        _retentionDays = retentionDays;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        _connection.Open();
        Initialize();
        ImportLegacyHistory(dataDirectory);
    }

    public void Write<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, _jsonOptions));
        var root = document.RootElement;
        var type = Text(root, "type");
        var time = Date(root, "timeUtc", DateTime.UtcNow);
        var runId = Text(root, "runId");

        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO records(time_utc, type, run_id, payload) VALUES($time, $type, $run, $payload);";
        command.Parameters.AddWithValue("$time", time.Ticks);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$payload", root.GetRawText());
        command.ExecuteNonQuery();

        if (DateTime.UtcNow >= _nextCleanupUtc)
        {
            Cleanup();
            _nextCleanupUtc = DateTime.UtcNow.AddHours(6);
        }
    }

    public void Flush()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        command.ExecuteNonQuery();
    }

    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays).Ticks;
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM records WHERE time_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.ExecuteNonQuery();
    }

    public static IEnumerable<string> ReadSince(string dataDirectory, DateTime cutoffUtc)
    {
        var path = Path.Combine(dataDirectory, "proclens.db");
        if (!File.Exists(path)) yield break;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM records WHERE time_utc >= $cutoff ORDER BY time_utc;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.Ticks);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    public static IEnumerable<string> ReadDashboardSince(string dataDirectory, DateTime cutoffUtc, int maximumSampleTimes = 480)
    {
        var path = Path.Combine(dataDirectory, "proclens.db");
        if (!File.Exists(path)) yield break;
        var bucketTicks = Math.Max(TimeSpan.TicksPerSecond,
            (DateTime.UtcNow.Ticks - cutoffUtc.Ticks) / Math.Max(1, maximumSampleTimes));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH sample_times AS (
                SELECT MAX(time_utc) AS time_utc
                FROM records
                WHERE type = 'system_sample' AND time_utc >= $cutoff
                GROUP BY ((time_utc - $cutoff) / $bucket)
            )
            SELECT payload
            FROM records
            WHERE time_utc >= $cutoff
              AND (
                type NOT IN ('system_sample', 'process_sample')
                OR time_utc IN (SELECT time_utc FROM sample_times)
              )
            ORDER BY time_utc;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.Ticks);
        command.Parameters.AddWithValue("$bucket", bucketTicks);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=3000;
            CREATE TABLE IF NOT EXISTS records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time_utc INTEGER NOT NULL,
                type TEXT NOT NULL,
                run_id TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_records_time ON records(time_utc);
            CREATE INDEX IF NOT EXISTS ix_records_type_time ON records(type, time_utc);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private void ImportLegacyHistory(string dataDirectory)
    {
        if (!Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(AppSettings.DataDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return;

        using (var count = _connection.CreateCommand())
        {
            count.CommandText = "SELECT EXISTS(SELECT 1 FROM records LIMIT 1);";
            if (Convert.ToInt32(count.ExecuteScalar()) != 0) return;
        }

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcWatch", "data");
        if (!Directory.Exists(legacy)) return;
        var files = Directory.EnumerateFiles(legacy, "procwatch-*.jsonl").OrderBy(x => x).ToList();
        if (files.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO records(time_utc, type, run_id, payload) VALUES($time, $type, $run, $payload);";
        var timeParameter = insert.Parameters.Add("$time", SqliteType.Integer);
        var typeParameter = insert.Parameters.Add("$type", SqliteType.Text);
        var runParameter = insert.Parameters.Add("$run", SqliteType.Text);
        var payloadParameter = insert.Parameters.Add("$payload", SqliteType.Text);

        foreach (var file in files)
        foreach (var line in File.ReadLines(file))
        {
            try
            {
                var node = JsonNode.Parse(line)?.AsObject();
                if (node is null) continue;
                node.Remove("machine");
                node.Remove("user");
                node.Remove("path");
                node.Remove("command");
                if (node.TryGetPropertyValue("orphan", out var orphan))
                {
                    node["unresolvedOwner"] = orphan?.DeepClone();
                    node.Remove("orphan");
                }
                node["schemaVersion"] = AppSettings.CurrentSchemaVersion;
                var time = node["timeUtc"]?.GetValue<DateTime>() ?? DateTime.UtcNow;
                timeParameter.Value = time.Ticks;
                typeParameter.Value = node["type"]?.GetValue<string>() ?? "unknown";
                runParameter.Value = node["runId"]?.GetValue<string>() ?? "legacy";
                payloadParameter.Value = node.ToJsonString(_jsonOptions);
                insert.ExecuteNonQuery();
            }
            catch { /* Skip partial or incompatible legacy records. */ }
        }
        transaction.Commit();
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) ? node.GetString() ?? "" : "";

    private static DateTime Date(JsonElement root, string name, DateTime fallback) =>
        root.TryGetProperty(name, out var node) && node.TryGetDateTime(out var value) ? value : fallback;

    public void Dispose() => _connection.Dispose();
}
