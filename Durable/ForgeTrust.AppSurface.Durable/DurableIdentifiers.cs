using System.Globalization;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies the trusted application scope that owns a durable aggregate.
/// </summary>
/// <remarks>
/// Scope identifiers must come from an application-authorized context. A caller-supplied route, form, event, or
/// instance identifier is not sufficient authorization to construct a durable operation.
/// </remarks>
public readonly record struct DurableScopeId
{
    /// <summary>
    /// Initializes a new durable scope identifier.
    /// </summary>
    /// <param name="value">Opaque, privacy-safe scope value.</param>
    public DurableScopeId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>
    /// Gets the opaque scope value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one accepted durable work aggregate independently of its retries and leases.
/// </summary>
public readonly record struct DurableWorkId
{
    /// <summary>
    /// Initializes a new durable work identifier.
    /// </summary>
    /// <param name="value">Opaque, privacy-safe identifier.</param>
    public DurableWorkId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>
    /// Gets the opaque identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a cryptographically random identifier suitable for a new work aggregate.
    /// </summary>
    /// <returns>A new durable work identifier.</returns>
    public static DurableWorkId New() => new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identifies one idempotent durable command.
/// </summary>
public readonly record struct DurableCommandId
{
    /// <summary>
    /// Initializes a new durable command identifier.
    /// </summary>
    /// <param name="value">Opaque, privacy-safe identifier.</param>
    public DurableCommandId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>
    /// Gets the opaque identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a cryptographically random identifier suitable for a new command.
    /// </summary>
    /// <returns>A new durable command identifier.</returns>
    public static DurableCommandId New() => new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Shared validation for opaque durable identifiers.
/// </summary>
internal static class DurableIdentifier
{
    /// <summary>
    /// Validates a required privacy-safe identifier without changing its ordinal value.
    /// </summary>
    internal static string Require(string value, string parameterName, int maximumLength)
    {
        var validated = RequireText(value, parameterName, maximumLength);
        if (validated.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException(
                "Durable identifiers may contain only ASCII letters, digits, hyphens, underscores, periods, and colons.",
                parameterName);
        }

        return validated;
    }

    /// <summary>Validates bounded non-control text without claiming it is an opaque identifier.</summary>
    internal static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Durable identifiers must not be null, empty, or whitespace.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Durable identifiers must not exceed {maximumLength} characters."),
                parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Durable identifiers must not contain control characters.", parameterName);
        }

        return value;
    }

    /// <summary>Validates a bounded human label while rejecting common secret, URL, email, and raw-payload shapes.</summary>
    internal static string RequireSafeLabel(string value, string parameterName, int maximumLength)
    {
        var validated = RequireText(value, parameterName, maximumLength);
        string[] forbiddenFragments =
        [
            "@",
            "://",
            "bearer ",
            "authorization:",
            "password=",
            "token=",
            "secret=",
            "api_key=",
            "apikey=",
        ];
        if (forbiddenFragments.Any(fragment => validated.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            || validated.TrimStart().StartsWith('{')
            || validated.TrimStart().StartsWith('['))
        {
            throw new ArgumentException(
                "Durable labels must not contain email addresses, URLs, credentials, secrets, or raw payloads.",
                parameterName);
        }

        return validated;
    }
}
