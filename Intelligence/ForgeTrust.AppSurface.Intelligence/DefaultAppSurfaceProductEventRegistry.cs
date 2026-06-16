using System.Collections.Frozen;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Default immutable composed product-event registry.
/// </summary>
/// <remarks>
/// The registry composes AppSurface built-in contracts with host/package contracts registered through options. Built-in
/// contracts are trusted framework contracts. Custom contracts keep the same runtime privacy sanitizers and are also
/// checked for registration-time constraints: no sensitive properties, no high-cardinality properties, no globally
/// forbidden property names, and no incompatible duplicate event names.
/// </remarks>
internal sealed class DefaultAppSurfaceProductEventRegistry : IAppSurfaceProductEventRegistry
{
    private static readonly IReadOnlySet<string> ForbiddenPropertyNames = AppSurfaceProductEventRegistry.ForbiddenProperties;
    private readonly IReadOnlyDictionary<string, AppSurfaceProductEventContract> _contractsByName;
    private readonly IReadOnlySet<string> _forbiddenProperties;

    internal DefaultAppSurfaceProductEventRegistry(IEnumerable<AppSurfaceProductEventContract> customContracts)
    {
        ArgumentNullException.ThrowIfNull(customContracts);
        var composed = new Dictionary<string, AppSurfaceProductEventContract>(StringComparer.Ordinal);

        foreach (var contract in AppSurfaceProductEventRegistry.All)
        {
            composed.Add(contract.Name, contract);
        }

        foreach (var contract in customContracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            ValidateCustomContract(contract);

            if (composed.TryGetValue(contract.Name, out var existing))
            {
                if (AreEquivalent(existing, contract))
                {
                    continue;
                }

                var existingOwner = AppSurfaceProductEventMetadata.SanitizeDiagnosticOwner(existing.Owner);
                var contractOwner = AppSurfaceProductEventMetadata.SanitizeDiagnosticOwner(contract.Owner);
                throw new AppSurfaceProductEventContractRegistrationException(
                    $"Event contract '{contract.Name}' is already registered by owner '{existingOwner}' and cannot be re-registered by owner '{contractOwner}' with a different schema.");
            }

            composed.Add(contract.Name, contract);
        }

        _contractsByName = composed;
        _forbiddenProperties = ForbiddenPropertyNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        All = Array.AsReadOnly(composed.Values.ToArray());
    }

    /// <inheritdoc />
    public IReadOnlyList<AppSurfaceProductEventContract> All { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> ForbiddenProperties => _forbiddenProperties;

    /// <inheritdoc />
    public AppSurfaceProductEventContract? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _contractsByName.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public AppSurfaceProductEventValidationResult Validate(AppSurfaceProductEvent productEvent)
    {
        return AppSurfaceProductEventValidationEngine.Validate(
            productEvent,
            _contractsByName,
            ForbiddenPropertyNames);
    }

    private static void ValidateCustomContract(AppSurfaceProductEventContract contract)
    {
        var owner = AppSurfaceProductEventMetadata.SanitizeDiagnosticOwner(contract.Owner);

        if (!AppSurfaceProductEventMetadata.IsSafeDiagnosticIdentifier(contract.Name))
        {
            throw new AppSurfaceProductEventContractRegistrationException(
                $"Event contract '{AppSurfaceProductEventMetadata.SanitizeDiagnosticEventName(contract.Name)}' owned by '{owner}' uses an unsafe event name.");
        }

        foreach (var property in contract.Properties)
        {
            if (!AppSurfaceProductEventMetadata.IsSafeDiagnosticIdentifier(property.Name))
            {
                throw new AppSurfaceProductEventContractRegistrationException(
                    $"Event contract '{contract.Name}' owned by '{owner}' uses unsafe property '{AppSurfaceProductEventMetadata.SanitizeDiagnosticPropertyName(property.Name)}'.");
            }

            if (ForbiddenPropertyNames.Contains(property.Name))
            {
                throw new AppSurfaceProductEventContractRegistrationException(
                    $"Event contract '{contract.Name}' owned by '{owner}' uses globally forbidden property '{property.Name}'.");
            }

            if (property.Sensitivity == AppSurfaceProductEventSensitivity.Sensitive)
            {
                throw new AppSurfaceProductEventContractRegistrationException(
                    $"Event contract '{contract.Name}' owned by '{owner}' uses sensitive property '{property.Name}'. Custom contract packs cannot register sensitive properties by default.");
            }

            if (property.Cardinality == AppSurfaceProductEventCardinality.High)
            {
                throw new AppSurfaceProductEventContractRegistrationException(
                    $"Event contract '{contract.Name}' owned by '{owner}' uses high-cardinality property '{property.Name}'. Custom contract packs cannot register high-cardinality properties by default.");
            }
        }
    }

    private static bool AreEquivalent(
        AppSurfaceProductEventContract left,
        AppSurfaceProductEventContract right)
    {
        return left.Name == right.Name
            && left.Lifecycle == right.Lifecycle
            && left.Purpose == right.Purpose
            && left.Owner == right.Owner
            && left.RetentionExpectation == right.RetentionExpectation
            && AreEquivalent(left.ForbiddenExamples, right.ForbiddenExamples)
            && left.Properties.Count == right.Properties.Count
            && AreEquivalent(left.Properties, right.Properties);
    }

    private static bool AreEquivalent(
        IReadOnlyList<AppSurfaceProductEventPropertyContract> left,
        IReadOnlyList<AppSurfaceProductEventPropertyContract> right)
    {
        var rightByName = right.ToDictionary(property => property.Name, StringComparer.Ordinal);
        return left.All(property =>
            rightByName.TryGetValue(property.Name, out var candidate)
            && AreEquivalent(property, candidate));
    }

    private static bool AreEquivalent(
        AppSurfaceProductEventPropertyContract left,
        AppSurfaceProductEventPropertyContract right)
    {
        return left.Name == right.Name
            && left.Description == right.Description
            && left.Sensitivity == right.Sensitivity
            && left.Cardinality == right.Cardinality
            && left.Required == right.Required
            && left.MaxLength == right.MaxLength
            && left.ValueShape == right.ValueShape
            && AreEquivalent(left.AllowedValues, right.AllowedValues);
    }

    private static bool AreEquivalent(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.Count == right.Count
            && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
    }
}
