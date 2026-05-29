namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Represents a Tailwind process startup failure.
/// </summary>
internal sealed class TailwindProcessStartException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TailwindProcessStartException"/> class.
    /// </summary>
    /// <param name="fileName">The Tailwind executable or shell launcher that failed to start.</param>
    /// <param name="innerException">The lower-level process startup exception.</param>
    public TailwindProcessStartException(string fileName, Exception innerException)
        : base($"Failed to start Tailwind CLI process: {fileName}", innerException)
    {
        FileName = fileName;
    }

    /// <summary>
    /// Gets the Tailwind executable or shell launcher that failed to start.
    /// </summary>
    public string FileName { get; }
}
