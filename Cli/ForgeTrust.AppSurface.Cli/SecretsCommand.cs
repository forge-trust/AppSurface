using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for AppSurface local secret commands.
/// </summary>
[Command("secrets", Description = "Manage AppSurface local development secrets and explicit remote transfers.")]
internal sealed partial class SecretsCommand : ICommand
{
    /// <summary>
    /// Prints the local secrets command family summary.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <returns>A completed task.</returns>
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; subcommands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Use 'appsurface secrets init', 'set', 'get', 'list', 'migrate', 'migrate-key', 'migrate-key recover', 'delete', and 'doctor' to manage local development secrets, or 'appsurface secrets transfer plan' and 'appsurface secrets transfer apply' for explicit remote transfer.");
    }
}

/// <summary>
/// Initializes or verifies a LocalSecrets namespace.
/// </summary>
[Command("secrets init", Description = "Initialize or verify an AppSurface LocalSecrets namespace.")]
internal sealed partial class SecretsInitCommand : SecretsCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var result = context.Store.Doctor(context.ApplicationName, context.Environment, context.KeyPrefix);
        await WriteResultAsync(console, result, successVerb: "Initialized");
    }
}

/// <summary>
/// Writes a local secret value.
/// </summary>
[Command("secrets set", Description = "Set one AppSurface local secret value.")]
internal sealed partial class SecretsSetCommand(LocalSecretsTransferCoordinator transferCoordinator) : SecretsKeyCommandBase
{
    /// <summary>
    /// Gets or sets the secret value.
    /// </summary>
    [CommandOption("value", 'v', Description = "Secret value to store. Prefer --stdin for real secrets.")]
    public string? Value { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to read the secret value from standard input.
    /// </summary>
    [CommandOption("stdin", Description = "Read the secret value from standard input instead of a command-line argument.")]
    public bool ReadFromStandardInput { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        if (Value != null && ReadFromStandardInput)
        {
            throw new CommandException("Use either --value or --stdin for `appsurface secrets set`, not both.");
        }

        var value = Value;
        if (ReadFromStandardInput)
        {
            value = (await console.Input.ReadToEndAsync()).TrimEnd('\r', '\n');
        }

        if (value == null)
        {
            throw new CommandException("Missing secret value for `appsurface secrets set`; pass --stdin or --value.");
        }

        var context = BuildContext();
        var identity = Normalize(context);
        var result = transferCoordinator.InvalidateBeforeMutation(
            context.Store,
            identity,
            () => context.Store.Set(identity, value));
        await WriteResultAsync(console, result, successVerb: "Set");
    }
}

/// <summary>
/// Verifies a local secret exists without printing its value.
/// </summary>
[Command("secrets get", Description = "Verify one AppSurface local secret without printing the value.")]
internal sealed partial class SecretsGetCommand : SecretsKeyCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var identity = Normalize(context);
        var result = context.Store.Get(identity);
        await WriteResultAsync(console, result, successVerb: "Found");
    }
}

/// <summary>
/// Lists currently retrievable local secret names in a namespace.
/// </summary>
[Command("secrets list", Description = "List currently retrievable AppSurface local secret names without values.")]
internal sealed partial class SecretsListCommand : SecretsCommandBase
{
    /// <summary>
    /// Gets or sets a value indicating whether to print only secret names.
    /// </summary>
    [CommandOption("names-only", Description = "Print only local secret names, without source metadata.")]
    public bool NamesOnly { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var result = context.Store.List(context.ApplicationName, context.Environment, context.KeyPrefix);
        if (result.Status == LocalSecretResultStatus.Found)
        {
            if (!NamesOnly)
            {
                await console.Output.WriteLineAsync($"Source: {result.Source}");
            }

            foreach (var key in result.Keys)
            {
                await console.Output.WriteLineAsync(key);
            }

            return;
        }

        throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Local secret list failed.");
    }
}

/// <summary>
/// Explicitly migrates readable legacy macOS LocalSecrets records into the current v2 Keychain namespace.
/// </summary>
/// <remarks>
/// The command is intentionally unavailable for deterministic file stores and other platforms. It never prints secret
/// values, never deletes v1 records, and never overwrites an existing v2 record; use <c>secrets set</c> to update a
/// canonical v2 value after migration.
/// </remarks>
[Command("secrets migrate", Description = "Migrate readable legacy macOS LocalSecrets records into the v2 namespace without printing values.")]
internal partial class SecretsMigrateCommand : SecretsCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        if (context.Store is not IAppSurfaceLocalSecretMigrationStore migrationStore)
        {
            throw new CommandException(MigrationUnsupported().ToDisplayString());
        }

        var result = migrationStore.Migrate(context.ApplicationName, context.Environment, context.KeyPrefix);
        if (result.Status != LocalSecretResultStatus.Found)
        {
            throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Local secret migration could not start.");
        }

        foreach (var row in result.Rows)
        {
            await console.Output.WriteLineAsync($"{row.Key}: {row.Action.ToDisplayString()}");
            if (row.Diagnostic != null)
            {
                await console.Output.WriteLineAsync(row.Diagnostic.ToDisplayString());
            }
        }

        await console.Output.WriteLineAsync($"Migrated: {result.Migrated}");
        await console.Output.WriteLineAsync($"AlreadyV2: {result.AlreadyV2}");
        await console.Output.WriteLineAsync($"Failed: {result.Failed}");
        await console.Output.WriteLineAsync($"Source: {result.Source}");

        if (result.Failed != 0)
        {
            throw new CommandException("One or more local secrets could not be migrated. Review the value-safe per-key diagnostics, resolve the issue, and rerun the same command.");
        }
    }

    private static AppSurfaceLocalSecretDiagnostic MigrationUnsupported() =>
        new(
            "local-secret-migration-unsupported",
            "Local secret migration is unavailable for this store.",
            "The selected backend cannot prove the shared lease, durable journal, confirmed operations, and index ordering required for safe migration.",
            "Use a backend with the complete migration capability, or set the intended value explicitly with `appsurface secrets set`.",
            "local-secrets-migration");
}

/// <summary>Migrates one exact stored LocalSecrets identifier to a strict logical destination key.</summary>
/// <remarks>
/// The default invocation previews identifiers and parsed destination segments without reading values.
/// After inspecting the preview, repeat it with <c>--apply</c> to confirm the journaled migration.
/// Display identifiers are escaped and bounded. Executable command output is omitted when an argument needs
/// display transformation, while the original source and destination remain unchanged for backend operations.
/// The inherited platform-store factory is the test seam for exercising this command without OS credentials.
/// </remarks>
[Command("secrets migrate-key", Description = "Copy, verify, and safely migrate one exact LocalSecrets identifier without printing values.")]
internal partial class SecretsMigrateKeyCommand : SecretsCommandBase
{
    /// <summary>Gets or sets the exact source identifier as stored by the selected backend.</summary>
    [CommandOption("from-stored-key", Description = "Exact source backend identifier; bypasses key parsing.")]
    public required string SourceStoredKey { get; set; }

    /// <summary>Gets or sets the strict destination logical key.</summary>
    [CommandOption("to", Description = "Strict colon path destination logical key.")]
    public required string DestinationKey { get; set; }

    /// <summary>Gets or sets whether to confirm the migration after reviewing its identifiers.</summary>
    [CommandOption("apply", Description = "Confirm the previewed exact-key migration. Omit to preview without reading or changing values.")]
    public bool Apply { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        if (!AppSurfaceConfigKey.TryParse(DestinationKey, out var destination))
        {
            throw new CommandException(new AppSurfaceLocalSecretDiagnostic(
                "config-key-invalid",
                "The migration destination is not a valid logical key.",
                "The destination must contain nonempty literal segments separated by colons.",
                "Correct --to; dots remain literal and no legacy translation is performed.",
                "https://appsurface.dev/guides/config-key-migration").ToDisplayString());
        }

        if (string.IsNullOrWhiteSpace(SourceStoredKey))
        {
            throw new CommandException("Supply --from-stored-key with the exact nonempty identifier reported by doctor.");
        }

        var context = BuildContext();
        if (context.Store is not IAppSurfaceLocalSecretMigrationStore migrationStore)
        {
            throw new CommandException(new AppSurfaceLocalSecretDiagnostic(
                "local-secret-migration-unsupported",
                "Exact-key migration is unsupported by the selected backend.",
                "The store does not implement the migration capability; no values were read.",
                "Choose a store with shared maintenance leases and durable migration support.",
                "https://appsurface.dev/guides/config-key-migration").ToDisplayString());
        }

        var destinationIdentity = migrationStore.GetKeyMigrationDestinationIdentity(
            context.ApplicationName, context.Environment, context.KeyPrefix, destination);
        if (!destinationIdentity.Succeeded || destinationIdentity.Identity is null)
        {
            throw new CommandException(destinationIdentity.Diagnostic!.ToDisplayString());
        }

        await console.Output.WriteLineAsync($"Source stored identifier: {DisplayIdentifier(SourceStoredKey)}");
        await console.Output.WriteLineAsync($"Destination logical key: {DisplayIdentifier(destination.Value)}");
        await console.Output.WriteLineAsync($"Destination segments: {ConfigDiagnosticText.Identifier($"[{string.Join(", ", destination.Segments.Select(ShellQuote))}]")}");
        await console.Output.WriteLineAsync($"Destination storage identity: {DisplayIdentifier(destinationIdentity.Identity.StorageName)}");
        string?[] commandArguments = [context.ApplicationName, context.Environment, context.KeyPrefix, StoreFile, SecretToolPath, SourceStoredKey, destination.Value];
        if (commandArguments.All(value => value is null || ConfigDiagnosticText.Identifier(value) == value))
        {
            await console.Output.WriteLineAsync($"Command: appsurface secrets migrate-key --app {ShellQuote(context.ApplicationName)} --environment {ShellQuote(context.Environment)}{FormatOptional("--prefix", context.KeyPrefix)}{FormatOptional("--store-file", StoreFile)}{FormatOptional("--secret-tool-path", SecretToolPath)} --from-stored-key {ShellQuote(SourceStoredKey)} --to {ShellQuote(destination.Value)}");
        }
        else
        {
            await console.Output.WriteLineAsync("Command preview omitted because an argument requires escaping or truncation. Repeat your original invocation with --apply; the displayed identifiers are for inspection only.");
        }

        if (!Apply)
        {
            await console.Output.WriteLineAsync("Preview only. Inspect the source, destination segments, and storage identity, then repeat with --apply to confirm migration.");
            return;
        }

        var result = migrationStore.MigrateKey(
            context.ApplicationName, context.Environment, context.KeyPrefix, SourceStoredKey, destination);
        if (result.Status != LocalSecretResultStatus.Found)
        {
            if (result.MigrationId.Length == 32 && result.MigrationId.All(Uri.IsHexDigit))
                await console.Output.WriteLineAsync($"Migration id: {DisplayIdentifier(result.MigrationId)}");
            throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Exact-key migration failed.");
        }

        await console.Output.WriteLineAsync($"Migration state: {result.State}");
        await console.Output.WriteLineAsync($"Migration id: {DisplayIdentifier(result.MigrationId)}");
    }

    private static string FormatOptional(string option, string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $" {option} {ShellQuote(value)}";

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>Uses the shared value-free renderer; displayed truncation is never reused as an executable argument.</summary>
    private static string DisplayIdentifier(string value) => ShellQuote(ConfigDiagnosticText.Identifier(value));
}

/// <summary>Inspects or explicitly retains/releases an unfinished file-backed exact-key migration.</summary>
/// <remarks>Neither operation writes or deletes a secret value. Release is for operator reconciliation only.</remarks>
[Command("secrets migrate-key recover", Description = "Inspect, retain, or release one stalled file LocalSecrets migration journal.")]
internal partial class SecretsMigrateKeyRecoverCommand : SecretsCommandBase
{
    /// <summary>Gets or sets the exact migration identifier returned by a failed migration.</summary>
    [CommandOption("migration-id", Description = "Exact migration journal id to inspect under the store lease.")]
    public required string MigrationId { get; set; }

    /// <summary>Gets or sets whether to perform the previewed durable metadata transition.</summary>
    [CommandOption("apply", Description = "Confirm retention or release after inspecting the preview.")]
    public bool Apply { get; set; }

    /// <summary>Gets or sets whether to release a retained journal after operator reconciliation.</summary>
    [CommandOption("release", Description = "Select a retained journal for explicit release after reconciliation.")]
    public bool Release { get; set; }

    /// <summary>Gets or sets the state printed by preview; required with <see cref="Apply"/>.</summary>
    [CommandOption("state", Description = "Exact previewed journal state; required with --apply.")]
    public string? ExpectedState { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        if (string.IsNullOrWhiteSpace(MigrationId))
            throw new CommandException("Supply the exact --migration-id from the failed migration or doctor output.");
        AppSurfaceLocalSecretMigrationState? state = null;
        if (Apply)
        {
            if (!Enum.TryParse<AppSurfaceLocalSecretMigrationState>(ExpectedState, false, out var parsed)
                || !Enum.IsDefined(parsed))
                throw new CommandException("Use --state with the exact state printed by the recovery preview before --apply.");
            state = parsed;
        }

        var context = BuildContext();
        if (context.Store is not IAppSurfaceLocalSecretMigrationRecoveryStore recovery)
            throw new CommandException("local-secret-migration-recovery-unsupported: The selected store has no shared file migration journal to recover.");
        var result = recovery.RecoverKeyMigration(context.ApplicationName, context.Environment, context.KeyPrefix,
            MigrationId, Apply, Release, state);
        if (result.Status != LocalSecretResultStatus.Found)
            throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Migration recovery failed.");

        await console.Output.WriteLineAsync($"Migration id: {DisplayIdentifier(result.MigrationId)}");
        await console.Output.WriteLineAsync($"State: {result.State}");
        await console.Output.WriteLineAsync($"Source stored identifier: {DisplayIdentifier(result.SourceStoredKey!)}");
        await console.Output.WriteLineAsync($"Destination stored identifier: {DisplayIdentifier(result.DestinationStoredKey!)}");
        await console.Output.WriteLineAsync($"Source present: {result.SourcePresent}");
        await console.Output.WriteLineAsync($"Destination present: {result.DestinationPresent}");
        await console.Output.WriteLineAsync($"Retained unresolved guard: {result.Retained}");
        if (!Apply)
            await console.Output.WriteLineAsync("Preview only. Repeat with --apply --state <the state above> after inspecting both exact identifiers.");
    }

    private static string DisplayIdentifier(string value) => $"'{ConfigDiagnosticText.Identifier(value).Replace("'", "'\\''", StringComparison.Ordinal)}'";
}

/// <summary>
/// Deletes one local secret.
/// </summary>
[Command("secrets delete", Description = "Delete one local secret and its local transfer attestation; never changes remote vaults.")]
internal sealed partial class SecretsDeleteCommand(LocalSecretsTransferCoordinator transferCoordinator) : SecretsKeyCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var identity = Normalize(context);
        var result = transferCoordinator.InvalidateBeforeMutation(
            context.Store,
            identity,
            () => context.Store.Delete(identity));
        await WriteResultAsync(console, result, successVerb: "Deleted");
    }
}

/// <summary>
/// Diagnoses LocalSecrets platform availability.
/// </summary>
[Command("secrets doctor", Description = "Diagnose AppSurface LocalSecrets store availability.")]
internal sealed partial class SecretsDoctorCommand : SecretsCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var result = context.Store.Doctor(context.ApplicationName, context.Environment, context.KeyPrefix);
        await WriteResultAsync(console, result, successVerb: "Ready");
    }
}

/// <summary>
/// Shared options and helpers for local secret commands.
/// </summary>
internal abstract class SecretsCommandBase : ICommand
{
    /// <summary>
    /// Gets or sets the AppSurface application identity.
    /// </summary>
    [CommandOption("app", Description = "Stable AppSurface application identity for the local secret namespace.")]
    public string ApplicationName { get; set; } = "AppSurfaceApp";

    /// <summary>
    /// Gets or sets the AppSurface environment identity.
    /// </summary>
    [CommandOption("environment", 'e', Description = "AppSurface environment for the local secret namespace. Defaults to Development.")]
    public string EnvironmentName { get; set; } = "Development";

    /// <summary>
    /// Gets or sets an optional LocalSecrets key prefix.
    /// </summary>
    [CommandOption("prefix", Description = "Optional LocalSecrets key prefix.")]
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets an optional file-backed store path for deterministic examples and tests.
    /// </summary>
    [CommandOption("store-file", Description = "Use a file-backed store instead of the OS store. Intended for examples and tests.")]
    public string? StoreFile { get; set; }

    /// <summary>
    /// Gets or sets an explicit Linux secret-tool executable path for nonstandard trusted installs.
    /// </summary>
    [CommandOption("secret-tool-path", Description = "Linux-only trusted absolute path to secret-tool. Not used with --store-file.")]
    public string? SecretToolPath { get; set; }

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <returns>A value task that completes when the command finishes.</returns>
    public abstract ValueTask ExecuteAsync(IConsole console);

    /// <summary>
    /// Builds a normalized command context.
    /// </summary>
    /// <returns>The command context.</returns>
    protected SecretsCommandContext BuildContext()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var probe = normalizer.Normalize(ApplicationName, EnvironmentName, KeyPrefix, "__probe__");
        if (!probe.Succeeded)
        {
            throw new CommandException(probe.Diagnostic!.ToDisplayString());
        }

        if (StoreFile != null && SecretToolPath != null)
        {
            throw new CommandException("Use either --store-file or --secret-tool-path for `appsurface secrets`, not both.");
        }

        IAppSurfaceLocalSecretStore store = string.IsNullOrWhiteSpace(StoreFile)
            ? CreatePlatformStore(new AppSurfaceLocalSecretsOptions
            {
                LinuxSecretToolPath = SecretToolPath
            })
            : new FileAppSurfaceLocalSecretStore(StoreFile);

        return new SecretsCommandContext(
            normalizer,
            store,
            probe.Identity!.ApplicationName,
            probe.Identity.Environment,
            probe.Identity.KeyPrefix);
    }

    /// <summary>
    /// Creates the OS-backed LocalSecrets store for commands that do not use the deterministic file store.
    /// </summary>
    /// <param name="options">Options derived from the CLI command line.</param>
    /// <returns>The platform-backed local secret store.</returns>
    protected virtual IAppSurfaceLocalSecretStore CreatePlatformStore(AppSurfaceLocalSecretsOptions options) =>
        new PlatformAppSurfaceLocalSecretStore(Options.Create(options));

    /// <summary>
    /// Writes a command result.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <param name="result">The local secret result.</param>
    /// <param name="successVerb">The success verb to display.</param>
    /// <returns>A value task that completes when output is written.</returns>
    /// <remarks>
    /// LocalSecrets treats <see cref="LocalSecretResultStatus.Missing"/> as failure everywhere except doctor-style
    /// readiness probes that return a ready-class posture diagnostic. Keep that exception explicit when adding commands
    /// so ordinary missing secrets do not report success.
    /// </remarks>
    protected static async ValueTask WriteResultAsync(
        IConsole console,
        AppSurfaceLocalSecretResult result,
        string successVerb)
    {
        if (result.Status == LocalSecretResultStatus.Found
            || result.Status == LocalSecretResultStatus.Missing
            && IsDoctorSuccessDiagnostic(result.Diagnostic?.Code))
        {
            await console.Output.WriteLineAsync($"{successVerb}: local secret namespace");
            await console.Output.WriteLineAsync($"Source: {result.Source}");
            if (result.Diagnostic != null)
            {
                await console.Output.WriteLineAsync(result.Diagnostic.ToDisplayString());
            }

            return;
        }

        if (result.Status == LocalSecretResultStatus.Missing)
        {
            throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Local secret was not found.");
        }

        throw new CommandException(result.Diagnostic?.ToDisplayString() ?? "Local secret command failed.");
    }

    private static bool IsDoctorSuccessDiagnostic(string? code) =>
        code is "local-secret-store-ready"
            or "local-secret-file-posture-repaired"
            or "local-secret-file-posture-degraded"
            or "local-secret-migration-recovery-pending";
}

/// <summary>
/// Shared options for commands that target one local secret key.
/// </summary>
internal abstract class SecretsKeyCommandBase : SecretsCommandBase
{
    /// <summary>
    /// Gets or sets the AppSurface config key.
    /// </summary>
    [CommandParameter(0, Description = "AppSurface config key, for example Stripe:ApiKey.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Normalizes the configured key into a local secret identity.
    /// </summary>
    /// <param name="context">The command context.</param>
    /// <returns>The local secret identity.</returns>
    protected AppSurfaceLocalSecretIdentity Normalize(SecretsCommandContext context)
    {
        var result = context.Normalizer.Normalize(context.ApplicationName, context.Environment, context.KeyPrefix, Key);
        if (!result.Succeeded)
        {
            throw new CommandException(result.Diagnostic!.ToDisplayString());
        }

        return result.Identity!;
    }
}

/// <summary>
/// Captures normalized command state.
/// </summary>
/// <param name="Normalizer">Identity normalizer.</param>
/// <param name="Store">Local secret store.</param>
/// <param name="ApplicationName">Normalized application identity.</param>
/// <param name="Environment">Normalized environment identity.</param>
/// <param name="KeyPrefix">Normalized optional key prefix.</param>
internal sealed record SecretsCommandContext(
    AppSurfaceLocalSecretIdentityNormalizer Normalizer,
    IAppSurfaceLocalSecretStore Store,
    string ApplicationName,
    string Environment,
    string? KeyPrefix);
