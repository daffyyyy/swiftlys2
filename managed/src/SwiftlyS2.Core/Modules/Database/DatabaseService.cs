using System.Data;
using System.Data.SQLite;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using MySqlConnector;
using SwiftlyS2.Core.Natives;
using SwiftlyS2.Shared.Database;

namespace SwiftlyS2.Core.Database;

internal class DatabaseService( ILogger<DatabaseService> logger ) : IDatabaseService, IDisposable
{
    private const int MaxPoolSize = 5;

    private readonly ConcurrentDictionary<string, Func<IDbConnection>> connectionFactories = new();
    private readonly ConcurrentDictionary<string, List<IDbConnection>> connectionPools = new();
    private readonly ConcurrentDictionary<string, int> roundRobinIndex = new();
    private readonly object poolLock = new();
    private bool disposed;

    static DatabaseService()
    {
    }

    private string ResolveConnectionName( string connectionName )
    {
        return NativeDatabase.ConnectionExists(connectionName) ? connectionName : NativeDatabase.GetDefaultConnectionName();
    }

    private static string BuildPoolKey( string driver, string host, ushort port, string database, string user, string pass, uint timeout )
    {
        return $"{driver}|{host}|{port}|{database}|{user}|{pass}|{timeout}";
    }

    private (string PoolKey, Func<IDbConnection> Factory) GetOrCreateConnectionFactory( string connectionName )
    {
        var resolvedName = ResolveConnectionName(connectionName);

        try
        {
            var driver = NativeDatabase.GetConnectionDriver(resolvedName);
            var host = NativeDatabase.GetConnectionHost(resolvedName);
            var database = NativeDatabase.GetConnectionDatabase(resolvedName);
            var user = NativeDatabase.GetConnectionUser(resolvedName);
            var pass = NativeDatabase.GetConnectionPass(resolvedName);
            var timeout = NativeDatabase.GetConnectionTimeout(resolvedName);
            var port = NativeDatabase.GetConnectionPort(resolvedName);

            var poolKey = BuildPoolKey(driver, host, port, database, user, pass, timeout);

            if (connectionFactories.TryGetValue(poolKey, out var cached))
            {
                return (poolKey, cached);
            }

            var factory = CreateConnectionFactory(driver, host, port, database, user, pass, timeout);
            _ = connectionFactories.TryAdd(poolKey, factory);
            return (poolKey, factory);
        }
        catch (Exception e)
        {
            if (!GlobalExceptionHandler.Handle(ref e))
            {
                throw;
            }

            logger.LogError(e, "Failed to create connection factory for '{ConnectionName}'", resolvedName);
            throw;
        }
    }

    private static Func<IDbConnection> CreateConnectionFactory( string driver, string host, ushort port, string database, string user, string pass, uint timeout )
    {
        return driver switch {
            "sqlite" => CreateSqliteFactory(database),
            "mysql" => CreateMySqlFactory(host, port, database, user, pass, timeout),
            "postgresql" => CreatePostgresFactory(host, port, database, user, pass, timeout),
            _ => throw new NotSupportedException($"Unsupported database driver: {driver}")
        };
    }

    private static Func<IDbConnection> CreateSqliteFactory( string database )
    {
        var connStr = $"Data Source={database}";
        return () => new SQLiteConnection(connStr);
    }

    private static Func<IDbConnection> CreateMySqlFactory( string host, ushort port, string database, string user, string pass, uint timeout )
    {
        var builder = new MySqlConnectionStringBuilder {
            Server = host,
            Port = port > 0 ? port : 3306u,
            Database = database,
            UserID = user,
            Password = pass
        };

        if (timeout > 0)
        {
            builder.ConnectionTimeout = timeout;
        }

        var connStr = builder.ConnectionString;
        return () => new MySqlConnection(connStr);
    }

    private static Func<IDbConnection> CreatePostgresFactory( string host, ushort port, string database, string user, string pass, uint timeout )
    {
        var builder = new NpgsqlConnectionStringBuilder {
            Host = host,
            Port = port > 0 ? port : 5432,
            Database = database,
            Username = user,
            Password = pass
        };

        if (timeout > 0)
        {
            builder.Timeout = (int)timeout;
        }

        var connStr = builder.ConnectionString;
        return () => new NpgsqlConnection(connStr);
    }

    public string GetConnectionString( string connectionName )
    {
        return GetConnectionInfo(connectionName).ToString();
    }

    public DatabaseConnectionInfo GetConnectionInfo( string connectionName )
    {
        var resolvedName = ResolveConnectionName(connectionName);
        return new DatabaseConnectionInfo(
            NativeDatabase.GetConnectionDriver(resolvedName),
            NativeDatabase.GetConnectionHost(resolvedName),
            NativeDatabase.GetConnectionDatabase(resolvedName),
            NativeDatabase.GetConnectionUser(resolvedName),
            NativeDatabase.GetConnectionPass(resolvedName),
            NativeDatabase.GetConnectionTimeout(resolvedName),
            NativeDatabase.GetConnectionPort(resolvedName),
            NativeDatabase.GetConnectionRawUri(resolvedName)
        );
    }

    public IDbConnection GetConnection( string connectionName )
    {
        var (poolKey, factory) = GetOrCreateConnectionFactory(connectionName);
        var pool = connectionPools.GetOrAdd(poolKey, _ => []);

        lock (poolLock)
        {
            for (var i = pool.Count - 1; i >= 0; i--)
            {
                if (!IsConnectionAlive(pool[i]))
                {
                    DisposeConnection(pool[i]);
                    pool.RemoveAt(i);
                }
            }

            if (pool.Count < MaxPoolSize)
            {
                var fresh = factory();
                fresh.Open();
                pool.Add(fresh);
                return fresh;
            }

            var idx = roundRobinIndex.AddOrUpdate(poolKey, 0, ( _, prev ) => (prev + 1) % pool.Count);
            return pool[idx % pool.Count];
        }
    }

    private static bool IsConnectionAlive( IDbConnection conn )
    {
        if (conn.State == ConnectionState.Broken)
        {
            return false;
        }

        if (conn.State == ConnectionState.Closed)
        {
            try
            {
                conn.Open();
                return true;
            }
            catch
            {
                return false;
            }
        }

        return conn.State == ConnectionState.Open;
    }

    private static void DisposeConnection( IDbConnection conn )
    {
        try { conn.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        lock (poolLock)
        {
            foreach (var pool in connectionPools.Values)
            {
                foreach (var conn in pool)
                {
                    DisposeConnection(conn);
                }
                pool.Clear();
            }

            connectionPools.Clear();
        }
    }
}
