using System.Text.Json;
using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Framework-neutral assertion helpers for AppSurface auth results and ProblemDetails responses.
/// </summary>
/// <remarks>
/// The helpers throw <see cref="AppSurfaceTestAuthAssertionException" /> instead of depending on a specific test
/// framework. They are intended for package smoke tests, consumer examples, and teams that want stable AppSurface auth
/// diagnostics without pulling xUnit into production package references.
/// </remarks>
public static class AppSurfaceAuthTestAssert
{
    /// <summary>
    /// Verifies an AppSurface auth result outcome and optional reason.
    /// </summary>
    /// <param name="result">Auth result to inspect.</param>
    /// <param name="expectedOutcome">Expected high-level outcome.</param>
    /// <param name="expectedReason">Optional expected reason.</param>
    /// <returns>The inspected result for fluent test code.</returns>
    public static AppSurfaceAuthResult HasOutcome(
        AppSurfaceAuthResult result,
        AppSurfaceAuthOutcome expectedOutcome,
        AppSurfaceAuthReason? expectedReason = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Outcome != expectedOutcome)
        {
            Throw($"Expected AppSurface auth outcome '{expectedOutcome}', but found '{result.Outcome}'.");
        }

        if (expectedReason is not null && result.Reason != expectedReason)
        {
            Throw($"Expected AppSurface auth reason '{expectedReason}', but found '{result.Reason}'.");
        }

        return result;
    }

    /// <summary>
    /// Verifies AppSurface auth ProblemDetails extensions.
    /// </summary>
    /// <param name="problem">ProblemDetails JSON element.</param>
    /// <param name="expectedOutcome">Expected <c>appsurfaceAuthOutcome</c> extension value.</param>
    /// <param name="expectedReason">Expected <c>appsurfaceAuthReason</c> extension value.</param>
    /// <param name="expectedStatus">Expected HTTP status in the ProblemDetails payload.</param>
    /// <param name="expectedPolicyName">Optional expected <c>appsurfacePolicyName</c> extension value.</param>
    /// <returns>The inspected JSON element for fluent test code.</returns>
    public static JsonElement HasProblemDetails(
        JsonElement problem,
        AppSurfaceAuthOutcome expectedOutcome,
        AppSurfaceAuthReason expectedReason,
        int expectedStatus,
        string? expectedPolicyName = null)
    {
        var status = ReadInt32(problem, "status");
        if (status != expectedStatus)
        {
            Throw($"Expected ProblemDetails status '{expectedStatus}', but found '{status}'.");
        }

        var outcome = ReadString(problem, "appsurfaceAuthOutcome");
        if (!string.Equals(outcome, expectedOutcome.ToString(), StringComparison.Ordinal))
        {
            Throw($"Expected ProblemDetails AppSurface outcome '{expectedOutcome}', but found '{outcome}'.");
        }

        var reason = ReadString(problem, "appsurfaceAuthReason");
        if (!string.Equals(reason, expectedReason.ToString(), StringComparison.Ordinal))
        {
            Throw($"Expected ProblemDetails AppSurface reason '{expectedReason}', but found '{reason}'.");
        }

        if (expectedPolicyName is not null)
        {
            var policyName = ReadString(problem, "appsurfacePolicyName");
            if (!string.Equals(policyName, expectedPolicyName, StringComparison.Ordinal))
            {
                Throw($"Expected ProblemDetails AppSurface policy '{expectedPolicyName}', but found '{policyName}'.");
            }
        }

        return problem;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            Throw($"Expected ProblemDetails property '{propertyName}' to be a string.");
        }

        return property.GetString()!;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            Throw($"Expected ProblemDetails property '{propertyName}' to be a number.");
        }

        return property.GetInt32();
    }

    private static void Throw(string message)
    {
        throw new AppSurfaceTestAuthAssertionException(
            $"Problem: AppSurface auth test assertion failed. Cause: {message} Fix: update the expected auth contract or the host auth setup. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.AssertionFailed}.");
    }
}
