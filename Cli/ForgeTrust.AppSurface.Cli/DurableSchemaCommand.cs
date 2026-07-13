using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for AppSurface durable deployment commands.
/// </summary>
[Command("durable", Description = "Inspect and deploy the AppSurface durable PostgreSQL schema.")]
internal sealed partial class DurableCommand : ICommand
{
    /// <summary>Prints the durable command family summary.</summary>
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; schema subcommands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync(
            "Use 'appsurface durable schema status', 'script', 'apply', or 'preflight'. Runtime work mutations are intentionally not CLI commands.");
    }
}

/// <summary>
/// Provides the discoverable root for explicit durable schema operations.
/// </summary>
[Command("durable schema", Description = "Inspect, script, apply, or preflight numbered durable migrations.")]
internal sealed partial class DurableSchemaCommand : ICommand
{
    /// <summary>Prints the durable schema command family summary.</summary>
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; leaf commands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync(
            "Use 'appsurface durable schema status', 'script', 'apply', or 'preflight'. Schema changes never run implicitly at app startup.");
    }
}

/// <summary>
/// Prints installed and required durable schema versions without mutation.
/// </summary>
[Command("durable schema status", Description = "Read durable schema version and compatibility without changing the database.")]
internal sealed partial class DurableSchemaStatusCommand(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var status = await Service.GetStatusAsync(ResolveConnectionString()).ConfigureAwait(false);
        await WriteStatusAsync(console, status).ConfigureAwait(false);
    }
}

/// <summary>
/// Generates deterministic SQL for pending numbered migrations.
/// </summary>
[Command("durable schema script", Description = "Generate deterministic durable migration SQL for deployment review.")]
internal sealed partial class DurableSchemaScriptCommand(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <summary>Gets or sets the last migration already installed.</summary>
    [CommandOption("from-version", Description = "Last installed migration, or 0 for a new database.")]
    public int FromVersion { get; set; }

    /// <summary>Gets or sets an optional output path. The script is printed when omitted.</summary>
    [CommandOption("output", 'o', Description = "Optional SQL output path. Defaults to standard output.")]
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets whether an existing output file may be replaced.</summary>
    [CommandOption("force", Description = "Replace an existing --output file.")]
    public bool Force { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var script = Service.GenerateScript(FromVersion);
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            await console.Output.WriteAsync(script).ConfigureAwait(false);
            return;
        }

        var path = Path.GetFullPath(OutputPath);
        if (File.Exists(path) && !Force)
        {
            throw new CommandException(
                $"Output file already exists: {path}. Pass --force to replace this generated migration artifact.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, script).ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Wrote durable migration script: {path}").ConfigureAwait(false);
    }
}

/// <summary>
/// Applies pending migrations through an explicitly configured migration-owner connection.
/// </summary>
[Command("durable schema apply", Description = "Apply pending numbered durable migrations under the package advisory lock.")]
internal sealed partial class DurableSchemaApplyCommand(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <summary>Gets or sets the required mutation confirmation.</summary>
    [CommandOption("apply", Description = "Required confirmation that migrations may be applied.")]
    public bool Apply { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        if (!Apply)
        {
            throw new CommandException(
                "Schema apply is disabled by default. Review `appsurface durable schema script`, then pass --apply using the migration-owner connection.");
        }

        var result = await Service.ApplyAsync(ResolveConnectionString()).ConfigureAwait(false);
        var applied = result.AppliedVersions.Count == 0
            ? "none"
            : string.Join(", ", result.AppliedVersions.Select(version => version.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));
        await console.Output.WriteLineAsync(
            $"Durable schema: {result.FromVersion} -> {result.ToVersion}; applied: {applied}.").ConfigureAwait(false);
    }
}

/// <summary>
/// Fails noninteractively unless the installed schema supports this runtime's readers and writers.
/// </summary>
[Command("durable schema preflight", Description = "Fail unless the durable schema is compatible with this runtime package.")]
internal sealed partial class DurableSchemaPreflightCommand(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var status = await Service.GetStatusAsync(ResolveConnectionString()).ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            throw new CommandException(
                $"Problem: durable schema preflight is {status.Compatibility}. " +
                $"Cause: {status.Problem ?? "the installed reader/writer range does not include this package"} " +
                "Fix: generate and apply the pending numbered migrations, or deploy a package supported by the installed compatibility range. " +
                "Docs: https://appsurface.dev/docs/durable/postgresql-schema");
        }

        await console.Output.WriteLineAsync(
            $"Compatible: durable schema {status.InstalledVersion}; runtime requires {status.RequiredVersion}.").ConfigureAwait(false);
    }
}

/// <summary>
/// Shared connection-source option and output behavior for durable schema commands.
/// </summary>
internal abstract class DurableSchemaCommandBase(IDurableSchemaCommandService service) : ICommand
{
    /// <summary>Gets the injected schema command service.</summary>
    protected IDurableSchemaCommandService Service { get; } = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>
    /// Gets or sets the environment variable that contains the PostgreSQL connection string.
    /// </summary>
    /// <remarks>
    /// Connection strings are intentionally not accepted as command-line values because process listings and shell
    /// history are not appropriate secret stores.
    /// </remarks>
    [CommandOption("connection-env", Description = "Environment variable containing the PostgreSQL connection string.")]
    public string ConnectionEnvironmentVariable { get; set; } = "APPSURFACE_DURABLE_CONNECTION";

    /// <summary>Executes the command.</summary>
    public abstract ValueTask ExecuteAsync(IConsole console);

    /// <summary>Resolves a connection string without printing or persisting it.</summary>
    protected string ResolveConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionEnvironmentVariable))
        {
            throw new CommandException("--connection-env must name a non-empty environment variable.");
        }

        var value = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandException(
                $"Environment variable '{ConnectionEnvironmentVariable}' does not contain a PostgreSQL connection string.");
        }

        return value;
    }

    /// <summary>Writes a stable schema status without exposing connection details.</summary>
    protected static async ValueTask WriteStatusAsync(IConsole console, DurableSchemaStatusView status)
    {
        await console.Output.WriteLineAsync($"Compatibility: {status.Compatibility}").ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Installed: {status.InstalledVersion}").ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Required: {status.RequiredVersion}").ConfigureAwait(false);
        await console.Output.WriteLineAsync(
            $"Pending: {(status.PendingVersions.Count == 0 ? "none" : string.Join(", ", status.PendingVersions))}").ConfigureAwait(false);
        if (status.Problem is not null)
        {
            await console.Output.WriteLineAsync($"Problem: {status.Problem}").ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Testable CLI boundary over the PostgreSQL schema manager.
/// </summary>
internal interface IDurableSchemaCommandService
{
    /// <summary>Reads compatibility without mutation.</summary>
    ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString);

    /// <summary>Generates deterministic migration SQL.</summary>
    string GenerateScript(int fromVersion);

    /// <summary>Applies pending numbered migrations.</summary>
    ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString);
}

/// <summary>
/// Production CLI adapter that creates and disposes a short-lived Npgsql data source per command.
/// </summary>
internal sealed class DurableSchemaCommandService : IDurableSchemaCommandService
{
    /// <inheritdoc />
    public async ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(RequireConnectionString(connectionString));
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var status = await manager.GetStatusAsync().ConfigureAwait(false);
        return new DurableSchemaStatusView(
            status.Compatibility.ToString(),
            status.InstalledVersion,
            status.RequiredVersion,
            status.PendingVersions,
            status.Problem,
            status.IsCompatible);
    }

    /// <inheritdoc />
    public string GenerateScript(int fromVersion)
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=offline-script-generation;Username=offline-script-generation");
        return new PostgreSqlDurableRuntimeSchemaManager(dataSource).GenerateScript(fromVersion);
    }

    /// <inheritdoc />
    public async ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(RequireConnectionString(connectionString));
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        var result = await manager.ApplyAsync().ConfigureAwait(false);
        return new DurableSchemaApplyView(result.PreviousVersion, result.CurrentVersion, result.AppliedVersions);
    }

    private static string RequireConnectionString(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(value))
            : value;
}

/// <summary>CLI-safe schema compatibility projection.</summary>
internal sealed record DurableSchemaStatusView(
    string Compatibility,
    int InstalledVersion,
    int RequiredVersion,
    IReadOnlyList<int> PendingVersions,
    string? Problem,
    bool IsCompatible);

/// <summary>CLI-safe schema apply projection.</summary>
internal sealed record DurableSchemaApplyView(
    int FromVersion,
    int ToVersion,
    IReadOnlyList<int> AppliedVersions);
