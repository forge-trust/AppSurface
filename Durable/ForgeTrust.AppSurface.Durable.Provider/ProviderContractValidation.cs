using System.Globalization;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Validates provider-facing identifiers at the contract boundary.</summary>
internal static class ProviderContractValidation
{
    /// <summary>Requires a non-default scope identifier.</summary>
    /// <param name="value">Scope identifier to validate.</param>
    /// <param name="parameterName">Parameter name used by validation exceptions.</param>
    /// <exception cref="ArgumentException">Thrown when the identifier is default or invalid.</exception>
    internal static void Require(DurableScopeId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

    /// <summary>Requires a non-default work identifier.</summary>
    /// <param name="value">Work identifier to validate.</param>
    /// <param name="parameterName">Parameter name used by validation exceptions.</param>
    /// <exception cref="ArgumentException">Thrown when the identifier is default or invalid.</exception>
    internal static void Require(DurableWorkId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

    /// <summary>Requires a non-default command identifier.</summary>
    /// <param name="value">Command identifier to validate.</param>
    /// <param name="parameterName">Parameter name used by validation exceptions.</param>
    /// <exception cref="ArgumentException">Thrown when the identifier is default or invalid.</exception>
    internal static void Require(DurableCommandId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

    /// <summary>Requires bounded portable identifier text.</summary>
    /// <param name="value">Identifier text to validate.</param>
    /// <param name="parameterName">Parameter name used by validation exceptions.</param>
    /// <param name="maximumLength">Maximum accepted character count.</param>
    /// <returns>The original validated value.</returns>
    /// <remarks>Accepted characters are ASCII letters, digits, hyphens, underscores, periods, and colons.</remarks>
    /// <exception cref="ArgumentException">Thrown when the value is blank, too long, or contains another character.</exception>
    internal static string Require(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Durable provider identifiers must not be null, empty, or whitespace.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Durable provider identifiers must not exceed {maximumLength} characters."),
                parameterName);
        }

        if (value.Any(static character =>
                char.IsControl(character)
                || (!char.IsAsciiLetterOrDigit(character)
                    && character is not '-' and not '_' and not '.' and not ':')))
        {
            throw new ArgumentException(
                "Durable provider identifiers may contain only ASCII letters, digits, hyphens, underscores, periods, and colons.",
                parameterName);
        }

        return value;
    }
}
