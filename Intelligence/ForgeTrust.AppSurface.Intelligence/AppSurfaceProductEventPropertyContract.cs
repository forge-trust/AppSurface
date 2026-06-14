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
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sensitivity" /> or <paramref name="cardinality" /> is not a defined product-event enum
    /// value, or when <paramref name="maxLength" /> is not positive.
    /// </exception>
    public AppSurfaceProductEventPropertyContract(
        string name,
        string description,
        AppSurfaceProductEventSensitivity sensitivity,
        AppSurfaceProductEventCardinality cardinality,
        bool required = false,
        IEnumerable<string>? allowedValues = null,
        int maxLength = 64)
        : this(
            name,
            description,
            sensitivity,
            cardinality,
            AppSurfaceProductEventValueShape.Token,
            required,
            allowedValues,
            maxLength)
    {
    }

    /// <summary>
    /// Creates a property contract for the typed product-event registry.
    /// </summary>
    /// <param name="name">Stable property name accepted in event payloads.</param>
    /// <param name="description">Human-readable purpose and expected value shape.</param>
    /// <param name="sensitivity">Privacy sensitivity classification.</param>
    /// <param name="cardinality">Expected cardinality budget.</param>
    /// <param name="valueShape">Sanitized value shape accepted for this property.</param>
    /// <param name="required">Whether capture should drop the event when the property is absent.</param>
    /// <param name="allowedValues">Optional bounded set of allowed values for low-cardinality dimensions.</param>
    /// <param name="maxLength">Maximum allowed emitted value length.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sensitivity" />, <paramref name="cardinality" />, or <paramref name="valueShape" /> is
    /// not a defined product-event enum value, or when <paramref name="maxLength" /> is not positive.
    /// </exception>
    public AppSurfaceProductEventPropertyContract(
        string name,
        string description,
        AppSurfaceProductEventSensitivity sensitivity,
        AppSurfaceProductEventCardinality cardinality,
        AppSurfaceProductEventValueShape valueShape,
        bool required = false,
        IEnumerable<string>? allowedValues = null,
        int maxLength = 64)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Maximum length must be positive.");
        }

        if (!Enum.IsDefined(sensitivity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sensitivity),
                sensitivity,
                "Sensitivity must be a defined product-event sensitivity value.");
        }

        if (!Enum.IsDefined(cardinality))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cardinality),
                cardinality,
                "Cardinality must be a defined product-event cardinality value.");
        }

        if (!Enum.IsDefined(valueShape))
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueShape),
                valueShape,
                "Value shape must be a defined product-event value shape.");
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
        ValueShape = allowedValues is not null && AllowedValues.Count > 0 && valueShape == AppSurfaceProductEventValueShape.Token
            ? AppSurfaceProductEventValueShape.AllowedValue
            : valueShape;

        if (ValueShape == AppSurfaceProductEventValueShape.AllowedValue && AllowedValues.Count == 0)
        {
            throw new ArgumentException(
                "Allowed-value properties must register at least one allowed value.",
                nameof(allowedValues));
        }

        if (AllowedValues.Count > 0 && ValueShape != AppSurfaceProductEventValueShape.AllowedValue)
        {
            throw new ArgumentException(
                "Allowed values may only be registered with the AllowedValue shape.",
                nameof(allowedValues));
        }
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

    /// <summary>
    /// Gets the sanitized value shape accepted for this property.
    /// </summary>
    /// <remarks>
    /// The registry uses this metadata to validate built-in and host-registered contract packs consistently. It no
    /// longer infers integer or token behavior from property names.
    /// </remarks>
    public AppSurfaceProductEventValueShape ValueShape { get; }
}
