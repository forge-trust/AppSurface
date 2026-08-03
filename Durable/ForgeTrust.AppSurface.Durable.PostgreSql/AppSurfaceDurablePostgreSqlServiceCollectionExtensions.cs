using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Registers the PostgreSQL durable runtime kernel and its separately opt-in host adapter.</summary>
public static class AppSurfaceDurablePostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers PostgreSQL durable clients, schema validation, health, drain, and the bounded pump without starting
    /// a background worker or applying migrations.
    /// </summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="dispatcherDataSource">Payload-free dispatcher-role data source used for global discovery only.</param>
    /// <param name="runtimeDataSource">Scoped runtime-role data source used for durable mutations and heartbeats.</param>
    /// <param name="workOptions">Validated active epoch and StoreId. The <paramref name="configure"/> callback selects the metadata-only wake-hint policy.</param>
    /// <param name="scheduleOptions">Validated exact runtime role and Schedule clock/lease safety settings.</param>
    /// <param name="configure">Optional process-local activation settings.</param>
    /// <returns>A builder that can explicitly add continuous host activation.</returns>
    /// <remarks>
    /// The supplied roles must remain distinct, non-owner, and free of <c>BYPASSRLS</c>; see the
    /// <see href="https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql">PostgreSQL role recipe</see>.
    /// This method performs no network I/O or DDL. Apply migrations with a separate migration-owner data source
    /// through <see cref="IDurableRuntimeSchemaManager"/> before a worker is started.
    /// </remarks>
    public static AppSurfaceDurablePostgreSqlBuilder AddAppSurfaceDurablePostgreSql(
        this IServiceCollection services,
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        PostgreSqlDurableWorkOptions workOptions,
        PostgreSqlDurableScheduleOptions scheduleOptions,
        Action<AppSurfaceDurablePostgreSqlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dispatcherDataSource);
        ArgumentNullException.ThrowIfNull(runtimeDataSource);
        ArgumentNullException.ThrowIfNull(workOptions);
        ArgumentNullException.ThrowIfNull(scheduleOptions);
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableRuntimeRegistration)))
        {
            throw new InvalidOperationException(
                "PostgreSQL durable storage is already registered. A service provider has exactly one durable runtime configuration.");
        }

        var configuredOptions = new AppSurfaceDurablePostgreSqlOptions();
        configure?.Invoke(configuredOptions);
        configuredOptions = configuredOptions.SnapshotAndValidate();
        var effectiveWorkOptions = new PostgreSqlDurableWorkOptions(
            workOptions.RuntimeEpoch,
            workOptions.ExpectedStoreId,
            configuredOptions.SendWakeNotifications
                ? PostgreSqlDurableWakeNotificationMode.Enabled
                : PostgreSqlDurableWakeNotificationMode.Disabled);
        var registration = new PostgreSqlDurableRuntimeRegistration(
            dispatcherDataSource,
            runtimeDataSource,
            effectiveWorkOptions,
            scheduleOptions,
            configuredOptions,
            Guid.NewGuid());
        services.AddSingleton(registration);

        services.TryAddSingleton<IDurablePayloadCodecRegistry, DurablePayloadCodecRegistry>();
        services.TryAddSingleton<IDurableWorkRegistry, DurableWorkRegistry>();
        services.TryAddSingleton<IDurableFlowRegistry, DurableFlowRegistry>();
        services.TryAddSingleton<IDurableRuntimeSchemaManager>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableRuntimeSchemaManager(runtime.RuntimeDataSource);
        });
        services.TryAddSingleton<IDurableWorkTransactionWriter>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkTransactionWriter(
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                runtime.WorkOptions);
        });
        services.TryAddSingleton<IDurableWorkClient>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkClient(
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                runtime.WorkOptions);
        });
        services.TryAddSingleton<IDurableFlowClient>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableFlowClient(
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableFlowRegistry>(),
                provider.GetRequiredService<IDurablePayloadCodecRegistry>(),
                runtime.WorkOptions);
        });
        services.TryAddSingleton<IDurableScheduleClient>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableScheduleClient(
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                runtime.WorkOptions,
                runtime.ScheduleOptions);
        });
        services.TryAddSingleton(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkStore(runtime.RuntimeDataSource, runtime.WorkOptions.RuntimeEpoch);
        });
        services.TryAddSingleton(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableFlowProcessor(
                runtime.DispatcherDataSource,
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableFlowRegistry>(),
                provider.GetRequiredService<IDurableWorkRegistry>(),
                provider.GetRequiredService<IDurablePayloadCodecRegistry>(),
                runtime.WorkOptions);
        });
        services.TryAddSingleton(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableScheduleProcessor(
                runtime.DispatcherDataSource,
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                runtime.WorkOptions,
                runtime.ScheduleOptions);
        });
        services.TryAddSingleton(static _ => new DurableRuntimeAdmissionGate());
        services.TryAddSingleton<IDurableRuntimeExecutionBoundary>(static _ => new UninstrumentedDurableRuntimeExecutionBoundary());
        services.TryAddSingleton<PostgreSqlDurableRuntimeHealth>(static provider => new PostgreSqlDurableRuntimeHealth(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<IDurableRuntimeSchemaManager>()));
        services.TryAddSingleton<IDurableRuntimeHealth>(static provider => provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>());
        services.TryAddSingleton<IDurableRuntimeDrainControl>(static provider => provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>());
        services.TryAddSingleton<PostgreSqlDurableControlClient>(static provider => new PostgreSqlDurableControlClient(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            provider.GetRequiredService<PostgreSqlDurableWorkStore>()));
        services.TryAddSingleton<IDurableWorkControlClient>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableControlClient>());
        services.TryAddSingleton<IDurableScopeControlClient>(static provider =>
            provider.GetRequiredService<PostgreSqlDurableControlClient>());
        services.TryAddSingleton<IDurableWorkOperatorClient>(static provider =>
        {
            var runtime = provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
            return new PostgreSqlDurableWorkOperatorClient(
                runtime.RuntimeDataSource,
                provider.GetRequiredService<IDurableWorkRegistry>(),
                provider,
                runtime.WorkOptions.RuntimeEpoch);
        });
        services.TryAddSingleton<IDurableRuntimePump>(static provider => new PostgreSqlDurableRuntimePump(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>(),
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            provider.GetRequiredService<PostgreSqlDurableFlowProcessor>(),
            provider.GetRequiredService<PostgreSqlDurableScheduleProcessor>(),
            provider.GetRequiredService<IDurableWorkRegistry>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDurableRuntimeExecutionBoundary>(),
            provider.GetRequiredService<DurableRuntimeAdmissionGate>()));
        return new AppSurfaceDurablePostgreSqlBuilder(services);
    }

    /// <summary>Adds the one critical continuous worker loop after passive PostgreSQL durable registration.</summary>
    /// <remarks>
    /// Calling this method never applies migrations. Startup validates compatibility and the active recovery epoch,
    /// then fails closed if either is unsuitable. Repeated calls are idempotent.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceDurableWorkerHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableRuntimeRegistration)))
        {
            throw new InvalidOperationException(
                "Register PostgreSQL durable storage with AddAppSurfaceDurablePostgreSql before adding a worker host.");
        }

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(PostgreSqlDurableHostedServiceMarker)))
        {
            return services;
        }

        services.AddSingleton<PostgreSqlDurableHostedServiceMarker>();
        services.AddSingleton<PostgreSqlDurableHostedService>(static provider => new PostgreSqlDurableHostedService(
            provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            provider.GetRequiredService<IDurableRuntimePump>(),
            provider.GetRequiredService<IDurableRuntimeDrainControl>(),
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<DurableRuntimeAdmissionGate>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PostgreSqlDurableHostedService>>()));
        services.AddSingleton<IHostedService>(static provider => provider.GetRequiredService<PostgreSqlDurableHostedService>());
        return services;
    }
}

internal sealed record PostgreSqlDurableRuntimeRegistration(
    NpgsqlDataSource DispatcherDataSource,
    NpgsqlDataSource RuntimeDataSource,
    PostgreSqlDurableWorkOptions WorkOptions,
    PostgreSqlDurableScheduleOptions ScheduleOptions,
    AppSurfaceDurablePostgreSqlOptions Options,
    Guid InstanceId);

internal sealed class PostgreSqlDurableHostedServiceMarker;
