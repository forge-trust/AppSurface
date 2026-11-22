using System.Diagnostics;
using System.Runtime.CompilerServices;
using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>
/// Owns PostgreSQL template registrations for one test assembly or host. Each callback receives a separate database.
/// Call <see cref="CleanupAllAsync"/> after the host has stopped admitting tests.
/// </summary>
public sealed class PostgreSqlFixture
{
    private readonly object gate = new();
    private readonly Dictionary<(string Connection, string Prefix), Template> templates = new();

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
                if (templates.Count == 0)
                {
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }

                templates.Add(key, template);
            }
            else
            {
                template = existing;
            }

            if (template.Cleaning)
            {
                throw new InvalidOperationException("The requested template is being cleaned up.");
            }

            if (template.Users++ == 0)
            {
                template.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            await template.Preparation.Value.WaitAsync(cancellationToken);
            await RunAsync(connectionString, databaseNamePrefix, template.Database.Name, action, cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                if (--template.Users == 0)
                {
                    template.Idle!.SetResult();
                }
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

    /// <summary>Attempts every template registered to this instance. Inspect <see cref="TemplateCleanupException.Result"/> on partial failure.</summary>
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

    private static async Task RunAsync(string connectionString, string prefix, string? template, Func<string, CancellationToken, Task> action,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var database = new OwnedDatabase(connectionString, prefix);
        try
        {
            await database.CreateAsync(template, token);
            await action(PostgreSqlCatalog.Target(connectionString, database.Name), token);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(PostgreSqlCatalog.Timeout);
            await database.DropAsync(deadline.Token);
        }
    }

    private async Task PrepareAsync(Template template, Func<string, CancellationToken, Task> initialize)
    {
        using var deadline = new CancellationTokenSource(PostgreSqlCatalog.Timeout);
        try
        {
            await template.Database.CreateAsync(null, deadline.Token);
            await initialize(PostgreSqlCatalog.Target(template.Connection, template.Database.Name, disablePooling: true), deadline.Token);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(PostgreSqlCatalog.Timeout);
            await template.Database.DropAsync(cleanup.Token);
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
        lock (gate)
        {
            selected = (select == null ? templates.Values.AsEnumerable() : templates.Values.Where(select)).ToArray();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PostgreSqlCatalog.Timeout);
        var outcomes = await Task.WhenAll(selected.Select(template => CleanupOneAsync(template, deadline.Token)));
        var result = new TemplateCleanupResult(outcomes, stopwatch.Elapsed);
        result.ThrowIfFailed();
        return result;
    }

    private async Task<TemplateCleanupOutcome> CleanupOneAsync(Template template, CancellationToken token)
    {
        Task idle;
        lock (gate)
        {
            if (template.Cleaning)
            {
                return TemplateCleanupOutcome.AlreadyInProgress(template.Database.Name);
            }

            template.Cleaning = true;
            idle = template.Idle?.Task ?? Task.CompletedTask;
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
            catch when (preparation.IsFaulted || preparation.IsCanceled)
            {
                // Initialization already attempted teardown. Retry any drop that failed.
            }

            await template.Database.DropAsync(token);
            lock (gate)
            {
                Forget(template);
            }

            return TemplateCleanupOutcome.Removed(template.Database.Name);
        }
        catch (Exception failure)
        {
            lock (gate)
            {
                template.Cleaning = false;
            }

            return TemplateCleanupOutcome.Failed(template.Database.Name, failure);
        }
    }

    // Called under gate. A failed initializer can be replaced while an earlier cleanup is still completing.
    private void Forget(Template template)
    {
        var key = (template.Connection, template.Prefix);
        if (templates.TryGetValue(key, out var current) && ReferenceEquals(current, template))
        {
            templates.Remove(key);
            if (templates.Count == 0)
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            }
        }
    }

    private void OnProcessExit(object? sender, EventArgs args)
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

    // The gate protects Users, Idle and Cleaning. Preparation is shared independently of individual waiters.
    private sealed class Template(string connection, string prefix)
    {
        public string Connection { get; } = connection;
        public string Prefix { get; } = prefix;
        public OwnedDatabase Database { get; } = new(connection, prefix);
        public Lazy<Task> Preparation { get; set; } = null!;
        public int Users { get; set; }
        public TaskCompletionSource? Idle { get; set; }
        public bool Cleaning { get; set; }
    }

    private sealed class OwnedDatabase(string connection, string prefix)
    {
        // Preserve a random suffix even for prefixes longer than PostgreSQL's 63-byte identifier limit.
        public string Name { get; } = $"{prefix[..Math.Min(prefix.Length, 46)]}_{Guid.NewGuid().ToString("N")[..16]}";
        private bool mayExist;

        public async Task CreateAsync(string? template, CancellationToken token)
        {
            try
            {
                await PostgreSqlCatalog.CreateAsync(connection, Name, template, () => mayExist = true, token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                // A name collision does not transfer ownership of an existing database.
                mayExist = false;
                throw;
            }
        }

        public async Task DropAsync(CancellationToken token)
        {
            if (mayExist)
            {
                await PostgreSqlCatalog.DropAsync(connection, Name, token);
                mayExist = false;
            }
        }
    }
}
