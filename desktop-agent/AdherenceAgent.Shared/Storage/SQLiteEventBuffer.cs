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
    private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
    private bool _isInitialized = false;
    private long _lastCapacityEnforceUtcTicks = 0;

    public SQLiteEventBuffer(AgentConfig config, ILogger<SQLiteEventBuffer> logger)
    {
        _config = config;
        _logger = logger;
        try
        {
            PathProvider.EnsureDirectories();

            var dbPath = PathProvider.DatabaseFile;
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                dbPath = Path.Combine(PathProvider.BaseDirectory, "events.db");
            }

            // Defensive: ensure rooted/absolute path to avoid SQLite internal Path.Combine issues.
            if (!Path.IsPathRooted(dbPath))
            {
                dbPath = Path.GetFullPath(dbPath, AppContext.BaseDirectory);
            }

            // Ensure absolute path (SQLite requires this when running as service)
            dbPath = Path.GetFullPath(dbPath);
            _logger.LogInformation("SQLite buffer path: {DbPath}", dbPath);
            
            // Use SQLiteConnectionStringBuilder to avoid parsing issues
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = true,
                // Help multi-process (service + tray) writes:
                // - WAL allows concurrent readers and one writer
                // - BusyTimeout retries "database is locked" for a bit instead of failing immediately
                JournalMode = SQLiteJournalModeEnum.Wal,
                BusyTimeout = 5000
            };
            _connectionString = builder.ConnectionString;
        }
        catch (Exception ex)
        {
            // Last-resort fallback: keep service alive and write buffer into ProgramData.
            var fallback = Path.GetFullPath(Path.Combine(@"C:\ProgramData", "AdherenceAgent", "events.db"));
            _logger.LogError(ex, "Failed to initialize SQLite buffer path. Falling back to {DbPath}", fallback);
            var fallbackBuilder = new SQLiteConnectionStringBuilder
            {
                DataSource = fallback,
                Pooling = true
            };
            _connectionString = fallbackBuilder.ConnectionString;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return; // Already initialized
            }

            try
            {
                PathProvider.EnsureDirectories();
                Directory.CreateDirectory(Path.GetDirectoryName(PathProvider.DatabaseFile)!);

                _logger.LogInformation("Initializing SQLite database at: {DbPath}", PathProvider.DatabaseFile);
                _logger.LogInformation("Connection string: {ConnString}", _connectionString);

                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                _logger.LogInformation("SQLite connection opened successfully");

                // Ensure WAL mode + busy timeout at runtime too (defense in depth)
                using (var pragma = new SQLiteCommand("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;", connection))
                {
                    await pragma.ExecuteNonQueryAsync(cancellationToken);
                }

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
            
            _isInitialized = true;
            _logger.LogInformation("SQLite database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database. ConnectionString={Conn}, DbPath={DbPath}", 
                    _connectionString, PathProvider.DatabaseFile);
                throw; // Re-throw so service knows initialization failed
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task AddAsync(AdherenceEvent adherenceEvent, CancellationToken cancellationToken)
    {
        // Ensure database is initialized before adding events
        if (!_isInitialized)
        {
            await _initializationLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isInitialized)
                {
                    await InitializeAsync(cancellationToken);
                }
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        // Enforce capacity at most once per minute to reduce write contention between Service and Tray.
        try
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastCapacityEnforceUtcTicks);
            if (last == 0 || (new DateTime(nowTicks) - new DateTime(last)).TotalSeconds >= 60)
            {
                Interlocked.Exchange(ref _lastCapacityEnforceUtcTicks, nowTicks);
                await EnforceCapacityAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - capacity enforcement is best-effort
            _logger.LogWarning(ex, "EnforceCapacity failed, continuing with AddAsync");
        }

        // Retry transient "database is locked/busy" errors (service + tray can contend).
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                using (var pragma = new SQLiteCommand("PRAGMA busy_timeout=5000;", connection))
                {
                    await pragma.ExecuteNonQueryAsync(cancellationToken);
                }

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
                return;
            }
            catch (SQLiteException ex) when (IsTransientLock(ex) && attempt < maxAttempts)
            {
                // Backoff a bit and retry
                var delayMs = 50 * attempt * attempt; // 50, 200, 450, 800...
                _logger.LogDebug(ex, "SQLite locked/busy during AddAsync (attempt {Attempt}/{Max}), retrying in {Delay}ms", attempt, maxAttempts, delayMs);
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add event to buffer. EventType={Type}, ConnectionString={Conn}",
                    adherenceEvent.EventType, _connectionString);
                throw; // Re-throw so caller knows event wasn't stored
            }
        }
    }

    private static bool IsTransientLock(SQLiteException ex)
    {
        // System.Data.SQLite exposes ResultCode; fallback to message matching
        try
        {
            return ex.ResultCode == SQLiteErrorCode.Busy ||
                   ex.ResultCode == SQLiteErrorCode.Locked ||
                   ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("database is busy", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("database is busy", StringComparison.OrdinalIgnoreCase);
        }
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
        // Don't enforce capacity if database isn't initialized yet
        if (!_isInitialized)
        {
            return;
        }

        try
        {
            using var connection = new SQLiteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Check if table exists first (defensive check)
            var tableCheckCmd = connection.CreateCommand();
            tableCheckCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='event_buffer';";
            var tableExists = await tableCheckCmd.ExecuteScalarAsync(cancellationToken) != null;
            
            if (!tableExists)
            {
                _logger.LogWarning("event_buffer table does not exist yet, skipping capacity enforcement");
                return;
            }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite EnforceCapacity failed. ConnectionString={Conn}; CurrentDir={Cwd}; BaseDir={BaseDir}",
                _connectionString, Environment.CurrentDirectory, AppContext.BaseDirectory);
            // Don't crash capture pipeline because of capacity enforcement failure.
        }
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

