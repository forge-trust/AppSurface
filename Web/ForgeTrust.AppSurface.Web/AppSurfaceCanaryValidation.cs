using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web;

internal static partial class AppSurfaceCanaryValidation
{
    internal const int MaximumNameLength = 128;
    internal const int MaximumDescriptionLength = 512;

    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length is 0 or > MaximumNameLength || !NameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                "ASCAN101: The canary name must be 1-128 lowercase characters in dot-separated segments containing letters, digits, and internal hyphens.",
                nameof(name));
        }
    }

    internal static AppSurfaceCanaryDescriptor CreateDescriptor(
        string name,
        Type evaluatorType,
        AppSurfaceCanaryRegistrationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DisplayName))
        {
            throw InvalidConfiguration("DisplayName must not be blank.");
        }

        if (options.Description?.Length > MaximumDescriptionLength)
        {
            throw InvalidConfiguration($"Description must not exceed {MaximumDescriptionLength} characters.");
        }

        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in options.Tags)
        {
            if (tag is null || !TagRegex().IsMatch(tag))
            {
                throw InvalidConfiguration(
                    "Tags must be 1-64 lowercase characters containing letters, digits, and internal hyphens.");
            }

            tags.Add(tag);
        }

        return new AppSurfaceCanaryDescriptor(
            name,
            options.DisplayName,
            options.Description,
            tags.ToArray(),
            options.MarkerRequired,
            options.FreshSinceRequired,
            evaluatorType);
    }

    private static ArgumentException InvalidConfiguration(string message) =>
        new($"ASCAN101: {message}", "configure");

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}
