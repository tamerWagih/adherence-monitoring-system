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
                nt_account TEXT NOT NULL,
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

        // Migration: Add nt_account column if it doesn't exist (for existing databases)
        // SQLite doesn't support IF NOT EXISTS for ALTER TABLE ADD COLUMN directly
        // So we check using PRAGMA table_info
        var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA table_info(event_buffer);";
        using var pragmaReader = await pragmaCmd.ExecuteReaderAsync(cancellationToken);
        bool hasNtAccount = false;
        while (await pragmaReader.ReadAsync(cancellationToken))
        {
            var columnName = pragmaReader.GetString(1);
            if (columnName == "nt_account")
            {
                hasNtAccount = true;
                break;
            }
        }
        pragmaReader.Close();

        if (!hasNtAccount)
        {
            var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE event_buffer ADD COLUMN nt_account TEXT NOT NULL DEFAULT '';";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Added nt_account column to event_buffer table (migration)");
        }
    }

    public async Task AddAsync(AdherenceEvent adherenceEvent, CancellationToken cancellationToken)
    {
        await EnforceCapacityAsync(cancellationToken);

        using var connection = new SQLiteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_buffer
            (event_type, event_timestamp, nt_account, application_name, application_path, window_title, is_work_application, metadata, status)
            VALUES (@type, @timestamp, @ntAccount, @appName, @appPath, @windowTitle, @isWork, @metadata, 'PENDING');
            """;
        cmd.Parameters.AddWithValue("@type", adherenceEvent.EventType);
        cmd.Parameters.AddWithValue("@timestamp", adherenceEvent.EventTimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@ntAccount", adherenceEvent.NtAccount ?? string.Empty);
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

        // Use a transaction to atomically select and mark events to prevent duplicates
        using var transaction = connection.BeginTransaction();
        try
        {
            // First, reset any stuck PROCESSING events (older than 5 minutes) back to PENDING
            // This handles cases where the service crashed or restarted while events were being uploaded
            var resetCmd = connection.CreateCommand();
            resetCmd.Transaction = transaction;
            resetCmd.CommandText = """
                UPDATE event_buffer
                SET status = 'PENDING'
                WHERE status = 'PROCESSING'
                  AND datetime(created_at) < datetime('now', '-5 minutes');
                """;
            await resetCmd.ExecuteNonQueryAsync(cancellationToken);

            // Now select the events we want to process
            var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = """
                SELECT id, event_type, event_timestamp, nt_account, application_name, application_path, window_title, is_work_application, metadata
                FROM event_buffer
                WHERE status IN ('PENDING','FAILED')
                  AND retry_count < @maxRetry
                ORDER BY created_at DESC
                LIMIT @batchSize;
                """;
            selectCmd.Parameters.AddWithValue("@batchSize", batchSize);
            selectCmd.Parameters.AddWithValue("@maxRetry", maxRetryAttempts);

            var eventIds = new List<long>();
            using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt64(0);
                    eventIds.Add(id);
                    results.Add(new AdherenceEvent
                    {
                        Id = id,
                        EventType = reader.GetString(1),
                        EventTimestampUtc = DateTime.Parse(reader.GetString(2)),
                        NtAccount = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        ApplicationName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ApplicationPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                        WindowTitle = reader.IsDBNull(6) ? null : reader.GetString(6),
                        IsWorkApplication = reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
                        Metadata = reader.IsDBNull(8)
                            ? null
                            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(8))
                    });
                }
            }

            // Mark selected events as 'PROCESSING' to prevent them from being selected again
            // They will be marked as 'SENT' or 'FAILED' after upload completes
            if (eventIds.Count > 0)
            {
                var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = $"UPDATE event_buffer SET status = 'PROCESSING' WHERE id IN ({string.Join(",", eventIds)});";
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
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
            SELECT COUNT(*) FROM event_buffer WHERE status IN ('PENDING','FAILED','PROCESSING');
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
                WHERE status IN ('PENDING','FAILED','PROCESSING')
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

