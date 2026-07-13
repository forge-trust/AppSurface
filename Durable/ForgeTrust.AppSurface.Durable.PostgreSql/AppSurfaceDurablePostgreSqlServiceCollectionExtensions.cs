using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Registers the portable PostgreSQL durable runtime and its separately opt-in worker host.
/// </summary>
public static class AppSurfaceDurablePostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers PostgreSQL durable clients, schema validation, and the bounded runtime pump without starting a
    /// background worker or applying migrations.
    /// </summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="dataSource">Runtime-role PostgreSQL data source.</param>
    /// <param name="runtimeEpoch">
    /// Out-of-band restore epoch. Rotate this non-empty value after a point-in-time restore before releasing work.
    /// </param>
    /// <param name="configure">Optional process execution settings.</param>
    /// <returns>A builder that can explicitly opt into hosted execution.</returns>
    /// <remarks>
    /// The supplied role should have only runtime privileges and must not own the durable schema or have
    /// <c>BYPASSRLS</c>. Apply embedded numbered migrations with a separate migration-role data source through
    /// <see cref="IDurableRuntimeSchemaManager"/> or the AppSurface CLI. Calling this method twice is rejected because
    /// one service provider cannot safely host two authoritative runtime epochs.
    /// </remarks>
    public static AppSurfaceDurablePostgreSqlBuilder AddAppSurfaceDurablePostgreSql(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        Action<AppSurfaceDurablePostgreSqlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableRuntimeRegistration)))
        {
            throw new InvalidOperationException(
                "PostgreSQL durable storage is already registered. One service provider must use exactly one data source and runtime epoch.");
        }

        var options = new AppSurfaceDurablePostgreSqlOptions();
        configure?.Invoke(options);
        var registration = new PostgreSqlDurableRuntimeRegistration(
            dataSource,
            runtimeEpoch,
            Guid.NewGuid(),
            options.SnapshotAndValidate());
        services.AddSingleton(registration);

        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        services.TryAddSingleton<IDurableFlowRegistry, DurableFlowRegistry>();
        services.TryAddSingleton<IDurableRuntimeSchemaManager>(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableRuntimeSchemaManager(configured.DataSource);
        });
        services.TryAddSingleton<IDurableWorkTransactionWriter>(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkTransactionWriter(
                configured.DataSource,
                configured.RuntimeEpoch,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                configured.Options.SendWakeNotifications);
        });
        services.TryAddSingleton<IDurableWorkClient>(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkClient(
                configured.DataSource,
                provider.GetRequiredService<IDurableWorkTransactionWriter>());
        });
        services.TryAddSingleton<IDurableFlowClient>(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            var scheduleProcessor = provider.GetRequiredService<PostgreSqlDurableScheduleProcessor>();
            return new PostgreSqlDurableFlowClient(
                configured.DataSource,
                provider.GetRequiredService<IDurableFlowRegistry>(),
                provider.GetRequiredService<IDurablePayloadCodecRegistry>(),
                configured.RuntimeEpoch,
                configured.Options.SendWakeNotifications,
                async (transaction, scopeId, instanceId, terminalCode, cancellationToken) =>
                {
                    await scheduleProcessor.ReleaseTargetAsync(
                        transaction,
                        scopeId,
                        DurableScheduleTargetKind.Flow,
                        instanceId.Value,
                        terminalCode,
                        cancellationToken).ConfigureAwait(false);
                });
        });
        services.TryAddSingleton<IDurableScheduleClient>(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableScheduleClient(
                configured.DataSource,
                provider.GetRequiredService<IDurablePayloadCodecRegistry>(),
                provider.GetRequiredService<IDurableWorkRegistry>(),
                provider.GetRequiredService<IDurableFlowRegistry>(),
                configured.RuntimeEpoch,
                provider.GetRequiredService<IDurableRuntimeSchemaManager>());
        });
        services.TryAddSingleton(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkStore(configured.DataSource, configured.RuntimeEpoch);
        });
        services.TryAddSingleton(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            var scheduleProcessor = provider.GetRequiredService<PostgreSqlDurableScheduleProcessor>();
            return new PostgreSqlDurableFlowStore(
                configured.DataSource,
                configured.RuntimeEpoch,
                configured.Options.SendWakeNotifications,
                async (transaction, scopeId, instanceId, terminalCode, cancellationToken) =>
                {
                    await scheduleProcessor.ReleaseTargetAsync(
                        transaction,
                        scopeId,
                        DurableScheduleTargetKind.Flow,
                        instanceId.Value,
                        terminalCode,
                        cancellationToken).ConfigureAwait(false);
                });
        });
        services.TryAddSingleton(static provider =>
        {
            var configured = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableScheduleProcessor(
                configured.DataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                provider.GetRequiredService<IDurableFlowRegistry>(),
                configured.RuntimeEpoch,
                configured.Options.SendWakeNotifications);
        });
        services.TryAddSingleton<PostgreSqlDurableControlClient>();
        services.TryAddSingleton<IDurableWorkControlClient>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableControlClient>());
        services.TryAddSingleton<IDurableScopeControlClient>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableControlClient>());
        services.TryAddSingleton<IDurableWorkOperatorClient, PostgreSqlDurableWorkOperatorClient>();
        services.TryAddSingleton<PostgreSqlDurableRuntimeHealth>();
        services.TryAddSingleton<IDurableRuntimeHealth>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>());
        services.TryAddSingleton<IDurableRuntimeDrainControl>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>());
        services.TryAddSingleton<IDurableRuntimePump, PostgreSqlDurableRuntimePump>();
        return new AppSurfaceDurablePostgreSqlBuilder(services);
    }

    /// <summary>
    /// Adds the critical continuous worker loop after PostgreSQL durable storage has been registered.
    /// </summary>
    /// <param name="services">Application service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// This method never applies schema migrations. Startup validates compatibility and fails closed. The hosted loop
    /// is deliberately separate from <see cref="AddAppSurfaceDurablePostgreSql"/> so query-only hosts, migration tools,
    /// tests, and externally activated deployments do not accidentally execute work.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDurableWorkerHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableRuntimeRegistration)))
        {
            throw new InvalidOperationException(
                "Register PostgreSQL durable storage with AddAppSurfaceDurablePostgreSql before adding the worker host.");
        }

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableHostedServiceMarker)))
        {
            return services;
        }

        services.AddSingleton<PostgreSqlDurableHostedServiceMarker>();
        services.AddSingleton<PostgreSqlDurableHostedService>();
        services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableHostedService>());
        return services;
    }
}

internal sealed record PostgreSqlDurableRuntimeRegistration(
    NpgsqlDataSource DataSource,
    Guid RuntimeEpoch,
    Guid InstanceId,
    AppSurfaceDurablePostgreSqlOptions Options);

internal sealed class PostgreSqlDurableHostedServiceMarker;
