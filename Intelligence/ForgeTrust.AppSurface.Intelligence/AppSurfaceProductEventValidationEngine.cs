using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Shared privacy-preserving validator for static and composed product-event registries.
/// </summary>
internal static class AppSurfaceProductEventValidationEngine
{
    internal static AppSurfaceProductEventValidationResult Validate(
        AppSurfaceProductEvent productEvent,
        IReadOnlyDictionary<string, AppSurfaceProductEventContract> contractsByName,
        IReadOnlySet<string> forbiddenPropertyNames)
    {
        ArgumentNullException.ThrowIfNull(productEvent);
        ArgumentNullException.ThrowIfNull(contractsByName);
        ArgumentNullException.ThrowIfNull(forbiddenPropertyNames);

        if (!contractsByName.TryGetValue(productEvent.Name, out var contract))
        {
            return new AppSurfaceProductEventValidationResult(
                null,
                isValid: false,
                sanitizedProperties: new Dictionary<string, string>(),
                rejectedProperties: [],
                diagnostics: ["Event name is not registered in the AppSurface product-intelligence registry."],
                reasonCodes: [AppSurfaceProductEventValidationFailureReason.EventNotRegistered],
                fixHint: "Register an event contract through options.RegisterEventContracts(...).");
        }

        var allowedProperties = contract.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        var diagnostics = new List<string>();
        var reasonCodes = new List<AppSurfaceProductEventValidationFailureReason>();

        if (contract.Lifecycle == AppSurfaceProductEventLifecycle.Deprecated)
        {
            diagnostics.Add($"Event '{contract.Name}' is deprecated and should not be used for new capture.");
        }

        foreach (var (key, value) in productEvent.Properties)
        {
            var safeKey = AppSurfaceProductEventMetadata.SanitizeDiagnosticPropertyName(key);
            if (!allowedProperties.TryGetValue(key, out var property))
            {
                rejected.Add(safeKey);
                diagnostics.Add($"Property '{safeKey}' is not registered for event '{contract.Name}'.");
                reasonCodes.Add(AppSurfaceProductEventValidationFailureReason.PropertyNotRegistered);
                continue;
            }

            if (forbiddenPropertyNames.Contains(key))
            {
                rejected.Add(safeKey);
                diagnostics.Add($"Property '{safeKey}' uses a globally forbidden property name.");
                reasonCodes.Add(AppSurfaceProductEventValidationFailureReason.ForbiddenPropertyName);
                continue;
            }

            if (!TrySanitizePropertyValue(property, value, out var sanitizedValue, out var diagnostic))
            {
                rejected.Add(safeKey);
                diagnostics.Add(diagnostic);
                reasonCodes.Add(AppSurfaceProductEventValidationFailureReason.InvalidPropertyValue);
                continue;
            }

            sanitized[key] = sanitizedValue;
        }

        var missingRequired = contract.Properties
            .Where(property => property.Required && !sanitized.ContainsKey(property.Name))
            .Select(property => property.Name)
            .ToArray();

        foreach (var property in missingRequired)
        {
            diagnostics.Add($"Required property '{property}' is missing for event '{contract.Name}'.");
            reasonCodes.Add(AppSurfaceProductEventValidationFailureReason.RequiredPropertyMissing);
        }

        return new AppSurfaceProductEventValidationResult(
            contract,
            isValid: missingRequired.Length == 0,
            sanitizedProperties: sanitized,
            rejectedProperties: rejected,
            diagnostics: diagnostics,
            reasonCodes: reasonCodes,
            fixHint: missingRequired.Length == 0
                ? null
                : "Send every required property declared by the matched event contract.");
    }

    private static bool TrySanitizePropertyValue(
        AppSurfaceProductEventPropertyContract property,
        string rawValue,
        out string sanitizedValue,
        out string diagnostic)
    {
        sanitizedValue = string.Empty;
        diagnostic = string.Empty;
        var value = AppSurfaceProductEventMetadata.NormalizeOptionalText(rawValue);
        if (value is null)
        {
            diagnostic = $"Property '{property.Name}' has an empty value.";
            return false;
        }

        if (AppSurfaceProductEventMetadata.ContainsForbiddenValueShape(value))
        {
            diagnostic = $"Property '{property.Name}' contains a forbidden value shape.";
            return false;
        }

        return property.ValueShape switch
        {
            AppSurfaceProductEventValueShape.NonNegativeInteger => TrySanitizeNonNegativeInteger(
                property,
                value,
                out sanitizedValue,
                out diagnostic),
            AppSurfaceProductEventValueShape.Boolean => TrySanitizeBoolean(
                property,
                value,
                out sanitizedValue,
                out diagnostic),
            AppSurfaceProductEventValueShape.AllowedValue => TrySanitizeAllowedValue(
                property,
                value,
                out sanitizedValue,
                out diagnostic),
            AppSurfaceProductEventValueShape.Token => TrySanitizeToken(
                property,
                value,
                out sanitizedValue,
                out diagnostic),
            AppSurfaceProductEventValueShape.BoundedText => TrySanitizeBoundedText(
                property,
                value,
                out sanitizedValue,
                out diagnostic),
            _ => ThrowUnexpectedValueShape(property.ValueShape)
        };
    }

    [ExcludeFromCodeCoverage(Justification = "Property contracts reject undefined value-shape enum values before validation.")]
    private static bool ThrowUnexpectedValueShape(AppSurfaceProductEventValueShape valueShape)
    {
        throw new ArgumentOutOfRangeException(
            nameof(valueShape),
            valueShape,
            "Unexpected product-event property value shape.");
    }

    private static bool TrySanitizeNonNegativeInteger(
        AppSurfaceProductEventPropertyContract property,
        string value,
        out string sanitizedValue,
        out string diagnostic)
    {
        sanitizedValue = string.Empty;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            diagnostic = $"Property '{property.Name}' must be a non-negative integer.";
            return false;
        }

        sanitizedValue = parsed.ToString(CultureInfo.InvariantCulture);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TrySanitizeBoolean(
        AppSurfaceProductEventPropertyContract property,
        string value,
        out string sanitizedValue,
        out string diagnostic)
    {
        if (bool.TryParse(value, out var parsed))
        {
            sanitizedValue = parsed ? "true" : "false";
            diagnostic = string.Empty;
            return true;
        }

        sanitizedValue = string.Empty;
        diagnostic = $"Property '{property.Name}' must be a boolean value.";
        return false;
    }

    private static bool TrySanitizeAllowedValue(
        AppSurfaceProductEventPropertyContract property,
        string value,
        out string sanitizedValue,
        out string diagnostic)
    {
        if (property.AllowedValues.Contains(value, StringComparer.Ordinal))
        {
            sanitizedValue = value;
            diagnostic = string.Empty;
            return true;
        }

        sanitizedValue = string.Empty;
        diagnostic = $"Property '{property.Name}' value is not in the registered allowed-value set.";
        return false;
    }

    private static bool TrySanitizeToken(
        AppSurfaceProductEventPropertyContract property,
        string value,
        out string sanitizedValue,
        out string diagnostic)
    {
        if (value.Length > property.MaxLength)
        {
            sanitizedValue = string.Empty;
            diagnostic = $"Property '{property.Name}' exceeds the maximum allowed value length.";
            return false;
        }

        if (!IsSafeTokenValue(value))
        {
            sanitizedValue = string.Empty;
            diagnostic = $"Property '{property.Name}' must be a bounded token value.";
            return false;
        }

        sanitizedValue = value;
        diagnostic = string.Empty;
        return true;
    }

    private static bool TrySanitizeBoundedText(
        AppSurfaceProductEventPropertyContract property,
        string value,
        out string sanitizedValue,
        out string diagnostic)
    {
        if (value.Length > property.MaxLength)
        {
            sanitizedValue = string.Empty;
            diagnostic = $"Property '{property.Name}' exceeds the maximum allowed value length.";
            return false;
        }

        sanitizedValue = value;
        diagnostic = string.Empty;
        return true;
    }

    private static bool IsSafeTokenValue(string value)
    {
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
