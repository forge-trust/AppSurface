namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identifies a durable schedule independently from work, Flow, command, and occurrence identities.
/// </summary>
public readonly record struct DurableScheduleId
{
    /// <summary>
    /// Initializes a schedule identifier.
    /// </summary>
    /// <param name="value">Opaque, privacy-safe value.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is invalid.</exception>
    public DurableScheduleId(string value)
    {
        Value = DurableIdentifier.Require(value, nameof(value), 200);
    }

    /// <summary>Gets the opaque identifier value.</summary>
    public string Value { get; }

    /// <summary>Creates a cryptographically random schedule identifier.</summary>
    /// <returns>A new schedule identifier.</returns>
    public static DurableScheduleId New() => new(Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}
