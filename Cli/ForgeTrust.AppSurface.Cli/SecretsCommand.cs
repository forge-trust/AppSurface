using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for AppSurface local secret commands.
/// </summary>
[Command("secrets", Description = "Manage AppSurface local development secrets.")]
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
        await console.Output.WriteLineAsync("Use 'appsurface secrets init', 'set', 'get', 'list', 'delete', and 'doctor' to manage local development secrets.");
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
internal sealed partial class SecretsSetCommand : SecretsKeyCommandBase
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
        var result = context.Store.Set(identity, value);
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
/// Deletes one local secret.
/// </summary>
[Command("secrets delete", Description = "Delete one AppSurface local secret value.")]
internal sealed partial class SecretsDeleteCommand : SecretsKeyCommandBase
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var context = BuildContext();
        var identity = Normalize(context);
        var result = context.Store.Delete(identity);
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

        IAppSurfaceLocalSecretStore store = string.IsNullOrWhiteSpace(StoreFile)
            ? new PlatformAppSurfaceLocalSecretStore()
            : new FileAppSurfaceLocalSecretStore(StoreFile);

        return new SecretsCommandContext(
            normalizer,
            store,
            probe.Identity!.ApplicationName,
            probe.Identity.Environment,
            probe.Identity.KeyPrefix);
    }

    /// <summary>
    /// Writes a command result.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <param name="result">The local secret result.</param>
    /// <param name="successVerb">The success verb to display.</param>
    /// <returns>A value task that completes when output is written.</returns>
    /// <remarks>
    /// LocalSecrets treats <see cref="LocalSecretResultStatus.Missing"/> as failure everywhere except doctor-style
    /// readiness probes that return the <c>local-secret-store-ready</c> diagnostic. Keep that exception explicit when
    /// adding commands so ordinary missing secrets do not report success.
    /// </remarks>
    protected static async ValueTask WriteResultAsync(
        IConsole console,
        AppSurfaceLocalSecretResult result,
        string successVerb)
    {
        if (result.Status == LocalSecretResultStatus.Found
            || result.Status == LocalSecretResultStatus.Missing
            && result.Diagnostic?.Code == "local-secret-store-ready")
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
