using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>Owns one database on a fixed disposable PostgreSQL server. Ownership is bound to its catalog name and OID.</summary>
public sealed class PostgreSqlDatabaseLease : IAsyncDisposable
{
    private readonly string maintenanceConnectionString;
    private readonly PostgreSqlCatalog.ServerIdentity server;
    private readonly PostgreSqlDatabaseLease? parent;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private int children;
    private bool dropped;

    private PostgreSqlDatabaseLease(string connectionString, string name, uint oid, PostgreSqlDatabaseLease? parent)
    {
        Name = name;
        Oid = oid;
        ConnectionString = PostgreSqlCatalog.Target(connectionString, name);
        maintenanceConnectionString = PostgreSqlCatalog.Target(connectionString, "postgres");
        server = PostgreSqlCatalog.ServerEndpoint(connectionString);
        this.parent = parent;
        if (parent != null)
        {
            Interlocked.Increment(ref parent.children);
        }
    }

    /// <summary>The generated database identifier, unique within this fixture run.</summary>
    public string Name { get; }

    /// <summary>The PostgreSQL database OID captured after acknowledged creation.</summary>
    public uint Oid { get; }

    /// <summary>The supplied connection settings targeting this database. This value may contain credentials.</summary>
    public string ConnectionString { get; }

    /// <summary>Whether this lease has children whose drops have not yet been verified.</summary>
    public bool HasRegisteredClones => Volatile.Read(ref children) != 0;

    /// <summary>Creates a fresh database on a single dedicated PostgreSQL server.</summary>
    public static Task<PostgreSqlDatabaseLease> CreateAsync(string connectionString, string databaseNamePrefix,
        CancellationToken cancellationToken = default) => CreateAsync(connectionString, databaseNamePrefix, null, cancellationToken);

    internal static Task<PostgreSqlDatabaseLease> CreateAsync(string connectionString, string databaseNamePrefix,
        Func<string, CancellationToken, ValueTask>? afterCreate, CancellationToken cancellationToken) =>
        CreateNamedAsync(connectionString, NewName(databaseNamePrefix), afterCreate, cancellationToken);

    internal static string NewName(string databaseNamePrefix)
    {
        PostgreSqlFixture.ValidateDatabaseNamePrefix(databaseNamePrefix);
        return $"{databaseNamePrefix[..Math.Min(databaseNamePrefix.Length, 46)]}_{Guid.NewGuid().ToString("N")[..16]}";
    }

    internal static Task<PostgreSqlDatabaseLease> CreateNamedAsync(string connectionString, string name,
        Func<string, CancellationToken, ValueTask>? afterCreate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgreSqlCatalog.ValidateSingleServer(connectionString);
        return CreateCoreAsync(connectionString, name, null, afterCreate, cancellationToken);
    }

    /// <summary>Copies this database into an independent child using FILE_COPY. Dispose children before their parent.</summary>
    public Task<PostgreSqlDatabaseLease> CloneAsync(string databaseNamePrefix, CancellationToken cancellationToken = default) =>
        CloneAsync(databaseNamePrefix, null, cancellationToken);

    internal async Task<PostgreSqlDatabaseLease> CloneAsync(string databaseNamePrefix,
        Func<string, CancellationToken, ValueTask>? afterCreate, CancellationToken cancellationToken)
    {
        var name = NewName(databaseNamePrefix);
        using var deadline = Deadline(cancellationToken);
        await lifecycle.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(dropped, this);
            return await CreateCoreAsync(ConnectionString, name, this, afterCreate, deadline.Token);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private static async Task<PostgreSqlDatabaseLease> CreateCoreAsync(string connectionString, string name,
        PostgreSqlDatabaseLease? parent, Func<string, CancellationToken, ValueTask>? afterCreate, CancellationToken token)
    {
        using var beforeDispatch = Deadline(token);
        await using var admin = new NpgsqlConnection(PostgreSqlCatalog.Target(connectionString, "postgres"));
        await admin.OpenAsync(beforeDispatch.Token);
        PostgreSqlDatabaseLease? owned = null;
        try
        {
            await PostgreSqlCatalog.WithDatabaseCatalogLockAsync(admin, async () =>
            {
                var sql = $"CREATE DATABASE {PostgreSqlCatalog.QuoteIdentifier(name)}";
                if (parent != null)
                {
                    if (!await parent.ExistsAsync(admin, beforeDispatch.Token))
                    {
                        throw new PostgreSqlDatabaseOwnershipException($"Clone source {parent.Name} is absent.");
                    }

                    await PostgreSqlCatalog.TerminateSessionsAsync(admin, parent.Oid, clientsOnly: false, beforeDispatch.Token);
                    sql += $" TEMPLATE {PostgreSqlCatalog.QuoteIdentifier(parent.Name)} STRATEGY FILE_COPY";
                }

                beforeDispatch.Token.ThrowIfCancellationRequested();
                // After dispatch, finish identification even if the requesting test cancels.
                using var capture = new CancellationTokenSource(PostgreSqlCatalog.Timeout);
                await using var create = new NpgsqlCommand(sql, admin);
                try
                {
                    await create.ExecuteNonQueryAsync(capture.Token);
                }
                catch (PostgresException)
                {
                    // The server rejected the command. A pre-existing target must be left alone.
                    throw;
                }
                catch (Exception error)
                {
                    throw UnknownIdentity(name, error);
                }

                try
                {
                    if (afterCreate != null)
                    {
                        await afterCreate(name, capture.Token).AsTask().WaitAsync(capture.Token);
                    }

                    await using var read = new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_database WHERE datname = @name", admin);
                    read.Parameters.AddWithValue("name", name);
                    var oid = (uint?)await read.ExecuteScalarAsync(capture.Token)
                        ?? throw new InvalidOperationException("The created database is absent.");
                    owned = new PostgreSqlDatabaseLease(connectionString, name, oid, parent);
                }
                catch (Exception error)
                {
                    throw UnknownIdentity(name, error);
                }
            }, beforeDispatch.Token);

            token.ThrowIfCancellationRequested();
            return owned!;
        }
        catch (Exception operationError) when (owned != null)
        {
            try
            {
                await owned.DisposeAsync();
            }
            catch (Exception cleanupError) when (!owned.dropped)
            {
                throw new PostgreSqlDatabaseCleanupException(owned, cleanupError, operationError);
            }

            throw;
        }
    }

    private static PostgreSqlDatabaseOwnershipException UnknownIdentity(string name, Exception error) =>
        new($"Database {name} has uncertain identity after CREATE ({error.GetType().Name}). Preserve the target and reconcile it on the disposable server.");

    /// <summary>Checks this lease's name and OID on its fixed server. Returns false only when both are absent.</summary>
    public async Task<bool> ExistsAsync(NpgsqlConnection maintenance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        RequireServer(maintenance);
        var identity = await PostgreSqlCatalog.ReadDatabaseIdentityAsync(maintenance, Name, Oid, null, cancellationToken);
        if (identity.OidAtName == Oid && identity.NameAtOid == Name)
        {
            return true;
        }

        if (identity.OidAtName == null && identity.NameAtOid == null)
        {
            return false;
        }

        throw Mismatch(identity.OidAtName);
    }

    /// <summary>Checks the database serving the supplied transaction before a consumer makes identity-sensitive changes.</summary>
    public async Task VerifyConnectedIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        RequireServer(connection);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        }

        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database() AND datname = @name",
            connection, transaction);
        command.Parameters.AddWithValue("name", Name);
        var actual = (uint?)await command.ExecuteScalarAsync(cancellationToken);
        if (actual != Oid)
        {
            throw Mismatch(actual);
        }
    }

    private void RequireServer(NpgsqlConnection connection)
    {
        if (!server.Matches(PostgreSqlCatalog.ServerEndpoint(connection.ConnectionString)))
        {
            throw new PostgreSqlDatabaseOwnershipException($"Database {Name} ownership mismatch: the connection uses a different server.");
        }
    }

    private PostgreSqlDatabaseOwnershipException Mismatch(uint? observed) =>
        new($"Database {Name} ownership mismatch (expected OID {Oid}, observed {observed?.ToString() ?? "absent"}). Preserve the resource.");

    /// <summary>Drops the database after ownership verification. A failed drop can be retried on this instance.</summary>
    public ValueTask DropAsync(CancellationToken cancellationToken = default) => DropAsync(null, cancellationToken);

    /// <summary>Performs a bounded drop independently of the token used by the test.</summary>
    public ValueTask DisposeAsync() => DropAsync();

    internal async ValueTask DropAsync(Func<NpgsqlConnection, ValueTask>? dropDatabaseAsync, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(cancellationToken);
        var token = deadline.Token;
        await lifecycle.WaitAsync(token);
        try
        {
            if (dropped)
            {
                return;
            }

            if (HasRegisteredClones)
            {
                throw new PostgreSqlDatabaseOwnershipException($"Database {Name} still has registered clones. Release them before template teardown.");
            }

            await using var admin = new NpgsqlConnection(maintenanceConnectionString);
            await admin.OpenAsync(token);
            await PostgreSqlCatalog.WithDatabaseCatalogLockAsync(admin, async () =>
            {
                if (await ExistsAsync(admin, token))
                {
                    if (dropDatabaseAsync == null)
                    {
                        await using var drop = new NpgsqlCommand($"DROP DATABASE {PostgreSqlCatalog.QuoteIdentifier(Name)} WITH (FORCE)", admin);
                        await drop.ExecuteNonQueryAsync(token);
                    }
                    else
                    {
                        await dropDatabaseAsync(admin);
                    }

                    if (await ExistsAsync(admin, token))
                    {
                        throw new PostgreSqlDatabaseOwnershipException($"Owned database {Name} still exists after drop.");
                    }
                }

                dropped = true;
                if (parent != null)
                {
                    Interlocked.Decrement(ref parent.children);
                }
            }, token);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PostgreSqlCatalog.Timeout);
        return deadline;
    }
}
