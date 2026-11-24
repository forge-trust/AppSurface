using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed partial class PostgreSqlIntegrationTestDatabase
{
    private Guid? _preparedTemplateStoreId;
    private Guid? _copiedTemplateStoreId;

    /// <summary>Creates and registers a template, clone, or empty case on one dedicated server.</summary>
    /// <remarks>
    /// Caller cancellation before dispatch creates nothing. After dispatch an independent deadline covers capture of
    /// the acknowledged database identity. An ambiguous create is preserved for the disposable server owner.
    /// The optional observer models a withheld create reply. Its argument is the generated database name.
    /// </remarks>
    internal static async ValueTask<PostgreSqlIntegrationTestDatabase> CreateOwnedAsync(
        string connectionString,
        PostgreSqlIntegrationTestDatabase? template = null,
        bool isTemplate = false,
        Func<string, CancellationToken, ValueTask>? afterCreate = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(LifecycleBudget);
        using var beforeDispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        PostgreSqlTestServer server;
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains(',') || (template is not null && isTemplate))
            {
                throw new PostgreSqlFixtureException("Fixture creation requires a single dedicated server and one resource kind.");
            }

            server = await CreateServerAsync(connectionString, container: null, beforeDispatch.Token);
            if (template is not null)
            {
                await template._lifecycleGate.WaitAsync(beforeDispatch.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new PostgreSqlFixtureException("Fixture creation timed out before dispatch. Check the dedicated server and active template operations.");
        }
        catch (Exception error) when (error is not (OperationCanceledException or PostgreSqlFixtureException))
        {
            throw new PostgreSqlFixtureException($"Fixture server verification failed ({PostgreSqlFixtureException.Describe(error)}). Check the dedicated PostgreSQL 16+ connection.");
        }

        if (template is null)
        {
            return await CreateRegisteredAsync(server, null, isTemplate, afterCreate, cancellationToken, deadline.Token);
        }

        try
        {
            if (template._closing || !template._isTemplate || template._preparedTemplateStoreId is null ||
                !new NpgsqlConnectionStringBuilder(template._maintenanceConnectionString)
                    .EquivalentTo(new NpgsqlConnectionStringBuilder(server.MaintenanceConnectionString)))
            {
                throw new PostgreSqlFixtureException("Clone creation requires a prepared, live template registered on the same configured server.");
            }

            var clone = await CreateRegisteredAsync(server, template, false, afterCreate, cancellationToken, deadline.Token);
            clone._copiedTemplateStoreId = template._preparedTemplateStoreId;
            return clone;
        }
        finally
        {
            template._lifecycleGate.Release();
        }
    }
}
