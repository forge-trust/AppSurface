// docs:snippet appsurface-canary-consumer:start
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
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The named-canary envelope must be a JSON object.");
        }

        var name = ReadRequiredString(root, "name");
        var status = ReadRequiredString(root, "status");

        var reasonCode = root.TryGetProperty("reasonCode", out var reasonProperty)
            && reasonProperty.ValueKind == JsonValueKind.String
                ? reasonProperty.GetString()
                : null;
        var action = SelectAction(status, reasonCode);

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

        return new CanaryConsumerResult(name, status, ready, reasonCode, action);
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
// docs:snippet appsurface-canary-consumer:end

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
    private static readonly IReadOnlyDictionary<AppSurfaceCanaryStatus, string> Reasons =
        new Dictionary<AppSurfaceCanaryStatus, string>
        {
            [AppSurfaceCanaryStatus.Pass] = "proof-observed",
            [AppSurfaceCanaryStatus.Pending] = "proof-not-observed",
            [AppSurfaceCanaryStatus.Fail] = "proof-rejected",
            [AppSurfaceCanaryStatus.Stale] = "proof-stale",
            [AppSurfaceCanaryStatus.NotConfigured] = "proof-not-configured",
        };

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

    private static string ReasonFor(AppSurfaceCanaryStatus status) => Reasons[status];
}

/// <summary>Produces migration-completion evidence using only the public named-canary API.</summary>
/// <param name="scenario">The evaluation scenario.</param>
public sealed class MigrationCompletionCanaryEvaluator(CanaryFixtureScenario scenario) : IAppSurfaceCanaryEvaluator
{
    private static readonly IReadOnlyDictionary<AppSurfaceCanaryStatus, string> Reasons =
        new Dictionary<AppSurfaceCanaryStatus, string>
        {
            [AppSurfaceCanaryStatus.Pass] = "migration-complete",
            [AppSurfaceCanaryStatus.Pending] = "migration-in-progress",
            [AppSurfaceCanaryStatus.Fail] = "checksum-mismatch",
            [AppSurfaceCanaryStatus.Stale] = "checkpoint-stale",
            [AppSurfaceCanaryStatus.NotConfigured] = "migration-not-configured",
        };

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

    private static string ReasonFor(AppSurfaceCanaryStatus status) => Reasons[status];
}

// docs:snippet appsurface-canary-forwarding-complete:start
/// <summary>Describes application-owned forwarding proof returned by a protected proof store.</summary>
/// <param name="Status">The named-canary status derived by the application.</param>
/// <param name="ObservedAt">The time the proof was observed, when meaningful.</param>
/// <param name="MatchedCount">The number of matching proof records.</param>
/// <param name="ReasonCode">The stable, operator-safe reason code.</param>
/// <param name="Summary">The bounded, operator-safe summary.</param>
/// <param name="CorrelationId">The non-secret application correlation identifier.</param>
public sealed record ForwardingProofSnapshot(
    AppSurfaceCanaryStatus Status,
    DateTimeOffset? ObservedAt,
    int MatchedCount,
    string ReasonCode,
    string Summary,
    string CorrelationId);

/// <summary>Reads existing forwarding proof without triggering the workflow under evaluation.</summary>
public interface IForwardingProofReader
{
    /// <summary>Finds proof for the exact marker and freshness boundary supplied by the deploy caller.</summary>
    /// <param name="marker">The required opaque, non-secret deploy marker.</param>
    /// <param name="freshSince">The required proof freshness boundary.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The application-owned proof decision and bounded evidence.</returns>
    ValueTask<ForwardingProofSnapshot> ReadAsync(
        string marker,
        DateTimeOffset freshSince,
        CancellationToken cancellationToken);
}

/// <summary>Evaluates existing forwarding proof without triggering forwarding itself.</summary>
public sealed class CompleteForwardingCanaryEvaluator(IForwardingProofReader proofReader) : IAppSurfaceCanaryEvaluator
{
    /// <summary>The application-owned detail key shared by registration and result construction.</summary>
    public const string ProofKindDetailKey = "proof.kind";

    /// <inheritdoc />
    public async ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var proof = await proofReader.ReadAsync(
            context.Marker!,
            context.FreshSince!.Value,
            cancellationToken);

        return new AppSurfaceCanaryResult(
            proof.Status,
            result =>
            {
                result.ObservedAt = proof.ObservedAt;
                result.MatchedCount = proof.MatchedCount;
                result.ReasonCode = proof.ReasonCode;
                result.Summary = proof.Summary;
                result.CorrelationId = proof.CorrelationId;
                result.AddDetail(ProofKindDetailKey, "forwarding");
            });
    }
}
// docs:snippet appsurface-canary-forwarding-complete:end
