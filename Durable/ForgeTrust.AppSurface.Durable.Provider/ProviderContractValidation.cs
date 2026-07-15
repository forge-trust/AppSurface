using System.Globalization;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Provider;

internal static class ProviderContractValidation
{
    internal static void Require(DurableScopeId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

    internal static void Require(DurableWorkId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

    internal static void Require(DurableCommandId value, string parameterName) =>
        _ = Require(value.Value, parameterName, 200);

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
