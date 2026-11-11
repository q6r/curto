using Curto.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Curto.Api.Storage;

/// <summary>
/// Owns the SQLite connection settings. Connections are cheap (the driver pools them), so every operation opens its
/// own; the database runs in WAL mode, which lets readers proceed while the single writer commits.
/// </summary>
internal sealed partial class Database
{
    private readonly string _connectionString;
    private readonly ILogger<Database> _logger;

    /// <summary>Creates the database accessor.</summary>
    /// <param name="options">Source of the database path.</param>
    /// <param name="environment">Resolves relative paths against the content root.</param>
    /// <param name="logger">Diagnostics.</param>
    public Database(IOptions<CurtoOptions> options, IHostEnvironment environment, ILogger<Database> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _logger = logger;
        FilePath = Path.GetFullPath(options.Value.DbPath, environment.ContentRootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>Absolute path of the database file.</summary>
    public string FilePath { get; }

    /// <summary>Opens a connection with the per-connection pragmas applied. The caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragmas = connection.CreateCommand("PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates the file and directory if needed, switches to WAL and applies pending migrations.</summary>
    /// <param name="cancellationToken">Cancels the migration.</param>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using (var wal = connection.CreateCommand("PRAGMA journal_mode = WAL;"))
        {
            await wal.ExecuteNonQueryAsync(cancellationToken);
        }

        long version;
        await using (var read = connection.CreateCommand("PRAGMA user_version;"))
        {
            version = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        for (var next = (int)version; next < Schema.Migrations.Count; next++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var script = connection.CreateCommand(Schema.Migrations[next], transaction))
            {
                await script.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            LogMigrated(_logger, next + 1);
        }
    }

    /// <summary>Cheap readiness probe: can we open the file and run a query?</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand("SELECT COUNT(*) FROM links;");
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (SqliteException exception)
        {
            LogPingFailed(_logger, exception);
            return false;
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Database schema is at version {Version}")]
    private static partial void LogMigrated(ILogger logger, int version);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Database readiness probe failed")]
    private static partial void LogPingFailed(ILogger logger, Exception exception);
}
