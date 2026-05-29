namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Classifies Tailwind standard-error output for host-specific logging.
/// </summary>
internal static class TailwindStderrClassifier
{
    public static TailwindOutputLevel Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return TailwindOutputLevel.Debug;
        }

        if (line.StartsWith("≈ tailwindcss v", StringComparison.Ordinal) ||
            line.StartsWith("Done in ", StringComparison.Ordinal))
        {
            return TailwindOutputLevel.Information;
        }

        return TailwindOutputLevel.Error;
    }
}
