using System.Diagnostics;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.PostgreSql.Testing;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlCohortFixture<TCohort> : IAsyncLifetime
    where TCohort : class
{
    private readonly PostgreSqlCohortFixtureCore _fixture;

    public PostgreSqlCohortFixture()
        : this(PostgreSqlCohortFixtureOptions.FromEnvironment(typeof(TCohort).Name))
    {
    }

    internal PostgreSqlCohortFixture(PostgreSqlCohortFixtureOptions options)
    {
        _fixture = new PostgreSqlCohortFixtureCore(options);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    internal ValueTask<PostgreSqlCohortReadyDatabase> CreateReadyDatabaseAsync(string actor, string reason) =>
        _fixture.CreateReadyDatabaseAsync(actor, reason);
    internal ValueTask<PostgreSqlIntegrationTestDatabase> CreateEmptyDatabaseAsync() =>
        _fixture.CreateEmptyDatabaseAsync();
}

internal sealed class PostgreSqlCohortFixtureCore : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFixtureDrainTimeout = TimeSpan.FromSeconds(60);
    private readonly PostgreSqlCohortFixtureOptions _options;
    private readonly TimeSpan _fixtureDrainTimeout;
    private readonly Func<PostgreSqlIntegrationTestDatabase, ValueTask>? _afterTemplatePreparedAsync;
    private readonly Func<ValueTask>? _beforeCaseDatabaseCreateAsync;
    private readonly Func<bool, ValueTask<PostgreSqlIntegrationTestDatabase>> _createCaseDatabaseAsync;
    private readonly Func<IAsyncDisposable, ValueTask> _disposeResourceAsync;
    private readonly HashSet<IAsyncDisposable> _failedCleanupResources = [];
    private readonly object _lifecycleLock = new();
    private TaskCompletionSource _caseDatabasesDrained = CompletedSignal();
    private PostgreSqlFixtureTimingCsv? _timings;
    private PostgreSqlIntegrationTestDatabase? _template;
    private Guid _templateStoreId;
    private int _activeCaseDatabases;
    private bool _closing;
    private Task? _initializeTask;
    private Task? _disposeTask;

    internal PostgreSqlCohortFixtureCore(
        PostgreSqlCohortFixtureOptions options,
        TimeSpan? fixtureDrainTimeout = null,
        Func<PostgreSqlIntegrationTestDatabase, ValueTask>? afterTemplatePreparedAsync = null,
        Func<ValueTask>? beforeCaseDatabaseCreateAsync = null,
        Func<bool, ValueTask<PostgreSqlIntegrationTestDatabase>>? createCaseDatabaseAsync = null,
        Func<IAsyncDisposable, ValueTask>? disposeResourceAsync = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fixtureDrainTimeout = fixtureDrainTimeout ?? DefaultFixtureDrainTimeout;
        if (_fixtureDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fixtureDrainTimeout));
        }

        _afterTemplatePreparedAsync = afterTemplatePreparedAsync;
        _beforeCaseDatabaseCreateAsync = beforeCaseDatabaseCreateAsync;
        _createCaseDatabaseAsync = createCaseDatabaseAsync ?? CreateCaseDatabaseAsync;
        _disposeResourceAsync = disposeResourceAsync ?? (static resource => resource.DisposeAsync());
    }

    internal Task InitializeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_closing)
            {
                throw new PostgreSqlFixtureException(
                    $"Cohort fixture {_options.CohortName} is closing and cannot initialize.");
            }

            return _initializeTask ??= InitializeCoreAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (_options.TimingsFilePath is not null)
        {
            _timings = PostgreSqlFixtureTimingCsv.Create(_options.TimingsFilePath);
        }

        if (!_options.UsesTemplate)
        {
            return;
        }

        var startedTimestamp = GetTimingStart();
        PostgreSqlIntegrationTestDatabase? template = null;
        var preparationRecorded = false;
        try
        {
            template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
                _options.ConnectionString!,
                isTemplate: true);
            template.ObserveTeardown = elapsed => _timings?.Record("template_teardown", elapsed);
            _templateStoreId = await template.PrepareTemplateAsync();
            if (_templateStoreId == Guid.Empty)
            {
                throw new PostgreSqlFixtureException("Template preparation returned an empty store identity.");
            }

            RecordTemplatePreparation(startedTimestamp);
            preparationRecorded = true;
            if (_afterTemplatePreparedAsync is not null)
            {
                await _afterTemplatePreparedAsync(template);
            }

            _template = template;
        }
        catch (Exception preparationError) when (!PostgreSqlFixture.IsFatalCleanupException(preparationError))
        {
            var failures = new List<Exception> { preparationError };
            if (!preparationRecorded)
            {
                TryRecord("template_prepare", startedTimestamp, failures);
            }

            if (template is not null)
            {
                try
                {
                    await _disposeResourceAsync(template);
                }
                catch (Exception cleanupError) when (!PostgreSqlFixture.IsFatalCleanupException(cleanupError))
                {
                    failures.Add(cleanupError);
                    _template = template;
                }
            }
            else if (preparationError is PostgreSqlDatabaseCleanupException failedCreate)
            {
                RetainFailedCleanupResource(failedCreate.Database);
            }

            var timings = _timings;
            _timings = null;
            if (timings is not null)
            {
                try
                {
                    await timings.DisposeAsync();
                }
                catch (Exception timingError) when (!PostgreSqlFixture.IsFatalCleanupException(timingError))
                {
                    failures.Add(timingError);
                }
            }

            throw CreateFailure("Template preparation", failures);
        }
    }

    internal async ValueTask<PostgreSqlCohortReadyDatabase> CreateReadyDatabaseAsync(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var tracked = await CreateTrackedDatabaseAsync(empty: false);
        var setupRecorded = false;
        try
        {
            var epoch = Guid.NewGuid();
            DurableRuntimeSchemaStatus status;
            if (_options.UsesTemplate)
            {
                status = await tracked.Database.ActivateCloneAsync(_templateStoreId, epoch, actor, reason);
            }
            else
            {
                var schema = new PostgreSqlDurableRuntimeSchemaManager(tracked.Database.DataSource);
                await schema.ApplyAsync();
                await schema.InitializeRuntimeEpochAsync(epoch, actor, reason);
                status = await schema.GetStatusAsync();
            }

            RecordSetup(tracked.StartedTimestamp);
            setupRecorded = true;
            return new PostgreSqlCohortReadyDatabase(tracked.Database, epoch, status);
        }
        catch (Exception setupError) when (!PostgreSqlFixture.IsFatalCleanupException(setupError))
        {
            var failures = new List<Exception> { setupError };
            if (!setupRecorded)
            {
                TryRecord("database_setup", tracked.StartedTimestamp, failures);
            }

            await CleanUpFailedSetupAsync(tracked, failures);

            if (setupError is OperationCanceledException && failures.Count == 1)
            {
                throw;
            }

            throw CreateFailure("Database setup", failures);
        }
    }

    internal async ValueTask<PostgreSqlIntegrationTestDatabase> CreateEmptyDatabaseAsync()
    {
        var tracked = await CreateTrackedDatabaseAsync(empty: true);
        try
        {
            RecordSetup(tracked.StartedTimestamp);
            return tracked.Database;
        }
        catch (Exception setupError) when (!PostgreSqlFixture.IsFatalCleanupException(setupError))
        {
            var failures = new List<Exception> { setupError };
            await CleanUpFailedSetupAsync(tracked, failures);

            if (setupError is OperationCanceledException && failures.Count == 1)
            {
                throw;
            }

            throw CreateFailure("Empty database setup", failures);
        }
    }

    private async ValueTask CleanUpFailedSetupAsync(TrackedCaseDatabase tracked, List<Exception> failures)
    {
        try
        {
            await _disposeResourceAsync(tracked.Database);
        }
        catch (Exception cleanupError) when (!PostgreSqlFixture.IsFatalCleanupException(cleanupError))
        {
            failures.Add(cleanupError);
            RetainFailedCleanupResource(tracked.Database);
            tracked.ReleaseCaseDatabase();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task? initializationTask;
        Task drained;
        Task disposalTask;
        lock (_lifecycleLock)
        {
            _closing = true;
            initializationTask = _initializeTask;
            drained = _activeCaseDatabases == 0
                ? Task.CompletedTask
                : _caseDatabasesDrained.Task;
            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
            {
                _disposeTask = DisposeCoreAsync(initializationTask, drained);
            }

            disposalTask = _disposeTask;
        }

        return new ValueTask(WaitForDisposeAsync(drained, disposalTask));
    }

    private async ValueTask<TrackedCaseDatabase> CreateTrackedDatabaseAsync(bool empty)
    {
        await InitializeAsync();
        BeginCaseDatabase();
        var startedTimestamp = GetTimingStart();
        try
        {
            if (_beforeCaseDatabaseCreateAsync is not null)
            {
                await _beforeCaseDatabaseCreateAsync();
            }

            var database = await _createCaseDatabaseAsync(empty);

            var released = 0;
            Action releaseCaseDatabase = () =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    EndCaseDatabase();
                }
            };
            database.ObserveTeardown = elapsed => _timings?.Record("database_teardown", elapsed);
            database.AfterDispose = releaseCaseDatabase;
            return new TrackedCaseDatabase(database, startedTimestamp, releaseCaseDatabase);
        }
        catch (Exception creationError) when (!PostgreSqlFixture.IsFatalCleanupException(creationError))
        {
            var failures = new List<Exception> { creationError };
            try
            {
                if (creationError is PostgreSqlDatabaseCleanupException failedCreate)
                {
                    RetainFailedCleanupResource(failedCreate.Database);
                }

                TryRecord("database_setup", startedTimestamp, failures);
            }
            finally
            {
                EndCaseDatabase();
            }

            if (creationError is (OperationCanceledException or SkipException) && failures.Count == 1)
            {
                throw;
            }

            throw CreateFailure("Database creation", failures);
        }
    }

    private ValueTask<PostgreSqlIntegrationTestDatabase> CreateCaseDatabaseAsync(bool empty) =>
        _options.ConnectionString is { } connectionString
            ? PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(connectionString, template: _options.UsesTemplate && !empty ? _template : null)
            : PostgreSqlIntegrationTestDatabase.TryCreateAsync();

    private async Task DisposeCoreAsync(Task? initializationTask, Task drained)
    {
        if (initializationTask is not null)
        {
            try
            {
                await initializationTask;
            }
            catch (Exception exception) when (!PostgreSqlFixture.IsFatalCleanupException(exception))
            {
                // Initialization already reported the error and retained any owned resource that still needs cleanup.
            }
        }

        await drained;
        var failures = new List<Exception>();
        await RetryFailedCleanupResourcesAsync(failures);
        if (failures.Count == 0 && _template is not null)
        {
            try
            {
                await _disposeResourceAsync(_template);
                _template = null;
            }
            catch (Exception cleanupError) when (!PostgreSqlFixture.IsFatalCleanupException(cleanupError))
            {
                failures.Add(cleanupError);
            }
        }

        var timings = _timings;
        _timings = null;
        if (timings is not null)
        {
            try
            {
                await timings.DisposeAsync();
            }
            catch (Exception timingError) when (!PostgreSqlFixture.IsFatalCleanupException(timingError))
            {
                failures.Add(timingError);
            }
        }

        if (failures.Count > 0)
        {
            throw CreateFailure("Cohort fixture teardown", failures);
        }
    }

    private async Task WaitForDisposeAsync(Task drained, Task disposalTask)
    {
        try
        {
            await drained.WaitAsync(_fixtureDrainTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new PostgreSqlFixtureException(
                $"Cohort fixture {_options.CohortName} still has case databases after {_fixtureDrainTimeout.TotalSeconds:0.###} seconds. Preserve the template and reconcile the test process ({PostgreSqlFixtureException.Describe(exception)}).");
        }

        await disposalTask;
    }

    private void BeginCaseDatabase()
    {
        lock (_lifecycleLock)
        {
            if (_closing)
            {
                throw new PostgreSqlFixtureException(
                    $"Cohort fixture {_options.CohortName} is closing and cannot create another case database.");
            }

            if (_activeCaseDatabases == 0)
            {
                _caseDatabasesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _activeCaseDatabases++;
        }
    }

    private void EndCaseDatabase()
    {
        lock (_lifecycleLock)
        {
            if (--_activeCaseDatabases == 0)
            {
                _caseDatabasesDrained.TrySetResult();
            }
        }
    }

    // Transfer failed setup resources to the cohort before releasing the case reservation.
    // Once the reservations drain, the cohort owns every cleanup the caller could not finish.
    private void RetainFailedCleanupResource(IAsyncDisposable resource)
    {
        lock (_lifecycleLock)
        {
            _failedCleanupResources.Add(resource);
        }
    }

    private async Task RetryFailedCleanupResourcesAsync(List<Exception> failures)
    {
        IAsyncDisposable[] resources;
        lock (_lifecycleLock)
        {
            resources = _failedCleanupResources.ToArray();
        }

        foreach (var resource in resources)
        {
            try
            {
                await _disposeResourceAsync(resource);
                lock (_lifecycleLock)
                {
                    _failedCleanupResources.Remove(resource);
                }
            }
            catch (Exception cleanupError) when (!PostgreSqlFixture.IsFatalCleanupException(cleanupError))
            {
                failures.Add(cleanupError);
            }
        }
    }

    private long GetTimingStart() => _timings is null ? 0 : Stopwatch.GetTimestamp();

    private void RecordSetup(long startedTimestamp)
    {
        if (_timings is not null)
        {
            _timings.RecordElapsed("database_setup", startedTimestamp);
        }
    }

    private void RecordTemplatePreparation(long startedTimestamp)
    {
        if (_timings is not null)
        {
            _timings.RecordElapsed("template_prepare", startedTimestamp);
        }
    }

    private void TryRecord(string phase, long startedTimestamp, List<Exception> failures)
    {
        if (_timings is null)
        {
            return;
        }

        try
        {
            _timings.RecordElapsed(phase, startedTimestamp);
        }
        catch (Exception timingError) when (!PostgreSqlFixture.IsFatalCleanupException(timingError))
        {
            failures.Add(timingError);
        }
    }

    private static PostgreSqlFixtureException CreateFailure(string phase, IReadOnlyList<Exception> failures)
    {
        var detail = string.Join(", ", failures.Select(DescribeFailure));
        return new PostgreSqlFixtureException($"{phase} failed ({detail}). Preserve the original diagnostic and reconcile cleanup before retrying.");
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        PostgreSqlDatabaseCleanupException cleanup => cleanup.OperationError is null
            ? cleanup.Message
            : $"{cleanup.Message} Operation failed ({DescribeFailure(cleanup.OperationError)}).",
        AggregateException aggregate => string.Join(", ", aggregate.InnerExceptions.Select(DescribeFailure)),
        _ => PostgreSqlFixtureException.Describe(error),
    };

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed record TrackedCaseDatabase(
        PostgreSqlIntegrationTestDatabase Database,
        long StartedTimestamp,
        Action ReleaseCaseDatabase);
}

internal sealed class PostgreSqlCohortReadyDatabase(
    PostgreSqlIntegrationTestDatabase database,
    Guid runtimeEpoch,
    DurableRuntimeSchemaStatus status) : IAsyncDisposable
{
    internal PostgreSqlIntegrationTestDatabase Database { get; } = database;

    internal Guid RuntimeEpoch { get; } = runtimeEpoch;

    internal DurableRuntimeSchemaStatus Status { get; } = status;

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
