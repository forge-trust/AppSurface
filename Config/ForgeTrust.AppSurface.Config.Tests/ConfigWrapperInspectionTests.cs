using System.ComponentModel.DataAnnotations;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigWrapperInspectionTests
{
    [Fact]
    public void ValidationFailureFactoryUsesFallbackMemberAndCanonicalKeyRendering()
    {
        var key = TranslatedKey();
        var failure = ConfigValidationFailureFactory.FromValidationResult(
            key, typeof(DefaultReference), typeof(string), "Nested",
            new ValidationResult("invalid"), defaultMemberName: "Field");

        Assert.Equal("Inspection:Value", failure.Key);
        Assert.Equal(["Nested.Field"], failure.MemberNames);

        var objectFailure = ConfigValidationFailureFactory.FromValidationResult(
            key, typeof(DefaultReference), typeof(string), "Nested", new ValidationResult(null, [" "]));
        Assert.Equal(["Nested"], objectFailure.MemberNames);
        Assert.Equal("The configuration value is invalid.", objectFailure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ObjectValidationRetainsMemberPath(bool valueType)
    {
        IConfigInspectable wrapper = valueType ? new ConfigStruct<RequiredStruct>() : new Config<RequiredReference>();
        object value = valueType ? new RequiredStruct() : new RequiredReference();

        var result = wrapper.Inspect(TranslatedKey(), value, ConfigAuditEntryState.Resolved);

        Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-validation-failed", diagnostic.Code);
        Assert.Equal("Inspection:Value", diagnostic.Key);
        Assert.Equal("Name", diagnostic.ConfigPath);
    }

    private sealed class RequiredReference
    {
        [Required]
        public string? Name { get; init; }
    }

    private struct RequiredStruct
    {
        [Required]
        public string? Name { get; init; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_DefaultsUseCanonicalRenderingAndTypedInterface(bool valueType)
    {
        var key = TranslatedKey();
        IConfigInspectable wrapper = valueType ? new DefaultStruct() : new DefaultReference();

        var result = wrapper.Inspect(key, null, ConfigAuditEntryState.Missing);

        Assert.Equal(ConfigAuditEntryState.Defaulted, result.State);
        Assert.Equal(valueType ? (object)3 : "default", result.Value);
        Assert.Equal(key.Value, result.DefaultSource?.ConfigPath);
        Assert.Equal(key.Value, result.DefaultSource?.AppliedToPath);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ResolvedValuesAndMissingValuesKeepProviderState(bool valueType)
    {
        var key = TranslatedKey();
        IConfigInspectable wrapper = valueType ? new ConfigStruct<int>() : new Config<string>();
        object value = valueType ? 8 : "configured";

        var resolved = wrapper.Inspect(key, value, ConfigAuditEntryState.Resolved);
        var missing = wrapper.Inspect(key, null, ConfigAuditEntryState.Missing);

        Assert.Equal(ConfigAuditEntryState.Resolved, resolved.State);
        Assert.Equal(value, resolved.Value);
        Assert.Null(resolved.DefaultSource);
        Assert.Empty(resolved.Diagnostics);
        Assert.Equal(ConfigAuditEntryState.Missing, missing.State);
        Assert.Null(missing.Value);
        Assert.Null(missing.DefaultSource);
        Assert.Empty(missing.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_TypeMismatchProducesCanonicalDiagnostic(bool valueType)
    {
        var key = TranslatedKey();
        IConfigInspectable wrapper = valueType ? new ConfigStruct<int>() : new Config<string>();

        var result = wrapper.Inspect(key, new object(), ConfigAuditEntryState.Resolved);

        Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-value-type-mismatch", diagnostic.Code);
        Assert.Equal(key.Value, diagnostic.Key);
        Assert.Equal(key.Value, diagnostic.ConfigPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ValidationFailureRendersKeyAndOmitsValidationMessage(bool valueType)
    {
        var key = TranslatedKey();
        IConfigInspectable wrapper = valueType ? new InvalidStruct() : new InvalidReference();

        var result = wrapper.Inspect(key, null, ConfigAuditEntryState.Missing);

        Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-validation-failed", diagnostic.Code);
        Assert.Equal(key.Value, diagnostic.Key);
        Assert.Equal(key.Value, diagnostic.ConfigPath);
        Assert.DoesNotContain("sensitive-validation-message", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_DefaultGetterExceptionIsContained(bool valueType)
    {
        var key = TranslatedKey();
        IConfigInspectable wrapper = valueType ? new ThrowingStruct() : new ThrowingReference();

        var result = wrapper.Inspect(key, null, ConfigAuditEntryState.Missing);

        Assert.Equal(ConfigAuditEntryState.Invalid, result.State);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("config-validation-threw", diagnostic.Code);
        Assert.Equal(key.Value, diagnostic.Key);
        Assert.DoesNotContain("sensitive-exception-message", diagnostic.Message, StringComparison.Ordinal);
    }

    private static AppSurfaceConfigKey TranslatedKey() =>
        AppSurfaceConfigKey.Parse("Inspection:Value").WithInput(ConfigKeyInputOrigin.TranslatedDot, "Inspection.Value");

    private sealed class DefaultReference : Config<string>
    {
        public override string DefaultValue => "default";
    }

    private sealed class DefaultStruct : ConfigStruct<int>
    {
        public override int? DefaultValue => 3;
    }

    private sealed class InvalidReference : Config<string>
    {
        public override string DefaultValue => "invalid";
        protected override IEnumerable<ValidationResult>? ValidateValue(string value, ValidationContext context) =>
            [new ValidationResult("sensitive-validation-message")];
    }

    private sealed class InvalidStruct : ConfigStruct<int>
    {
        public override int? DefaultValue => 3;
        protected override IEnumerable<ValidationResult>? ValidateValue(int value, ValidationContext context) =>
            [new ValidationResult("sensitive-validation-message")];
    }

    private sealed class ThrowingReference : Config<string>
    {
        public override string DefaultValue => throw new InvalidOperationException("sensitive-exception-message");
    }

    private sealed class ThrowingStruct : ConfigStruct<int>
    {
        public override int? DefaultValue => throw new InvalidOperationException("sensitive-exception-message");
    }
}
