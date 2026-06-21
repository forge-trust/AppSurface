namespace ForgeTrust.AppSurface.Observability;

/// <summary>
/// Reads environment variables for observability plan resolution.
/// </summary>
/// <remarks>
/// This internal seam keeps endpoint precedence tests deterministic without mutating process-wide environment state.
/// Implementations should preserve the platform behavior of returning <see langword="null"/> when a variable is not set;
/// callers normalize blank values separately so empty environment variables do not become configured endpoints.
/// </remarks>
internal interface IAppSurfaceEnvironmentReader
{
    /// <summary>
    /// Gets the value of an environment variable.
    /// </summary>
    /// <param name="variable">The environment variable name to read.</param>
    /// <returns>The configured value, or <see langword="null"/> when the variable is not present.</returns>
    string? GetEnvironmentVariable(string variable);
}

/// <summary>
/// Default process environment reader used by AppSurface observability.
/// </summary>
/// <remarks>
/// The singleton <see cref="Instance"/> avoids repeated allocation while keeping the production path explicit. Tests
/// should use <see cref="IAppSurfaceEnvironmentReader"/> rather than changing real environment variables.
/// </remarks>
internal sealed class AppSurfaceEnvironmentReader : IAppSurfaceEnvironmentReader
{
    /// <summary>
    /// Gets the shared process environment reader.
    /// </summary>
    internal static readonly AppSurfaceEnvironmentReader Instance = new();

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);
}
