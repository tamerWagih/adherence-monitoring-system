using System.Data.SQLite;
using AdherenceAgent.Shared.Configuration;
using AdherenceAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AdherenceAgent.Shared.Storage;

public interface IEventBuffer
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task AddAsync(AdherenceEvent adherenceEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdherenceEvent>> GetPendingAsync(int batchSize, int maxRetryAttempts, CancellationToken cancellationToken);
    Task MarkSentAsync(IEnumerable<long> ids, CancellationToken cancellationToken);
    Task MarkFailedAsync(IEnumerable<long> ids, string error, CancellationToken cancellationToken);
    Task CleanupAsync(int keepSentDays, CancellationToken cancellationToken);
    Task<int> CountPendingAsync(CancellationToken cancellationToken);
}

public class SQLiteEventBuffer : IEventBuffer
{
    private readonly AgentConfig _config;
    private readonly ILogger<SQLiteEventBuffer> _logger;
    private readonly string _connectionString;

    public SQLiteEventBuffer(AgentConfig config, ILogger<SQLiteEventBuffer> logger)
    {
        _config = config;
        _logger = logger;
        PathProvider.EnsureDirectories();
        var dbPath = PathProvider.DatabaseFile;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = Path.Combine(PathProvider.BaseDirectory, "events.db");
        }
        _connectionString = $"Data Source={dbPath};Pooling=true";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        PathProvider.EnsureDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(PathProvider.DatabaseFile)!);

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = """
            CREATE TABLE IF NOT EXISTS event_buffer (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                event_timestamp TEXT NOT NULL,
                application_name TEXT,
                application_path TEXT,
                window_title TEXT,
                is_work_application INTEGER,
                metadata TEXT,
                status TEXT DEFAULT 'PENDING',
                retry_count INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                sent_at TEXT,
                error_message TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_buffer_pending ON event_buffer(status, created_at);
            """;

        using var command = new SQLiteCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAsync(AdherenceEvent adherenceEvent, CancellationToken cancellationToken)
    {
        await EnforceCapacityAsync(cancellationToken);

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_buffer
            (event_type, event_timestamp, application_name, application_path, window_title, is_work_application, metadata, status)
            VALUES (@type, @timestamp, @appName, @appPath, @windowTitle, @isWork, @metadata, 'PENDING');
            """;
        cmd.Parameters.AddWithValue("@type", adherenceEvent.EventType);
        cmd.Parameters.AddWithValue("@timestamp", adherenceEvent.EventTimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@appName", (object?)adherenceEvent.ApplicationName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appPath", (object?)adherenceEvent.ApplicationPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@windowTitle", (object?)adherenceEvent.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isWork", adherenceEvent.IsWorkApplication.HasValue ? (object)(adherenceEvent.IsWorkApplication.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", adherenceEvent.Metadata is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(adherenceEvent.Metadata));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdherenceEvent>> GetPendingAsync(int batchSize, int maxRetryAttempts, CancellationToken cancellationToken)
    {
        var results = new List<AdherenceEvent>();

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, event_timestamp, application_name, application_path, window_title, is_work_application, metadata
            FROM event_buffer
            WHERE status IN ('PENDING','FAILED')
              AND retry_count < @maxRetry
            ORDER BY created_at DESC
            LIMIT @batchSize;
            """;
        cmd.Parameters.AddWithValue("@batchSize", batchSize);
        cmd.Parameters.AddWithValue("@maxRetry", maxRetryAttempts);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AdherenceEvent
            {
                Id = reader.GetInt64(0),
                EventType = reader.GetString(1),
                EventTimestampUtc = DateTime.Parse(reader.GetString(2)),
                ApplicationName = reader.IsDBNull(3) ? null : reader.GetString(3),
                ApplicationPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                WindowTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsWorkApplication = reader.IsDBNull(6) ? null : reader.GetInt32(6) == 1,
                Metadata = reader.IsDBNull(7)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(7))
            });
        }

        return results;
    }

    private async Task EnforceCapacityAsync(CancellationToken cancellationToken)
    {
        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Count eligible rows (pending or failed)
        var countCmd = connection.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*) FROM event_buffer WHERE status IN ('PENDING','FAILED');
            """;
        var countObj = await countCmd.ExecuteScalarAsync(cancellationToken);
        var count = Convert.ToInt32(countObj);

        if (count < _config.MaxBufferSize)
        {
            return;
        }

        // Delete oldest rows to make room (keep a small headroom of 10 rows)
        var rowsToDelete = Math.Max(1, count - _config.MaxBufferSize + 10);
        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = $"""
            DELETE FROM event_buffer
            WHERE id IN (
                SELECT id FROM event_buffer
                WHERE status IN ('PENDING','FAILED')
                ORDER BY created_at ASC
                LIMIT {rowsToDelete}
            );
            """;
        var deleted = await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogWarning("Buffer at capacity ({Count}/{Max}); deleted {Deleted} oldest events.", count, _config.MaxBufferSize, deleted);
    }

    public async Task MarkSentAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        if (!ids.Any()) return;

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE event_buffer SET status = 'SENT', sent_at = CURRENT_TIMESTAMP WHERE id IN ({string.Join(",", ids)});";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(IEnumerable<long> ids, string error, CancellationToken cancellationToken)
    {
        if (!ids.Any()) return;

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var id in ids)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE event_buffer
                SET status = 'FAILED',
                    retry_count = retry_count + 1,
                    error_message = @error
                WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@error", error);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task CleanupAsync(int keepSentDays, CancellationToken cancellationToken)
    {
        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM event_buffer
            WHERE status = 'SENT'
              AND datetime(sent_at) < datetime('now', '-' || @days || ' days');
            """;
        cmd.Parameters.AddWithValue("@days", keepSentDays);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken)
    {
        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_buffer WHERE status = 'PENDING';";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }
}

