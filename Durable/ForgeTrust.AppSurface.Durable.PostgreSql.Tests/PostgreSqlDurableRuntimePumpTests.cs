using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimePumpTests
{
    private const string TypedExitWorkName = "tests.runtime-pump.typed-exit";

    [Fact]
    public async Task RunOnceAsync_ProcessesRegisteredWorkThroughTheProviderExecutionBoundary()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "initial");
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            workOptions,
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var accepted = await client.EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-scope"),
            new DurableCommandId("runtime-pump-command"),
            "runtime-pump-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(new DurableScopeId("runtime-pump-scope"), accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal("runtime-pump-key", snapshot.Value.ProviderKey);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Theory]
    [InlineData(DurableWorkExitKind.Succeeded, DurableWorkState.Succeeded, "completed", "known_succeeded", 1, 0)]
    [InlineData(DurableWorkExitKind.RetryBeforeEffect, DurableWorkState.Ready, "app.gmail.sender_list_transient", "proven_no_effect", 0, 1)]
    [InlineData(DurableWorkExitKind.FailedTerminal, DurableWorkState.Suspended, "app.gmail.sender_list_invalid", "granted", 0, 1)]
    [InlineData(DurableWorkExitKind.AmbiguousExternalOutcome, DurableWorkState.Suspended, "app.gmail.sender_list_unknown", "ambiguous", 0, 1)]
    public async Task RunOnceAsync_TranslatesExplicitExitFactsAfterTheEffectPermit(
        DurableWorkExitKind exitKind,
        DurableWorkState expectedState,
        string expectedCode,
        string expectedPermitStatus,
        int expectedProcessed,
        int expectedFailed)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", $"typed-exit-{exitKind}");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.input", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.result", "v1");
        var executor = new TypedExitExecutor(exitKind, expectedCode);
        var registration = new DurableWorkExitRegistration<byte[], byte[], TypedExitExecutor>(
            TypedExitWorkName,
            "v2",
            workCodec,
            resultCodec);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton(executor);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = $"runtime-pump-typed-exit-{exitKind}-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-typed-exit-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId($"runtime-pump-typed-exit-{exitKind}-command"),
            $"runtime-pump-typed-exit-{exitKind}-key",
            TypedExitWorkName,
            "v2",
            workCodec.Encode(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(expectedProcessed, result.Processed);
        Assert.Equal(expectedFailed, result.Failed);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(expectedState, snapshot.Value!.State);
        Assert.Equal(expectedCode, snapshot.Value.TerminalCode);
        if (exitKind == DurableWorkExitKind.Succeeded)
        {
            Assert.Equal("result", Encoding.UTF8.GetString(snapshot.Value.Result!.Content.Span));
        }

        await using var permit = database.DataSource.CreateCommand(
            "SELECT status FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;");
        permit.Parameters.AddWithValue("scope_id", scope.Value);
        permit.Parameters.AddWithValue("work_id", accepted.Value.WorkId.Value);
        Assert.Equal(expectedPermitStatus, await permit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RunOnceAsync_RegistersAndExecutesTheTypedExitPathThroughThePublicServiceCollectionExtension()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "typed-exit-extension");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.extension.input", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.extension.result", "v1");
        var services = new ServiceCollection();
        services.AddDurableWorkExit<byte[], byte[], SuccessfulTypedExitExecutor>(
            "tests.runtime-pump.typed-exit.extension",
            "v2",
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-typed-exit-extension-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var registration = Assert.IsType<DurableWorkExitRegistration<byte[], byte[], SuccessfulTypedExitExecutor>>(
            provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("tests.runtime-pump.typed-exit.extension", "v2"));
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-typed-exit-extension-scope"),
            new DurableCommandId("runtime-pump-typed-exit-extension-command"),
            "runtime-pump-typed-exit-extension-key",
            registration.WorkName,
            registration.WorkVersion,
            workCodec.Encode(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(new DurableScopeId("runtime-pump-typed-exit-extension-scope"), accepted.Value!.WorkId));

        Assert.Equal(1, result.Processed);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal("completed", snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenALegacyBoundaryInvokesANonSuccessTypedExitAfterTheEffectPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "typed-exit-legacy-boundary");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.legacy.input", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.legacy.result", "v1");
        var registration = new DurableWorkExitRegistration<byte[], byte[], TypedExitExecutor>(
            "tests.runtime-pump.typed-exit.legacy",
            "v2",
            workCodec,
            resultCodec);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton(new TypedExitExecutor(DurableWorkExitKind.RetryBeforeEffect, "app.gmail.sender_list_transient"));
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-typed-exit-legacy-worker";
                options.SendWakeNotifications = false;
            });
        services.AddSingleton<IDurableRuntimeExecutionBoundary, LegacySuccessOnlyExecutionBoundary>();
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-typed-exit-legacy-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-typed-exit-legacy-command"),
            "runtime-pump-typed-exit-legacy-key",
            registration.WorkName,
            registration.WorkVersion,
            workCodec.Encode(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));

        Assert.Equal(1, result.Failed);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.Value.TerminalCode);
        await using var permit = database.DataSource.CreateCommand(
            "SELECT status FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;");
        permit.Parameters.AddWithValue("scope_id", scope.Value);
        permit.Parameters.AddWithValue("work_id", accepted.Value.WorkId.Value);
        Assert.Equal("ambiguous", await permit.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(TypedExitFailure.Throws)]
    [InlineData(TypedExitFailure.ReturnsNull)]
    [InlineData(TypedExitFailure.ResultCodecThrows)]
    public async Task RunOnceAsync_PreservesAmbiguityForTypedExitExecutorFailuresAfterTheEffectPermit(
        TypedExitFailure failure)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", $"typed-exit-failure-{failure}");
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.failure.input", "v1");
        IDurablePayloadCodec<byte[]> resultCodec = failure == TypedExitFailure.ResultCodecThrows
            ? new ThrowingEncodeCodec(new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.failure.result", "v1"))
            : new PostgreSqlOpaqueTestCodec("tests.runtime-pump.typed-exit.failure.result", "v1");
        var executor = new TypedFailureExitExecutor(failure);
        var registration = new DurableWorkExitRegistration<byte[], byte[], TypedFailureExitExecutor>(
            "tests.runtime-pump.typed-exit.failure",
            "v2",
            workCodec,
            resultCodec);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton(executor);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = $"runtime-pump-typed-exit-failure-{failure}-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-typed-exit-failure-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId($"runtime-pump-typed-exit-failure-{failure}-command"),
            $"runtime-pump-typed-exit-failure-{failure}-key",
            registration.WorkName,
            registration.WorkVersion,
            workCodec.Encode(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));

        Assert.Equal(1, result.Failed);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.Value.TerminalCode);
        await using var permit = database.DataSource.CreateCommand(
            "SELECT status FROM appsurface_durable.effect_permit WHERE scope_id = @scope_id AND work_id = @work_id;");
        permit.Parameters.AddWithValue("scope_id", scope.Value);
        permit.Parameters.AddWithValue("work_id", accepted.Value.WorkId.Value);
        Assert.Equal("ambiguous", await permit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RunOnceAsync_DiscoversOnlyTheHostRegisteredWorkContracts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "contract-discovery");
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        var foreignRegistration = new NamedSuccessfulWorkRegistration("tests.runtime-pump.foreign");
        var localRegistration = new NamedSuccessfulWorkRegistration("tests.runtime-pump.local");
        await using var foreignProvider = CreateWorkProvider(
            database,
            workOptions,
            foreignRegistration,
            "runtime-pump-foreign-host");
        await using var localProvider = CreateWorkProvider(
            database,
            workOptions,
            localRegistration,
            "runtime-pump-local-host");
        var scope = new DurableScopeId("runtime-pump-contract-discovery-scope");
        var foreignAccepted = await EnqueueAsync(
            foreignProvider,
            scope,
            "runtime-pump-foreign-command",
            foreignRegistration);
        await using (var markForeignRecoveryShaped = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET runtime_epoch = @other_epoch WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            markForeignRecoveryShaped.Parameters.AddWithValue("other_epoch", Guid.NewGuid());
            markForeignRecoveryShaped.Parameters.AddWithValue("scope_id", scope.Value);
            markForeignRecoveryShaped.Parameters.AddWithValue("work_id", foreignAccepted.WorkId.Value);
            Assert.Equal(1, await markForeignRecoveryShaped.ExecuteNonQueryAsync());
        }

        var localAccepted = await EnqueueAsync(
            localProvider,
            scope,
            "runtime-pump-local-command",
            localRegistration);
        var foreignBefore = await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, foreignAccepted.WorkId);

        var localResult = await localProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, localResult.Discovered);
        Assert.Equal(1, localResult.Claimed);
        Assert.Equal(1, localResult.Processed);
        Assert.Equal(0, localResult.Failed);
        Assert.Equal(
            foreignBefore,
            await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, foreignAccepted.WorkId));
        var localSnapshot = await localProvider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, localAccepted.WorkId));
        Assert.True(localSnapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, localSnapshot.Value!.State);

        await using (var restoreForeignEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET runtime_epoch = @runtime_epoch WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            restoreForeignEpoch.Parameters.AddWithValue("runtime_epoch", epoch);
            restoreForeignEpoch.Parameters.AddWithValue("scope_id", scope.Value);
            restoreForeignEpoch.Parameters.AddWithValue("work_id", foreignAccepted.WorkId.Value);
            Assert.Equal(1, await restoreForeignEpoch.ExecuteNonQueryAsync());
        }

        var foreignResult = await foreignProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        Assert.Equal(1, foreignResult.Processed);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotDiscoverAnAcceptedNewerWorkVersionUntilItsRegistryIsDeployed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "exit-version-discovery");
        var options = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        const string workName = "tests.runtime-pump.exit-version";
        var legacyV1 = new NamedSuccessfulWorkRegistration(workName, "v1");
        var exitAwareV2 = new NamedSuccessfulWorkRegistration(workName, "v2");
        await using var legacyProvider = CreateWorkProvider(database, options, legacyV1, "runtime-pump-exit-v1-worker");
        await using var capableProvider = CreateWorkProvider(database, options, exitAwareV2, "runtime-pump-exit-v2-worker");
        var scope = new DurableScopeId("runtime-pump-exit-version-scope");
        var accepted = await EnqueueAsync(
            capableProvider,
            scope,
            "runtime-pump-exit-version-command",
            exitAwareV2);
        var before = await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, accepted.WorkId);

        var legacyPass = await legacyProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(0, legacyPass.Discovered);
        Assert.Equal(0, legacyPass.Claimed);
        Assert.Equal(before, await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, accepted.WorkId));

        var capablePass = await capableProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        Assert.Equal(1, capablePass.Discovered);
        Assert.Equal(1, capablePass.Processed);
    }

    [Fact]
    public async Task RunOnceAsync_KeepsTheResolvedWorkContractSnapshotWhenARegistryLaterChanges()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "fixed-contract-snapshot");
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        var localRegistration = new NamedSuccessfulWorkRegistration("tests.runtime-pump.snapshot-local");
        var foreignRegistration = new NamedSuccessfulWorkRegistration("tests.runtime-pump.snapshot-foreign");
        var registry = new MutableWorkRegistry(localRegistration);
        var services = new ServiceCollection();
        services.AddSingleton<IDurableWorkRegistry>(registry);
        services.AddSingleton<DurableWorkRegistration>(localRegistration);
        services.AddAppSurfaceDurablePostgreSql(
            database.CreateDataSource(),
            database.CreateDataSource(),
            workOptions,
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-fixed-contract-snapshot-host";
                options.SendWakeNotifications = false;
            });
        await using var localProvider = services.BuildServiceProvider();
        _ = localProvider.GetRequiredService<IDurableRuntimePump>();
        await using var foreignProvider = CreateWorkProvider(
            database,
            workOptions,
            foreignRegistration,
            "runtime-pump-fixed-contract-snapshot-foreign-host");
        var scope = new DurableScopeId("runtime-pump-fixed-contract-snapshot-scope");
        var localAccepted = await EnqueueAsync(
            localProvider,
            scope,
            "runtime-pump-fixed-contract-snapshot-local-command",
            localRegistration);
        var foreignAccepted = await EnqueueAsync(
            foreignProvider,
            scope,
            "runtime-pump-fixed-contract-snapshot-foreign-command",
            foreignRegistration);
        registry.Add(foreignRegistration);
        var foreignBefore = await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, foreignAccepted.WorkId);

        var result = await localProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 2, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Processed);
        Assert.Equal(
            foreignBefore,
            await ReadWorkIsolationSnapshotAsync(database.DataSource, scope, foreignAccepted.WorkId));
        var localSnapshot = await localProvider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, localAccepted.WorkId));
        Assert.True(localSnapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, localSnapshot.Value!.State);
    }

    [Fact]
    public async Task Constructor_RejectsMissingWorkContractSelection()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "missing-contract-selection");
        var registration = new SuccessfulWorkRegistration();
        await using var provider = CreateWorkProvider(
            database,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            registration,
            "runtime-pump-missing-contract-selection-worker");

        var failure = Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableRuntimePump(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>(),
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            provider.GetRequiredService<PostgreSqlDurableFlowProcessor>(),
            provider.GetRequiredService<PostgreSqlDurableScheduleProcessor>(),
            provider.GetRequiredService<IDurableWorkRegistry>(),
            null!,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDurableRuntimeExecutionBoundary>(),
            provider.GetRequiredService<DurableRuntimeAdmissionGate>()));

        Assert.Equal("workContractSelection", failure.ParamName);
    }

    [Fact]
    public async Task RunOnceAsync_ProcessesRegisteredFlowThroughTheProviderProcessor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "flow");
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.flow", "v1");
        var flow = new CompletingFlowRegistration(contextCodec);
        var services = new ServiceCollection();
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        services.AddSingleton<DurableFlowRegistration>(flow);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-flow-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-flow-scope");
        var instance = new DurableFlowInstanceId("runtime-pump-flow-instance");
        var accepted = await provider.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("runtime-pump-flow-command"),
            "runtime-pump-flow-key",
            instance,
            flow.FlowId,
            flow.FlowVersion,
            contextCodec.EncodeObject(Encoding.UTF8.GetBytes("context"))));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Flow));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableFlowClient>().GetAsync(
            new DurableFlowGetRequest(scope, instance));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableFlowState.Completed, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_DefersWhenTheDiscoveredFlowRevisionIsStale()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "stale-flow");
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.stale-flow", "v1");
        var flow = new CompletingFlowRegistration(contextCodec);
        var services = new ServiceCollection();
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        services.AddSingleton<DurableFlowRegistration>(flow);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-stale-flow-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-stale-flow-scope");
        var instance = new DurableFlowInstanceId("runtime-pump-stale-flow-instance");
        var accepted = await provider.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("runtime-pump-stale-flow-command"),
            "runtime-pump-stale-flow-key",
            instance,
            flow.FlowId,
            flow.FlowVersion,
            contextCodec.EncodeObject(Encoding.UTF8.GetBytes("context"))));
        Assert.True(accepted.IsSuccess);

        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.flow_instance SET revision = revision + 1 WHERE scope_id = @scope_id AND flow_instance_id = @flow_instance_id;"))
        {
            update.Parameters.AddWithValue("scope_id", scope.Value);
            update.Parameters.AddWithValue("flow_instance_id", instance.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Flow));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task RunOnceAsync_ProcessesDueScheduleThroughTheWorkFirstProcessor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "schedule");
        var registration = new SuccessfulWorkRegistration();
        var scheduleCodec = new RuntimeSchedulePayloadCodec(
            registration.InputCodec.ContractName,
            registration.InputCodec.ContractVersion,
            registration.InputCodec.Classification);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton<IDurablePayloadCodec>(scheduleCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-schedule-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-schedule-scope");
        var created = await provider.GetRequiredService<IDurableScheduleClient>().CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("runtime-pump-schedule-command"),
            "runtime-pump-schedule-key",
            new DurableScheduleId("runtime-pump-schedule"),
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(
                SuccessfulWorkRegistration.Name,
                "v1",
                Encoding.UTF8.GetBytes("input"),
                scheduleCodec)));
        Assert.True(created.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Schedule));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Failed);
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;");
        count.Parameters.AddWithValue("scope_id", scope.Value);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RunOnceAsync_CountsAClaimSuspendedByAnAmbiguousEffectAsFailed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "suspended-before-permit");
        var registration = new PreparationGatedWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-suspended-before-permit-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-suspended-before-permit-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-suspended-before-permit-command"),
            "runtime-pump-suspended-before-permit-key",
            PreparationGatedWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var claimed = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(claimed.IsSuccess);
        Assert.Equal(DurableWorkState.Claimed, claimed.Value!.State);
        await SetCancellationIntentAsync(database.DataSource, scope, accepted.Value.WorkId);
        await InsertAmbiguousPermitFromAnotherAttemptAsync(database.DataSource, claimed.Value, epoch);

        registration.AllowPreparation.Set();
        var result = await running;

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal("cancellation_with_ambiguous_effect", snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_DefersWhenAClaimedScheduleProducesNoDurableFacts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "empty-schedule-result");
        var registration = new SuccessfulWorkRegistration();
        var scheduleCodec = new RuntimeSchedulePayloadCodec(
            registration.InputCodec.ContractName,
            registration.InputCodec.ContractVersion,
            registration.InputCodec.Classification);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton<IDurablePayloadCodec>(scheduleCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-empty-schedule-result-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-empty-schedule-result-scope");
        var scheduleId = new DurableScheduleId("runtime-pump-empty-schedule-result");
        var created = await provider.GetRequiredService<IDurableScheduleClient>().CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("runtime-pump-empty-schedule-result-command"),
            "runtime-pump-empty-schedule-result-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow + TimeSpan.FromDays(1)),
            DurableScheduleTarget.Work(
                SuccessfulWorkRegistration.Name,
                "v1",
                Encoding.UTF8.GetBytes("input"),
                scheduleCodec)));
        Assert.True(created.IsSuccess);
        await MakeScheduleDispatchAvailableAsync(database.DataSource, scope, scheduleId);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Schedule));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
        var snapshot = await provider.GetRequiredService<IDurableScheduleClient>().GetAsync(scope, scheduleId);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Active, snapshot.Value!.State);
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;");
        count.Parameters.AddWithValue("scope_id", scope.Value);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RunOnceAsync_TreatsASuspendedScheduleAsACommittedTurn()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "suspended-schedule");
        var registration = new SuccessfulWorkRegistration();
        var scheduleCodec = new RuntimeSchedulePayloadCodec(
            registration.InputCodec.ContractName,
            registration.InputCodec.ContractVersion,
            registration.InputCodec.Classification);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton<IDurablePayloadCodec>(scheduleCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface", maximumClockAdvance: TimeSpan.FromTicks(1)),
            options =>
            {
                options.WorkerId = "runtime-pump-suspended-schedule-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-suspended-schedule-scope");
        var scheduleId = new DurableScheduleId("runtime-pump-suspended-schedule");
        var scheduleClient = provider.GetRequiredService<IDurableScheduleClient>();
        var created = await scheduleClient.CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("runtime-pump-suspended-schedule-command"),
            "runtime-pump-suspended-schedule-key",
            scheduleId,
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(
                SuccessfulWorkRegistration.Name,
                "v1",
                Encoding.UTF8.GetBytes("input"),
                scheduleCodec)));
        Assert.True(created.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Schedule));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.True(result.HasMore);
        var snapshot = await scheduleClient.GetAsync(scope, scheduleId);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableScheduleState.Suspended, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsAnEmptyHealthyPassWhenEverySelectedSurfaceIsQuiescent()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "empty");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-empty-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All));

        Assert.Equal(0, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);

        var attempt = await provider.GetRequiredService<IDurableRuntimePumpAdmission>().TryRunOnceAsync(
            new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All));
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempt.Kind);
        Assert.NotNull(attempt.Result);
        Assert.Equal(0, attempt.Result.Discovered);
        Assert.Null(attempt.ProblemCode);
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsAnEmptyResultWhenAdmissionIsClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "admission-closed");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-admission-closed-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<DurableRuntimeAdmissionGate>().Close();

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.All));
        var attempt = await provider.GetRequiredService<IDurableRuntimePumpAdmission>().TryRunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.All));

        Assert.Equal(0, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, attempt.Kind);
        Assert.Null(attempt.Result);
        Assert.Null(attempt.ProblemCode);
    }

    [Fact]
    public async Task RunOnceAsync_SetsHasMoreWhenTheItemBudgetStopsBeforeRemainingWork()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "budget");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-budget-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        for (var index = 0; index < 2; index++)
        {
            var accepted = await client.EnqueueAsync(new DurableWorkRequest(
                new DurableScopeId($"runtime-pump-budget-scope-{index}"),
                new DurableCommandId($"runtime-pump-budget-command-{index}"),
                $"runtime-pump-budget-key-{index}",
                SuccessfulWorkRegistration.Name,
                "v1",
                registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
                DurableProviderSafety.Idempotent));
            Assert.True(accepted.IsSuccess);
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task RunOnceAsync_PreservesAmbiguityWhenAPermittedProviderFails()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "failure");
        var registration = new FailingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-failure-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-failure-scope"),
            new DurableCommandId("runtime-pump-failure-command"),
            "runtime-pump-failure-key",
            FailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(new DurableScopeId("runtime-pump-failure-scope"), accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.Value!.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_RejectsAnOverlappingPassUntilTheActiveProviderCallCompletes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "overlap");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-overlap-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-overlap-scope"),
            new DurableCommandId("runtime-pump-overlap-command"),
            "runtime-pump-overlap-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        var request = new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work);
        var activePass = pump.RunOnceAsync(request).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pump.RunOnceAsync(request));
        Assert.StartsWith(DurableProblemCodes.WorkerIdentityConflict, overlap.Message, StringComparison.Ordinal);
        Assert.Equal(
            "ASDUR405: This runtime instance already has an active Pass.",
            overlap.Message);
        var admissionOverlap = await provider.GetRequiredService<IDurableRuntimePumpAdmission>()
            .TryRunOnceAsync(request);
        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, admissionOverlap.Kind);
        Assert.Null(admissionOverlap.Result);

        registration.Complete.TrySetResult(registration.ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
        Assert.Equal(1, (await activePass).Processed);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenPersistedProviderSafetyNoLongerMatchesTheRegistration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "contract-mismatch");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-contract-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-contract-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-contract-command"),
            "runtime-pump-contract-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);
        await using (var corrupt = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET provider_safety = 'provider_keyed' WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            corrupt.Parameters.AddWithValue("scope_id", scope.Value);
            corrupt.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenProviderPreparationRejectsTheClaim()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "prepare-failure");
        var registration = new PreparationFailingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-preparation-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-preparation-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-preparation-command"),
            "runtime-pump-preparation-key",
            PreparationFailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_DefersWhenTheDiscoveredWorkClaimIsStale()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "stale-claim");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-stale-claim-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-stale-claim-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-stale-claim-command"),
            "runtime-pump-stale-claim-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET revision = revision + 1 WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            update.Parameters.AddWithValue("scope_id", scope.Value);
            update.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task RunOnceAsync_CountsAClaimTimeSuspensionAsFailed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "claim-suspension");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-claim-suspension-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-claim-suspension-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-claim-suspension-command"),
            "runtime-pump-claim-suspension-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET runtime_epoch = @other_epoch WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            update.Parameters.AddWithValue("other_epoch", Guid.NewGuid());
            update.Parameters.AddWithValue("scope_id", scope.Value);
            update.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_RenewsLeasesAndHeartbeatsWhileAProviderInvocationIsStillRunning()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "lease-renewal");
        var registration = new SlowWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-lease-worker";
                options.SendWakeNotifications = false;
                options.IdlePollingInterval = TimeSpan.FromMilliseconds(20);
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(1);
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-lease-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-lease-command"),
            "runtime-pump-lease-key",
            SlowWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent,
            new DurableWorkRetryPolicy(
                maximumAttempts: 2,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromMilliseconds(10),
                maximumRetryDelay: TimeSpan.FromMilliseconds(10),
                leaseDuration: TimeSpan.FromSeconds(2),
                renewalCadence: TimeSpan.FromMilliseconds(200),
                maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                backoffAlgorithm: "exponential-v1")));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Processed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Theory]
    [InlineData(RenewalBehavior.Expired)]
    [InlineData(RenewalBehavior.Missing)]
    [InlineData(RenewalBehavior.Fails)]
    [InlineData(RenewalBehavior.CancellationRequested)]
    public async Task RunOnceAsync_StopsTheInvocationWhenLeaseRenewalCannotContinue(RenewalBehavior behavior)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", $"renewal-{behavior}");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = $"runtime-pump-renewal-{behavior}-worker";
                options.SendWakeNotifications = false;
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(2);
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId($"runtime-pump-renewal-{behavior}-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId($"runtime-pump-renewal-{behavior}-command"),
            $"runtime-pump-renewal-{behavior}-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent,
            new DurableWorkRetryPolicy(
                maximumAttempts: 2,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(1),
                leaseDuration: TimeSpan.FromSeconds(2),
                renewalCadence: TimeSpan.FromMilliseconds(25),
                maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                backoffAlgorithm: "exponential-v1")));
        Assert.True(accepted.IsSuccess);

        var store = new RenewalOutcomeWorkStore(database.DataSource, epoch, behavior);
        var pump = CreatePump(provider, store);
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(
            maximumItems: 1,
            surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Processed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_FinalizesAmbiguousOutcomeWhenCallerCancelsBeforeProviderReturns()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "cancel-after-permit");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-cancel-after-permit-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-cancel-after-permit-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-cancel-after-permit-command"),
            "runtime-pump-cancel-after-permit-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        using var cancellation = new CancellationTokenSource();
        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work),
            cancellation.Token).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_WaitsForTheRenewalCadenceAfterASlowLeaseRenewal()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "slow-lease-renewal");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-slow-renewal-worker";
                options.SendWakeNotifications = false;
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(2);
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-slow-renewal-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-slow-renewal-command"),
            "runtime-pump-slow-renewal-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent,
            new DurableWorkRetryPolicy(
                maximumAttempts: 2,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(1),
                leaseDuration: TimeSpan.FromSeconds(2),
                renewalCadence: TimeSpan.FromMilliseconds(250),
                maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                backoffAlgorithm: "exponential-v1")));
        Assert.True(accepted.IsSuccess);

        var delayedStore = new DelayedLeaseRenewalWorkStore(database.DataSource, epoch);
        var pump = CreatePump(provider, delayedStore);
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(
            maximumItems: 1,
            surfaces: DurableRuntimeSurface.Work)).AsTask();

        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delayedStore.FirstRenewalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, delayedStore.RenewalCount);
        registration.Complete.TrySetResult(registration.CompletionResult);

        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.Processed);
    }

    [Fact]
    public async Task RunOnceAsync_ObservesCancellationThatCommitsAfterClaimBeforeAnEffectPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "cancel-before-permit");
        var registration = new PreparationGatedWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-cancel-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-cancel-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-cancel-command"),
            "runtime-pump-cancel-key",
            PreparationGatedWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var claimed = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(claimed.IsSuccess);
        Assert.Equal(DurableWorkState.Claimed, claimed.Value!.State);
        var canceled = await control.CancelAsync(new DurableWorkCancelRequest(
            scope,
            accepted.Value.WorkId,
            "runtime-pump-cancel-operator",
            "requested",
            claimed.Value.Revision));
        Assert.True(canceled.IsSuccess);

        registration.AllowPreparation.Set();
        var result = await running;

        Assert.Equal(1, result.Deferred);
        var snapshot = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_CountsAClaimCanceledBeforePermitAsProcessed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "canceled-before-permit");
        var registration = new PreparationGatedWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-canceled-before-permit-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-canceled-before-permit-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-canceled-before-permit-command"),
            "runtime-pump-canceled-before-permit-key",
            PreparationGatedWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var cancel = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET cancellation_requested_at = clock_timestamp() WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            cancel.Parameters.AddWithValue("scope_id", scope.Value);
            cancel.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await cancel.ExecuteNonQueryAsync());
        }

        registration.AllowPreparation.Set();
        var result = await running;

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenAProviderKeyedEffectCannotReportTerminalTruth()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "retryable-failure");
        var registration = new FailingWorkRegistration(DurableProviderSafety.ProviderKeyed);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-retryable-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-retryable-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-retryable-command"),
            "runtime-pump-retryable-key",
            FailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsAnEmptyResultWhenTheRuntimeHealthPassIsAlreadyActive()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "health-pass-active");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-health-pass-active-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-health-pass-active-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-health-pass-active-command"),
            "runtime-pump-health-pass-active-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var firstPump = provider.GetRequiredService<IDurableRuntimePump>();
        var firstPass = firstPump.RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondPump = CreatePump(
            provider,
            new PostgreSqlDurableWorkStore(database.CreateDataSource(), database.CreateDataSource(), epoch));
        var secondResult = await secondPump.RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));
        var secondAttempt = await secondPump.TryRunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(0, secondResult.Discovered);
        Assert.Equal(0, secondResult.Claimed);
        Assert.Equal(0, secondResult.Processed);
        Assert.Equal(0, secondResult.Deferred);
        Assert.Equal(0, secondResult.Failed);
        Assert.False(secondResult.HasMore);
        Assert.Equal(TimeSpan.Zero, secondResult.Elapsed);
        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, secondAttempt.Kind);
        Assert.Null(secondAttempt.Result);

        registration.Complete.TrySetResult(registration.CompletionResult);
        Assert.Equal(1, (await firstPass).Processed);
    }

    [Fact]
    public async Task RunOnceAsync_CountsWorkExhaustedBeforeClaimAsProcessed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "exhausted-before-claim");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-exhausted-before-claim-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-exhausted-before-claim-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-exhausted-before-claim-command"),
            "runtime-pump-exhausted-before-claim-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        await using (var exhaust = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET attempt_number = maximum_attempts WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            exhaust.Parameters.AddWithValue("scope_id", scope.Value);
            exhaust.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await exhaust.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.FailedTerminal, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_DefersCompletionWhenTheOwningScopeIsFencedAfterTheEffectStarts()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "stale-completion");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-stale-completion-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-stale-completion-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-stale-completion-command"),
            "runtime-pump-stale-completion-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disabled = await provider.GetRequiredService<IDurableScopeControlClient>().DisableAsync(
            new DurableScopeDisableRequest(scope, "runtime-pump-test", "fence", expectedGeneration: 1));
        Assert.True(disabled.IsSuccess);

        registration.Complete.TrySetResult(registration.CompletionResult);
        var result = await running;

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunOnceAsync_CountsAFlowEvaluationSuspensionAsFailed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "flow-suspension");
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.flow-suspension", "v1");
        var flow = new FailingFlowRegistration(contextCodec);
        var services = new ServiceCollection();
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        services.AddSingleton<DurableFlowRegistration>(flow);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-flow-suspension-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-flow-suspension-scope");
        var instance = new DurableFlowInstanceId("runtime-pump-flow-suspension-instance");
        var accepted = await provider.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("runtime-pump-flow-suspension-command"),
            "runtime-pump-flow-suspension-key",
            instance,
            flow.FlowId,
            flow.FlowVersion,
            contextCodec.EncodeObject(Encoding.UTF8.GetBytes("context"))));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Flow));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Deferred);
        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableFlowClient>().GetAsync(
            new DurableFlowGetRequest(scope, instance));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableFlowState.Suspended, snapshot.Value!.State);
    }

    [Fact]
    public async Task TryRunOnceAsync_ProjectsSchemaIncompatibilityWithoutEnteringExecution()
    {
        using var dispatcher = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=durable_pump;Username=durable;Timeout=1");
        using var runtime = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=2;Database=durable_pump;Username=durable;Timeout=1");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            runtime,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var status = CreateSchemaStatus(DurableRuntimeSchemaCompatibility.UpgradeRequired);
        var expected = new DurableRuntimeSchemaException(status);
        var schema = new StubSchemaManager(
            _ => ValueTask.FromException(expected));
        var executorCalls = 0;
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            schemaManager: schema,
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(
                    new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
            });
        var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

        var attempt = await pump.TryRunOnceAsync(request);
        var legacy = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await pump.RunOnceAsync(request));

        Assert.Equal(DurableRuntimePumpAttemptKind.Incompatible, attempt.Kind);
        Assert.Equal(DurableProblemCodes.SchemaUpgradeRequired, attempt.ProblemCode);
        Assert.Null(attempt.Result);
        Assert.Same(expected, legacy);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task TryRunOnceAsync_RevalidatesSchemaInsideStoreAdmissionAfterPrecheckRace()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "schema-admission-race");
        var status = await schema.GetStatusAsync();
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-schema-admission-race-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var mutatedAfterPrecheck = 0;
        var racingSchema = new StubSchemaManager(async cancellationToken =>
        {
            await schema.ValidateAsync(cancellationToken);
            if (Interlocked.Exchange(ref mutatedAfterPrecheck, 1) == 0)
            {
                await using var makeUnsupported = database.DataSource.CreateCommand(
                    "DROP SCHEMA appsurface_durable CASCADE;");
                await makeUnsupported.ExecuteNonQueryAsync(cancellationToken);
            }
        });
        var executorCalls = 0;
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            schemaManager: racingSchema,
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(
                    new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
            });

        var attempt = await pump.TryRunOnceAsync(
            new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All));

        Assert.Equal(DurableRuntimePumpAttemptKind.Incompatible, attempt.Kind);
        Assert.Equal(DurableProblemCodes.SchemaMissing, attempt.ProblemCode);
        Assert.Null(attempt.Result);
        Assert.Equal(1, mutatedAfterPrecheck);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task PumpProjections_RefuseDrainingWorkerWithoutEnteringExecution()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "draining-projections");
        await using var runtimeDataSource = database.CreateDataSource();
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-draining-projections-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDurableRuntimeDrainControl>().BeginDrainAsync();
        var executorCalls = 0;
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(
                    new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
            });
        var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

        var attempt = await pump.TryRunOnceAsync(request);
        var legacy = await pump.RunOnceAsync(request);

        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, attempt.Kind);
        Assert.Null(attempt.ProblemCode);
        Assert.Null(attempt.Result);
        Assert.Equal(0, legacy.Discovered);
        Assert.Equal(0, legacy.Claimed);
        Assert.Equal(0, legacy.Processed);
        Assert.Equal(0, legacy.Deferred);
        Assert.Equal(0, legacy.Failed);
        Assert.False(legacy.HasMore);
        Assert.Null(legacy.NextDueAtUtc);
        Assert.Equal(TimeSpan.Zero, legacy.Elapsed);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task TryRunOnceAsync_ProjectsRuntimeStoreOutageWithoutEnteringExecution()
    {
        using var dispatcher = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=durable_pump;Username=durable;Timeout=1");
        using var runtime = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=2;Database=durable_pump;Username=durable;Timeout=1");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            dispatcher,
            runtime,
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), Guid.NewGuid()),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var schema = new StubSchemaManager(
            _ => ValueTask.CompletedTask);
        var executorCalls = 0;
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            schemaManager: schema,
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(
                    new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
            });
        var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

        var attempt = await pump.TryRunOnceAsync(request);
        await Assert.ThrowsAnyAsync<NpgsqlException>(
            async () => await pump.RunOnceAsync(request));

        Assert.Equal(DurableRuntimePumpAttemptKind.Unavailable, attempt.Kind);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, attempt.ProblemCode);
        Assert.Null(attempt.Result);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task TryRunOnceAsync_ProjectsRuntimeAdmissionPermissionFailureWithoutEnteringExecution()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "permission-failure");
        var status = await schema.GetStatusAsync();
        var role = $"runtime_admission_denied_{Guid.NewGuid():N}";
        const string password = "runtime-admission-test-password";
        await using (var createRole = database.DataSource.CreateCommand(
            $"""
            CREATE ROLE {role}
                LOGIN PASSWORD '{password}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO {role};
            GRANT SELECT ON TABLE
                appsurface_durable.store_metadata,
                appsurface_durable.runtime_heartbeat
                TO {role};
            """))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;
        var restrictedDispatcherDataSource = NpgsqlDataSource.Create(connectionString);
        var restrictedRuntimeDataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            var services = new ServiceCollection();
            services.AddAppSurfaceDurablePostgreSql(
                restrictedDispatcherDataSource,
                restrictedRuntimeDataSource,
                new PostgreSqlDurableWorkOptions(epoch, status.StoreId),
                new PostgreSqlDurableScheduleOptions("appsurface"),
                options =>
                {
                    options.WorkerId = "runtime-pump-permission-worker";
                    options.SendWakeNotifications = false;
                });
            await using var provider = services.BuildServiceProvider();
            var executorCalls = 0;
            var pump = CreatePump(
                provider,
                provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
                schemaManager: new StubSchemaManager(_ => ValueTask.CompletedTask),
                passExecutor: (_, _) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return ValueTask.FromResult(
                        new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
                });
            var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

            var attempt = await pump.TryRunOnceAsync(request);
            var legacy = await Assert.ThrowsAsync<PostgresException>(
                async () => await pump.RunOnceAsync(request));

            Assert.Equal(DurableRuntimePumpAttemptKind.Unavailable, attempt.Kind);
            Assert.Equal(DurableProblemCodes.StoreUnavailable, attempt.ProblemCode);
            Assert.Null(attempt.Result);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, legacy.SqlState);
            Assert.Equal(0, executorCalls);
        }
        finally
        {
            await restrictedDispatcherDataSource.DisposeAsync();
            await restrictedRuntimeDataSource.DisposeAsync();
            await using var dropRole = database.DataSource.CreateCommand(
                $"DROP OWNED BY {role}; DROP ROLE {role};");
            await dropRole.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task TryRunOnceAsync_ProjectsEpochAndWorkerGenerationBeforeEnteringExecution()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var activeEpoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(activeEpoch, "runtime-pump-tests", "projection-fences");
        var status = await schema.GetStatusAsync();
        var executorCalls = 0;

        var epochServices = new ServiceCollection();
        epochServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(Guid.NewGuid(), status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-epoch-projection-worker";
                options.SendWakeNotifications = false;
            });
        await using (var epochProvider = epochServices.BuildServiceProvider())
        {
            var epochPump = CreatePump(
                epochProvider,
                epochProvider.GetRequiredService<PostgreSqlDurableWorkStore>(),
                passExecutor: (_, _) =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return ValueTask.FromResult(
                        new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
                });
            var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

            var attempt = await epochPump.TryRunOnceAsync(request);
            var legacy = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await epochPump.RunOnceAsync(request));

            Assert.Equal(DurableRuntimePumpAttemptKind.Incompatible, attempt.Kind);
            Assert.Equal(DurableProblemCodes.RecoveryEpochRequired, attempt.ProblemCode);
            Assert.Equal(
                "ASDUR108: The configured runtime epoch is not active in PostgreSQL.",
                legacy.Message);
        }

        var workerServices = new ServiceCollection();
        workerServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(activeEpoch, status.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-generation-projection-worker";
                options.SendWakeNotifications = false;
            });
        await using var workerProvider = workerServices.BuildServiceProvider();
        var registration = workerProvider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>();
        var otherGeneration = new PostgreSqlDurableRuntimeHealth(
            registration with { InstanceId = Guid.NewGuid() },
            schema);
        Assert.True(await otherGeneration.TryBeginPassAsync(CancellationToken.None));
        var workerPump = CreatePump(
            workerProvider,
            workerProvider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(
                    new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero));
            });
        var workerRequest = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);

        var workerAttempt = await workerPump.TryRunOnceAsync(workerRequest);
        var workerLegacy = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await workerPump.RunOnceAsync(workerRequest));

        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, workerAttempt.Kind);
        Assert.Null(workerAttempt.ProblemCode);
        Assert.Null(workerAttempt.Result);
        Assert.Equal(
            "ASDUR405: This worker generation no longer owns the configured worker identity.",
            workerLegacy.Message);
        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task TryRunOnceAsync_EntersTheInjectedExecutorExactlyOnceAndPropagatesItsFailure()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "execution-seam");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-execution-seam-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var expected = new TimeoutException("application execution boundary failed");
        var executorCalls = 0;
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromException<DurableRuntimePumpResult>(expected);
            });

        var observed = await Assert.ThrowsAsync<TimeoutException>(
            async () => await pump.TryRunOnceAsync(
                new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All)));

        Assert.Same(expected, observed);
        Assert.Equal(1, executorCalls);
        var snapshot = await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync();
        Assert.False(snapshot.IsPassActive);
    }

    [Fact]
    public async Task TryRunOnceAsync_FinalizesWhenCallerCancelsAsTheProviderReturns()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "post-provider-cancellation");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-post-provider-cancellation-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        using var callerCancellation = new CancellationTokenSource();
        var expected = new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero);
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            passExecutor: (_, _) =>
            {
                callerCancellation.Cancel();
                return ValueTask.FromResult(expected);
            });

        var attempt = await pump.TryRunOnceAsync(
            new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All),
            callerCancellation.Token);

        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempt.Kind);
        Assert.Same(expected, attempt.Result);
        Assert.Null(attempt.ProblemCode);
        Assert.True(callerCancellation.IsCancellationRequested);
        var snapshot = await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync();
        Assert.False(snapshot.IsPassActive);
        Assert.NotNull(snapshot.LastSuccessfulSweepAtUtc);
    }

    [Fact]
    public async Task TryRunOnceAsync_CleansUpIndeterminateStoreAdmissionWithoutReplacingTheOutcome()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "admission-ack-loss");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-admission-ack-loss-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var executorCalls = 0;
        var expectedResult = new DurableRuntimePumpResult(
            0,
            0,
            0,
            0,
            0,
            false,
            null,
            TimeSpan.Zero);
        var pump = CreatePump(
            provider,
            provider.GetRequiredService<PostgreSqlDurableWorkStore>(),
            passExecutor: (_, _) =>
            {
                Interlocked.Increment(ref executorCalls);
                return ValueTask.FromResult(expectedResult);
            });
        await using (var trigger = database.DataSource.CreateCommand(
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.test_runtime_pump_lose_admission_ack()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.pass_active AND NOT OLD.pass_active THEN
                    PERFORM pg_terminate_backend(pg_backend_pid());
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER test_runtime_pump_lose_admission_ack
            BEFORE UPDATE ON appsurface_durable.runtime_heartbeat
            FOR EACH ROW
            EXECUTE FUNCTION appsurface_durable.test_runtime_pump_lose_admission_ack();
            """))
        {
            await trigger.ExecuteNonQueryAsync();
        }

        var request = new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All);
        var unavailable = await pump.TryRunOnceAsync(request);
        Assert.Equal(DurableRuntimePumpAttemptKind.Unavailable, unavailable.Kind);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, unavailable.ProblemCode);
        Assert.Equal(0, executorCalls);

        await using (var cleanup = database.DataSource.CreateCommand(
            """
            DROP TRIGGER IF EXISTS test_runtime_pump_lose_admission_ack
                ON appsurface_durable.runtime_heartbeat;
            DROP FUNCTION IF EXISTS appsurface_durable.test_runtime_pump_lose_admission_ack();
            """))
        {
            await cleanup.ExecuteNonQueryAsync();
        }

        var completed = await pump.TryRunOnceAsync(request);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, completed.Kind);
        Assert.Same(expectedResult, completed.Result);
        Assert.Equal(1, executorCalls);
    }

    [Fact]
    public async Task RunOnceAsync_PreservesProviderFailureWhenFailedPassRecordingLosesTheRuntimeEpoch()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "failed-pass-fenced");
        var registration = new StartedFailingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-failed-pass-fenced-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-failed-pass-fenced-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-failed-pass-fenced-command"),
            "runtime-pump-failed-pass-fenced-key",
            StartedFailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var running = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var changeEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = @epoch WHERE singleton;"))
        {
            changeEpoch.Parameters.AddWithValue("epoch", Guid.NewGuid());
            Assert.Equal(1, await changeEpoch.ExecuteNonQueryAsync());
        }

        registration.Fail.Set();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, failure.Message, StringComparison.Ordinal);
    }

    private static PostgreSqlDurableRuntimePump CreatePump(
        ServiceProvider provider,
        PostgreSqlDurableWorkStore workStore,
        IDurableRuntimeSchemaManager? schemaManager = null,
        PostgreSqlDurablePassExecutor? passExecutor = null) =>
        new(
            provider.GetRequiredService<PostgreSqlDurableRuntimeRegistration>(),
            schemaManager ?? provider.GetRequiredService<IDurableRuntimeSchemaManager>(),
            provider.GetRequiredService<PostgreSqlDurableRuntimeHealth>(),
            workStore,
            provider.GetRequiredService<PostgreSqlDurableFlowProcessor>(),
            provider.GetRequiredService<PostgreSqlDurableScheduleProcessor>(),
            provider.GetRequiredService<IDurableWorkRegistry>(),
            new PostgreSqlDurableWorkContractSelection(provider.GetRequiredService<IDurableWorkRegistry>()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDurableRuntimeExecutionBoundary>(),
            provider.GetRequiredService<DurableRuntimeAdmissionGate>(),
            NullLogger<PostgreSqlDurableRuntimePump>.Instance,
            passExecutor);

    private static DurableRuntimeSchemaStatus CreateSchemaStatus(
        DurableRuntimeSchemaCompatibility compatibility) =>
        new(
            compatibility,
            Guid.NewGuid(),
            activeRuntimeEpoch: null,
            installedVersion: 9,
            requiredVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
            minimumReaderVersion: 1,
            maximumReaderVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
            minimumWriterVersion: 1,
            maximumWriterVersion: PostgreSqlDurableRuntimeSchemaManager.RequiredVersion,
            appliedVersions: Enumerable.Range(1, 9).ToArray(),
            pendingVersions: [PostgreSqlDurableRuntimeSchemaManager.RequiredVersion],
            problem: "test status");

    private sealed class StubSchemaManager(
        Func<CancellationToken, ValueTask> validate) : IDurableRuntimeSchemaManager
    {
        public ValueTask<DurableRuntimeSchemaStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GenerateScript(int fromVersion = 0) => throw new NotSupportedException();

        public ValueTask<DurableRuntimeSchemaApplyResult> ApplyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
            validate(cancellationToken);

        public ValueTask<DurableRuntimeEpochActivationResult> InitializeRuntimeEpochAsync(
            Guid initialEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DurableRuntimeEpochRotationResult> RotateRuntimeEpochAsync(
            Guid expectedActiveEpoch,
            Guid newActiveEpoch,
            string actorId,
            string reasonCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ServiceProvider CreateWorkProvider(
        PostgreSqlIntegrationTestDatabase database,
        PostgreSqlDurableWorkOptions workOptions,
        DurableWorkRegistration registration,
        string workerId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.CreateDataSource(),
            database.CreateDataSource(),
            workOptions,
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = workerId;
                options.SendWakeNotifications = false;
            });
        return services.BuildServiceProvider();
    }

    private static async ValueTask<DurableWorkAcceptance> EnqueueAsync(
        ServiceProvider provider,
        DurableScopeId scope,
        string commandId,
        NamedSuccessfulWorkRegistration registration)
    {
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId(commandId),
            commandId,
            registration.WorkName,
            registration.WorkVersion,
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);
        return accepted.Value!;
    }

    private static async ValueTask<string> ReadWorkIsolationSnapshotAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scope,
        DurableWorkId workId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT jsonb_build_object(
                       'work', to_jsonb(work),
                       'dispatch', to_jsonb(dispatch),
                       'history', COALESCE(
                           jsonb_agg(to_jsonb(history) ORDER BY history.event_id)
                               FILTER (WHERE history.event_id IS NOT NULL),
                           '[]'::jsonb))::text
            FROM appsurface_durable.work AS work
            LEFT JOIN appsurface_durable.dispatch AS dispatch
              ON dispatch.scope_id = work.scope_id
             AND dispatch.aggregate_kind = 'work'
             AND dispatch.aggregate_id = work.work_id
            LEFT JOIN appsurface_durable.work_history AS history
              ON history.scope_id = work.scope_id
             AND history.work_id = work.work_id
            WHERE work.scope_id = @scope_id
              AND work.work_id = @work_id
            GROUP BY work.scope_id, work.work_id, dispatch.dispatch_id;
            """);
        command.Parameters.AddWithValue("scope_id", scope.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async ValueTask SetCancellationIntentAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET cancellation_requested_at = clock_timestamp(), revision = revision + 1 WHERE scope_id = @scope_id AND work_id = @work_id;");
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("work_id", workId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class LegacySuccessOnlyExecutionBoundary : IDurableRuntimeExecutionBoundary
    {
        public async ValueTask<DurableEncodedWorkExit> InvokeExitAsync(
            DurablePreparedWorkInvocation invocation,
            CancellationToken cancellationToken) =>
            DurableEncodedWorkExit.Succeeded(await invocation.InvokeAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertAmbiguousPermitFromAnotherAttemptAsync(
        NpgsqlDataSource dataSource,
        DurableWorkSnapshot snapshot,
        Guid runtimeEpoch)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.effect_permit
                (permit_id, scope_id, work_id, activity_id, attempt_number, lease_generation, scope_generation, runtime_epoch, status)
            VALUES
                (@permit_id, @scope_id, @work_id, @activity_id, @attempt_number, @lease_generation, @scope_generation, @runtime_epoch, 'ambiguous');
            """);
        command.Parameters.AddWithValue("permit_id", Guid.NewGuid());
        command.Parameters.AddWithValue("scope_id", snapshot.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", snapshot.WorkId.Value);
        command.Parameters.AddWithValue("activity_id", snapshot.ProviderKey ?? "runtime-pump-ambiguous-activity");
        command.Parameters.AddWithValue("attempt_number", snapshot.AttemptNumber + 1);
        command.Parameters.AddWithValue("lease_generation", 1L);
        command.Parameters.AddWithValue("scope_generation", 1L);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask MakeScheduleDispatchAvailableAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableScheduleId scheduleId)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.schedule_dispatch SET due_at = clock_timestamp(), state = 'available', lease_owner = NULL, lease_expires_at = NULL, updated_at = clock_timestamp() WHERE scope_id = @scope_id AND schedule_id = @schedule_id;");
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("schedule_id", scheduleId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public enum RenewalBehavior
    {
        Expired,
        Missing,
        Fails,
        CancellationRequested,
    }

    private sealed class RenewalOutcomeWorkStore(
        NpgsqlDataSource dataSource,
        Guid runtimeEpoch,
        RenewalBehavior behavior) : PostgreSqlDurableWorkStore(dataSource, runtimeEpoch)
    {
        internal override ValueTask<PostgreSqlDurableWorkClaim?> RenewLeaseAsync(
            PostgreSqlDurableWorkClaim claim,
            CancellationToken cancellationToken = default) =>
            behavior switch
            {
                RenewalBehavior.Expired => ValueTask.FromResult<PostgreSqlDurableWorkClaim?>(
                    claim with { LeaseExpiresAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1) }),
                RenewalBehavior.Missing => ValueTask.FromResult<PostgreSqlDurableWorkClaim?>(null),
                RenewalBehavior.Fails => ValueTask.FromException<PostgreSqlDurableWorkClaim?>(
                    new InvalidOperationException("Simulated lease renewal failure.")),
                RenewalBehavior.CancellationRequested => ValueTask.FromResult<PostgreSqlDurableWorkClaim?>(
                    claim with { CancellationRequested = true }),
                _ => throw new InvalidDataException($"Unknown test renewal behavior '{behavior}'."),
            };
    }

    private sealed class SuccessfulWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump";

        internal SuccessfulWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            _ = WorkCodec.DecodeObject(work.Payload);
            return new SuccessfulPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class TypedExitExecutor(DurableWorkExitKind kind, string code) : IDurableWorkExitExecutor<byte[], byte[]>
    {
        public ValueTask<DurableWorkExit<byte[]>> ExecuteAsync(
            DurableWorkerEnvelope<byte[]> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(kind switch
            {
                DurableWorkExitKind.Succeeded => DurableWorkExit<byte[]>.Succeeded(Encoding.UTF8.GetBytes("result")),
                DurableWorkExitKind.RetryBeforeEffect => DurableWorkExit<byte[]>.RetryBeforeEffect(code),
                DurableWorkExitKind.FailedTerminal => DurableWorkExit<byte[]>.FailedTerminal(code),
                DurableWorkExitKind.AmbiguousExternalOutcome => DurableWorkExit<byte[]>.AmbiguousExternalOutcome(code),
                _ => throw new InvalidOperationException("Unexpected test exit kind."),
            });
    }

    private sealed class SuccessfulTypedExitExecutor : IDurableWorkExitExecutor<byte[], byte[]>
    {
        public ValueTask<DurableWorkExit<byte[]>> ExecuteAsync(
            DurableWorkerEnvelope<byte[]> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableWorkExit<byte[]>.Succeeded(Encoding.UTF8.GetBytes("result")));
    }

    public enum TypedExitFailure
    {
        Throws,
        ReturnsNull,
        ResultCodecThrows,
    }

    private sealed class TypedFailureExitExecutor(TypedExitFailure failure) : IDurableWorkExitExecutor<byte[], byte[]>
    {
        public ValueTask<DurableWorkExit<byte[]>> ExecuteAsync(
            DurableWorkerEnvelope<byte[]> work,
            CancellationToken cancellationToken = default) =>
            failure switch
            {
                TypedExitFailure.Throws => ValueTask.FromException<DurableWorkExit<byte[]>>(
                    new InvalidOperationException("Simulated typed executor failure.")),
                TypedExitFailure.ReturnsNull => ValueTask.FromResult<DurableWorkExit<byte[]>>(null!),
                TypedExitFailure.ResultCodecThrows => ValueTask.FromResult(
                    DurableWorkExit<byte[]>.Succeeded(Encoding.UTF8.GetBytes("result"))),
                _ => throw new InvalidOperationException("Unexpected typed executor failure."),
            };
    }

    private sealed class ThrowingEncodeCodec(IDurablePayloadCodec<byte[]> inner) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => inner.PayloadType;

        public string ContractName => inner.ContractName;

        public string ContractVersion => inner.ContractVersion;

        public DurableDataClassification Classification => inner.Classification;

        public string RetentionPolicyId => inner.RetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) =>
            throw new InvalidOperationException("Simulated result codec failure.");

        public byte[] Decode(DurableEncodedPayload payload) => inner.Decode(payload);

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<byte[]>(value));

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class NamedSuccessfulWorkRegistration : DurableWorkRegistration
    {
        internal NamedSuccessfulWorkRegistration(string workName, string workVersion = "v1")
            : base(
                workName,
                workVersion,
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec($"{workName}.input", "v1"),
                new PostgreSqlOpaqueTestCodec($"{workName}.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            _ = WorkCodec.DecodeObject(work.Payload);
            return new SuccessfulPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class MutableWorkRegistry(params DurableWorkRegistration[] registrations) : IDurableWorkRegistry
    {
        private readonly List<DurableWorkRegistration> _registrations = [.. registrations];

        public IReadOnlyList<DurableWorkContractIdentity> RegisteredContracts => _registrations
            .Select(static registration => new DurableWorkContractIdentity(registration.WorkName, registration.WorkVersion))
            .ToArray();

        public DurableWorkRegistration GetRequired(string workName, string workVersion) => _registrations
            .Single(registration => StringComparer.Ordinal.Equals(registration.WorkName, workName)
                && StringComparer.Ordinal.Equals(registration.WorkVersion, workVersion));

        internal void Add(DurableWorkRegistration registration) => _registrations.Add(registration);
    }

    private sealed class FailingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.failure";

        internal FailingWorkRegistration(DurableProviderSafety providerSafety = DurableProviderSafety.Idempotent)
            : base(
                Name,
                "v1",
                providerSafety,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.failure.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.failure.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new FailingPreparedWork();

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class BlockingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.blocking";

        internal BlockingWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.blocking.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.blocking.result", "v1"))
        {
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<DurableEncodedPayload> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        internal DurableEncodedPayload CompletionResult => ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result"));

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new BlockingPreparedWork(Started, Complete);

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class StartedFailingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.started-failure";

        internal StartedFailingWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.started-failure.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.started-failure.result", "v1"))
        {
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Fail { get; } = new(false);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new StartedFailingPreparedWork(Started, Fail);

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class PreparationFailingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.prepare-failure";

        internal PreparationFailingWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-failure.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-failure.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            throw new InvalidOperationException("Simulated provider preparation failure.");

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DurableEncodedPayload>(new InvalidOperationException("Preparation must run first."));

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class SlowWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.slow";

        internal SlowWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.slow.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.slow.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new SlowPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class DelayedLeaseRenewalWorkStore : PostgreSqlDurableWorkStore
    {
        private int _renewalCount;

        internal DelayedLeaseRenewalWorkStore(NpgsqlDataSource dataSource, Guid runtimeEpoch)
            : base(dataSource, runtimeEpoch)
        {
        }

        internal TaskCompletionSource FirstRenewalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int RenewalCount => Volatile.Read(ref _renewalCount);

        internal override async ValueTask<PostgreSqlDurableWorkClaim?> RenewLeaseAsync(
            PostgreSqlDurableWorkClaim claim,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _renewalCount);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            FirstRenewalCompleted.TrySetResult();
            return claim with { LeaseExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1) };
        }
    }

    private sealed class PreparationGatedWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.prepare-gated";

        internal PreparationGatedWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-gated.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-gated.result", "v1"))
        {
        }

        internal TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim AllowPreparation { get; } = new(false);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            PreparationStarted.TrySetResult();
            AllowPreparation.Wait();
            return new SuccessfulPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class SuccessfulPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class FailingPreparedWork : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DurableEncodedPayload>(new InvalidOperationException("Simulated provider failure."));
    }

    private sealed class BlockingPreparedWork(
        TaskCompletionSource started,
        TaskCompletionSource<DurableEncodedPayload> complete) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return new ValueTask<DurableEncodedPayload>(complete.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class StartedFailingPreparedWork(
        TaskCompletionSource started,
        ManualResetEventSlim fail) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            fail.Wait(cancellationToken);
            return ValueTask.FromException<DurableEncodedPayload>(
                new InvalidOperationException("Simulated provider failure after start."));
        }
    }

    private sealed class SlowPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override async ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(850), cancellationToken);
            return result;
        }
    }

    private sealed class CompletingFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.runtime-pump.flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-runtime-pump-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('f', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Complete,
                input.NodeId,
                input.Context,
                nextNodeId: null,
                eventName: null,
                timeout: null,
                fault: null,
                activity: null));
    }

    private sealed class FailingFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.runtime-pump.flow-suspension";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-runtime-pump-flow-suspension-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('e', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DurableFlowEvaluationResult>(
                new InvalidOperationException("Simulated Flow evaluation failure."));
    }

    private sealed class RuntimeSchedulePayloadCodec(
        string contractName,
        string contractVersion,
        DurableDataClassification classification) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion { get; } = contractVersion;

        public DurableDataClassification Classification { get; } = classification;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) =>
            new(ContractName, ContractVersion, Classification, value, RetentionPolicyId);

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<byte[]>(value));

        public byte[] Decode(DurableEncodedPayload payload) => (byte[])DecodeObject(payload);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.ContractName != ContractName
                || payload.ContractVersion != ContractVersion
                || payload.Classification != Classification
                || payload.RetentionPolicyId != RetentionPolicyId)
            {
                throw new InvalidOperationException("The runtime Schedule test payload does not match its registered contract.");
            }

            return payload.Content.ToArray();
        }
    }
}
