using ForgeTrust.AppSurface.Durable;
using Npgsql;
using PolicyTests = ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHandoffRejectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task U7_CurrentPermitAllowsLegacyAndOptedInCallback(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync(legacy);

        var result = await InvokeAsync(fixture);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HandoffPrepared);
        Assert.Equal(1, await fixture.ReadLedgerCountAsync());
    }

    [Theory]
    [InlineData("live", null)]
    [InlineData("deadline", DurableProblemCodes.ExecutionDeadlineReached)]
    [InlineData("recorded-deadline", DurableProblemCodes.ExecutionDeadlineReached)]
    [InlineData("lease-and-deadline", DurableProblemCodes.LeaseLost)]
    public void U7_TimeProblemUsesLeaseFirstAndPermanentDeadlineFacts(string scenario, string? expected)
    {
        var now = PolicyTests.Anchor;
        PostgreSqlDurableHandoffFacts facts = scenario switch
        {
            "live" => new(now.AddMinutes(1), null, false, false),
            "deadline" => new(now.AddMinutes(1), now, false, true),
            "recorded-deadline" => new(now.AddMinutes(1), now.AddMinutes(5), true, true),
            "lease-and-deadline" => new(now, now, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(expected, PostgreSqlDurableHandoffFacts.TimeProblem(facts, now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task U7_LegitimateRenewalMayAdvanceCurrentRevisionAndExpiry(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync(legacy);
        Assert.NotNull(await fixture.Lab.Store.RenewLeaseAsync(fixture.Permit.Claim));

        var result = await InvokeAsync(fixture);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, await fixture.ReadLedgerCountAsync());
    }

    [Fact]
    public async Task N1_WrongPhysicalDatabaseThrowsBeforeCallback()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: false);
        await using var other = await PolicyTests.Lab.CreateAsync();
        await CreateLedgerAsync(other.Database.DataSource);

        var exception = await AssertExceptionAsync<InvalidOperationException>(fixture,
            transactionDatabase: other.Database);

        Assert.Contains("host, port, and database", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var count = other.Database.DataSource.CreateCommand(
            "SELECT count(*) FROM public.handoff_rejection_ledger;");
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task N2_WrongStoreIdUsesExistingCodedExceptionBeforeCallback()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: false);
        var fence = new PostgreSqlDurableWorkHandoffFence(fixture.Lab.Database.DataSource,
            new(fixture.Lab.Epoch, Guid.NewGuid()));

        var exception = await AssertExceptionAsync<InvalidOperationException>(fixture, fence);

        Assert.Contains(DurableProblemCodes.StoreIdentityMismatch, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task N3_UnsupportedSchemaUsesSafeSchemaExceptionBeforeCallback()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: false);
        await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.store_metadata SET schema_version=schema_version-1 WHERE singleton;");

        var exception = await AssertExceptionAsync<DurableRuntimeSchemaException>(fixture);

        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, exception.Status.Compatibility);
    }

    [Fact]
    public async Task N4_WrongConfiguredEpochUsesExistingCodedExceptionBeforeCallback()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: false);
        var fence = new PostgreSqlDurableWorkHandoffFence(fixture.Lab.Database.DataSource,
            new(Guid.NewGuid(), fixture.StoreId));

        var exception = await AssertExceptionAsync<InvalidOperationException>(fixture, fence);

        Assert.Contains(DurableProblemCodes.RecoveryEpochRequired, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "scope-disabled", DurableProblemCodes.ScopeDisabled)]
    [InlineData(false, "scope-generation", DurableProblemCodes.ScopeGenerationConflict)]
    [InlineData(false, "work-scope-generation", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "activity", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "attempt", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "lease-generation", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "lease-owner", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "work-epoch", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "scope-disabled", DurableProblemCodes.ScopeDisabled)]
    [InlineData(true, "scope-generation", DurableProblemCodes.ScopeGenerationConflict)]
    [InlineData(true, "work-scope-generation", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "activity", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "attempt", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "lease-generation", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "lease-owner", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "work-epoch", DurableProblemCodes.ClaimLost)]
    public async Task N5ToN8_ScopeAndWorkIdentityFactsRefuseWithExactCode(bool legacy, string fault, string code)
    {
        await using var fixture = await Fixture.CreateAsync(legacy);
        var claim = fixture.Permit.Claim;
        PostgreSqlEffectPermit? permit = null;
        switch (fault)
        {
            case "scope-disabled":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.scope SET state='disabled' WHERE scope_id=@scope_id;",
                    ("scope_id", claim.ScopeId.Value));
                break;
            case "scope-generation":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.scope SET generation=generation+1 WHERE scope_id=@scope_id;",
                    ("scope_id", claim.ScopeId.Value));
                break;
            case "work-scope-generation":
                await fixture.UpdateWorkAsync("scope_generation=scope_generation+1");
                break;
            case "activity":
                await fixture.UpdateWorkAsync("activity_id='wrong-activity'");
                break;
            case "attempt":
                await fixture.UpdateWorkAsync("attempt_number=attempt_number+1");
                break;
            case "lease-generation":
                await fixture.UpdateWorkAsync("lease_generation=lease_generation+1");
                break;
            case "lease-owner":
                await fixture.UpdateWorkAsync("lease_owner='wrong-owner'");
                break;
            case "work-epoch":
                await fixture.UpdateWorkAsync("runtime_epoch=gen_random_uuid()");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        await AssertRefusedAsync(fixture, code, permit);
    }

    [Theory]
    [InlineData(false, "expired-lease", DurableProblemCodes.LeaseLost)]
    [InlineData(true, "expired-lease", DurableProblemCodes.LeaseLost)]
    [InlineData(false, "missing-lease", DurableProblemCodes.LeaseLost)]
    [InlineData(true, "missing-lease", DurableProblemCodes.LeaseLost)]
    [InlineData(false, "missing-lease-owner", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "missing-lease-owner", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "missing-permit", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "missing-permit", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "permit-status", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "permit-status", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "permit-identity", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "permit-identity", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "dispatch-id", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "dispatch-id", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "dispatch-state", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "dispatch-state", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "dispatch-revision", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "dispatch-revision", DurableProblemCodes.ClaimLost)]
    [InlineData(false, "dispatch-missing", DurableProblemCodes.ClaimLost)]
    [InlineData(true, "dispatch-missing", DurableProblemCodes.ClaimLost)]
    public async Task N9ToN10_LeaseDispatchAndPermitFactsRefuse(bool legacy, string fault, string code)
    {
        await using var fixture = await Fixture.CreateAsync(legacy);
        var permit = fixture.Permit;
        var claim = permit.Claim;
        switch (fault)
        {
            case "expired-lease":
                await fixture.ExpireLeaseAsync();
                break;
            case "missing-lease":
                await fixture.UpdateWorkAsync("lease_expires_at=NULL");
                break;
            case "missing-lease-owner":
                await fixture.UpdateWorkAsync("lease_owner=NULL");
                break;
            case "missing-permit":
                permit = permit with { PermitId = Guid.NewGuid() };
                break;
            case "permit-status":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.effect_permit SET status='proven_no_effect' WHERE permit_id=@permit_id;",
                    ("permit_id", permit.PermitId));
                break;
            case "permit-identity":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.effect_permit SET activity_id='wrong-permit-activity' WHERE permit_id=@permit_id;",
                    ("permit_id", permit.PermitId));
                break;
            case "dispatch-id":
                permit = permit with { Claim = claim with { DispatchId = Guid.NewGuid() } };
                break;
            case "dispatch-state":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.dispatch SET state='available' WHERE dispatch_id=@dispatch_id;",
                    ("dispatch_id", claim.DispatchId));
                break;
            case "dispatch-revision":
                await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.dispatch SET expected_revision=expected_revision+1 WHERE dispatch_id=@dispatch_id;",
                    ("dispatch_id", claim.DispatchId));
                break;
            case "dispatch-missing":
                await fixture.ExecuteNativeAsync("DELETE FROM appsurface_durable.dispatch WHERE dispatch_id=@dispatch_id;",
                    ("dispatch_id", claim.DispatchId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        await AssertRefusedAsync(fixture, code, permit);
    }

    [Theory]
    [InlineData("missing-work", DurableProblemCodes.WorkNotFound)]
    [InlineData("missing-scope-work", DurableProblemCodes.ScopeNotFound)]
    [InlineData("terminal-succeeded", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("terminal-succeeded-after-cancel", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("terminal-failed", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("terminal-canceled-before-effect", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("canceled", DurableProblemCodes.ClaimLost)]
    [InlineData("deadline", DurableProblemCodes.ExecutionDeadlineReached)]
    public async Task N10_MissingTerminalCanceledAndDeadlineFactsRefuse(string fault, string code)
    {
        DateTimeOffset? deadline = fault == "deadline" ? PolicyTests.Anchor.AddMinutes(2) : null;
        await using var fixture = await Fixture.CreateAsync(legacy: false, deadline);
        switch (fault)
        {
            case "missing-work":
                await fixture.DeleteWorkAndDependentsAsync(deleteScope: false);
                break;
            case "missing-scope-work":
                await fixture.DeleteWorkAndDependentsAsync(deleteScope: true);
                break;
            case "terminal-succeeded":
                await fixture.UpdateWorkAsync("state='succeeded', terminal_at=clock_timestamp(), terminal_code='tests.succeeded'");
                break;
            case "terminal-succeeded-after-cancel":
                await fixture.UpdateWorkAsync("state='succeeded_after_cancel_requested', cancellation_requested_at=clock_timestamp(), terminal_at=clock_timestamp(), terminal_code='tests.succeeded_after_cancel'");
                break;
            case "terminal-failed":
                await fixture.UpdateWorkAsync("state='failed', terminal_at=clock_timestamp(), terminal_code='tests.failed'");
                break;
            case "terminal-canceled-before-effect":
                await fixture.UpdateWorkAsync("state='canceled_before_effect', cancellation_requested_at=clock_timestamp(), terminal_at=clock_timestamp(), terminal_code='canceled_before_effect'");
                break;
            case "canceled":
                await fixture.UpdateWorkAsync("state='cancel_pending', cancellation_requested_at=COALESCE(cancellation_requested_at, clock_timestamp())");
                break;
            case "deadline":
                await fixture.Lab.Database.SetExecutionTimeAsync(deadline!.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        await AssertRefusedAsync(fixture, code);
    }

    [Theory]
    [InlineData("identity-expired", DurableProblemCodes.ClaimLost)]
    [InlineData("missing-scope-work", DurableProblemCodes.ScopeNotFound)]
    [InlineData("terminal-expired", DurableProblemCodes.AlreadyTerminal)]
    [InlineData("canceled-expired", DurableProblemCodes.ClaimLost)]
    [InlineData("lease-deadline-expired", DurableProblemCodes.LeaseLost)]
    public async Task U7_MultiFaultsReturnTheFirstApplicableCode(string faults, string code)
    {
        DateTimeOffset? deadline = faults == "lease-deadline-expired" ? PolicyTests.Anchor.AddMinutes(2) : null;
        await using var fixture = await Fixture.CreateAsync(legacy: false, deadline);
        switch (faults)
        {
            case "identity-expired":
                await fixture.Lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddHours(1));
                await AssertRefusedAsync(fixture, code,
                    fixture.Permit with { Claim = fixture.Permit.Claim with { ActivityId = "wrong-activity" } });
                return;
            case "missing-scope-work":
                await fixture.DeleteWorkAndDependentsAsync(deleteScope: true);
                break;
            case "terminal-expired":
                await fixture.UpdateWorkAsync("state='failed', terminal_at=clock_timestamp(), terminal_code='tests.failed', lease_expires_at=appsurface_durable.work_execution_now()-interval '1 second'");
                break;
            case "canceled-expired":
                await fixture.UpdateWorkAsync("state='cancel_pending', cancellation_requested_at=COALESCE(cancellation_requested_at, clock_timestamp()), lease_expires_at=appsurface_durable.work_execution_now()-interval '1 second'");
                break;
            case "lease-deadline-expired":
                await fixture.Lab.Database.SetExecutionTimeAsync(deadline!.Value.AddHours(1));
                await fixture.ExpireLeaseAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(faults));
        }

        await AssertRefusedAsync(fixture, code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseStopsBeforeMissingDispatchAndPermitAreRead(bool legacy)
    {
        await using var fixture = await Fixture.CreateAsync(legacy);
        await fixture.ExpireLeaseAsync();
        await fixture.ExecuteNativeAsync("DELETE FROM appsurface_durable.effect_permit WHERE permit_id=@permit_id;",
            ("permit_id", fixture.Permit.PermitId));
        await fixture.ExecuteNativeAsync("DELETE FROM appsurface_durable.dispatch WHERE dispatch_id=@dispatch_id;",
            ("dispatch_id", fixture.Permit.Claim.DispatchId));

        await AssertRefusedAsync(fixture, DurableProblemCodes.LeaseLost);
    }

    [Fact]
    public async Task OptedInDeadlineStopsBeforeMissingDispatchAndPermitAreRead()
    {
        var deadline = PolicyTests.Anchor.AddMinutes(2);
        await using var fixture = await Fixture.CreateAsync(legacy: false, deadline);
        await fixture.Lab.Database.SetExecutionTimeAsync(deadline);
        await fixture.ExecuteNativeAsync("DELETE FROM appsurface_durable.effect_permit WHERE permit_id=@permit_id;",
            ("permit_id", fixture.Permit.PermitId));
        await fixture.ExecuteNativeAsync("DELETE FROM appsurface_durable.dispatch WHERE dispatch_id=@dispatch_id;",
            ("dispatch_id", fixture.Permit.Claim.DispatchId));

        await AssertRefusedAsync(fixture, DurableProblemCodes.ExecutionDeadlineReached);
    }

    [Fact]
    public async Task RecordedDeadlineRemainsARefusalAfterExecutionClockMovesBackward()
    {
        var deadline = PolicyTests.Anchor.AddMinutes(2);
        await using var fixture = await Fixture.CreateAsync(legacy: false, deadline);
        await fixture.Lab.Database.SetExecutionTimeAsync(deadline);
        Assert.False(await fixture.Lab.Store.TryAdmitInvocationAsync(fixture.Permit));
        Assert.True(await fixture.Lab.ScalarAsync<bool>("SELECT execution_deadline_reached_at IS NOT NULL FROM appsurface_durable.work;"));
        Assert.Equal("effect_permitted", await fixture.Lab.TextAsync("state"));
        await fixture.Lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor);

        await AssertRefusedAsync(fixture, DurableProblemCodes.ExecutionDeadlineReached);
    }

    [Fact]
    public async Task ExactPermitLookupIgnoresLongHistoricalPermitRowsAndUsesPrimaryKey()
    {
        await using var fixture = await Fixture.CreateAsync(legacy: false);
        var claim = fixture.Permit.Claim;
        const int currentAttempt = 101;
        await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.work SET attempt_number=@attempt, lease_generation=@generation, revision=revision+1 WHERE scope_id=@scope_id AND work_id=@work_id;",
            ("attempt", currentAttempt), ("generation", (long)currentAttempt), ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
        await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.effect_permit SET attempt_number=@attempt, lease_generation=@generation WHERE permit_id=@permit_id;",
            ("attempt", currentAttempt), ("generation", (long)currentAttempt), ("permit_id", fixture.Permit.PermitId));
        await fixture.ExecuteNativeAsync("UPDATE appsurface_durable.dispatch SET expected_revision=(SELECT revision FROM appsurface_durable.work WHERE scope_id=@scope_id AND work_id=@work_id) WHERE dispatch_id=@dispatch_id;",
            ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value), ("dispatch_id", claim.DispatchId));
        await fixture.ExecuteNativeAsync("""
            INSERT INTO appsurface_durable.effect_permit
                (permit_id, scope_id, work_id, activity_id, attempt_number, lease_generation, scope_generation, runtime_epoch, status)
            SELECT gen_random_uuid(), @scope_id, @work_id, 'historical-bad-permit', attempt_number, attempt_number,
                   @scope_generation, @runtime_epoch, 'proven_no_effect'
            FROM generate_series(1, 100) AS history(attempt_number);
            """,
            ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value),
            ("scope_generation", claim.ScopeGeneration), ("runtime_epoch", claim.RuntimeEpoch));
        var currentPermit = fixture.Permit with
        {
            Claim = claim with { AttemptNumber = currentAttempt, LeaseGeneration = currentAttempt },
        };

        var plan = await fixture.ExplainPermitLookupAsync(fixture.Permit.PermitId);
        Assert.Contains("effect_permit_pkey", plan, StringComparison.Ordinal);
        Assert.True((await InvokeAsync(fixture, currentPermit)).IsSuccess);
        Assert.Equal(1, await fixture.ReadLedgerCountAsync());
    }

    private static async Task<DurableOperationResult<DurableWorkHandoffResult>> InvokeAsync(
        Fixture fixture,
        PostgreSqlEffectPermit? permit = null,
        PostgreSqlDurableWorkHandoffFence? fence = null,
        PostgreSqlIntegrationTestDatabase? transactionDatabase = null)
    {
        var selectedFence = fence ?? fixture.Fence;
        await using var connection = await (transactionDatabase ?? fixture.Lab.Database).DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = selectedFence.EnterInvocation(permit ?? fixture.Permit);
        var callbackCount = 0;
        var result = await selectedFence.ExecuteAsync(transaction, async (callbackTransaction, token) =>
        {
            callbackCount++;
            await using var insert = new NpgsqlCommand(
                "INSERT INTO public.handoff_rejection_ledger DEFAULT VALUES;",
                callbackTransaction.Connection, callbackTransaction);
            await insert.ExecuteNonQueryAsync(token);
            return true;
        });
        Assert.Equal(result.IsSuccess ? 1 : 0, callbackCount);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task AssertRefusedAsync(
        Fixture fixture,
        string expectedCode,
        PostgreSqlEffectPermit? permit = null)
    {
        var result = await InvokeAsync(fixture, permit);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Problem!.Code);
        Assert.Equal(0, await fixture.ReadLedgerCountAsync());
    }

    private static async Task<TException> AssertExceptionAsync<TException>(
        Fixture fixture,
        PostgreSqlDurableWorkHandoffFence? fence = null,
        PostgreSqlIntegrationTestDatabase? transactionDatabase = null)
        where TException : Exception
    {
        var selectedFence = fence ?? fixture.Fence;
        await using var connection = await (transactionDatabase ?? fixture.Lab.Database).DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = selectedFence.EnterInvocation(fixture.Permit);
        var callbackCount = 0;
        var exception = await Assert.ThrowsAsync<TException>(async () =>
            await selectedFence.ExecuteAsync(transaction, (_, _) =>
            {
                callbackCount++;
                return ValueTask.FromResult(true);
            }));
        Assert.Equal(0, callbackCount);
        await transaction.CommitAsync();
        Assert.Equal(0, await fixture.ReadLedgerCountAsync());
        return exception;
    }

    private static async Task CreateLedgerAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "CREATE TABLE public.handoff_rejection_ledger (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY);");
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(PolicyTests.Lab lab, PostgreSqlEffectPermit permit, Guid storeId,
            PostgreSqlDurableWorkHandoffFence fence)
        {
            Lab = lab;
            Permit = permit;
            StoreId = storeId;
            Fence = fence;
        }

        internal PolicyTests.Lab Lab { get; }
        internal PostgreSqlEffectPermit Permit { get; }
        internal Guid StoreId { get; }
        internal PostgreSqlDurableWorkHandoffFence Fence { get; }

        internal static async Task<Fixture> CreateAsync(bool legacy, DateTimeOffset? deadline = null)
        {
            var lab = await PolicyTests.Lab.CreateAsync();
            try
            {
                var request = PolicyTests.Request($"handoff-rejection-{Guid.NewGuid():N}", deadline: deadline);
                if (legacy)
                {
                    request = new DurableWorkRequest(request.ScopeId, request.CommandId, request.IdempotencyKey,
                        request.WorkName, request.WorkVersion, request.Payload, request.ProviderSafety, request.RetryPolicy);
                }

                var permit = await lab.AdmitReadyAsync(request);
                Assert.True(await lab.Store.TryAdmitInvocationAsync(permit));
                if (deadline.HasValue)
                {
                    await using var lease = lab.Database.DataSource.CreateCommand("""
                        UPDATE appsurface_durable.work
                        SET lease_expires_at=@lease_expiry
                        WHERE scope_id=@scope_id AND work_id=@work_id;
                        """);
                    lease.Parameters.AddWithValue("lease_expiry", deadline.Value.AddMinutes(1));
                    lease.Parameters.AddWithValue("scope_id", permit.Claim.ScopeId.Value);
                    lease.Parameters.AddWithValue("work_id", permit.Claim.WorkId.Value);
                    await lease.ExecuteNonQueryAsync();
                }
                var storeId = await lab.ScalarAsync<Guid>("SELECT store_id FROM appsurface_durable.store_metadata WHERE singleton;");
                var fence = new PostgreSqlDurableWorkHandoffFence(lab.Database.DataSource,
                    new PostgreSqlDurableWorkOptions(lab.Epoch, storeId));
                await CreateLedgerAsync(lab.Database.DataSource);
                return new(lab, permit, storeId, fence);
            }
            catch
            {
                await lab.DisposeAsync();
                throw;
            }
        }

        internal async Task<int> ExecuteNativeAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Lab.Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", Permit.Claim.ScopeId.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var affected = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return affected;
        }

        internal async Task UpdateWorkAsync(string assignments) =>
            Assert.Equal(1, await ExecuteNativeAsync(
                $"UPDATE appsurface_durable.work SET {assignments} WHERE scope_id=@scope_id AND work_id=@work_id;",
                ("scope_id", Permit.Claim.ScopeId.Value), ("work_id", Permit.Claim.WorkId.Value)));

        internal async Task ExpireLeaseAsync() => await UpdateWorkAsync("lease_expires_at=CASE WHEN execution_policy_schema IS NULL " +
            "THEN pg_catalog.clock_timestamp()-interval '1 second' " +
            "ELSE appsurface_durable.work_execution_now()-interval '1 second' END");

        internal async Task DeleteWorkAndDependentsAsync(bool deleteScope)
        {
            var claim = Permit.Claim;
            await ExecuteNativeAsync("DELETE FROM appsurface_durable.effect_permit WHERE scope_id=@scope_id AND work_id=@work_id;",
                ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
            await ExecuteNativeAsync("DELETE FROM appsurface_durable.work_history WHERE scope_id=@scope_id AND work_id=@work_id;",
                ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
            await ExecuteNativeAsync("DELETE FROM appsurface_durable.work_operator_command WHERE scope_id=@scope_id AND work_id=@work_id;",
                ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
            await ExecuteNativeAsync("DELETE FROM appsurface_durable.dispatch WHERE scope_id=@scope_id AND aggregate_id=@work_id;",
                ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
            await ExecuteNativeAsync("DELETE FROM appsurface_durable.work WHERE scope_id=@scope_id AND work_id=@work_id;",
                ("scope_id", claim.ScopeId.Value), ("work_id", claim.WorkId.Value));
            if (deleteScope)
            {
                await ExecuteNativeAsync("DELETE FROM appsurface_durable.scope_history WHERE scope_id=@scope_id;",
                    ("scope_id", claim.ScopeId.Value));
                await ExecuteNativeAsync("DELETE FROM appsurface_durable.scope WHERE scope_id=@scope_id;",
                    ("scope_id", claim.ScopeId.Value));
            }
        }

        internal async Task<string> ExplainPermitLookupAsync(Guid permitId)
        {
            await using var connection = await Lab.Database.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope_id", Permit.Claim.ScopeId.Value);
                await scope.ExecuteNonQueryAsync();
            }

            await using var command = new NpgsqlCommand("""
                EXPLAIN (FORMAT TEXT, COSTS OFF)
                SELECT permit_id FROM appsurface_durable.effect_permit WHERE permit_id=@permit_id FOR UPDATE;
                """, connection, transaction);
            command.Parameters.AddWithValue("permit_id", permitId);
            var lines = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            }
            await transaction.RollbackAsync();
            return string.Join(Environment.NewLine, lines);
        }

        internal async Task<long> ReadLedgerCountAsync()
        {
            await using var connection = await Lab.Database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM public.handoff_rejection_ledger;", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => Lab.DisposeAsync();
    }
}
