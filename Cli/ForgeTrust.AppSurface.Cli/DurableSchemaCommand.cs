using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Provides the discoverable root for AppSurface durable deployment commands.</summary>
/// <remarks>
/// This command family deliberately owns schema lifecycle only. It does not expose Work, Flow, Schedule, recovery,
/// or generic durable operator mutations because applications must authorize those controls themselves.
/// </remarks>
[Command("durable", Description = "Inspect and deploy the AppSurface durable PostgreSQL schema.")]
internal sealed partial class DurableCommand : ICommand
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; schema subcommands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        await console.Output.WriteLineAsync(
            "Use 'appsurface durable schema status', 'script', 'preflight', or 'apply'. Runtime work mutations are intentionally not CLI commands.").ConfigureAwait(false);
    }
}

/// <summary>Provides the discoverable root for explicit durable schema operations.</summary>
[Command("durable schema", Description = "Inspect, script, preflight, or explicitly apply numbered durable migrations.")]
internal sealed partial class DurableSchemaCommand : ICommand
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; leaf commands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        await console.Output.WriteLineAsync(
            "Use 'appsurface durable schema status', 'script', 'preflight', or 'apply'. Schema changes never run implicitly at app startup.").ConfigureAwait(false);
    }
}

/// <summary>Prints installed and required durable schema versions without mutation.</summary>
[Command("durable schema status", Description = "Read durable schema version and compatibility without changing the database.")]
internal sealed partial class DurableSchemaStatusCommand(IDurableSchemaCommandService service) : DurableSchemaOnlineCommandBase(service)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var status = await RunOnlineAsync(
            ResolveConnectionString(),
            console.RegisterCancellationHandler(),
            Service.GetStatusAsync).ConfigureAwait(false);
        await WriteStatusAsync(console, status).ConfigureAwait(false);
    }
}

/// <summary>Generates deterministic SQL for pending numbered migrations without opening a database connection.</summary>
[Command("durable schema script", Description = "Generate deterministic durable migration SQL for deployment review without connecting to PostgreSQL.")]
internal sealed partial class DurableSchemaScriptCommand(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <summary>Gets or sets the last migration already installed.</summary>
    [CommandOption("from-version", Description = "Last reviewed migration already installed, from 0 through the current required version. Default: 0.")]
    public int FromVersion { get; set; }

    /// <summary>Gets or sets an optional output path. The script is written to standard output when omitted.</summary>
    [CommandOption("output", 'o', Description = "Optional SQL output path. Defaults to standard output.")]
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets whether an existing output file may be atomically replaced.</summary>
    [CommandOption("force", Description = "Atomically replace an existing --output file after the script is generated.")]
    public bool Force { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        string script;
        try
        {
            script = Service.GenerateScript(FromVersion);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new CommandException(
                "--from-version must be between 0 and the current durable migration version. Run 'appsurface durable schema script' without it for a blank database.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            await console.Output.WriteAsync(script).ConfigureAwait(false);
            return;
        }

        var outputPath = await DurableSchemaScriptOutput.WriteAsync(OutputPath, script, Force, console.RegisterCancellationHandler()).ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Wrote durable migration script: {outputPath}").ConfigureAwait(false);
    }
}

/// <summary>Applies pending migrations through an explicitly configured migration-owner connection.</summary>
[Command("durable schema apply", Description = "Apply pending numbered durable migrations under the package advisory lock.")]
internal sealed partial class DurableSchemaApplyCommand(IDurableSchemaCommandService service) : DurableSchemaOnlineCommandBase(service)
{
    /// <summary>Gets or sets the required mutation confirmation.</summary>
    [CommandOption("apply", Description = "Required confirmation that reviewed migrations may be applied with the migration-owner connection.")]
    public bool Apply { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (!Apply)
        {
            throw new CommandException(
                "Schema apply is disabled by default. Review 'appsurface durable schema script', then pass --apply using a migration-owner connection environment variable.");
        }

        var result = await RunOnlineAsync(
            ResolveConnectionString(),
            console.RegisterCancellationHandler(),
            Service.ApplyAsync).ConfigureAwait(false);
        var applied = result.AppliedVersions.Count == 0
            ? "none"
            : string.Join(", ", result.AppliedVersions.Select(static version => version.ToString("D4", CultureInfo.InvariantCulture)));
        await console.Output.WriteLineAsync(
            $"Durable schema: {result.FromVersion} -> {result.ToVersion}; applied: {applied}.").ConfigureAwait(false);
    }
}

/// <summary>Fails noninteractively unless the installed schema supports this runtime's readers and writers.</summary>
[Command("durable schema preflight", Description = "Fail unless the durable schema is compatible with this runtime package.")]
internal sealed partial class DurableSchemaPreflightCommand(IDurableSchemaCommandService service) : DurableSchemaOnlineCommandBase(service)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var status = await RunOnlineAsync(
            ResolveConnectionString(),
            console.RegisterCancellationHandler(),
            Service.GetStatusAsync).ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightFailure(status.Compatibility));
        }

        await console.Output.WriteLineAsync(
            $"Compatible: durable schema {status.InstalledVersion.ToString(CultureInfo.InvariantCulture)}; runtime requires {status.RequiredVersion.ToString(CultureInfo.InvariantCulture)}.").ConfigureAwait(false);
    }
}

/// <summary>Shared connection-source option and safety behavior for durable schema commands.</summary>
internal abstract class DurableSchemaCommandBase(IDurableSchemaCommandService service) : ICommand
{
    /// <summary>Gets the injected schema command service.</summary>
    protected IDurableSchemaCommandService Service { get; } = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public abstract ValueTask ExecuteAsync(IConsole console);
}

/// <summary>Supplies the secret-safe connection source and bounded execution shared only by online schema commands.</summary>
internal abstract class DurableSchemaOnlineCommandBase(IDurableSchemaCommandService service) : DurableSchemaCommandBase(service)
{
    /// <summary>Maximum duration for a single online schema operation, including advisory-lock waiting.</summary>
    internal static readonly TimeSpan OnlineOperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the environment variable that contains the PostgreSQL connection string.</summary>
    /// <remarks>
    /// Connection strings are intentionally not accepted as command-line values because process listings and shell
    /// history are not appropriate secret stores. The variable's value is neither logged nor rendered by this command.
    /// </remarks>
    [CommandOption("connection-env", Description = "Environment variable containing the PostgreSQL connection string. Default: APPSURFACE_DURABLE_CONNECTION.")]
    public string ConnectionEnvironmentVariable { get; set; } = "APPSURFACE_DURABLE_CONNECTION";

    /// <summary>Resolves a connection string without printing or persisting it.</summary>
    protected string ResolveConnectionString()
    {
        var name = ConnectionEnvironmentVariable?.Trim();
        if (!IsEnvironmentVariableName(name))
        {
            throw new CommandException("--connection-env must name a non-empty environment variable using letters, digits, and underscores.");
        }

        var value = Environment.GetEnvironmentVariable(name!);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandException(
                $"Environment variable '{name}' is missing or blank. Set it to a migration-owner or read-only PostgreSQL connection before running this command.");
        }

        return value;
    }

    /// <summary>Runs an online command with a bounded linked cancellation token and safe provider failure mapping.</summary>
    protected static async ValueTask<T> RunOnlineAsync<T>(
        string connectionString,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OnlineOperationTimeout);
        try
        {
            return await operation(connectionString, deadline.Token).ConfigureAwait(false);
        }
        catch (DurableRuntimeSchemaException exception)
        {
            throw new CommandException(DurableSchemaDiagnostics.SchemaIncompatible(exception.Status.Compatibility));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new CommandException(
                $"Durable schema operation was canceled or exceeded its {FormatOnlineOperationTimeout()} deadline. Check PostgreSQL readiness or the package advisory lock, then retry.");
        }
        catch (NpgsqlException)
        {
            throw new CommandException(
                "Durable schema database operation failed. Check PostgreSQL reachability, role grants, and the package advisory lock; connection and server details were not printed.");
        }
        catch (TimeoutException)
        {
            throw new CommandException(
                $"Durable schema database operation timed out after its {FormatOnlineOperationTimeout()} deadline. Check PostgreSQL readiness or the package advisory lock, then retry.");
        }
    }

    /// <summary>Writes a stable schema status without exposing connection or server details.</summary>
    protected static async ValueTask WriteStatusAsync(IConsole console, DurableSchemaStatusView status)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(status);
        await console.Output.WriteLineAsync($"Compatibility: {status.Compatibility}").ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Installed: {status.InstalledVersion.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await console.Output.WriteLineAsync($"Required: {status.RequiredVersion.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await console.Output.WriteLineAsync(
            $"Pending: {(status.PendingVersions.Count == 0 ? "none" : string.Join(", ", status.PendingVersions.Select(static version => version.ToString("D4", CultureInfo.InvariantCulture))))}").ConfigureAwait(false);
        if (!status.IsCompatible)
        {
            await console.Output.WriteLineAsync($"Problem: {DurableSchemaDiagnostics.Cause(status.Compatibility)}").ConfigureAwait(false);
        }
    }

    private static bool IsEnvironmentVariableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        return value.All(static character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string FormatOnlineOperationTimeout() =>
        $"{OnlineOperationTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}-second";
}

/// <summary>Testable CLI boundary over the PostgreSQL schema manager.</summary>
internal interface IDurableSchemaCommandService
{
    /// <summary>Reads compatibility without mutation.</summary>
    ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Generates deterministic migration SQL without opening a connection.</summary>
    string GenerateScript(int fromVersion);

    /// <summary>Applies pending migrations with the supplied bounded token.</summary>
    ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString, CancellationToken cancellationToken);
}

/// <summary>Production CLI adapter that creates and disposes a short-lived Npgsql data source per online command.</summary>
internal sealed class DurableSchemaCommandService : IDurableSchemaCommandService
{
    /// <inheritdoc />
    public async ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(RequireConnectionString(connectionString));
        var status = await new PostgreSqlDurableRuntimeSchemaManager(dataSource).GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return DurableSchemaStatusView.From(status);
    }

    /// <inheritdoc />
    public string GenerateScript(int fromVersion)
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=offline-script-generation.invalid;Database=offline-script-generation;Username=offline-script-generation");
        return new PostgreSqlDurableRuntimeSchemaManager(dataSource).GenerateScript(fromVersion);
    }

    /// <inheritdoc />
    public async ValueTask<DurableSchemaApplyView> ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(RequireConnectionString(connectionString));
        var result = await new PostgreSqlDurableRuntimeSchemaManager(dataSource).ApplyAsync(cancellationToken).ConfigureAwait(false);
        return new DurableSchemaApplyView(result.PreviousVersion, result.CurrentVersion, result.AppliedVersions);
    }

    private static string RequireConnectionString(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(value))
            : value;
}

/// <summary>CLI-safe schema compatibility projection.</summary>
internal sealed record DurableSchemaStatusView(
    DurableRuntimeSchemaCompatibility Compatibility,
    int InstalledVersion,
    int RequiredVersion,
    IReadOnlyList<int> PendingVersions)
{
    /// <summary>Gets whether schema reads and writes may begin.</summary>
    internal bool IsCompatible => Compatibility == DurableRuntimeSchemaCompatibility.Compatible;

    /// <summary>Projects only safe schema status fields for the CLI.</summary>
    internal static DurableSchemaStatusView From(DurableRuntimeSchemaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new DurableSchemaStatusView(status.Compatibility, status.InstalledVersion, status.RequiredVersion, status.PendingVersions);
    }
}

/// <summary>CLI-safe schema apply projection.</summary>
internal sealed record DurableSchemaApplyView(int FromVersion, int ToVersion, IReadOnlyList<int> AppliedVersions);

/// <summary>Renders stable, secret-safe schema diagnostics.</summary>
internal static class DurableSchemaDiagnostics
{
    private const string DocumentationPath = "https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment";

    /// <summary>Builds the single-block incompatibility diagnostic used by preflight.</summary>
    internal static string PreflightFailure(DurableRuntimeSchemaCompatibility compatibility) =>
        $"Problem: durable schema preflight is {compatibility}. " +
        $"Cause: {Cause(compatibility)} " +
        "Fix: inspect status, generate a reviewed forward script, apply it with the migration owner, then retry preflight. " +
        $"Docs: {DocumentationPath}";

    /// <summary>Builds a stable failure for schema-manager incompatibility.</summary>
    internal static string SchemaIncompatible(DurableRuntimeSchemaCompatibility compatibility) =>
        $"Durable schema is {compatibility}. {Cause(compatibility)} Run 'appsurface durable schema status' before changing the schema.";

    /// <summary>Returns a package-defined safe explanation without forwarding server exception text.</summary>
    internal static string Cause(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => "The durable schema is not installed.",
        DurableRuntimeSchemaCompatibility.UpgradeRequired => "The installed schema is older than this runtime requires.",
        DurableRuntimeSchemaCompatibility.StoreTooNew => "The installed schema does not admit this runtime's protocol version.",
        DurableRuntimeSchemaCompatibility.Inconsistent => "Migration metadata is incomplete, altered, or invalid.",
        _ => "The installed reader/writer compatibility range does not include this runtime.",
    };
}

/// <summary>Writes generated scripts with atomic publication and explicit overwrite protection.</summary>
internal static class DurableSchemaScriptOutput
{
    /// <summary>Writes <paramref name="script"/> beside the requested destination then atomically publishes it.</summary>
    internal static async Task<string> WriteAsync(string requestedPath, string script, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new CommandException("--output must name a SQL file, or omit --output to write the script to standard output.");
        }

        ArgumentNullException.ThrowIfNull(script);
        string path;
        try
        {
            path = Path.GetFullPath(requestedPath.Trim());
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new IOException("The output path has no directory.");
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporaryPath, path, overwrite: force);
                }
                catch (IOException) when (!force)
                {
                    throw new CommandException($"Output file already exists: {path}. Pass --force to atomically replace this generated migration artifact.");
                }

                return path;
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        catch (CommandException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new CommandException("Writing the durable migration script was canceled. No partially written output was published.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new CommandException("Durable migration script output could not be written. Check that --output names a writable destination.");
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
