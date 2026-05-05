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
    private const int MaxPoolSize = 10;

    private readonly ConcurrentDictionary<string, Func<IDbConnection>> connectionFactories = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<IDbConnection>> connectionPools = new();
    private bool disposed;

    static DatabaseService()
    {
    }

    private string ResolveConnectionName( string connectionName )
    {
        return NativeDatabase.ConnectionExists(connectionName) ? connectionName : NativeDatabase.GetDefaultConnectionName();
    }

    private Func<IDbConnection> GetOrCreateConnectionFactory( string connectionName )
    {
        var resolvedName = ResolveConnectionName(connectionName);

        if (connectionFactories.TryGetValue(resolvedName, out var cached))
        {
            return cached;
        }

        try
        {
            var factory = CreateConnectionFactory(resolvedName);
            _ = connectionFactories.TryAdd(resolvedName, factory);
            return factory;
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

    private static Func<IDbConnection> CreateConnectionFactory( string connectionName )
    {
        var driver = NativeDatabase.GetConnectionDriver(connectionName);
        var host = NativeDatabase.GetConnectionHost(connectionName);
        var database = NativeDatabase.GetConnectionDatabase(connectionName);
        var user = NativeDatabase.GetConnectionUser(connectionName);
        var pass = NativeDatabase.GetConnectionPass(connectionName);
        var timeout = NativeDatabase.GetConnectionTimeout(connectionName);
        var port = NativeDatabase.GetConnectionPort(connectionName);

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
        var resolvedName = ResolveConnectionName(connectionName);
        var pool = connectionPools.GetOrAdd(resolvedName, _ => []);

        while (pool.TryTake(out var conn))
        {
            if (IsConnectionAlive(conn))
            {
                return new PooledConnection(conn, () => ReturnConnection(resolvedName, conn));
            }

            DisposeConnection(conn);
        }

        var factory = GetOrCreateConnectionFactory(resolvedName);
        var fresh = factory();
        fresh.Open();
        return new PooledConnection(fresh, () => ReturnConnection(resolvedName, fresh));
    }

    private void ReturnConnection( string resolvedName, IDbConnection conn )
    {
        if (disposed)
        {
            DisposeConnection(conn);
            return;
        }

        var pool = connectionPools.GetOrAdd(resolvedName, _ => []);

        if (pool.Count >= MaxPoolSize || !IsConnectionAlive(conn))
        {
            DisposeConnection(conn);
            return;
        }

        pool.Add(conn);
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

        foreach (var pool in connectionPools.Values)
        {
            while (pool.TryTake(out var conn))
            {
                DisposeConnection(conn);
            }
        }

        connectionPools.Clear();
    }

    private sealed class PooledConnection( IDbConnection inner, Action returnToPool ) : IDbConnection
    {
        private bool returned;

#pragma warning disable CS8769
        string IDbConnection.ConnectionString {
            get => inner.ConnectionString!;
            set => inner.ConnectionString = value;
        }
#pragma warning restore CS8769

        public int ConnectionTimeout => inner.ConnectionTimeout;
        public string Database => inner.Database;
        public ConnectionState State => inner.State;

        public IDbTransaction BeginTransaction() => inner.BeginTransaction();
        public IDbTransaction BeginTransaction( IsolationLevel il ) => inner.BeginTransaction(il);
        public void ChangeDatabase( string databaseName ) => inner.ChangeDatabase(databaseName);
        public void Close() => inner.Close();
        public IDbCommand CreateCommand() => inner.CreateCommand();
        public void Open() => inner.Open();

        public void Dispose()
        {
            if (returned) return;
            returned = true;
            returnToPool();
        }
    }
}
