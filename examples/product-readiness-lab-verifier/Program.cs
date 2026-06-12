using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductReadinessLab.Verifier;

/// <summary>
/// Verifies the AppHost-backed product-readiness lab through its public readiness endpoint.
/// </summary>
public static class ProductReadinessAppHostVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the verifier.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero when the AppHost-backed report proves all locally provable rows; otherwise non-zero.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var httpClient = new HttpClient();
        return await RunAsync(args, Console.Out, Console.Error, httpClient);
    }

    /// <summary>
    /// Runs the verifier with injectable output and HTTP transport for tests.
    /// </summary>
    /// <param name="args">Command-line arguments. Use <c>--target &lt;url&gt;</c> to override the target.</param>
    /// <param name="output">Output writer for successful verification messages.</param>
    /// <param name="error">Error writer for bounded failure diagnostics.</param>
    /// <param name="httpClient">HTTP client used for probes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Zero when verification succeeds; otherwise non-zero.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        var options = ProductReadinessVerifierOptions.Parse(args, Environment.GetEnvironmentVariable);
        if (!options.IsValid)
        {
            await error.WriteLineAsync(options.Error);
            return 2;
        }

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        ProductReadinessProbeDocument? report = null;
        Exception? lastException = null;
        var readinessUri = new Uri(options.TargetUri!, "/readiness");

        while (!linked.IsCancellationRequested)
        {
            try
            {
                using var response = await httpClient.GetAsync(readinessUri, linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                    report = await JsonSerializer.DeserializeAsync<ProductReadinessProbeDocument>(stream, JsonOptions, linked.Token);
                    break;
                }

                lastException = new HttpRequestException($"Readiness endpoint returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastException = exception;
            }

            try
            {
                await Task.Delay(options.PollInterval, linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (report is null)
        {
            await error.WriteLineAsync("Product-readiness AppHost verification could not read /readiness before timeout.");
            await error.WriteLineAsync($"Cause: {lastException?.GetType().Name ?? "TimedOut"}.");
            await error.WriteLineAsync("Fix: run through the AppHost verify profile so Postgres and the web app start together.");
            return 1;
        }

        var result = Verify(report);
        if (!result.Succeeded)
        {
            await error.WriteLineAsync("Product-readiness AppHost verification failed.");
            foreach (var failure in result.Failures)
            {
                await error.WriteLineAsync($"- {failure}");
            }

            return 1;
        }

        await output.WriteLineAsync("Product-readiness AppHost verification passed.");
        await output.WriteLineAsync("Postgres product-state persistence is proven locally; Durable Task backend/storage remains host-owned.");
        return 0;
    }

    /// <summary>
    /// Verifies the readiness report produced by the AppHost-backed lab.
    /// </summary>
    /// <param name="report">Readiness report JSON returned by the lab.</param>
    /// <returns>A verification result with safe failure messages.</returns>
    public static ProductReadinessVerificationResult Verify(ProductReadinessProbeDocument report)
    {
        var failures = new List<string>();
        if (report.Rows is null || report.Rows.Count == 0)
        {
            return new ProductReadinessVerificationResult(false, ["readiness report has no rows."]);
        }

        var rows = new Dictionary<string, ProductReadinessProbeRow>(StringComparer.Ordinal);
        foreach (var row in report.Rows.Where(row => !rows.TryAdd(row.Area, row)))
        {
            failures.Add($"{row.Area} row is duplicated.");
        }

        RequireStatus(rows, "startup-routing", "proven-locally", failures);
        RequireStatus(rows, "config-summary", "proven-locally", failures);
        RequireStatus(rows, "proof-auth", "unsafe-to-copy", failures);
        RequireStatus(rows, "workflow", "proven-locally", failures);
        RequireStatus(rows, "postgres-product-state", "proven-locally", failures);
        RequireStatus(rows, "in-process-host-shape", "proven-locally", failures);
        RequireStatus(rows, "durabletask-backend-boundary", "host-owned", failures);
        RequireStatus(rows, "deployment", "deferred", failures);
        RequireStatus(rows, "secrets", "host-owned", failures);

        foreach (var row in report.Rows.Where(row => string.Equals(row.Status, "blocked", StringComparison.Ordinal)))
        {
            failures.Add($"{row.Area} is blocked: {row.Problem}");
        }

        return new ProductReadinessVerificationResult(failures.Count == 0, failures);
    }

    private static void RequireStatus(
        IReadOnlyDictionary<string, ProductReadinessProbeRow> rows,
        string area,
        string expectedStatus,
        ICollection<string> failures)
    {
        if (!rows.TryGetValue(area, out var row))
        {
            failures.Add($"{area} row is missing.");
            return;
        }

        if (!string.Equals(row.Status, expectedStatus, StringComparison.Ordinal))
        {
            failures.Add($"{area} expected {expectedStatus} but was {row.Status}.");
        }
    }
}

/// <summary>
/// Parsed verifier options.
/// </summary>
/// <param name="TargetUri">Base URI for the product-readiness lab web app.</param>
/// <param name="Timeout">Maximum time to wait for readiness.</param>
/// <param name="PollInterval">Delay between readiness probes.</param>
/// <param name="IsValid">Whether options are valid.</param>
/// <param name="Error">Safe validation error text.</param>
public sealed record ProductReadinessVerifierOptions(
    Uri? TargetUri,
    TimeSpan Timeout,
    TimeSpan PollInterval,
    bool IsValid,
    string Error)
{
    /// <summary>
    /// Parses command-line and environment configuration.
    /// </summary>
    /// <param name="args">Verifier command-line arguments.</param>
    /// <param name="environment">Environment variable reader.</param>
    /// <returns>Parsed options.</returns>
    public static ProductReadinessVerifierOptions Parse(
        IReadOnlyList<string> args,
        Func<string, string?> environment)
    {
        var target = environment("PRODUCT_READINESS_TARGET_URL");
        var timeoutSeconds = 120;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Count:
                    target = args[++i];
                    break;
                case "--timeout-seconds" when i + 1 < args.Count && int.TryParse(args[++i], out var parsed):
                    timeoutSeconds = parsed;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(target) || !Uri.TryCreate(target, UriKind.Absolute, out var targetUri))
        {
            return Invalid("Missing or invalid PRODUCT_READINESS_TARGET_URL. Run through the AppHost verify profile or pass --target <url>.");
        }

        if (targetUri.Scheme is not ("http" or "https"))
        {
            return Invalid("PRODUCT_READINESS_TARGET_URL must use http or https.");
        }

        if (timeoutSeconds <= 0 || timeoutSeconds > 300)
        {
            return Invalid("--timeout-seconds must be between 1 and 300.");
        }

        return new ProductReadinessVerifierOptions(targetUri, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(1), true, string.Empty);
    }

    private static ProductReadinessVerifierOptions Invalid(string error) =>
        new(null, TimeSpan.Zero, TimeSpan.Zero, false, error);
}

/// <summary>
/// Verification result for the AppHost-backed readiness report.
/// </summary>
/// <param name="Succeeded">Whether verification passed.</param>
/// <param name="Failures">Safe failure messages.</param>
public sealed record ProductReadinessVerificationResult(bool Succeeded, IReadOnlyList<string> Failures);

/// <summary>
/// Readiness report JSON consumed by the verifier.
/// </summary>
/// <param name="Rows">Readiness rows returned by the lab.</param>
public sealed record ProductReadinessProbeDocument(
    [property: JsonPropertyName("rows")] IReadOnlyList<ProductReadinessProbeRow> Rows);

/// <summary>
/// Readiness report row JSON consumed by the verifier.
/// </summary>
/// <param name="Area">Readiness area id.</param>
/// <param name="Status">Stable readiness status wire name.</param>
/// <param name="Problem">Safe problem text.</param>
public sealed record ProductReadinessProbeRow(
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("problem")] string Problem);
