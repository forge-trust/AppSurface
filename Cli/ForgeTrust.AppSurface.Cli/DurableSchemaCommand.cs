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
            var cancellationToken = console.RegisterCancellationHandler();
            await console.Output.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
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
    /// <summary>
    /// The apply deadline covers the package lock wait, all pending migration commands (including the two with
    /// 330-second deadlines), status reads, metadata writes, transaction boundaries, and lock cleanup.
    /// </summary>
    internal static readonly TimeSpan ApplyOperationTimeout = TimeSpan.FromMinutes(45);

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
            Service.ApplyAsync,
            ApplyOperationTimeout).ConfigureAwait(false);
        var applied = result.AppliedVersions.Count == 0
            ? "none"
            : string.Join(", ", result.AppliedVersions.Select(static version => version.ToString("D4", CultureInfo.InvariantCulture)));
        await console.Output.WriteLineAsync(
            $"Durable schema: {result.FromVersion} -> {result.ToVersion}; applied: {applied}.").ConfigureAwait(false);
    }
}

/// <summary>Verifies a complete reviewed role manifest and schema in one fenced, read-only snapshot.</summary>
/// <remarks>
/// Both inputs are mandatory even for one pair. Input validation precedes connection resolution. Runtime
/// evidence covers only the observed credential; activation requires every distinct runtime and the continuous
/// application-owned guard described in Durable/heartbeat-retention-operations.md.
/// </remarks>
[Command("durable schema preflight", Description = "Verify schema and the complete reviewed role set using runtime or owner-diagnostic credentials.")]
internal sealed partial class DurableSchemaPreflightCommand(IDurableSchemaCommandService service) : DurableSchemaOnlineCommandBase(service)
{
    /// <summary>Gets or sets the complete reviewed version-1 UTF-8 manifest path, including for one pair.</summary>
    [CommandOption("role-pairs-file", Description = "Required complete reviewed version-1 role manifest (1–32 pairs, strict UTF-8, at most 64 KiB). Required for one pair too.")]
    public string? RolePairsFile { get; set; }

    /// <summary>Gets or sets the independently reviewed migration-owner role; no catalog-derived default exists.</summary>
    [CommandOption("migration-owner-role", Description = "Required independently reviewed migration-owner role name, distinct from every manifest role.")]
    public string? MigrationOwnerRole { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var cancellationToken = console.RegisterCancellationHandler();
        DurablePreflightRequest request;
        try
        {
            if (RolePairsFile is null || MigrationOwnerRole is null)
            {
                throw new ArgumentException("Both --role-pairs-file and --migration-owner-role are required, including for one pair.");
            }

            var manifest = await DurableRoleManifest.ReadAsync(RolePairsFile, cancellationToken).ConfigureAwait(false);
            request = new DurablePreflightRequest(manifest, MigrationOwnerRole);
        }
        catch (ArgumentException)
        {
            throw new CommandException(DurableSchemaDiagnostics.ManifestInputFailure());
        }
        catch (OperationCanceledException)
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("timeout"));
        }

        string connectionString;
        try
        {
            connectionString = ResolveConnectionString();
        }
        catch (CommandException)
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("connection_input"));
        }

        var result = await RunOnlineAsync(
            connectionString, cancellationToken,
            async (connection, token) =>
            {
                var evidence = await Service.PreflightAsync(connection, request, token).ConfigureAwait(false);
                DurableSchemaPreflightVerifier.ValidateEvidenceResult(evidence, request);
                return evidence;
            }, preflightDiagnostics: true).ConfigureAwait(false);
        if (!result.Status.IsCompatible)
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightFailure(
                result.Status.Compatibility,
                result.Status.Compatibility == DurableRuntimeSchemaCompatibility.UpgradeRequired
                    && result.Status.PendingVersions is [11]));
        }

        if (result.FailedChecks.Count != 0)
        {
            throw new CommandException(DurableSchemaDiagnostics.RetentionStructureFailure(result.FailedChecks));
        }

        await console.Output.WriteLineAsync(
            $"Compatible: durable schema {result.Status.InstalledVersion.ToString(CultureInfo.InvariantCulture)}; runtime requires {result.Status.RequiredVersion.ToString(CultureInfo.InvariantCulture)}.").ConfigureAwait(false);
        await console.Output.WriteLineAsync(
            $"caller={result.CallerEvidenceKind}; pair={result.PairIndex?.ToString(CultureInfo.InvariantCulture) ?? "none"}; role={System.Text.Json.JsonSerializer.Serialize(result.CallerRole)}; owner={System.Text.Json.JsonSerializer.Serialize(request.MigrationOwnerRole)}; runtimes={request.Manifest.Pairs.Length.ToString(CultureInfo.InvariantCulture)}; manifest_sha256={request.Manifest.Sha256}; store_id={result.StoreId:D}; active_epoch={result.ActiveRuntimeEpoch?.ToString("D", CultureInfo.InvariantCulture) ?? "null"}.").ConfigureAwait(false);
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
        Func<string, CancellationToken, ValueTask<T>> operation,
        TimeSpan? operationTimeout = null,
        bool preflightDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var timeout = operationTimeout ?? OnlineOperationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await operation(connectionString, deadline.Token).ConfigureAwait(false);
        }
        catch (DurablePreflightException exception)
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure(exception.Category));
        }
        catch (DurableRuntimeSchemaException exception)
        {
            if (preflightDiagnostics)
            {
                throw new CommandException(DurableSchemaDiagnostics.PreflightFailure(exception.Status.Compatibility));
            }
            throw new CommandException(DurableSchemaDiagnostics.SchemaIncompatible(exception.Status.Compatibility));
        }
        catch (Exception exception) when (preflightDiagnostics && exception is (InvalidDataException or InvalidOperationException))
        {
            throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("catalog_result"));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (preflightDiagnostics)
            {
                throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("timeout"));
            }
            throw new CommandException(
                $"Durable schema operation was canceled or exceeded its {FormatOnlineOperationTimeout(timeout)} deadline. Check PostgreSQL readiness or the package advisory lock, then retry.");
        }
        catch (NpgsqlException)
        {
            if (preflightDiagnostics)
            {
                throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("database_operation"));
            }
            throw new CommandException(
                "Durable schema database operation failed. Check PostgreSQL reachability, role grants, and the package advisory lock; connection and server details were not printed.");
        }
        catch (TimeoutException)
        {
            if (preflightDiagnostics)
            {
                throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("timeout"));
            }
            throw new CommandException(
                $"Durable schema database operation timed out after its {FormatOnlineOperationTimeout(timeout)} deadline. Check PostgreSQL readiness or the package advisory lock, then retry.");
        }
        catch (ArgumentException)
        {
            if (preflightDiagnostics)
            {
                throw new CommandException(DurableSchemaDiagnostics.PreflightOperationFailure("connection_input"));
            }
            throw new CommandException(
                "Durable schema connection configuration is invalid. Check the named environment variable without printing its value, then retry.");
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

    private static string FormatOnlineOperationTimeout(TimeSpan timeout) =>
        $"{timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}-second";
}

/// <summary>Testable CLI boundary over the PostgreSQL schema manager.</summary>
internal interface IDurableSchemaCommandService
{
    /// <summary>Reads compatibility without mutation.</summary>
    ValueTask<DurableSchemaStatusView> GetStatusAsync(string connectionString, CancellationToken cancellationToken);

    /// <summary>Reads compatibility, caller identity and every complete-manifest structural check in one fenced snapshot.</summary>
    /// <remarks>Owns one nonpooled session through checked cleanup; the request is validated before database access.</remarks>
    ValueTask<DurablePreflightResult> PreflightAsync(string connectionString, DurablePreflightRequest request, CancellationToken cancellationToken);

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
    public ValueTask<DurablePreflightResult> PreflightAsync(
        string connectionString, DurablePreflightRequest request, CancellationToken cancellationToken) =>
        DurableSchemaPreflightVerifier.VerifyAsync(RequireConnectionString(connectionString), request, cancellationToken);

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
/// <param name="Compatibility">The package-defined compatibility state that determines whether durable reads and writes may begin.</param>
/// <param name="InstalledVersion">The non-negative durable schema version currently installed in the target database.</param>
/// <param name="RequiredVersion">The non-negative durable schema version required by this package version.</param>
/// <param name="PendingVersions">The ordered, non-null migration versions that remain to be applied; empty when no migration is pending.</param>
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
/// <param name="FromVersion">The non-negative schema version observed before the explicit apply operation began.</param>
/// <param name="ToVersion">The non-negative schema version observed after the apply operation completed.</param>
/// <param name="AppliedVersions">The ordered, non-null migration versions applied by this invocation; empty when the schema was already current.</param>
internal sealed record DurableSchemaApplyView(int FromVersion, int ToVersion, IReadOnlyList<int> AppliedVersions);

/// <summary>Renders stable, secret-safe schema diagnostics.</summary>
internal static class DurableSchemaDiagnostics
{
    private const string DocumentationPath = "https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment";

    /// <summary>Builds the single-block incompatibility diagnostic used by preflight.</summary>
    internal static string PreflightFailure(DurableRuntimeSchemaCompatibility compatibility, bool heartbeatRetentionPending = false) =>
        heartbeatRetentionPending
            ? "Problem: durable schema preflight found pending migration 0011, which requires a drained maintenance window. This is an expected downtime finding, not a passing gate. " +
              "Cause: heartbeat retention structures are not installed. " +
              "Fix: keep activation closed, drain old runtimes, apply the reviewed 0011 script as migration owner, reapply the role recipe, then rerun preflight with the runtime role. " +
              $"Docs: {DocumentationPath}"
            : $"Problem: durable schema preflight is {compatibility}. " +
              $"Cause: {Cause(compatibility)} " +
              "Fix: inspect status, generate a reviewed forward script, apply it with the migration owner, then retry preflight. " +
              $"Docs: {DocumentationPath}";

    /// <summary>Builds a fixed, secret-safe failure for schema-11 structure or privilege drift.</summary>
    internal static string RetentionStructureFailure(IReadOnlyList<string> failedChecks) =>
        "Problem: durable schema 0011 structural preflight failed. Cause: the function, runtime grants, role membership, or index does not match the complete reviewed contract. Fix: inspect the named checks, obtain a reviewed repair (the role recipe may refuse broader drift), then rerun with the unchanged complete manifest under every distinct runtime credential. Owner diagnostics do not count as runtime evidence. Keep activation closed until the continuously guarded combined gate passes. Docs: " +
        PreflightDocumentationPath + " Failed checks: " + string.Join(", ", failedChecks) + ".";

    private const string PreflightDocumentationPath = "https://github.com/forge-trust/AppSurface/blob/main/Durable/heartbeat-retention-operations.md#deploy-schema-11";

    /// <summary>Returns a fixed input diagnostic without echoing paths, JSON, role input or provider errors.</summary>
    internal static string ManifestInputFailure() =>
        "Problem: durable preflight manifest_input failed before database access. Cause: --role-pairs-file and --migration-owner-role are required, including for one pair, and must satisfy the complete strict UTF-8 version-1 manifest and independent owner contract. Fix: review the complete file (64 KiB, depth 8, 1–32 pairs, 63 UTF-8 byte role names), its exact byte hash and the disjoint owner; then rerun every distinct runtime with the unchanged file. Docs: " + PreflightDocumentationPath;

    /// <summary>Returns a fixed bounded-operation diagnostic; only trusted category names are rendered.</summary>
    internal static string PreflightOperationFailure(string category)
    {
        var (safeCategory, cause, fix) = category switch
        {
            "connection_input" => ("connection_input", "The connection environment name, value, or PostgreSQL configuration is invalid.",
                "Set the named environment variable to the reviewed credential without printing its value, then rerun with the unchanged complete manifest."),
            "database_operation" => ("database_operation", "The read-only database operation could not complete.",
                "Inspect PostgreSQL reachability, exact role grants and the session-affine package fence, then repeat the complete guarded gate."),
            "timeout" => ("timeout", "Cancellation or the bounded session deadline interrupted preflight.",
                "Resolve connection or lock blockers, verify resource release, then repeat the complete guarded gate."),
            "cleanup" => ("cleanup", "The owned session fence or physical cleanup could not be verified.",
                "Verify the owned backend and advisory lock were released, repair the session-affine endpoint, then repeat the complete guarded gate."),
            _ => ("catalog_result", "The catalog or service returned incomplete or unexpected evidence.",
                "Inspect schema compatibility and the reviewed catalog contract, obtain a reviewed repair, then repeat the complete guarded gate.")
        };
        return $"Problem: durable preflight {safeCategory} failed. Cause: {cause} Fix: keep activation closed. {fix} Docs: {PreflightDocumentationPath}";
    }

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
    private static readonly AsyncLocal<Action?> TemporaryFileWrittenHook = new();

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
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(requestedPath.Trim());
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new IOException("The output path has no directory.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Join(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
                TemporaryFileWrittenHook.Value?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>Runs a callback after temporary SQL output is written and before publication.</summary>
    /// <remarks>
    /// This test-only seam is async-flow-local so concurrent output tests can deterministically exercise the final
    /// publish window without affecting production writes or other tests.
    /// </remarks>
    internal static IDisposable UseTemporaryFileWrittenHookForTesting(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previous = TemporaryFileWrittenHook.Value;
        TemporaryFileWrittenHook.Value = callback;
        return new TemporaryFileWrittenHookScope(previous);
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not mask the script output's primary outcome.
        }
    }

    private sealed class TemporaryFileWrittenHookScope(Action? previous) : IDisposable
    {
        public void Dispose()
        {
            TemporaryFileWrittenHook.Value = previous;
        }
    }
}
