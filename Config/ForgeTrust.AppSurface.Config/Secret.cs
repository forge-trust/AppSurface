using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Represents a scalar secret destination with independent activation and availability.</summary>
/// <typeparam name="T">A non-null scalar supported by <see cref="ConfigValueConverter"/>.</typeparam>
/// <remarks>
/// Declare this type on a configuration member to opt its containing root into file secret composition.
/// The public constructor creates an empty destination. Only configuration resolution supplies values or disables
/// references. A required wrapper does not imply a value exists; check <see cref="HasValue"/> in application validation.
/// See <see href="https://appsurface.dev/config/secret-references">the secret reference guide</see>.
/// Default JSON serialization and formatting omit the payload. Application access, third-party serializers and
/// inspection of private process memory are outside that guarantee.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class Secret<T> : IConfigSecretValue where T : notnull
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly T? _value;

    /// <summary>Creates an enabled, empty destination without a source.</summary>
    public Secret() { }

    /// <summary>Creates an execution-owned value; callers must supply only validated scalar values.</summary>
    internal Secret(bool enabled, bool hasValue, T? value, string? provider)
    {
        if (hasValue && value is null) throw new ArgumentNullException(nameof(value));
        Enabled = enabled;
        HasValue = hasValue;
        _value = hasValue ? value : default;
        ResolvedProvider = hasValue ? provider : null;
    }

    /// <summary>Whether the declared reference is active; this does not activate an application feature.</summary>
    public bool Enabled { get; } = true;

    /// <summary>Whether a non-null effective value exists, including an empty string when accepted.</summary>
    public bool HasValue { get; }

    /// <summary>Gets the sensitive value. Never log or serialize the returned value.</summary>
    /// <exception cref="InvalidOperationException">No effective value exists.</exception>
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public T Value => HasValue ? _value! : throw new InvalidOperationException("The secret destination has no value.");

    /// <summary>Gets the canonical secret-provider id or the name of the effective base/environment provider.</summary>
    public string? ResolvedProvider { get; }

    /// <summary>Reads the sensitive value without throwing when the destination is empty.</summary>
    /// <param name="value">The sensitive value on success; otherwise the default value.</param>
    /// <returns>Whether a non-null effective value exists.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return HasValue;
    }

    /// <summary>Formats activation, availability and source only; never formats the payload.</summary>
    public override string ToString() => $"Secret {{ Enabled = {Enabled}, HasValue = {HasValue}, ResolvedProvider = {ResolvedProvider ?? "none"} }}";
}

/// <summary>Exposes only opaque state to framework traversal without a payload getter.</summary>
internal interface IConfigSecretValue
{
    /// <summary>Whether the reference is active.</summary>
    bool Enabled { get; }
    /// <summary>Whether a value is present.</summary>
    bool HasValue { get; }
    /// <summary>The effective source identity.</summary>
    string? ResolvedProvider { get; }
}
