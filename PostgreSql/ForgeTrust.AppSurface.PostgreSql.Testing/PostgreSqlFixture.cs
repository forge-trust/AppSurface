using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>
/// Owns PostgreSQL template registrations for one test assembly or host. Each callback receives a separate database.
/// Call <see cref="CleanupAllAsync"/> after the host has stopped admitting tests.
/// </summary>
public sealed class PostgreSqlFixture
{
    private readonly object gate = new();
    private readonly Dictionary<(string Connection, string Prefix), Template> templates = new();

    private readonly HashSet<PostgreSqlDatabaseLease> failedFreshDrops = [];
    private bool watchingProcessExit;

    /// <summary>Creates an empty database for the callback and drops it even when the callback fails or is canceled.</summary>
    public Task RunFreshAsync(string connectionString, string databaseNamePrefix, Func<string, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ValidatePrefix(databaseNamePrefix);
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(connectionString, databaseNamePrefix, null, action, cancellationToken);
    }

    /// <summary>
    /// Prepares the template once per connection string and template prefix, then runs the callback in a private clone.
    /// The initialization callback gets an independent five-minute deadline. Each test callback gets its caller's token.
    /// Supply a different template prefix for each distinct schema or seed.
    /// </summary>
    public async Task RunFromTemplateAsync(string connectionString, string databaseNamePrefix, string templateNamePrefix,
        Func<string, CancellationToken, Task> initializeTemplateAsync, Func<string, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ValidatePrefix(databaseNamePrefix);
        ValidatePrefix(templateNamePrefix);
        ArgumentNullException.ThrowIfNull(initializeTemplateAsync);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (connectionString, templateNamePrefix);
        Template template;
        lock (gate)
        {
            if (!templates.TryGetValue(key, out var existing))
            {
                template = new Template(connectionString, templateNamePrefix);
                template.Preparation = new Lazy<Task>(() => PrepareAsync(template, initializeTemplateAsync));
                templates.Add(key, template);
                UpdateExitHandler();
            }
            else
            {
                template = existing;
            }

            template.Admit();
        }

        try
        {
            await template.Preparation.Value.WaitAsync(cancellationToken);
            await RunAsync(connectionString, databaseNamePrefix, template, action, cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                template.Release();
            }
        }
    }

    /// <summary>Serializes cluster-wide bootstrap such as role creation on a dedicated PostgreSQL session with a five-minute deadline.</summary>
    public static Task WithClusterInitializationLockAsync(string connectionString, Func<CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(action);
        return PostgreSqlCatalog.WithClusterInitializationLockAsync(connectionString, action);
    }

    /// <summary>Retries failed fresh database drops and cleans every template registered to this instance. Inspect <see cref="TemplateCleanupException.Result"/> on partial failure.</summary>
    public ValueTask<TemplateCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default) =>
        CleanupAsync(null, cancellationToken);

    /// <summary>Cleans only this instance's registrations matching the connection and exact logical prefixes. Safe to repeat.</summary>
    public ValueTask<TemplateCleanupResult> CleanupTemplatesAsync(string connectionString, IReadOnlyCollection<string> templateNamePrefixes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(templateNamePrefixes);
        if (templateNamePrefixes.Count == 0)
        {
            throw new ArgumentException("At least one template prefix is required.", nameof(templateNamePrefixes));
        }

        foreach (var prefix in templateNamePrefixes)
        {
            ValidatePrefix(prefix, nameof(templateNamePrefixes));
        }

        var prefixes = templateNamePrefixes.ToHashSet(StringComparer.Ordinal);
        return CleanupAsync(template => template.Connection == connectionString && prefixes.Contains(template.Prefix), cancellationToken);
    }

    private async Task RunAsync(string connectionString, string prefix, Template? template, Func<string, CancellationToken, Task> action,
        CancellationToken token)
    {
        PostgreSqlDatabaseLease database;
        try
        {
            database = template == null
                ? await PostgreSqlDatabaseLease.CreateAsync(connectionString, prefix, token)
                : await template.Database!.CloneAsync(prefix, null, token, disconnectTemplateSessions: true);
        }
        catch (PostgreSqlDatabaseCleanupException failure)
        {
            RememberFailedDrop(template, failure.Database);
            throw;
        }

        Exception? actionError = null;
        try
        {
            await action(database.ConnectionString, token);
        }
        catch (Exception error)
        {
            actionError = error;
            throw;
        }
        finally
        {
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception failure)
            {
                RememberFailedDrop(template, database);
                throw new PostgreSqlDatabaseCleanupException(database, failure, actionError);
            }
        }
    }

    private void RememberFailedDrop(Template? template, PostgreSqlDatabaseLease database)
    {
        lock (gate)
        {
            (template?.FailedDrops ?? failedFreshDrops).Add(database);
            UpdateExitHandler();
        }
    }

    private async Task PrepareAsync(Template template, Func<string, CancellationToken, Task> initialize)
    {
        using var deadline = new CancellationTokenSource(PostgreSqlCatalog.Timeout);
        try
        {
            template.Database = await PostgreSqlDatabaseLease.CreateNamedAsync(template.Connection, template.Name, null, deadline.Token);
            await initialize(PostgreSqlCatalog.Target(template.Connection, template.Name, disablePooling: true), deadline.Token);
        }
        catch (PostgreSqlDatabaseCleanupException failure) when (template.Database is null)
        {
            template.Database = failure.Database;
            throw;
        }
        catch (Exception initializationError)
        {
            if (template.Database != null)
            {
                try
                {
                    await template.Database.DisposeAsync();
                }
                catch (Exception cleanupError) when (!IsFatalCleanupException(cleanupError))
                {
                    throw new PostgreSqlDatabaseCleanupException(template.Database, cleanupError, initializationError);
                }
            }

            lock (gate)
            {
                Forget(template);
            }

            throw;
        }
    }

    private async ValueTask<TemplateCleanupResult> CleanupAsync(Func<Template, bool>? select, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        Template[] selected;
        PostgreSqlDatabaseLease[] freshRetries;
        lock (gate)
        {
            freshRetries = select == null ? failedFreshDrops.ToArray() : [];
            selected = (select == null ? templates.Values.AsEnumerable() : templates.Values.Where(select)).ToArray();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PostgreSqlCatalog.Timeout);
        var cleanupTasks = selected.Select(template => CleanupOneAsync(template, deadline.Token)).ToArray();
        var freshRetryTasks = freshRetries.Select(database => RetryDropAsync(database, failedFreshDrops, deadline.Token)).ToArray();
        var outcomes = await Task.WhenAll(cleanupTasks);
        var freshOutcomes = await Task.WhenAll(freshRetryTasks);
        var result = new TemplateCleanupResult(outcomes, stopwatch.Elapsed) { FreshDatabaseOutcomes = freshOutcomes };
        result.ThrowIfFailed();
        return result;
    }

    private async Task<TemplateCleanupOutcome> CleanupOneAsync(Template template, CancellationToken token)
    {
        Task idle;
        lock (gate)
        {
            if (!template.TryBeginCleanup(out idle))
            {
                return TemplateCleanupOutcome.AlreadyInProgress(template.Name);
            }
        }

        try
        {
            await idle.WaitAsync(token);
            // The last caller may have canceled its wait while shared initialization was still running.
            var preparation = template.Preparation.Value;
            try
            {
                await preparation.WaitAsync(token);
            }
            catch when (preparation.IsCompleted && template.Database != null && !token.IsCancellationRequested)
            {
                // Initialization already attempted teardown. Retry any drop that failed.
            }
            catch (Exception preparationError) when (preparation.IsCompleted && template.Database == null
                && preparationError is not PostgreSqlDatabaseOwnershipException && !token.IsCancellationRequested)
            {
                return TemplateCleanupOutcome.Removed(template.Name);
            }

            PostgreSqlDatabaseLease[] retries;
            lock (gate)
            {
                retries = template.FailedDrops.ToArray();
            }

            var attempts = await Task.WhenAll(retries.Select(database => RetryDropAsync(database, template.FailedDrops, token)));
            var incomplete = attempts.Where(outcome => outcome.Status == TemplateCleanupStatus.Failed).ToArray();
            if (incomplete.Length != 0)
            {
                return TemplateCleanupOutcome.Failed(template.Name, new InvalidOperationException(string.Join(", ",
                    incomplete.Select(outcome => $"{outcome.DatabaseName}: {outcome.Failure}"))));
            }

            await template.Database!.DropAsync(token);
            lock (gate)
            {
                Forget(template);
            }

            return TemplateCleanupOutcome.Removed(template.Name);
        }
        catch (Exception failure)
        {
            return TemplateCleanupOutcome.Failed(template.Name, failure);
        }
        finally
        {
            lock (gate)
            {
                template.EndCleanup();
            }
        }
    }

    private async Task<TemplateCleanupOutcome> RetryDropAsync(PostgreSqlDatabaseLease database,
        HashSet<PostgreSqlDatabaseLease> failedDrops, CancellationToken token)
    {
        try
        {
            await database.DropAsync(token);
            lock (gate)
            {
                failedDrops.Remove(database);
                UpdateExitHandler();
            }

            return TemplateCleanupOutcome.Removed(database.Name);
        }
        catch (Exception error)
        {
            return TemplateCleanupOutcome.Failed(database.Name, error);
        }
    }

    // Called under gate. A failed initializer can be replaced while an earlier cleanup is still completing.
    private void Forget(Template template)
    {
        var key = (template.Connection, template.Prefix);
        if (templates.TryGetValue(key, out var current) && ReferenceEquals(current, template))
        {
            templates.Remove(key);
            UpdateExitHandler();
        }
    }

    // Called under gate. Failed fresh drops also need the process-exit fallback.
    private void UpdateExitHandler()
    {
        var needed = templates.Count != 0 || failedFreshDrops.Count != 0;
        if (needed == watchingProcessExit)
        {
            return;
        }

        if (needed)
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        else
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        watchingProcessExit = needed;
    }

    /// <summary>Attempts bounded teardown during process exit. Explicit cleanup can retry any retained leases.</summary>
    internal void OnProcessExit(object? sender, EventArgs args)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            CleanupAllAsync(deadline.Token).AsTask().WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Explicit host teardown reports failures. Process exit is a bounded fallback.
        }
    }

    /// <summary>Checks the fixture's ASCII database identifier prefix rules without requiring a configured server.</summary>
    public static void ValidateDatabaseNamePrefix(string databaseNamePrefix) => ValidatePrefix(databaseNamePrefix);

    private static void ValidatePrefix(string prefix, [CallerArgumentExpression(nameof(prefix))] string? argumentName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix, argumentName);
        if (prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Database name prefixes may only contain ASCII letters, digits, and underscores.", argumentName);
        }
    }

    /// <summary>Identifies CLR exceptions that must propagate directly from template cleanup.</summary>
    /// <param name="exception">The cleanup exception to classify.</param>
    /// <returns><see langword="true"/> when the exception represents a fatal CLR condition.</returns>
    internal static bool IsFatalCleanupException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    // Lifecycle methods are called under the fixture gate. Preparation is shared independently of individual waiters.
    private sealed class Template(string connection, string prefix)
    {
        public string Connection { get; } = connection;
        public string Prefix { get; } = prefix;
        public string Name { get; } = PostgreSqlDatabaseLease.NewName(prefix);
        public PostgreSqlDatabaseLease? Database { get; set; }
        public HashSet<PostgreSqlDatabaseLease> FailedDrops { get; } = [];
        public Lazy<Task> Preparation { get; set; } = null!;

        private int users;
        private TaskCompletionSource? idle;
        private bool cleaning;

        public void Admit()
        {
            if (cleaning)
            {
                throw new InvalidOperationException("The requested template is being cleaned up.");
            }

            if (users++ == 0)
            {
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            if (--users == 0)
            {
                idle!.SetResult();
            }
        }

        public bool TryBeginCleanup(out Task idleTask)
        {
            if (cleaning)
            {
                idleTask = Task.CompletedTask;
                return false;
            }

            cleaning = true;
            idleTask = idle?.Task ?? Task.CompletedTask;
            return true;
        }

        public void EndCleanup() => cleaning = false;
    }
}
