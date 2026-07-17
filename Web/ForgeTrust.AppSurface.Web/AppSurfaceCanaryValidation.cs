using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Validates named-canary identifiers and snapshots registration options into internal descriptors.
/// </summary>
internal static partial class AppSurfaceCanaryValidation
{
    /// <summary>The maximum registered canary-name length.</summary>
    internal const int MaximumNameLength = 128;

    /// <summary>The maximum optional registration-description length.</summary>
    internal const int MaximumDescriptionLength = 512;

    /// <summary>
    /// Validates a named-canary identifier.
    /// </summary>
    /// <param name="name">The identifier to validate.</param>
    /// <remarks>
    /// Names contain 1-128 lowercase ASCII letters or digits in dot-separated segments. Hyphens are allowed only inside
    /// a segment, never at an edge. Empty segments and consecutive dots are rejected.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> violates the grammar or length limit.</exception>
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

    /// <summary>
    /// Validates configured metadata and creates an immutable descriptor snapshot.
    /// </summary>
    /// <param name="name">The previously validated exact registration name.</param>
    /// <param name="evaluatorType">
    /// The concrete registered service type expected to implement <see cref="IAppSurfaceCanaryEvaluator"/>.
    /// </param>
    /// <param name="options">The non-null completed registration options.</param>
    /// <returns>
    /// A descriptor whose tags are deduplicated and stored in ordinal order and whose required-input flags are frozen.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The display name is blank, the description exceeds 512 characters, or a tag violates its 1-64 character
    /// lowercase letter/digit/internal-hyphen grammar.
    /// </exception>
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

        if (options.AllowedDetailKeys.Count > AppSurfaceCanaryResultValidation.MaximumDetails)
        {
            throw InvalidConfiguration(
                $"At most {AppSurfaceCanaryResultValidation.MaximumDetails} allowed detail keys may be declared.");
        }

        var allowedDetailKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in options.AllowedDetailKeys)
        {
            if (key is null)
            {
                throw InvalidConfiguration("Allowed detail keys must not be null.");
            }

            if (!AppSurfaceCanaryResultValidation.IsValidDetailKey(key))
            {
                throw InvalidConfiguration(
                    "Allowed detail keys must be 1-64 lowercase ASCII characters in dot-separated letter, digit, and internal-hyphen segments.");
            }

            allowedDetailKeys.Add(key);
        }

        return new AppSurfaceCanaryDescriptor(
            name,
            options.DisplayName,
            options.Description,
            tags.ToArray(),
            options.MarkerRequired,
            options.FreshSinceRequired,
            allowedDetailKeys,
            evaluatorType);
    }

    private static ArgumentException InvalidConfiguration(string message) =>
        new($"ASCAN101: {message}", "configure");

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}
