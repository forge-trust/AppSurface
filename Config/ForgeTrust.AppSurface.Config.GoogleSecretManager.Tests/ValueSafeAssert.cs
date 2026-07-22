namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

/// <summary>Asserts value-safe output without echoing the protected value in a failing assertion.</summary>
internal static class ValueSafeAssert
{
    /// <summary>Fails with a paste-safe message when text exposes the protected value.</summary>
    internal static void DoesNotExpose(string sensitiveValue, string? text) =>
        Assert.False(
            text?.Contains(sensitiveValue, StringComparison.Ordinal) == true,
            "Output exposed a sensitive value.");
}
