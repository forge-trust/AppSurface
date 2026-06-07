namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes one allowed property on a registered AppSurface product event.
/// </summary>
public sealed class AppSurfaceProductEventPropertyContract
{
    /// <summary>
    /// Creates a property contract for the typed product-event registry.
    /// </summary>
    /// <param name="name">Stable property name accepted in event payloads.</param>
    /// <param name="description">Human-readable purpose and expected value shape.</param>
    /// <param name="sensitivity">Privacy sensitivity classification.</param>
    /// <param name="cardinality">Expected cardinality budget.</param>
    /// <param name="required">Whether capture should drop the event when the property is absent.</param>
    /// <param name="allowedValues">Optional bounded set of allowed values for low-cardinality dimensions.</param>
    /// <param name="maxLength">Maximum allowed emitted value length.</param>
    public AppSurfaceProductEventPropertyContract(
        string name,
        string description,
        AppSurfaceProductEventSensitivity sensitivity,
        AppSurfaceProductEventCardinality cardinality,
        bool required = false,
        IEnumerable<string>? allowedValues = null,
        int maxLength = 64)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Maximum length must be positive.");
        }

        Name = AppSurfaceProductEventMetadata.RequireIdentifier(name, nameof(name));
        Description = AppSurfaceProductEventMetadata.RequireText(description, nameof(description));
        Sensitivity = sensitivity;
        Cardinality = cardinality;
        Required = required;
        AllowedValues = AppSurfaceProductEventMetadata.NormalizeOptionalTextList(
            allowedValues,
            nameof(allowedValues));
        MaxLength = maxLength;
    }

    /// <summary>
    /// Gets the stable property name accepted in event payloads.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the human-readable purpose and expected value shape.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the privacy sensitivity classification.
    /// </summary>
    public AppSurfaceProductEventSensitivity Sensitivity { get; }

    /// <summary>
    /// Gets the expected cardinality budget.
    /// </summary>
    public AppSurfaceProductEventCardinality Cardinality { get; }

    /// <summary>
    /// Gets a value indicating whether capture should drop the event when the property is absent.
    /// </summary>
    public bool Required { get; }

    /// <summary>
    /// Gets the optional bounded set of allowed values for low-cardinality dimensions.
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; }

    /// <summary>
    /// Gets the maximum allowed emitted value length.
    /// </summary>
    public int MaxLength { get; }
}
