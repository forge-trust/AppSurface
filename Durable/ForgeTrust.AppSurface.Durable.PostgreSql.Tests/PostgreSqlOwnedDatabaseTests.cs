using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlOwnedDatabaseTests
{
    private const string SentinelTable = "public.issue_931_ownership_sentinel";
    private const string SentinelValue = "fixture-owned-true";
    private const string RoleReplacementComment = "issue-931 role replacement sentinel true";

    [Fact]
    public async Task CreateOwnedAsync_WithheldCreateAcknowledgment_PreservesObservedDatabaseUntilTestCleanup()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        string? databaseName = null;
        uint? observedOid = null;

        try
        {
            var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                    maintenanceConnectionString,
                    afterCreate: async (name, cancellationToken) =>
                    {
                        databaseName = name;
                        await WriteSentinelAsync(anchor.ConnectionString, name, cancellationToken);
                        await using var observer = await OpenObserverAsync(maintenanceConnectionString, cancellationToken);
                        observedOid = await ReadDatabaseOidAsync(observer, name, cancellationToken);
                        throw new InvalidOperationException("Injected lost create acknowledgment.");
                    }));

            Assert.NotNull(databaseName);
            Assert.NotNull(observedOid);
            Assert.Contains("uncertain identity", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(databaseName, error.Message, StringComparison.Ordinal);
            Assert.True(error.Message.Contains("InvalidOperationException", StringComparison.Ordinal));

            await using var independentObserver = await OpenObserverAsync(maintenanceConnectionString);
            Assert.Equal(observedOid, await ReadDatabaseOidAsync(independentObserver, databaseName));
            Assert.Equal(SentinelValue, await ReadSentinelAsync(anchor.ConnectionString, databaseName));
        }
        finally
        {
            if (databaseName is not null && observedOid is not null)
            {
                await DropDatabaseAfterIdentityProofAsync(
                    anchor.ConnectionString,
                    maintenanceConnectionString,
                    databaseName,
                    observedOid.Value,
                    SentinelValue);
            }
        }
    }

    [Fact]
    public async Task CreateOwnedAsync_CanceledBeforeDispatch_DoesNotInvokeCreateObserver()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var createObserverCalled = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                GetMaintenanceConnectionString(anchor.ConnectionString),
                afterCreate: (_, _) =>
                {
                    createObserverCalled = true;
                    return ValueTask.CompletedTask;
                },
                cancellationToken: cancellation.Token));

        Assert.False(createObserverCalled);
    }

    [Fact]
    public async Task CreateOwnedAsync_CanceledAfterServerCreate_RegistersThenRemovesTheObservedDatabase()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        string? databaseName = null;
        uint? observedOid = null;

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                    maintenanceConnectionString,
                    afterCreate: async (name, cancellationToken) =>
                    {
                        databaseName = name;
                        await WriteSentinelAsync(anchor.ConnectionString, name, cancellationToken);
                        await using var observer = await OpenObserverAsync(maintenanceConnectionString, cancellationToken);
                        observedOid = await ReadDatabaseOidAsync(observer, name, cancellationToken);
                        cancellation.Cancel();
                    },
                    cancellationToken: cancellation.Token));

            Assert.NotNull(databaseName);
            Assert.NotNull(observedOid);
            await using var independentObserver = await OpenObserverAsync(maintenanceConnectionString);
            Assert.Null(await ReadDatabaseOidAsync(independentObserver, databaseName));
        }
        finally
        {
            if (databaseName is not null && observedOid is not null)
            {
                await DropDatabaseAfterIdentityProofAsync(
                    anchor.ConnectionString,
                    maintenanceConnectionString,
                    databaseName,
                    observedOid.Value,
                    SentinelValue);
            }
        }
    }

    [Fact]
    public async Task ActivateCloneAsync_PreservesDistinctIdentitiesAndIsolatesWrites()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        PostgreSqlIntegrationTestDatabase? template = null;
        PostgreSqlIntegrationTestDatabase? firstClone = null;
        PostgreSqlIntegrationTestDatabase? secondClone = null;

        try
        {
            template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                isTemplate: true);
            var templateStoreId = await template.PrepareTemplateAsync();

            firstClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                template: template);
            secondClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                template: template);

            var firstEpoch = Guid.NewGuid();
            var secondEpoch = Guid.NewGuid();
            var first = await firstClone.ActivateCloneAsync(
                templateStoreId,
                firstEpoch,
                "issue-931-tests",
                "first-clone");
            var second = await secondClone.ActivateCloneAsync(
                templateStoreId,
                secondEpoch,
                "issue-931-tests",
                "second-clone");
            var templateStatus = await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync();

            Assert.NotEqual(
                GetDatabaseName(firstClone.ConnectionString),
                GetDatabaseName(secondClone.ConnectionString));
            Assert.NotEqual(Guid.Empty, templateStatus.StoreId);
            Assert.NotEqual(Guid.Empty, first.StoreId);
            Assert.NotEqual(Guid.Empty, second.StoreId);
            Assert.NotEqual(templateStatus.StoreId, first.StoreId);
            Assert.NotEqual(templateStatus.StoreId, second.StoreId);
            Assert.NotEqual(first.StoreId, second.StoreId);
            Assert.Equal(firstEpoch, first.ActiveRuntimeEpoch);
            Assert.Equal(secondEpoch, second.ActiveRuntimeEpoch);
            Assert.NotEqual(first.ActiveRuntimeEpoch, second.ActiveRuntimeEpoch);

            await WriteSentinelAsync(firstClone.ConnectionString, GetDatabaseName(firstClone.ConnectionString));
            Assert.Equal(SentinelValue, await ReadSentinelAsync(firstClone.ConnectionString, GetDatabaseName(firstClone.ConnectionString)));
            await Assert.ThrowsAsync<PostgresException>(async () =>
                await ReadSentinelAsync(secondClone.ConnectionString, GetDatabaseName(secondClone.ConnectionString)));
        }
        finally
        {
            if (secondClone is not null)
            {
                await secondClone.DisposeAsync();
            }

            if (firstClone is not null)
            {
                await firstClone.DisposeAsync();
            }

            if (template is not null)
            {
                await template.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ActivateCloneAsync_FailureAfterRekey_RemovesOwnedClone()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        PostgreSqlIntegrationTestDatabase? template = null;
        PostgreSqlIntegrationTestDatabase? clone = null;
        string? cloneName = null;
        uint? cloneOid = null;
        Guid? rekeyedStoreId = null;
        Guid templateStoreId = Guid.Empty;

        try
        {
            template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                isTemplate: true);
            templateStoreId = await template.PrepareTemplateAsync();
            clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                template: template);
            cloneName = GetDatabaseName(clone.ConnectionString);
            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                cloneOid = await ReadDatabaseOidAsync(observer, cloneName);
            }

            var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await clone.ActivateCloneAsync(
                    templateStoreId,
                    Guid.NewGuid(),
                    "issue-931-tests",
                    "after-rekey-failure",
                    async cancellationToken =>
                    {
                        rekeyedStoreId = (await new PostgreSqlDurableRuntimeSchemaManager(clone.DataSource)
                            .GetStatusAsync(cancellationToken)).StoreId;
                        throw new InvalidOperationException("Injected post-rekey activation failure.");
                    }));

            Assert.NotNull(rekeyedStoreId);
            Assert.NotEqual(Guid.Empty, rekeyedStoreId);
            Assert.NotEqual(templateStoreId, rekeyedStoreId);
            Assert.Contains("after rekey", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("removed", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var independentObserver = await OpenObserverAsync(maintenanceConnectionString);
            Assert.Null(await ReadDatabaseOidAsync(independentObserver, cloneName));
        }
        finally
        {
            if (clone is not null)
            {
                await clone.DisposeAsync();
            }

            if (cloneName is not null && cloneOid is not null)
            {
                await DropDatabaseAfterIdentityProofAsync(
                    anchor.ConnectionString,
                    maintenanceConnectionString,
                    cloneName,
                    cloneOid.Value,
                    expectedSentinel: null);
            }

            if (template is not null)
            {
                await template.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ActivateCloneAsync_PostRekeyFailureAndCleanupFailure_PreservesBothFailuresAndReplacement()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        PostgreSqlIntegrationTestDatabase? template = null;
        PostgreSqlIntegrationTestDatabase? clone = null;
        string? cloneName = null;
        string? parkedCloneName = null;
        uint? cloneOid = null;
        uint? replacementOid = null;
        Guid? rekeyedStoreId = null;
        var originalCloneParked = false;
        var replacementCreated = false;
        Guid templateStoreId = Guid.Empty;

        try
        {
            template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                isTemplate: true);
            templateStoreId = await template.PrepareTemplateAsync();
            clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                anchor.ConnectionString,
                template: template);
            cloneName = GetDatabaseName(clone.ConnectionString);
            parkedCloneName = $"{cloneName}_old";

            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                cloneOid = await ReadDatabaseOidAsync(observer, cloneName);
            }

            var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() =>
                clone.ActivateCloneAsync(
                    templateStoreId,
                    Guid.NewGuid(),
                    "issue-931-tests",
                    "post-rekey-cleanup-failure",
                    async cancellationToken =>
                    {
                        rekeyedStoreId = (await new PostgreSqlDurableRuntimeSchemaManager(clone.DataSource)
                            .GetStatusAsync(cancellationToken)).StoreId;
                        clone.DataSource.Clear();
                        await using (var observer = await OpenObserverAsync(maintenanceConnectionString, cancellationToken))
                        {
                            Assert.Equal(cloneOid, await ReadDatabaseOidAsync(observer, cloneName, cancellationToken));
                            Assert.Equal(0, await ReadDatabaseSessionCountAsync(observer, cloneOid!.Value, cancellationToken));
                            await ExecuteNonQueryAsync(
                                observer,
                                $"ALTER DATABASE {QuoteIdentifier(cloneName)} RENAME TO {QuoteIdentifier(parkedCloneName)};");
                            originalCloneParked = true;
                            await ExecuteNonQueryAsync(observer, $"CREATE DATABASE {QuoteIdentifier(cloneName)};");
                            replacementCreated = true;
                            replacementOid = await ReadDatabaseOidAsync(observer, cloneName, cancellationToken);
                        }

                        await WriteSentinelAsync(anchor.ConnectionString, cloneName, cancellationToken);
                        throw new InvalidOperationException("Injected post-rekey activation failure.");
                    }).AsTask());

            Assert.NotNull(rekeyedStoreId);
            Assert.NotEqual(Guid.Empty, rekeyedStoreId);
            Assert.NotEqual(templateStoreId, rekeyedStoreId);
            Assert.Contains("after rekey", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("InvalidOperationException", error.Message, StringComparison.Ordinal);
            Assert.Contains("cleanup failed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ownership mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(cloneOid);
            Assert.NotNull(replacementOid);
            Assert.NotEqual(cloneOid, replacementOid);

            await using (var independentObserver = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(replacementOid, await ReadDatabaseOidAsync(independentObserver, cloneName));
                Assert.Equal(cloneOid, await ReadDatabaseOidAsync(independentObserver, parkedCloneName));
            }

            Assert.Equal(SentinelValue, await ReadSentinelAsync(anchor.ConnectionString, cloneName));
            await using var parkedDataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(anchor.ConnectionString)
            {
                Database = parkedCloneName,
                Pooling = false,
            }.ConnectionString);
            Assert.Equal(
                rekeyedStoreId,
                (await new PostgreSqlDurableRuntimeSchemaManager(parkedDataSource).GetStatusAsync()).StoreId);
            var templateStatus = await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync();
            Assert.Equal(templateStoreId, templateStatus.StoreId);
            Assert.Null(templateStatus.ActiveRuntimeEpoch);
        }
        finally
        {
            if (clone is not null)
            {
                try
                {
                    await clone.DisposeAsync();
                }
                catch (PostgreSqlFixtureException)
                {
                    // The registered name may still resolve to the deliberate replacement.
                }
            }

            if (replacementCreated && cloneName is not null)
            {
                if (replacementOid is null)
                {
                    await using var observer = await OpenObserverAsync(maintenanceConnectionString);
                    replacementOid = await ReadDatabaseOidAsync(observer, cloneName);
                }

                if (replacementOid is not null)
                {
                    await DropDatabaseAfterIdentityProofAsync(
                        anchor.ConnectionString,
                        maintenanceConnectionString,
                        cloneName,
                        replacementOid.Value,
                        SentinelValue);
                }
            }

            if (originalCloneParked && cloneName is not null && parkedCloneName is not null && cloneOid is not null)
            {
                await RenameDatabaseAfterIdentityProofAsync(
                    maintenanceConnectionString,
                    parkedCloneName,
                    cloneName,
                    cloneOid.Value);
            }

            if (clone is not null)
            {
                await clone.DisposeAsync();
            }

            if (template is not null)
            {
                await template.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_ClosesPrimaryAndAdditionalPoolsAndPreservesUnrelatedSession()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var owned = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(anchor.ConnectionString);
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        var ownedName = GetDatabaseName(owned.ConnectionString);
        uint? ownedOid = null;
        var primaryPid = 0;
        var additionalPid = 0;
        var unrelatedPid = 0;
        NpgsqlConnection? primary = null;
        NpgsqlConnection? additional = null;
        NpgsqlConnection? unrelated = null;
        Task? disposal = null;

        try
        {
            await WriteSentinelAsync(owned.ConnectionString, ownedName);
            var extraSource = owned.CreateDataSource();
            primary = await owned.DataSource.OpenConnectionAsync();
            additional = await extraSource.OpenConnectionAsync();
            unrelated = await anchor.DataSource.OpenConnectionAsync();
            primaryPid = await ReadBackendPidAsync(primary);
            additionalPid = await ReadBackendPidAsync(additional);
            unrelatedPid = await ReadBackendPidAsync(unrelated);

            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                ownedOid = await ReadDatabaseOidAsync(observer, ownedName);
                Assert.True(await BackendExistsAsync(observer, primaryPid));
                Assert.True(await BackendExistsAsync(observer, additionalPid));
                Assert.True(await BackendExistsAsync(observer, unrelatedPid));
            }

            disposal = owned.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(15));

            await using var independentObserver = await OpenObserverAsync(maintenanceConnectionString);
            Assert.Null(await ReadDatabaseOidAsync(independentObserver, ownedName));
            Assert.False(await BackendExistsAsync(independentObserver, primaryPid));
            Assert.False(await BackendExistsAsync(independentObserver, additionalPid));
            Assert.True(await BackendExistsAsync(independentObserver, unrelatedPid));
            await using var unrelatedProbe = new NpgsqlCommand("SELECT 1;", unrelated);
            Assert.Equal(1, await unrelatedProbe.ExecuteScalarAsync());
        }
        finally
        {
            if (primary is not null)
            {
                await primary.DisposeAsync();
            }

            if (additional is not null)
            {
                await additional.DisposeAsync();
            }

            if (disposal is null)
            {
                disposal = owned.DisposeAsync().AsTask();
            }

            if (!disposal.IsCompleted)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (disposal.IsFaulted)
            {
                try
                {
                    await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (PostgreSqlFixtureException)
                {
                    // The identity-checked manual cleanup below handles a test failure without touching replacements.
                }
            }

            if (ownedOid is not null)
            {
                await DropDatabaseAfterIdentityProofAsync(
                    anchor.ConnectionString,
                    maintenanceConnectionString,
                    ownedName,
                    ownedOid.Value,
                    SentinelValue);
            }

            if (unrelated is not null)
            {
                await unrelated.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_FailedDropIsBoundedAndDuplicateDisposalIsIdempotent()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var owned = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(anchor.ConnectionString);
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        var ownedName = GetDatabaseName(owned.ConnectionString);
        uint? ownedOid = null;

        try
        {
            await WriteSentinelAsync(owned.ConnectionString, ownedName);
            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                ownedOid = await ReadDatabaseOidAsync(observer, ownedName);
            }

            var expected = new InvalidOperationException("Injected bounded database-drop failure.");
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await owned.DisposeAsync(_ => ValueTask.FromException(expected))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(expected, actual);

            await using (var independentObserver = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(ownedOid, await ReadDatabaseOidAsync(independentObserver, ownedName));
                Assert.Equal(SentinelValue, await ReadSentinelAsync(anchor.ConnectionString, ownedName));
            }

            await Task.WhenAll(owned.DisposeAsync().AsTask(), owned.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(10));
            await using var finalObserver = await OpenObserverAsync(maintenanceConnectionString);
            Assert.Null(await ReadDatabaseOidAsync(finalObserver, ownedName));
        }
        finally
        {
            if (ownedOid is not null)
            {
                await DropDatabaseAfterIdentityProofAsync(
                    anchor.ConnectionString,
                    maintenanceConnectionString,
                    ownedName,
                    ownedOid.Value,
                    SentinelValue);
            }

            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_SameNameDatabaseReplacement_PreservesReplacementAndClockRoleDependencies()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        var owned = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(anchor.ConnectionString);
        var ownedName = GetDatabaseName(owned.ConnectionString);
        var parkedDatabaseName = $"{ownedName}_previous";
        string originalRoleName = string.Empty;
        uint? originalDatabaseOid = null;
        uint? originalRoleOid = null;
        uint? originalFunctionOid = null;
        uint? replacementDatabaseOid = null;
        string? roleDependencyFunctionName = null;
        uint? roleDependencyFunctionOid = null;
        var originalDatabaseRenamed = false;
        var replacementDatabaseCreated = false;

        try
        {
            await WriteSentinelAsync(owned.ConnectionString, ownedName);
            await new PostgreSqlDurableRuntimeSchemaManager(owned.DataSource).ApplyAsync();
            await owned.SetExecutionTimeAsync(DateTimeOffset.UtcNow);
            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                originalDatabaseOid = await ReadDatabaseOidAsync(observer, ownedName);
            }

            (originalRoleName, originalRoleOid, originalFunctionOid) = await ReadClockRoleIdentityAsync(owned.ConnectionString);
            roleDependencyFunctionName = await CreateRoleDependencyAsync(maintenanceConnectionString, originalRoleName);
            roleDependencyFunctionOid = await ReadRoleDependencyOidAsync(
                maintenanceConnectionString,
                originalRoleName,
                roleDependencyFunctionName);

            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(originalDatabaseOid, await ReadDatabaseOidAsync(observer, ownedName));
                owned.DataSource.Clear();
                Assert.Equal(0, await ReadDatabaseSessionCountAsync(observer, originalDatabaseOid!.Value));
                await ExecuteNonQueryAsync(observer, $"ALTER DATABASE {QuoteIdentifier(ownedName)} RENAME TO {QuoteIdentifier(parkedDatabaseName)};");
                originalDatabaseRenamed = true;
                await ExecuteNonQueryAsync(observer, $"CREATE DATABASE {QuoteIdentifier(ownedName)};");
                replacementDatabaseCreated = true;
                replacementDatabaseOid = await ReadDatabaseOidAsync(observer, ownedName);
            }

            await WriteSentinelAsync(anchor.ConnectionString, ownedName);
            Assert.NotEqual(originalDatabaseOid, replacementDatabaseOid);

            var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => owned.DisposeAsync().AsTask());
            Assert.Contains("ownership mismatch", error.Message, StringComparison.OrdinalIgnoreCase);

            await using (var independentObserver = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(replacementDatabaseOid, await ReadDatabaseOidAsync(independentObserver, ownedName));
                Assert.Equal(originalDatabaseOid, await ReadDatabaseOidAsync(independentObserver, parkedDatabaseName));
                Assert.Equal(originalRoleOid, await ReadRoleOidAsync(independentObserver, originalRoleName));
            }

            Assert.Equal(SentinelValue, await ReadSentinelAsync(anchor.ConnectionString, ownedName));
            Assert.Equal(originalRoleOid, await ReadClockFunctionOwnerOidAsync(anchor.ConnectionString, parkedDatabaseName));
            Assert.Equal(
                roleDependencyFunctionOid,
                await ReadRoleDependencyOidAsync(
                    maintenanceConnectionString,
                    originalRoleName,
                    roleDependencyFunctionName!));
        }
        finally
        {
            try
            {
                await owned.DisposeAsync();
            }
            catch (PostgreSqlFixtureException)
            {
                // The registered name may still resolve to the deliberate replacement. Restore its identity below.
            }

            if (replacementDatabaseCreated)
            {
                if (replacementDatabaseOid is null)
                {
                    await using var observer = await OpenObserverAsync(maintenanceConnectionString);
                    replacementDatabaseOid = await ReadDatabaseOidAsync(observer, ownedName);
                }

                if (replacementDatabaseOid is not null)
                {
                    await DropDatabaseAfterIdentityProofAsync(
                        anchor.ConnectionString,
                        maintenanceConnectionString,
                        ownedName,
                        replacementDatabaseOid.Value,
                        SentinelValue);
                }
            }

            if (roleDependencyFunctionName is not null
                && roleDependencyFunctionOid is not null
                && originalRoleOid is not null)
            {
                await DropRoleDependencyAfterIdentityProofAsync(
                    maintenanceConnectionString,
                    originalRoleName,
                    roleDependencyFunctionName,
                    roleDependencyFunctionOid.Value,
                    originalRoleOid.Value);
            }

            if (originalDatabaseRenamed)
            {
                await RenameDatabaseAfterIdentityProofAsync(
                    maintenanceConnectionString,
                    parkedDatabaseName,
                    ownedName,
                    originalDatabaseOid!.Value);
            }

            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_SameNameClockRoleReplacement_PreservesRoleDependencyAndDatabaseSentinel()
    {
        await using var anchor = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = GetMaintenanceConnectionString(anchor.ConnectionString);
        var owned = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(anchor.ConnectionString);
        var ownedName = GetDatabaseName(owned.ConnectionString);
        string roleName = string.Empty;
        string parkedRoleName = string.Empty;
        uint? databaseOid = null;
        uint? originalRoleOid = null;
        uint? functionOid = null;
        uint? replacementRoleOid = null;
        string? roleDependencyFunctionName = null;
        uint? roleDependencyFunctionOid = null;
        var originalRoleRenamed = false;
        var replacementRoleCreated = false;

        try
        {
            await WriteSentinelAsync(owned.ConnectionString, ownedName);
            await new PostgreSqlDurableRuntimeSchemaManager(owned.DataSource).ApplyAsync();
            await owned.SetExecutionTimeAsync(DateTimeOffset.UtcNow);
            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                databaseOid = await ReadDatabaseOidAsync(observer, ownedName);
            }

            (roleName, originalRoleOid, functionOid) = await ReadClockRoleIdentityAsync(owned.ConnectionString);
            parkedRoleName = $"{roleName}_old";
            roleDependencyFunctionName = await CreateRoleDependencyAsync(maintenanceConnectionString, roleName);
            roleDependencyFunctionOid = await ReadRoleDependencyOidAsync(
                maintenanceConnectionString,
                roleName,
                roleDependencyFunctionName);

            await using (var observer = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(originalRoleOid, await ReadRoleOidAsync(observer, roleName));
                Assert.Null(await ReadRoleOidAsync(observer, parkedRoleName));
                await ExecuteNonQueryAsync(
                    observer,
                    $"ALTER ROLE {QuoteIdentifier(roleName)} RENAME TO {QuoteIdentifier(parkedRoleName)};");
                originalRoleRenamed = true;
                await ExecuteNonQueryAsync(observer, $"CREATE ROLE {QuoteIdentifier(roleName)} NOLOGIN;");
                replacementRoleCreated = true;
                await ExecuteNonQueryAsync(
                    observer,
                    $"COMMENT ON ROLE {QuoteIdentifier(roleName)} IS '{RoleReplacementComment}';");
                replacementRoleOid = await ReadRoleOidAsync(observer, roleName);
                Assert.NotEqual(originalRoleOid, replacementRoleOid);
            }

            var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => owned.DisposeAsync().AsTask());
            Assert.Contains("role", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ownership mismatch", error.Message, StringComparison.OrdinalIgnoreCase);

            await using (var independentObserver = await OpenObserverAsync(maintenanceConnectionString))
            {
                Assert.Equal(databaseOid, await ReadDatabaseOidAsync(independentObserver, ownedName));
                Assert.Equal(replacementRoleOid, await ReadRoleOidAsync(independentObserver, roleName));
                Assert.Equal(RoleReplacementComment, await ReadRoleCommentAsync(independentObserver, roleName));
                Assert.Equal(originalRoleOid, await ReadRoleOidAsync(independentObserver, parkedRoleName));
            }

            Assert.Equal(SentinelValue, await ReadSentinelAsync(owned.ConnectionString, ownedName));
            Assert.Equal(originalRoleOid, await ReadClockFunctionOwnerOidAsync(owned.ConnectionString, ownedName));
            Assert.Equal(functionOid, await ReadClockFunctionOidAsync(owned.ConnectionString, ownedName));
            Assert.Equal(
                roleDependencyFunctionOid,
                await ReadRoleDependencyOidAsync(
                    maintenanceConnectionString,
                    parkedRoleName,
                    roleDependencyFunctionName!));
        }
        finally
        {
            if (roleDependencyFunctionName is not null && roleDependencyFunctionOid is not null && originalRoleOid is not null)
            {
                await DropRoleDependencyAfterIdentityProofAsync(
                    maintenanceConnectionString,
                    originalRoleRenamed ? parkedRoleName : roleName,
                    roleDependencyFunctionName,
                    roleDependencyFunctionOid.Value,
                    originalRoleOid.Value);
            }

            if (replacementRoleCreated)
            {
                if (replacementRoleOid is null)
                {
                    await using var observer = await OpenObserverAsync(maintenanceConnectionString);
                    replacementRoleOid = await ReadRoleOidAsync(observer, roleName);
                }

                if (replacementRoleOid is not null)
                {
                    await DropRoleAfterIdentityProofAsync(
                        maintenanceConnectionString,
                        roleName,
                        replacementRoleOid.Value);
                }
            }

            if (originalRoleRenamed)
            {
                await RenameRoleAfterIdentityProofAsync(
                    maintenanceConnectionString,
                    parkedRoleName,
                    roleName,
                    originalRoleOid!.Value);
            }

            await owned.DisposeAsync();
        }
    }

    private static string GetMaintenanceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

    private static string GetDatabaseName(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Database
        ?? throw new InvalidOperationException("The owned fixture connection string has no database name.");

    private static async ValueTask<NpgsqlConnection> OpenObserverAsync(
        string maintenanceConnectionString,
        CancellationToken cancellationToken = default)
    {
        var observer = new NpgsqlConnection(maintenanceConnectionString);
        try
        {
            await observer.OpenAsync(cancellationToken);
            return observer;
        }
        catch
        {
            await observer.DisposeAsync();
            throw;
        }
    }

    private static async Task<uint?> ReadDatabaseOidAsync(
        NpgsqlConnection observer,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_database WHERE datname = @name;",
            observer);
        command.Parameters.AddWithValue("name", databaseName);
        return (uint?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task WriteSentinelAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var targetSettings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var target = new NpgsqlConnection(targetSettings.ConnectionString);
        await target.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(target, $"CREATE TABLE {SentinelTable} (value text NOT NULL);");
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {SentinelTable} (value) VALUES (@value);",
            target);
        insert.Parameters.AddWithValue("value", SentinelValue);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadSentinelAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var targetSettings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var target = new NpgsqlConnection(targetSettings.ConnectionString);
        await target.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT value FROM {SentinelTable};", target);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task DropDatabaseAfterIdentityProofAsync(
        string connectionString,
        string maintenanceConnectionString,
        string databaseName,
        uint expectedOid,
        string? expectedSentinel)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        var observedOid = await ReadDatabaseOidAsync(observer, databaseName);
        if (observedOid is null)
        {
            return;
        }

        if (observedOid != expectedOid)
        {
            throw new InvalidOperationException("Cleanup preserved a database whose catalog identity changed.");
        }

        if (expectedSentinel is not null)
        {
            Assert.Equal(expectedSentinel, await ReadSentinelAsync(connectionString, databaseName));
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE {QuoteIdentifier(databaseName)} WITH (FORCE);", observer);
        await drop.ExecuteNonQueryAsync();
        Assert.Null(await ReadDatabaseOidAsync(observer, databaseName));
    }

    private static string QuoteIdentifier(string identifier)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(identifier);
    }

    private static async Task<int> ReadBackendPidAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid();", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> BackendExistsAsync(NpgsqlConnection observer, int backendPid)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid = @pid);",
            observer);
        command.Parameters.AddWithValue("pid", backendPid);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReadDatabaseSessionCountAsync(
        NpgsqlConnection observer,
        uint databaseOid,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE datid = @database_oid AND pid <> pg_catalog.pg_backend_pid();",
            observer);
        command.Parameters.AddWithValue("database_oid", NpgsqlTypes.NpgsqlDbType.Oid, databaseOid);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(string Name, uint RoleOid, uint FunctionOid)> ReadClockRoleIdentityAsync(
        string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT role.rolname, role.oid, function.oid
            FROM pg_catalog.pg_roles AS role
            JOIN pg_catalog.pg_proc AS function ON function.proowner = role.oid
            WHERE function.oid = pg_catalog.to_regprocedure('appsurface_durable.work_execution_now()');
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The owned clock role and function should be registered.");
        return (reader.GetString(0), reader.GetFieldValue<uint>(1), reader.GetFieldValue<uint>(2));
    }

    private static async Task<uint?> ReadRoleOidAsync(NpgsqlConnection observer, string roleName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @name;",
            observer);
        command.Parameters.AddWithValue("name", roleName);
        return (uint?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ReadRoleCommentAsync(NpgsqlConnection observer, string roleName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.shobj_description(oid, 'pg_authid') FROM pg_catalog.pg_roles WHERE rolname = @name;",
            observer);
        command.Parameters.AddWithValue("name", roleName);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<uint?> ReadClockFunctionOwnerOidAsync(string connectionString, string databaseName)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT proowner FROM pg_catalog.pg_proc WHERE oid = pg_catalog.to_regprocedure('appsurface_durable.work_execution_now()');",
            connection);
        return (uint?)await command.ExecuteScalarAsync();
    }

    private static async Task<uint?> ReadClockFunctionOidAsync(string connectionString, string databaseName)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_proc WHERE oid = pg_catalog.to_regprocedure('appsurface_durable.work_execution_now()');",
            connection);
        return (uint?)await command.ExecuteScalarAsync();
    }

    private static async Task<string> CreateRoleDependencyAsync(string maintenanceConnectionString, string roleName)
    {
        var functionName = $"issue_931_role_dependency_{Guid.NewGuid():N}";
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        await ExecuteNonQueryAsync(
            observer,
            $"CREATE FUNCTION public.{QuoteIdentifier(functionName)}() RETURNS boolean LANGUAGE sql AS 'SELECT true'; " +
            $"ALTER FUNCTION public.{QuoteIdentifier(functionName)}() OWNER TO {QuoteIdentifier(roleName)};");
        return functionName;
    }

    private static async Task<uint?> ReadRoleDependencyOidAsync(
        string maintenanceConnectionString,
        string roleName,
        string functionName)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT function.oid
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = function.pronamespace
            JOIN pg_catalog.pg_roles AS owner ON owner.oid = function.proowner
            WHERE namespace.nspname = 'public' AND function.proname = @function AND owner.rolname = @role;
            """,
            observer);
        command.Parameters.AddWithValue("function", functionName);
        command.Parameters.AddWithValue("role", roleName);
        return (uint?)await command.ExecuteScalarAsync();
    }

    private static async Task DropRoleDependencyAfterIdentityProofAsync(
        string maintenanceConnectionString,
        string roleName,
        string functionName,
        uint expectedFunctionOid,
        uint expectedRoleOid)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        var actualRoleOid = await ReadRoleOidAsync(observer, roleName);
        if (actualRoleOid != expectedRoleOid)
        {
            throw new InvalidOperationException("Cleanup refused to touch a role dependency whose owner identity changed.");
        }

        var actualFunctionOid = await ReadRoleDependencyOidAsync(
            maintenanceConnectionString,
            roleName,
            functionName);
        if (actualFunctionOid is null)
        {
            return;
        }

        if (actualFunctionOid != expectedFunctionOid)
        {
            throw new InvalidOperationException("Cleanup refused to touch a role dependency whose function identity changed.");
        }

        await ExecuteNonQueryAsync(observer, $"DROP FUNCTION public.{QuoteIdentifier(functionName)}();");
        Assert.Null(await ReadRoleDependencyOidAsync(maintenanceConnectionString, roleName, functionName));
    }

    private static async Task DropRoleAfterIdentityProofAsync(
        string maintenanceConnectionString,
        string roleName,
        uint expectedRoleOid)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        var actualRoleOid = await ReadRoleOidAsync(observer, roleName);
        if (actualRoleOid is null)
        {
            return;
        }

        if (actualRoleOid != expectedRoleOid)
        {
            throw new InvalidOperationException("Cleanup refused to drop a role whose catalog identity changed.");
        }

        await ExecuteNonQueryAsync(observer, $"DROP ROLE {QuoteIdentifier(roleName)};");
        Assert.Null(await ReadRoleOidAsync(observer, roleName));
    }

    private static async Task RenameRoleAfterIdentityProofAsync(
        string maintenanceConnectionString,
        string sourceName,
        string targetName,
        uint expectedRoleOid)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        var sourceOid = await ReadRoleOidAsync(observer, sourceName);
        if (sourceOid != expectedRoleOid)
        {
            throw new InvalidOperationException("Cleanup refused to rename a role whose catalog identity changed.");
        }

        if (await ReadRoleOidAsync(observer, targetName) is not null)
        {
            throw new InvalidOperationException("Cleanup refused to rename a role over an existing role.");
        }

        await ExecuteNonQueryAsync(
            observer,
            $"ALTER ROLE {QuoteIdentifier(sourceName)} RENAME TO {QuoteIdentifier(targetName)};");
        Assert.Equal(expectedRoleOid, await ReadRoleOidAsync(observer, targetName));
    }

    private static async Task RenameDatabaseAfterIdentityProofAsync(
        string maintenanceConnectionString,
        string sourceName,
        string targetName,
        uint expectedDatabaseOid)
    {
        await using var observer = await OpenObserverAsync(maintenanceConnectionString);
        var sourceOid = await ReadDatabaseOidAsync(observer, sourceName);
        if (sourceOid != expectedDatabaseOid)
        {
            throw new InvalidOperationException("Cleanup refused to rename a database whose catalog identity changed.");
        }

        if (await ReadDatabaseOidAsync(observer, targetName) is not null)
        {
            throw new InvalidOperationException("Cleanup refused to rename a database over an existing database.");
        }

        await ExecuteNonQueryAsync(
            observer,
            $"ALTER DATABASE {QuoteIdentifier(sourceName)} RENAME TO {QuoteIdentifier(targetName)};");
        Assert.Equal(expectedDatabaseOid, await ReadDatabaseOidAsync(observer, targetName));
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
