namespace ForgeTrust.Runnable.Config;

/// <summary>
/// Requires a strongly typed configuration wrapper to resolve a value during initialization.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to a concrete <see cref="Config{T}"/> or <see cref="ConfigStruct{T}"/>
/// wrapper when startup should fail if provider/default resolution leaves the wrapper without a
/// value. A <see cref="Config{T}.DefaultValue"/> or <see cref="ConfigStruct{T}.DefaultValue"/>
/// satisfies the requirement because the requirement is resolved presence, not provider-source
/// auditing.
/// </para>
/// <para>
/// This attribute is intentionally separate from scalar value validation attributes such as
/// <see cref="ConfigValueNotEmptyAttribute"/>. Use <see cref="ConfigKeyRequiredAttribute"/> when
/// absence should fail, and use value validation when a resolved value must satisfy shape or range
/// rules.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ConfigKeyRequiredAttribute : Attribute
{
}
