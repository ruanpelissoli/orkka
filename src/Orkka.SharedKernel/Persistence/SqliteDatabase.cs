namespace Orkka.SharedKernel.Persistence;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The one SQLite database every module context shares (<c>{DataDir}/orkka.db</c>): the
/// connection string, the per-connection pragmas and the per-module context registration.
/// </summary>
public static class SqliteDatabase
{
    /// <summary>The database file's name inside the data directory.</summary>
    public const string FileName = "orkka.db";

    /// <summary>How long a connection waits for another writer before SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 10_000;

    /// <summary>The connection string for a database file at <paramref name="path"/>.</summary>
    public static string ConnectionStringFor(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    /// <summary>
    /// <c>ConnectionStrings:DefaultConnection</c>, normalized: shared cache off, pooling on,
    /// foreign keys on and the parent folder created. The host sets it from <c>Local:DataDir</c>.
    /// </summary>
    public static string GetSqliteConnectionString(this IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        var builder = new SqliteConnectionStringBuilder(raw)
        {
            Pooling = true,
            ForeignKeys = true,
            // Microsoft.Data.Sqlite retries a busy statement until this (seconds) elapses, on top
            // of the busy_timeout pragma below.
            DefaultTimeout = 30,
        };

        var dataSource = builder.DataSource;
        if (!string.IsNullOrEmpty(dataSource) && dataSource != ":memory:" && builder.Mode != SqliteOpenMode.Memory)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
        }

        return builder.ToString();
    }

    /// <summary>
    /// SQLite for one module context: its own migrations history table
    /// (<c>__EFMigrationsHistory_{module}</c>), the pragmas on every connection and
    /// <c>DateTimeOffset.UtcNow</c> evaluated as a parameter (see <see cref="ClientClockEvaluatableExpressionFilter"/>).
    /// </summary>
    public static DbContextOptionsBuilder UseOrkkaSqlite<TContext>(
        this DbContextOptionsBuilder options, string connectionString, string module)
        where TContext : DbContext
    {
        options.UseSqlite(connectionString, sqlite => sqlite
            .MigrationsAssembly(typeof(TContext).Assembly.FullName)
            .MigrationsHistoryTable(HistoryTable(module)));
        options.AddInterceptors(SqlitePragmaInterceptor.Instance);
        options.ReplaceService<IEvaluatableExpressionFilter, ClientClockEvaluatableExpressionFilter>();
        return options;
    }

    /// <summary>The migrations history table of a module: every module migrates the same file.</summary>
    public static string HistoryTable(string module) => $"__EFMigrationsHistory_{module}";

    /// <summary>Registers a module's context on the shared database and with the startup migrator.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services, IConfiguration configuration, string module)
        where TContext : ModuleDbContext
    {
        services.AddDbContext<TContext>(options =>
            options.UseOrkkaSqlite<TContext>(configuration.GetSqliteConnectionString(), module));
        services.AddModuleMigration<TContext>();
        return services;
    }

    /// <summary>True when <paramref name="exception"/> is a SQLite constraint violation (unique, FK, check).</summary>
    public static bool IsConstraintViolation(Exception? exception) =>
        exception is SqliteException { SqliteErrorCode: 19 }
        || exception?.InnerException is SqliteException { SqliteErrorCode: 19 };
}

/// <summary>
/// Runs the pragmas every connection needs, each time EF opens one (pooled handles included):
/// WAL so readers never block the writer, a busy timeout so a second writer waits instead of
/// failing, and foreign keys (off by default in SQLite).
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    private static readonly string Pragmas =
        $"PRAGMA journal_mode = WAL; PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMs}; " +
        "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Makes <c>DateTimeOffset.UtcNow</c>/<c>Now</c> inside a query a parameter read from this
/// process's clock at execution time, instead of a server-side function. SQLite has no
/// DateTimeOffset function, and every DateTimeOffset column is stored as UTC ticks
/// (<see cref="UtcTicksConverter"/>), so the value must arrive already converted. One process on
/// one machine: the client clock IS the database's clock.
/// </summary>
#pragma warning disable EF1001 // Internal EF Core API: the documented extension point for this.
public sealed class ClientClockEvaluatableExpressionFilter(
    EvaluatableExpressionFilterDependencies dependencies,
    RelationalEvaluatableExpressionFilterDependencies relationalDependencies)
    : RelationalEvaluatableExpressionFilter(dependencies, relationalDependencies)
{
    public override bool IsEvaluatableExpression(System.Linq.Expressions.Expression expression, Microsoft.EntityFrameworkCore.Metadata.IModel model)
    {
        if (expression is System.Linq.Expressions.MemberExpression { Member.DeclaringType: var type } member
            && type == typeof(DateTimeOffset)
            && member.Member.Name is nameof(DateTimeOffset.UtcNow) or nameof(DateTimeOffset.Now))
            return true;

        return base.IsEvaluatableExpression(expression, model);
    }
}
#pragma warning restore EF1001
