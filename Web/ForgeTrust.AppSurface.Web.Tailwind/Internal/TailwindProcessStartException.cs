namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Represents a Tailwind process startup failure.
/// </summary>
internal sealed class TailwindProcessStartException : Exception
{
    public TailwindProcessStartException(string fileName, Exception innerException)
        : base($"Failed to start Tailwind CLI process: {fileName}", innerException)
    {
        FileName = fileName;
    }

    public string FileName { get; }
}
