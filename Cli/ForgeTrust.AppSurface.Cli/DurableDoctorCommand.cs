using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Inspects one configured Durable store and runtime without changing the database.</summary>
/// <remarks>
/// Connection strings and epochs are read only from the selected environment variables. Doctor validates every bound
/// string before calling its dedicated observation service, writes one final text or JSON result to stdout, and sets
/// the process exit code from that result. It does not perform schema migration or application verification.
/// </remarks>
[Command("durable doctor", Description = "Check one configured Durable store and runtime without changing the database.")]
internal sealed partial class DurableDoctorCommand(IDurableDoctorService service) : ICommand
{
    private readonly IDurableDoctorService _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>Gets or sets the environment variable containing the PostgreSQL connection string.</summary>
    [CommandOption(
        "connection-env",
        Description = "Environment variable containing the PostgreSQL connection string. Default: APPSURFACE_DURABLE_CONNECTION.")]
    public string? ConnectionEnvironmentName { get; set; }

    /// <summary>Gets or sets the environment variable containing the configured runtime epoch GUID.</summary>
    [CommandOption(
        "runtime-epoch-env",
        Description = "Environment variable containing the configured runtime epoch GUID. Default: APPSURFACE_DURABLE_RUNTIME_EPOCH.")]
    public string? EpochEnvironmentName { get; set; }

    /// <summary>Gets or sets an optional operator-selected worker identifier.</summary>
    [CommandOption(
        "worker-id",
        Description = "Optional 1–200 character Durable worker label. Pair with --stale-after; do not use a personal or secret value.")]
    public string? WorkerId { get; set; }

    /// <summary>Gets or sets the caller-supplied heartbeat comparison threshold, required with --worker-id.</summary>
    [CommandOption(
        "stale-after",
        Description = "Positive duration from 1s through 1h, required with --worker-id. Use the host's effective HeartbeatStaleAfter setting.")]
    public string? StaleAfter { get; set; }

    /// <summary>Gets or sets the bounded total observation timeout.</summary>
    [CommandOption("timeout", Description = "Positive duration from 1s through 2m. Default: 10s.")]
    public string? Timeout { get; set; }

    /// <summary>Gets or sets the output format.</summary>
    [CommandOption("format", Description = "Output format: text or json. Default: text.")]
    public string? Format { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        var cancellationToken = CancellationToken.None;
        DurableDoctorInput? input = null;
        DurableDoctorRequest? request = null;
        var format = Format is "json" or "text" ? Format : "text";
        DurableDoctorResult result;

        try
        {
            cancellationToken = console.RegisterCancellationHandler();
            input = DurableDoctorInput.Create(
                ConnectionEnvironmentName,
                EpochEnvironmentName,
                WorkerId,
                StaleAfter,
                Timeout,
                Format);
            request = input.Request;
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await input.InspectAsync(_service, cancellationToken).ConfigureAwait(false);
            result = DurableDoctorClassifier.Classify(request, observation);
        }
        catch (DurableDoctorInputException)
        {
            request = null;
            result = DurableDoctorClassifier.Terminal(request, "invalid-input", ["input"]);
        }
        catch (DurableDoctorFailureException failure)
        {
            result = DurableDoctorClassifier.Terminal(
                request,
                failure.Kind switch
                {
                    DurableDoctorFailureKind.Unavailable => "unavailable",
                    DurableDoctorFailureKind.Canceled => "canceled",
                    _ => "failed",
                },
                failure.Categories);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = DurableDoctorClassifier.Terminal(request, "canceled", ["caller-canceled"]);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            result = DurableDoctorClassifier.Terminal(request, "failed", ["catalog-contract"]);
        }

        await WriteResultAsync(console.Output, result, request, format).ConfigureAwait(false);
    }

    /// <summary>Writes one doctor result, replacing render-contract rejection with one canonical failed envelope.</summary>
    /// <param name="outputWriter">The command output sink that receives the single rendered value.</param>
    /// <param name="result">The classified result to render.</param>
    /// <param name="request">Validated request intent, or null for input failures.</param>
    /// <param name="format">The already admitted output format.</param>
    /// <remarks>A broken sink sets exit code 1 and is never retried, so it cannot duplicate a partial result.</remarks>
    internal static async Task WriteResultAsync(
        TextWriter outputWriter,
        DurableDoctorResult result,
        DurableDoctorRequest? request,
        string format)
    {
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentNullException.ThrowIfNull(result);

        string output;
        try
        {
            output = DurableDoctorRenderer.Render(result, request, format);
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            try
            {
                result = DurableDoctorClassifier.Terminal(request, "failed", ["catalog-contract"]);
                output = DurableDoctorRenderer.Render(result, request, format);
            }
            catch (Exception renderException) when (IsNonfatal(renderException))
            {
                Environment.ExitCode = 1;
                return;
            }
        }

        if (output.Length == 0 || output[^1] != '\n')
        {
            output += "\n";
        }

        try
        {
            await outputWriter.WriteAsync(output).ConfigureAwait(false);
            Environment.ExitCode = result.ExitCode;
        }
        catch (Exception exception) when (IsNonfatal(exception))
        {
            // A failed output sink cannot be reported through that same sink; preserve a non-success process result.
            Environment.ExitCode = 1;
        }
    }

    private static bool IsNonfatal(Exception exception) => exception is not StackOverflowException
        and not OutOfMemoryException and not AccessViolationException;
}
