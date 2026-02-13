using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Screener.Core.Persistence;

/// <summary>
/// SQLite database context for application persistence.
/// </summary>
public sealed class DatabaseContext : IAsyncDisposable
{
    private readonly ILogger<DatabaseContext> _logger;
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public DatabaseContext(ILogger<DatabaseContext> logger, string? databasePath = null)
    {
        _logger = logger;

        var dbPath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Screener",
            "screener.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connectionString = $"Data Source={dbPath}";
    }

    /// <summary>
    /// Initialize the database and create tables if they don't exist.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(ct);

        await CreateTablesAsync(ct);

        _logger.LogInformation("Database initialized at {ConnectionString}", _connectionString);
    }

    private async Task CreateTablesAsync(CancellationToken ct)
    {
        const string createTables = """
            -- Scheduled Recordings
            CREATE TABLE IF NOT EXISTS schedules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                start_time TEXT NOT NULL,
                duration_ticks INTEGER NOT NULL,
                preset TEXT NOT NULL,
                filename_template TEXT,
                recurrence_json TEXT,
                auto_upload INTEGER NOT NULL DEFAULT 0,
                upload_provider_id TEXT,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                last_run_at TEXT
            );

            -- Upload Queue
            CREATE TABLE IF NOT EXISTS upload_queue (
                id TEXT PRIMARY KEY,
                local_file_path TEXT NOT NULL,
                remote_path TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                status TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 0,
                bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                retry_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                metadata_json TEXT
            );

            -- Recordings History
            CREATE TABLE IF NOT EXISTS recordings (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT,
                duration_ticks INTEGER,
                preset TEXT NOT NULL,
                file_size INTEGER,
                schedule_id TEXT,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (schedule_id) REFERENCES schedules(id)
            );

            -- Clips
            CREATE TABLE IF NOT EXISTS clips (
                id TEXT PRIMARY KEY,
                recording_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                in_point_ticks INTEGER NOT NULL,
                out_point_ticks INTEGER NOT NULL,
                duration_ticks INTEGER NOT NULL,
                name TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY (recording_id) REFERENCES recordings(id)
            );

            -- Markers
            CREATE TABLE IF NOT EXISTS markers (
                id TEXT PRIMARY KEY,
                recording_id TEXT NOT NULL,
                timecode TEXT NOT NULL,
                position_ticks INTEGER NOT NULL,
                label TEXT,
                color TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY (recording_id) REFERENCES recordings(id)
            );

            -- Application Settings
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Golf: Golfer Profiles
            CREATE TABLE IF NOT EXISTS golfers (
                id TEXT PRIMARY KEY,
                first_name TEXT NOT NULL,
                last_name TEXT NOT NULL,
                display_name TEXT,
                handicap REAL,
                photo_path TEXT,
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Golf: Sessions
            CREATE TABLE IF NOT EXISTS golf_sessions (
                id TEXT PRIMARY KEY,
                golfer_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                source1_recording_path TEXT,
                source2_recording_path TEXT,
                total_swings INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                FOREIGN KEY (golfer_id) REFERENCES golfers(id)
            );

            -- Golf: Swing Sequences
            CREATE TABLE IF NOT EXISTS swing_sequences (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                golfer_id TEXT NOT NULL,
                sequence_number INTEGER NOT NULL,
                in_point_ticks INTEGER NOT NULL,
                out_point_ticks INTEGER,
                detection_method TEXT NOT NULL,
                exported_clip_path TEXT,
                export_status TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT NOT NULL,
                FOREIGN KEY (session_id) REFERENCES golf_sessions(id),
                FOREIGN KEY (golfer_id) REFERENCES golfers(id)
            );

            -- Golf: Overlay Configurations
            CREATE TABLE IF NOT EXISTS overlay_configs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT NOT NULL,
                is_default INTEGER NOT NULL DEFAULT 0,
                config_json TEXT NOT NULL,
                asset_path TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- Create indexes
            CREATE INDEX IF NOT EXISTS idx_schedules_start_time ON schedules(start_time);
            CREATE INDEX IF NOT EXISTS idx_schedules_enabled ON schedules(is_enabled);
            CREATE INDEX IF NOT EXISTS idx_upload_queue_status ON upload_queue(status);
            CREATE INDEX IF NOT EXISTS idx_recordings_start_time ON recordings(start_time);
            CREATE INDEX IF NOT EXISTS idx_clips_recording ON clips(recording_id);
            CREATE INDEX IF NOT EXISTS idx_markers_recording ON markers(recording_id);
            CREATE INDEX IF NOT EXISTS idx_golf_sessions_golfer ON golf_sessions(golfer_id);
            CREATE INDEX IF NOT EXISTS idx_swing_sequences_session ON swing_sequences(session_id);
            """;

        await _connection!.ExecuteAsync(createTables);
    }

    /// <summary>
    /// Get a database connection.
    /// </summary>
    public IDbConnection GetConnection()
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");

        return _connection;
    }

    /// <summary>
    /// Execute a query and return results.
    /// </summary>
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        return await GetConnection().QueryAsync<T>(sql, param);
    }

    /// <summary>
    /// Execute a query and return a single result.
    /// </summary>
    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        return await GetConnection().QuerySingleOrDefaultAsync<T>(sql, param);
    }

    /// <summary>
    /// Execute a command.
    /// </summary>
    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        return await GetConnection().ExecuteAsync(sql, param);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
