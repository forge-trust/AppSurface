using System.Net;
using System.Text.Json;

namespace AuthAspireKeycloakVerifier;

/// <summary>
/// Verifies the AppHost-backed Auth Aspire Keycloak web proof through public endpoints.
/// </summary>
public static class Program
{
    private static readonly string[] ForbiddenRealmEvidenceFragments =
    [
        "keycloak-admin-password",
        "clientSecret",
        "client_secret",
        "access_token",
        "id_token",
        "refresh_token",
    ];

    /// <summary>
    /// Runs the verifier.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero when verification succeeds; otherwise non-zero.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler);
        return await RunAsync(args, Console.Out, Console.Error, httpClient);
    }

    /// <summary>
    /// Runs verifier probes with injectable output and transport.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="output">Output writer.</param>
    /// <param name="error">Error writer.</param>
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
        var options = VerifierOptions.Parse(args, Environment.GetEnvironmentVariable);
        if (!options.IsValid)
        {
            await error.WriteLineAsync(options.Error);
            return 2;
        }

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var failures = await ProbeUntilReadyAsync(options, httpClient, linked.Token);
        if (failures.Count > 0)
        {
            await error.WriteLineAsync("AppSurface Auth Aspire Keycloak verification failed.");
            foreach (var failure in failures)
            {
                await error.WriteLineAsync($"- {failure}");
            }

            return 1;
        }

        await output.WriteLineAsync("AppSurface Auth Aspire Keycloak verification passed.");
        await output.WriteLineAsync("Public status, protected challenge, and generated realm evidence are proven locally.");
        return 0;
    }

    private static async Task<IReadOnlyList<string>> ProbeUntilReadyAsync(
        VerifierOptions options,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var lastFailures = new List<string>();
        while (!cancellationToken.IsCancellationRequested)
        {
            lastFailures = await ProbeOnceAsync(options, httpClient, cancellationToken);
            if (lastFailures.Count == 0)
            {
                return [];
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return lastFailures.Count == 0 ? ["verification timed out before probes succeeded."] : lastFailures;
    }

    private static async Task<List<string>> ProbeOnceAsync(
        VerifierOptions options,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        await CheckStatusAsync(options, httpClient, failures, cancellationToken);
        await CheckProtectedChallengeAsync(options, httpClient, failures, cancellationToken);
        CheckRealmEvidence(options, failures);
        return failures;
    }

    private static async Task CheckStatusAsync(
        VerifierOptions options,
        HttpClient httpClient,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(options.TargetUri!, "auth/proof/status"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"/auth/proof/status returned HTTP {(int)response.StatusCode}.");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("isAuthenticated", out var authenticated)
                || authenticated.GetBoolean())
            {
                failures.Add("/auth/proof/status did not report unauthenticated AppSurface state before login.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"/auth/proof/status could not be read: {exception.GetType().Name}.");
        }
    }

    private static async Task CheckProtectedChallengeAsync(
        VerifierOptions options,
        HttpClient httpClient,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(options.TargetUri!, "auth/proof/protected"), cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.RedirectMethod))
            {
                failures.Add($"/auth/proof/protected expected redirect challenge but returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"/auth/proof/protected could not be read: {exception.GetType().Name}.");
        }
    }

    private static void CheckRealmEvidence(VerifierOptions options, ICollection<string> failures)
    {
        if (options.RealmImportFile is null)
        {
            failures.Add("realm import evidence path is missing.");
            return;
        }

        if (!File.Exists(options.RealmImportFile))
        {
            failures.Add("realm import evidence file does not exist.");
            return;
        }

        var importJson = File.ReadAllText(options.RealmImportFile);
        CheckRealmSecretEvidence(importJson, failures);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(importJson);
        }
        catch (JsonException)
        {
            failures.Add("realm import evidence is not valid JSON.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("clients", out var clients) || clients.ValueKind != JsonValueKind.Array)
            {
                failures.Add("realm import evidence does not contain a clients array.");
                return;
            }

            if (!root.TryGetProperty("users", out var userElements) || userElements.ValueKind != JsonValueKind.Array)
            {
                failures.Add("realm import evidence does not contain a users array.");
                return;
            }

            var hasClient = clients.EnumerateArray()
                .Any(client => string.Equals(GetOptionalString(client, "clientId"), options.ClientId, StringComparison.Ordinal));
            if (!hasClient)
            {
                failures.Add("realm import evidence does not contain the configured client id.");
            }

            var users = userElements.EnumerateArray()
                .Select(user => GetOptionalString(user, "username"))
                .ToHashSet(StringComparer.Ordinal);
            if (!users.Contains("admin") || !users.Contains("viewer"))
            {
                failures.Add("realm import evidence does not contain admin and viewer users.");
            }
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void CheckRealmSecretEvidence(string importJson, ICollection<string> failures)
    {
        foreach (var fragment in ForbiddenRealmEvidenceFragments)
        {
            if (importJson.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"realm import evidence contains forbidden secret marker '{fragment}'.");
            }
        }
    }
}

internal sealed record VerifierOptions(
    Uri? TargetUri,
    string? ClientId,
    string? RealmImportFile,
    TimeSpan Timeout,
    TimeSpan PollInterval,
    bool IsValid,
    string Error)
{
    public static VerifierOptions Parse(
        IReadOnlyList<string> args,
        Func<string, string?> environment)
    {
        var target = environment("AUTH_ASPIRE_KEYCLOAK_TARGET_URL");
        var clientId = environment("AUTH_ASPIRE_KEYCLOAK_CLIENT_ID") ?? "appsurface-web";
        var realmImportFile = environment("AUTH_ASPIRE_KEYCLOAK_REALM_IMPORT_FILE");
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

        if (!Uri.TryCreate(EnsureTrailingSlash(target), UriKind.Absolute, out var targetUri))
        {
            return Invalid("Problem: verifier target URL is missing or invalid. Cause: AUTH_ASPIRE_KEYCLOAK_TARGET_URL was not supplied by the AppHost. Fix: run through the AppHost verify profile. Docs: examples/auth-aspire-keycloak-apphost/README.md.");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Invalid("Problem: verifier client id is missing. Cause: AUTH_ASPIRE_KEYCLOAK_CLIENT_ID was not supplied by the AppHost. Fix: run through the AppHost verify profile. Docs: examples/auth-aspire-keycloak-apphost/README.md.");
        }

        return new VerifierOptions(
            targetUri,
            clientId,
            realmImportFile,
            TimeSpan.FromSeconds(timeoutSeconds),
            TimeSpan.FromSeconds(1),
            true,
            string.Empty);
    }

    private static VerifierOptions Invalid(string error) =>
        new(null, null, null, TimeSpan.Zero, TimeSpan.Zero, false, error);

    private static string? EnsureTrailingSlash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }
}
