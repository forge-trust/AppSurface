namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Exception thrown by framework-neutral AppSurface auth testing assertion helpers.
/// </summary>
public sealed class AppSurfaceTestAuthAssertionException : Exception
{
    /// <summary>
    /// Creates an assertion exception.
    /// </summary>
    /// <param name="message">Assertion failure message safe for test output.</param>
    public AppSurfaceTestAuthAssertionException(string message)
        : base(message)
    {
    }
}
