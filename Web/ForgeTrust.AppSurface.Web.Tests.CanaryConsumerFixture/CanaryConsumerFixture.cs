using System.Text.Json;

namespace ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture;

/// <summary>Lists the next operator action selected from a semantic named-canary envelope.</summary>
public enum CanaryOperatorAction
{
    /// <summary>Continue the deployment decision after acceptable proof.</summary>
    Continue,

    /// <summary>Wait for proof that may still arrive.</summary>
    Wait,

    /// <summary>Refresh stale proof or its trigger boundary.</summary>
    Refresh,

    /// <summary>Configure the unavailable proof dependency.</summary>
    Configure,

    /// <summary>Investigate a completed negative outcome.</summary>
    Investigate,

    /// <summary>Roll back after a known migration-integrity failure.</summary>
    RollBack,
}

/// <summary>Represents the required compatibility core and selected operator action parsed by a public consumer.</summary>
/// <param name="Name">The exact registered canary name.</param>
/// <param name="Status">The defined lowercase wire status.</param>
/// <param name="Ready">The compatibility projection of whether <paramref name="Status"/> is <c>pass</c>.</param>
/// <param name="ReasonCode">The optional response-only machine reason.</param>
/// <param name="Action">The operator action selected from the semantic envelope.</param>
public sealed record CanaryConsumerResult(
    string Name,
    string Status,
    bool Ready,
    string? ReasonCode,
    CanaryOperatorAction Action);

/// <summary>Parses named-canary JSON by semantic field name without depending on property order or optional fields.</summary>
public static class CanaryEnvelopeConsumer
{
    private static readonly HashSet<string> DefinedStatuses =
    [
        "pass",
        "pending",
        "fail",
        "stale",
        "not-configured",
    ];

    /// <summary>Parses and validates the required named-canary compatibility core.</summary>
    /// <param name="json">The non-null JSON envelope to parse.</param>
    /// <returns>The validated core fields and selected operator action.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">A required field is absent, invalid, or semantically inconsistent.</exception>
    public static CanaryConsumerResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var name = ReadRequiredString(root, "name");
        var status = ReadRequiredString(root, "status");
        if (!DefinedStatuses.Contains(status))
        {
            throw new JsonException("The named-canary status is not recognized.");
        }

        if (!root.TryGetProperty("ready", out var readyProperty)
            || readyProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new JsonException("The named-canary ready projection is required and must be Boolean.");
        }

        var ready = readyProperty.GetBoolean();
        if (ready != string.Equals(status, "pass", StringComparison.Ordinal))
        {
            throw new JsonException("The named-canary ready projection does not match status.");
        }

        var reasonCode = root.TryGetProperty("reasonCode", out var reasonProperty)
            && reasonProperty.ValueKind == JsonValueKind.String
                ? reasonProperty.GetString()
                : null;

        return new CanaryConsumerResult(name, status, ready, reasonCode, SelectAction(status, reasonCode));
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException($"The named-canary {propertyName} is required and must be a nonblank string.");
        }

        return property.GetString()!;
    }

    private static CanaryOperatorAction SelectAction(string status, string? reasonCode) => status switch
    {
        "pass" => CanaryOperatorAction.Continue,
        "pending" => CanaryOperatorAction.Wait,
        "stale" => CanaryOperatorAction.Refresh,
        "not-configured" => CanaryOperatorAction.Configure,
        "fail" when string.Equals(reasonCode, "checksum-mismatch", StringComparison.Ordinal) =>
            CanaryOperatorAction.RollBack,
        "fail" => CanaryOperatorAction.Investigate,
        _ => throw new JsonException("The named-canary status is not recognized."),
    };
}

/// <summary>Configures one public consumer-fixture evaluation.</summary>
/// <param name="Status">The result status returned by the evaluator.</param>
/// <param name="Ambiguous">Whether the forwarding fixture should return multiple ambiguous matches.</param>
public sealed record CanaryFixtureScenario(AppSurfaceCanaryStatus Status, bool Ambiguous = false);

/// <summary>Proves the original status-only result constructor remains usable from a non-friend assembly.</summary>
/// <param name="scenario">The evaluation scenario.</param>
public sealed class StatusOnlyCanaryEvaluator(CanaryFixtureScenario scenario) : IAppSurfaceCanaryEvaluator
{
    /// <inheritdoc />
    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AppSurfaceCanaryResult(scenario.Status));
}

/// <summary>Produces forwarding-proof evidence using only the public named-canary API.</summary>
/// <param name="scenario">The evaluation scenario.</param>
public sealed class ForwardingProofCanaryEvaluator(CanaryFixtureScenario scenario) : IAppSurfaceCanaryEvaluator
{
    /// <summary>The application-owned detail key used for the forwarding proof kind.</summary>
    public const string ProofKindDetailKey = "proof.kind";

    /// <inheritdoc />
    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            new AppSurfaceCanaryResult(
                scenario.Status,
                options =>
                {
                    options.ObservedAt = new DateTimeOffset(2026, 7, 16, 4, 31, 2, TimeSpan.FromHours(-4));
                    options.MatchedCount = scenario.Ambiguous ? 2 : scenario.Status == AppSurfaceCanaryStatus.Pass ? 1 : 0;
                    options.ReasonCode = scenario.Ambiguous
                        ? "ambiguous-matches"
                        : ReasonFor(scenario.Status);
                    options.Summary = scenario.Ambiguous
                        ? "Multiple matching forwarding proofs require operator review."
                        : "Forwarding proof evaluation completed.";
                    options.CorrelationId = "deploy-20260716-004006";
                    options.AddDetail(ProofKindDetailKey, "forwarding");
                }));

    private static string ReasonFor(AppSurfaceCanaryStatus status) => status switch
    {
        AppSurfaceCanaryStatus.Pass => "proof-observed",
        AppSurfaceCanaryStatus.Pending => "proof-not-observed",
        AppSurfaceCanaryStatus.Fail => "proof-rejected",
        AppSurfaceCanaryStatus.Stale => "proof-stale",
        AppSurfaceCanaryStatus.NotConfigured => "proof-not-configured",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <summary>Produces migration-completion evidence using only the public named-canary API.</summary>
/// <param name="scenario">The evaluation scenario.</param>
public sealed class MigrationCompletionCanaryEvaluator(CanaryFixtureScenario scenario) : IAppSurfaceCanaryEvaluator
{
    /// <summary>The application-owned detail key used for the migration kind.</summary>
    public const string MigrationKindDetailKey = "migration.kind";

    /// <inheritdoc />
    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            new AppSurfaceCanaryResult(
                scenario.Status,
                options =>
                {
                    options.MatchedCount = scenario.Status == AppSurfaceCanaryStatus.Pass ? 24 : 0;
                    options.ReasonCode = ReasonFor(scenario.Status);
                    options.Summary = "Migration checkpoint evaluation completed.";
                    options.AddDetail(MigrationKindDetailKey, "schema");
                }));

    private static string ReasonFor(AppSurfaceCanaryStatus status) => status switch
    {
        AppSurfaceCanaryStatus.Pass => "migration-complete",
        AppSurfaceCanaryStatus.Pending => "migration-in-progress",
        AppSurfaceCanaryStatus.Fail => "checksum-mismatch",
        AppSurfaceCanaryStatus.Stale => "checkpoint-stale",
        AppSurfaceCanaryStatus.NotConfigured => "migration-not-configured",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
