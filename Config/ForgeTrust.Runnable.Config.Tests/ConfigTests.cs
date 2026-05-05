using System.ComponentModel.DataAnnotations;
using FakeItEasy;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.Runnable.Config.Tests;

public class ConfigTests
{
    private sealed class TestConfig : Config<string>
    {
        public override string DefaultValue => "fallback";
    }

    private sealed class TestStructConfig : ConfigStruct<int>
    {
        public override int? DefaultValue => 42;
    }

    private sealed class AnnotatedOptions
    {
        [Required]
        public string? Name { get; init; }

        [Range(1, 5)]
        public int RetryCount { get; init; }
    }

    private sealed class AnnotatedOptionsConfig : Config<AnnotatedOptions>
    {
    }

    private sealed class FieldAnnotatedOptions
    {
        [Required]
        public string? Name = null;

        [Range(1, 5)]
        public int RetryCount;
    }

    private sealed class FieldAnnotatedOptionsConfig : Config<FieldAnnotatedOptions>
    {
    }

    private sealed class DefaultAnnotatedOptionsConfig : Config<AnnotatedOptions>
    {
        public override AnnotatedOptions DefaultValue { get; } = new()
        {
            Name = "default",
            RetryCount = 0
        };
    }

    private sealed class ValidatableOptions : IValidatableObject
    {
        public bool Invalid { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Invalid)
            {
                yield return new ValidationResult("Object is invalid.");
            }
        }
    }

    private sealed class MultiMemberValidatableOptions : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield return new ValidationResult("Both values are invalid.", ["Second", "First"]);
        }
    }

    private sealed class ShortCircuitOptions : IValidatableObject
    {
        public static bool ObjectValidationRan { get; set; }

        [Required]
        public string? Name { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            ObjectValidationRan = true;
            yield return new ValidationResult("Object validation ran.");
        }
    }

    private sealed class ValidatableOptionsConfig : Config<ValidatableOptions>
    {
    }

    private sealed class MultiMemberValidatableOptionsConfig : Config<MultiMemberValidatableOptions>
    {
    }

    private sealed class ShortCircuitOptionsConfig : Config<ShortCircuitOptions>
    {
    }

    private sealed class NestedOptions
    {
        [Required]
        public string? Host { get; init; }
    }

    private sealed class EndpointOptions
    {
        [Required]
        public string? Url { get; init; }
    }

    private sealed class RecursiveOptions
    {
        [ValidateObjectMembers]
        public NestedOptions? Database { get; init; }

        [ValidateEnumeratedItems]
        public List<EndpointOptions?> Endpoints { get; init; } = [];
    }

    private sealed class NonRecursiveOptions
    {
        public NestedOptions? Database { get; init; }

        public List<EndpointOptions?> Endpoints { get; init; } = [];
    }

    private sealed class OptionsWithUnmarkedThrowingGetter
    {
        [Required]
        public string? Name { get; init; }

        public NestedOptions Database => throw new InvalidOperationException("Getter should not run.");
    }

    private sealed class RecursiveFieldOptions
    {
        [ValidateObjectMembers]
        public NestedOptions? Database = new();

        [ValidateEnumeratedItems]
        public List<EndpointOptions?> Endpoints = [];
    }

    private sealed class NestedValidatableOptions : IValidatableObject
    {
        public bool Invalid { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Invalid)
            {
                yield return new ValidationResult("Nested object is invalid.");
            }
        }
    }

    private sealed class OptionsWithNestedObjectLevelValidation
    {
        [ValidateObjectMembers]
        public NestedValidatableOptions Child { get; init; } = new();
    }

    private sealed class OptionsWithNullMarkedMembers
    {
        [ValidateObjectMembers]
        public NestedOptions? Database { get; init; }

        [ValidateEnumeratedItems]
        public List<EndpointOptions?>? Endpoints { get; init; }
    }

    private sealed class UnsupportedEnumeratedItemsValidatorOptions
    {
        [ValidateEnumeratedItems(typeof(object))]
        public List<EndpointOptions> Endpoints { get; init; } = [];
    }

    private readonly struct AnnotatedStructOptions
    {
        [Required]
        public string? Name { get; init; }

        [Range(1, 5)]
        public int RetryCount { get; init; }
    }

    private sealed class AnnotatedStructOptionsConfig : ConfigStruct<AnnotatedStructOptions>
    {
    }

    private sealed class DefaultAnnotatedStructOptionsConfig : ConfigStruct<AnnotatedStructOptions>
    {
        public override AnnotatedStructOptions? DefaultValue => new()
        {
            Name = "default",
            RetryCount = 0
        };
    }

    private readonly struct StructNestedOptions
    {
        [Required]
        public string? Name { get; init; }
    }

    private sealed class OptionsWithStructNestedObject
    {
        [ValidateObjectMembers]
        public StructNestedOptions Child { get; init; }
    }

    private sealed class RecursiveOptionsConfig : Config<RecursiveOptions>
    {
    }

    private sealed class NonRecursiveOptionsConfig : Config<NonRecursiveOptions>
    {
    }

    private sealed class OptionsWithUnmarkedThrowingGetterConfig : Config<OptionsWithUnmarkedThrowingGetter>
    {
    }

    private sealed class RecursiveFieldOptionsConfig : Config<RecursiveFieldOptions>
    {
    }

    private sealed class OptionsWithNestedObjectLevelValidationConfig : Config<OptionsWithNestedObjectLevelValidation>
    {
    }

    private sealed class OptionsWithNullMarkedMembersConfig : Config<OptionsWithNullMarkedMembers>
    {
    }

    private sealed class UnsupportedEnumeratedItemsValidatorOptionsConfig : Config<UnsupportedEnumeratedItemsValidatorOptions>
    {
    }

    private sealed class OptionsWithStructNestedObjectConfig : Config<OptionsWithStructNestedObject>
    {
    }

    private sealed class OptionsWithSharedReferences
    {
        [ValidateObjectMembers]
        public NestedOptions? Primary { get; init; }

        [ValidateObjectMembers]
        public NestedOptions? Secondary { get; init; }
    }

    private sealed class OptionsWithSharedReferencesConfig : Config<OptionsWithSharedReferences>
    {
    }

    private sealed class CyclicOptions
    {
        [ValidateObjectMembers]
        public CyclicOptions? Next { get; set; }
    }

    private sealed class CyclicOptionsConfig : Config<CyclicOptions>
    {
    }

    private sealed class UnsupportedValidatorOptions
    {
        [ValidateObjectMembers(typeof(object))]
        public NestedOptions Child { get; init; } = new();
    }

    private sealed class UnsupportedValidatorOptionsConfig : Config<UnsupportedValidatorOptions>
    {
    }

    [Fact]
    public void Init_UsesDefaultValueWhenManagerReturnsNull()
    {
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        var config = new TestConfig();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<string>("Production", "Test.Key"))
            .Returns(null);

        ((IConfig)config).Init(configManager, environmentProvider, "Test.Key");

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal("fallback", config.Value);
    }

    [Fact]
    public void Init_PopulatesValueFromConfigManagerWhenPresent()
    {
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        var config = new TestConfig();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<string>("Production", "Test.Key"))
            .Returns("value");

        ((IConfig)config).Init(configManager, environmentProvider, "Test.Key");

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Equal("value", config.Value);
    }

    [Fact]
    public void Init_ForStructConfigUsesManagerValueWhenPresent()
    {
        var configManager = A.Fake<IConfigManager>();
        A.CallTo(() => configManager.GetValue<int?>(A<string>._, A<string>._))
            .Returns(7);

        var environmentProvider = A.Fake<IEnvironmentProvider>();
        var config = new TestStructConfig();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");

        ((IConfig)config).Init(configManager, environmentProvider, "Struct.Key");

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Equal(7, config.Value);
    }

    private sealed class RawConfig : Config<string>
    {
    }

    private sealed class RawStructConfig : ConfigStruct<int>
    {
    }

    [ConfigKeyRequired]
    private sealed class RequiredStringConfig : Config<string>
    {
    }

    [ConfigKeyRequired]
    private sealed class DefaultRequiredStringConfig : Config<string>
    {
        public override string DefaultValue => "fallback";
    }

    [ConfigKeyRequired]
    private sealed class RequiredIntConfig : ConfigStruct<int>
    {
    }

    [ConfigKeyRequired]
    private sealed class DefaultRequiredIntConfig : ConfigStruct<int>
    {
        public override int? DefaultValue => 42;
    }

    [ConfigKeyRequired]
    private sealed class RequiredAnnotatedOptionsConfig : Config<AnnotatedOptions>
    {
    }

    [ConfigKey("Base.Required", root: true)]
    [ConfigKeyRequired]
    private class BaseRequiredStringConfig : Config<string>
    {
    }

    private sealed class DerivedRequiredStringConfig : BaseRequiredStringConfig
    {
    }

    [ConfigValueNotEmpty]
    private sealed class NotEmptyStringConfig : Config<string>
    {
    }

    [ConfigKeyRequired]
    [ConfigValueNotEmpty]
    private sealed class RequiredNotEmptyStringConfig : Config<string>
    {
    }

    [ConfigValueNotEmpty]
    private sealed class DefaultInvalidNotEmptyStringConfig : Config<string>
    {
        public override string DefaultValue => string.Empty;
    }

    [ConfigValueMinLength(3)]
    private sealed class MinLengthStringConfig : Config<string>
    {
    }

    [ConfigValueRange(1, 5)]
    private sealed class RangedIntConfig : ConfigStruct<int>
    {
    }

    [ConfigValueRange(1, 5)]
    private sealed class DefaultInvalidRangedIntConfig : ConfigStruct<int>
    {
        public override int? DefaultValue => 6;
    }

    [ConfigValueRange(1.5, 5.5)]
    private sealed class RangedDoubleConfig : ConfigStruct<double>
    {
    }

    [ConfigValueRange(1, 5)]
    private sealed class RangedDoubleWithIntegerBoundsConfig : ConfigStruct<double>
    {
    }

    [ConfigValueNotEmpty]
    private sealed class NotEmptyGuidConfig : ConfigStruct<Guid>
    {
    }

    [ConfigValueMinLength(3)]
    private sealed class UnsupportedMinLengthIntConfig : ConfigStruct<int>
    {
    }

    [RejectUnlessAllowed]
    private class BaseInheritedScalarConfig : Config<string>
    {
    }

    private sealed class DerivedInheritedScalarConfig : BaseInheritedScalarConfig
    {
    }

    [RejectUnlessAllowed]
    private sealed class CustomScalarAttributeConfig : Config<string>
    {
    }

    [CaptureContext]
    private sealed class ContextAwareScalarConfig : Config<string>
    {
    }

    private sealed class HookFailureConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult> ValidateValue(
            string value,
            ValidationContext validationContext)
        {
            yield return new ValidationResult("Hook failure.", ["HookMember"]);
        }
    }

    private sealed class HookDefaultFailureConfig : Config<string>
    {
        public override string DefaultValue => "fallback";

        protected override IEnumerable<ValidationResult> ValidateValue(
            string value,
            ValidationContext validationContext)
        {
            if (value == DefaultValue)
            {
                yield return new ValidationResult("Default hook failure.");
            }
        }
    }

    private sealed class HookSuccessNoiseConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            string value,
            ValidationContext validationContext) =>
            [null!, ValidationResult.Success!];
    }

    private sealed class HookNullEnumerableConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult>? ValidateValue(
            string value,
            ValidationContext validationContext) =>
            null;
    }

    private sealed class HookThrowsConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult> ValidateValue(
            string value,
            ValidationContext validationContext) =>
            throw new InvalidOperationException("Hook broke.");
    }

    [ConfigValueMinLength(5)]
    private sealed class AttributeAndHookFailureConfig : Config<string>
    {
        protected override IEnumerable<ValidationResult> ValidateValue(
            string value,
            ValidationContext validationContext)
        {
            yield return new ValidationResult("Hook failure.");
        }
    }

    [ConfigValueNotEmpty]
    private sealed class ObjectConfigWithScalarValidation : Config<AnnotatedOptions>
    {
        public static bool HookRan { get; set; }

        protected override IEnumerable<ValidationResult> ValidateValue(
            AnnotatedOptions value,
            ValidationContext validationContext)
        {
            HookRan = true;
            yield return new ValidationResult("Object hook should not run.");
        }
    }

    private sealed class HookStructConfig : ConfigStruct<int>
    {
        protected override IEnumerable<ValidationResult> ValidateValue(
            int value,
            ValidationContext validationContext)
        {
            yield return new ValidationResult("Struct hook failure.");
        }
    }

    private sealed class RejectUnlessAllowedAttribute : ConfigValueValidationAttribute
    {
        protected override ValidationResult? IsValid(
            object? value,
            ValidationContext validationContext) =>
            value as string == "allowed"
                ? ValidationResult.Success
                : new ValidationResult("Only 'allowed' is accepted.");
    }

    private sealed class CaptureContextAttribute : ConfigValueValidationAttribute
    {
        protected override ValidationResult? IsValid(
            object? value,
            ValidationContext validationContext)
        {
            var valid = validationContext.ObjectInstance is ContextAwareScalarConfig
                        && validationContext.ObjectType == typeof(ContextAwareScalarConfig)
                        && validationContext.DisplayName == "App.Settings"
                        && validationContext.MemberName == null
                        && validationContext.GetService(typeof(object)) == null;

            return valid
                ? ValidationResult.Success
                : new ValidationResult("Unexpected validation context.");
        }
    }

    [Fact]
    public void Base_DefaultValue_ReturnsNull()
    {
        var config = new RawConfig();
        Assert.Null(config.DefaultValue);

        var structConfig = new RawStructConfig();
        Assert.Null(structConfig.DefaultValue);
    }

    [Fact]
    public void Init_WithNoValueAndNoDefault_SetsHasValueToFalse()
    {
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();
        var config = new RawConfig();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<string>(A<string>._, A<string>._)).Returns(null);

        ((IConfig)config).Init(configManager, environmentProvider, "Key");

        Assert.False(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Null(config.Value);
    }

    [Fact]
    public void Struct_Init_PopulatesValueFromConfigManager()
    {
        var config = new RawStructConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<int?>(A<string>._, A<string>._)).Returns(42);

        ((IConfig)config).Init(configManager, environmentProvider, "Key");

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Equal(42, config.Value);
    }

    [Fact]
    public void Struct_Init_SetsIsDefaultValueToTrueWhenMatchesDefault()
    {
        var config = new TestStructConfig(); // DefaultValue is 42
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<int?>(A<string>._, A<string>._)).Returns(42);

        ((IConfig)config).Init(configManager, environmentProvider, "Key");

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal(42, config.Value);
    }

    [Fact]
    public void Struct_Init_WithMissingValueAndNoDefault_DoesNotValidateDefaultStruct()
    {
        var config = new AnnotatedStructOptionsConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<AnnotatedStructOptions?>("Production", "Struct.Settings"))
            .Returns(null);

        ((IConfig)config).Init(configManager, environmentProvider, "Struct.Settings");

        Assert.False(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Null(config.Value);
    }

    [Fact]
    public void Init_WithValidAnnotatedObject_PopulatesValue()
    {
        var config = new AnnotatedOptionsConfig();
        var value = new AnnotatedOptions
        {
            Name = "payments",
            RetryCount = 3
        };

        Init(config, value);

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Same(value, config.Value);
    }

    [Fact]
    public void Init_WithInvalidAnnotatedObject_ThrowsStructuredValidationException()
    {
        var config = new AnnotatedOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new AnnotatedOptions { RetryCount = 7 }));

        Assert.Equal("App.Settings", exception.Key);
        Assert.Equal(typeof(AnnotatedOptionsConfig), exception.ConfigType);
        Assert.Equal(typeof(AnnotatedOptions), exception.ValueType);
        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains(
            exception.Failures,
            failure => failure.MemberNames.SequenceEqual(["Name"])
                       && failure.Message.Contains("Name", StringComparison.Ordinal));
        var nameFailure = Assert.Single(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Name"]));
        Assert.Equal("App.Settings", nameFailure.Key);
        Assert.Equal(typeof(AnnotatedOptionsConfig), nameFailure.ConfigType);
        Assert.Equal(typeof(AnnotatedOptions), nameFailure.ValueType);
        Assert.Contains(
            exception.Failures,
            failure => failure.MemberNames.SequenceEqual(["RetryCount"])
                       && failure.Message.Contains("between 1 and 5", StringComparison.Ordinal));
        Assert.DoesNotContain("7", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithInvalidPublicFields_ThrowsStructuredValidationException()
    {
        var config = new FieldAnnotatedOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new FieldAnnotatedOptions { RetryCount = 7 }));

        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Name"]));
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["RetryCount"]));
    }

    [Fact]
    public void ValidationFailures_AreImmutableSnapshots()
    {
        var memberNames = new List<string> { "Name" };
        var failure = new ConfigurationValidationFailure(
            "App.Settings",
            typeof(AnnotatedOptionsConfig),
            typeof(AnnotatedOptions),
            memberNames,
            "Name is required.");
        var failures = new List<ConfigurationValidationFailure> { failure };

        var exception = new ConfigurationValidationException(
            "App.Settings",
            typeof(AnnotatedOptionsConfig),
            typeof(AnnotatedOptions),
            failures);

        memberNames.Add("RetryCount");
        failures.Clear();

        Assert.Equal(["Name"], failure.MemberNames);
        Assert.Single(exception.Failures);
        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)failure.MemberNames).Add("Other"));
        Assert.Throws<NotSupportedException>(() => ((ICollection<ConfigurationValidationFailure>)exception.Failures).Clear());
    }

    [Fact]
    public void Init_WithInvalidDefault_ValidatesDefaultAndThrows()
    {
        var config = new DefaultAnnotatedOptionsConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<AnnotatedOptions>(A<string>._, A<string>._))
            .Returns(null);

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ((IConfig)config).Init(configManager, environmentProvider, "App.Settings"));

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["RetryCount"]));
    }

    [Fact]
    public void Struct_Init_WithInvalidAnnotatedObject_ThrowsStructuredValidationException()
    {
        var config = new AnnotatedStructOptionsConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<AnnotatedStructOptions?>("Production", "Struct.Settings"))
            .Returns(new AnnotatedStructOptions { RetryCount = 7 });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ((IConfig)config).Init(configManager, environmentProvider, "Struct.Settings"));

        Assert.Equal("Struct.Settings", exception.Key);
        Assert.Equal(typeof(AnnotatedStructOptionsConfig), exception.ConfigType);
        Assert.Equal(typeof(AnnotatedStructOptions), exception.ValueType);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Name"]));
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["RetryCount"]));
    }

    [Fact]
    public void Struct_Init_WithInvalidDefault_ValidatesDefaultAndThrows()
    {
        var config = new DefaultAnnotatedStructOptionsConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<AnnotatedStructOptions?>("Production", "Struct.Settings"))
            .Returns(null);

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ((IConfig)config).Init(configManager, environmentProvider, "Struct.Settings"));

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["RetryCount"]));
    }

    [Fact]
    public void Init_WithObjectLevelValidationFailure_UsesObjectFailureLabel()
    {
        var config = new ValidatableOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new ValidatableOptions { Invalid = true }));

        var failure = Assert.Single(exception.Failures);
        Assert.Empty(failure.MemberNames);
        Assert.Contains("- <object>: Object is invalid.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithMultipleMemberValidationResult_PreservesAllMemberNames()
    {
        var config = new MultiMemberValidatableOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new MultiMemberValidatableOptions()));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(["Second", "First"], failure.MemberNames);
        Assert.Contains("- First, Second: Both values are invalid.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithPropertyFailures_DoesNotForceObjectLevelValidation()
    {
        ShortCircuitOptions.ObjectValidationRan = false;
        var config = new ShortCircuitOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new ShortCircuitOptions()));

        Assert.False(ShortCircuitOptions.ObjectValidationRan);
        Assert.Single(exception.Failures);
    }

    [Fact]
    public void Init_WithNestedOptionsWithoutMarkers_DoesNotValidateRecursively()
    {
        var config = new NonRecursiveOptionsConfig();

        Init(
            config,
            new NonRecursiveOptions
            {
                Database = new NestedOptions(),
                Endpoints = [new EndpointOptions()]
            });

        Assert.True(config.HasValue);
    }

    [Fact]
    public void Init_WithUnmarkedThrowingGetter_DoesNotInvokeGetter()
    {
        var config = new OptionsWithUnmarkedThrowingGetterConfig();

        Init(config, new OptionsWithUnmarkedThrowingGetter { Name = "payments" });

        Assert.True(config.HasValue);
    }

    [Fact]
    public void Init_WithRecursiveMarkers_ValidatesNestedObjectAndCollectionItems()
    {
        var config = new RecursiveOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(
                config,
                new RecursiveOptions
                {
                    Database = new NestedOptions(),
                    Endpoints = [new EndpointOptions(), null, new EndpointOptions { Url = "https://example.test" }]
                }));

        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Database.Host"]));
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Endpoints[0].Url"]));
        Assert.DoesNotContain("Endpoints[1]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithRecursiveMarkersOnFields_ValidatesNestedObjectAndCollectionItems()
    {
        var config = new RecursiveFieldOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(
                config,
                new RecursiveFieldOptions
                {
                    Database = new NestedOptions(),
                    Endpoints = [new EndpointOptions()]
                }));

        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Database.Host"]));
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Endpoints[0].Url"]));
    }

    [Fact]
    public void Init_WithNestedObjectLevelValidationFailure_UsesNestedObjectPath()
    {
        var config = new OptionsWithNestedObjectLevelValidationConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(
                config,
                new OptionsWithNestedObjectLevelValidation
                {
                    Child = new NestedValidatableOptions { Invalid = true }
                }));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(["Child"], failure.MemberNames);
        Assert.Contains("- Child: Nested object is invalid.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithNullMarkedMembers_SkipsRecursiveValidation()
    {
        var config = new OptionsWithNullMarkedMembersConfig();

        Init(config, new OptionsWithNullMarkedMembers());

        Assert.True(config.HasValue);
    }

    [Fact]
    public void Init_WithUnsupportedEnumeratedItemsValidatorType_ReportsFailure()
    {
        var config = new UnsupportedEnumeratedItemsValidatorOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new UnsupportedEnumeratedItemsValidatorOptions()));

        Assert.Contains(
            exception.Failures,
            failure => failure.MemberNames.SequenceEqual(["Endpoints"])
                       && failure.Message.Contains(nameof(ValidateEnumeratedItemsAttribute), StringComparison.Ordinal));
    }

    [Fact]
    public void Init_WithRecursiveStructMember_ValidatesBoxedValueType()
    {
        var config = new OptionsWithStructNestedObjectConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new OptionsWithStructNestedObject()));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(["Child.Name"], failure.MemberNames);
    }

    [Fact]
    public void Init_WithSharedRecursiveReference_ReportsEachReachablePath()
    {
        var config = new OptionsWithSharedReferencesConfig();
        var shared = new NestedOptions();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(
                config,
                new OptionsWithSharedReferences
                {
                    Primary = shared,
                    Secondary = shared
                }));

        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Primary.Host"]));
        Assert.Contains(exception.Failures, failure => failure.MemberNames.SequenceEqual(["Secondary.Host"]));
    }

    [Fact]
    public void Init_WithCyclicRecursiveMarkers_DoesNotLoopForever()
    {
        var config = new CyclicOptionsConfig();
        var value = new CyclicOptions();
        value.Next = value;

        Init(config, value);

        Assert.True(config.HasValue);
    }

    [Fact]
    public void Init_WithUnsupportedOptionsValidatorType_ReportsFailure()
    {
        var config = new UnsupportedValidatorOptionsConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, new UnsupportedValidatorOptions()));

        Assert.Contains(
            exception.Failures,
            failure => failure.MemberNames.SequenceEqual(["Child"])
                       && failure.Message.Contains("custom validator types are not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void Init_WithScalarPrimitiveConfig_DoesNotRunDataAnnotations()
    {
        var config = new TestConfig();

        Init(config, string.Empty);

        Assert.True(config.HasValue);
        Assert.Equal(string.Empty, config.Value);
    }

    [Fact]
    public void Init_WithConfigValueNotEmptyString_RejectsEmptyAndWhitespace()
    {
        var emptyException = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new NotEmptyStringConfig(), string.Empty));
        var whitespaceException = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new NotEmptyStringConfig(), "   "));

        Assert.Empty(Assert.Single(emptyException.Failures).MemberNames);
        Assert.Contains("- <value>: The configuration value must not be empty.", emptyException.Message, StringComparison.Ordinal);
        Assert.Contains("- <value>: The configuration value must not be empty.", whitespaceException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   ", whitespaceException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithConfigValueNotEmptyString_AcceptsNonEmptyValue()
    {
        var config = new NotEmptyStringConfig();

        Init(config, "token");

        Assert.True(config.HasValue);
        Assert.Equal("token", config.Value);
    }

    [Fact]
    public void Init_WithConfigValueNotEmptyAndMissingValue_DoesNotRequirePresence()
    {
        var config = new NotEmptyStringConfig();
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<string>("Production", "App.Settings"))
            .Returns(null);

        ((IConfig)config).Init(configManager, environmentProvider, "App.Settings");

        Assert.False(config.HasValue);
        Assert.Null(config.Value);
    }

    [Fact]
    public void Init_WithRequiredClassConfigAndMissingValue_ThrowsStructuredValidationException()
    {
        var config = new RequiredStringConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, null));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal("App.Settings", failure.Key);
        Assert.Equal(typeof(RequiredStringConfig), failure.ConfigType);
        Assert.Equal(typeof(string), failure.ValueType);
        Assert.Empty(failure.MemberNames);
        Assert.Equal("A value is required for this configuration key.", failure.Message);
        Assert.Contains("- <value>: A value is required for this configuration key.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Production", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_Init_WithRequiredConfigAndMissingValue_ThrowsStructuredValidationException()
    {
        var config = new RequiredIntConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(config, null));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal("App.Settings", failure.Key);
        Assert.Equal(typeof(RequiredIntConfig), failure.ConfigType);
        Assert.Equal(typeof(int), failure.ValueType);
        Assert.Empty(failure.MemberNames);
        Assert.Equal("A value is required for this configuration key.", failure.Message);
        Assert.Contains("- <value>: A value is required for this configuration key.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Production", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithRequiredClassConfig_AcceptsProviderValue()
    {
        var config = new RequiredStringConfig();

        Init(config, "token");

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Equal("token", config.Value);
    }

    [Fact]
    public void Struct_Init_WithRequiredConfig_AcceptsProviderValueIncludingZero()
    {
        var config = new RequiredIntConfig();

        InitStruct(config, 0);

        Assert.True(config.HasValue);
        Assert.False(config.IsDefaultValue);
        Assert.Equal(0, config.Value);
    }

    [Fact]
    public void Init_WithRequiredClassDefaultValue_SatisfiesPresence()
    {
        var config = new DefaultRequiredStringConfig();

        Init(config, null);

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal("fallback", config.Value);
    }

    [Fact]
    public void Struct_Init_WithRequiredDefaultValue_SatisfiesPresence()
    {
        var config = new DefaultRequiredIntConfig();

        InitStruct(config, null);

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal(42, config.Value);
    }

    [Fact]
    public void Init_WithRequiredAndNotEmptyMissingValue_ReportsOnlyPresenceFailure()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new RequiredNotEmptyStringConfig(), null));

        var failure = Assert.Single(exception.Failures);
        Assert.Contains("required", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empty", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Init_WithRequiredAndNotEmptyEmptyValue_ReportsOnlyValueFailure()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new RequiredNotEmptyStringConfig(), string.Empty));

        var failure = Assert.Single(exception.Failures);
        Assert.Contains("must not be empty", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("required", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Init_WithInheritedRequiredAttribute_UsesDerivedConfigKeySemantics()
    {
        var config = new DerivedRequiredStringConfig();
        var key = ConfigKeyAttribute.GetKeyPath(config.GetType());

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, null, key));

        Assert.Equal($"{nameof(ConfigTests)}.{nameof(DerivedRequiredStringConfig)}", key);
        Assert.Equal($"{nameof(ConfigTests)}.{nameof(DerivedRequiredStringConfig)}", exception.Key);
        Assert.NotEqual("Base.Required", exception.Key);
        Assert.Equal(typeof(DerivedRequiredStringConfig), exception.ConfigType);
        Assert.Equal("A value is required for this configuration key.", Assert.Single(exception.Failures).Message);
    }

    [Fact]
    public void Init_WithRequiredObjectConfigAndMissingValue_ThrowsPresenceFailure()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new RequiredAnnotatedOptionsConfig(), null));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(typeof(RequiredAnnotatedOptionsConfig), failure.ConfigType);
        Assert.Equal(typeof(AnnotatedOptions), failure.ValueType);
        Assert.Empty(failure.MemberNames);
        Assert.Equal("A value is required for this configuration key.", failure.Message);
        Assert.Contains("- <object>: A value is required for this configuration key.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithRequiredObjectConfigAndInvalidValue_RunsDataAnnotations()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(
                new RequiredAnnotatedOptionsConfig(),
                new AnnotatedOptions
                {
                    Name = null,
                    RetryCount = 0
                }));

        Assert.DoesNotContain(
            exception.Failures,
            failure => failure.Message.Contains("required for this configuration key", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            exception.Failures,
            failure => failure.MemberNames.SequenceEqual(["Name"])
                       && failure.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Init_WithScalarClassDefaultValue_ValidatesDefault()
    {
        var config = new DefaultInvalidNotEmptyStringConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, null));

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal(string.Empty, config.Value);
        Assert.Contains("must not be empty", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithScalarClassDefaultValue_RunsValidateValueHook()
    {
        var config = new HookDefaultFailureConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(config, null));

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal("fallback", config.Value);
        Assert.Contains("Default hook failure.", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithConfigValueMinLength_RejectsShortString()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new MinLengthStringConfig(), "ab"));

        Assert.Contains("at least 3", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_Init_WithConfigValueRange_RejectsOutOfRangeIntAndDouble()
    {
        var intException = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(new RangedIntConfig(), 6));
        var doubleException = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(new RangedDoubleConfig(), 6.5));

        Assert.Contains("between 1 and 5", Assert.Single(intException.Failures).Message, StringComparison.Ordinal);
        Assert.Contains("between 1.5 and 5.5", Assert.Single(doubleException.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_Init_WithScalarDefaultValue_ValidatesDefault()
    {
        var config = new DefaultInvalidRangedIntConfig();

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(config, null));

        Assert.True(config.HasValue);
        Assert.True(config.IsDefaultValue);
        Assert.Equal(6, config.Value);
        Assert.Contains("between 1 and 5", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_Init_WithConfigValueRange_AcceptsValidIntAndDouble()
    {
        var intConfig = new RangedIntConfig();
        var doubleConfig = new RangedDoubleConfig();

        InitStruct(intConfig, 3);
        InitStruct(doubleConfig, 3.5);

        Assert.Equal(3, intConfig.Value);
        Assert.Equal(3.5, doubleConfig.Value);
    }

    [Fact]
    public void Struct_Init_WithConfigValueRange_AcceptsDoubleWithIntegerLiteralBounds()
    {
        var config = new RangedDoubleWithIntegerBoundsConfig();

        InitStruct(config, 3.5);

        Assert.Equal(3.5, config.Value);
    }

    [Fact]
    public void Struct_Init_WithConfigValueNotEmptyGuid_RejectsEmptyGuid()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(new NotEmptyGuidConfig(), Guid.Empty));

        Assert.Contains("must not be empty", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithUnsupportedBuiltInType_ThrowsStructuredValidationException()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(new UnsupportedMinLengthIntConfig(), 42));

        var failure = Assert.Single(exception.Failures);
        Assert.Empty(failure.MemberNames);
        Assert.Contains("ConfigValueMinLengthAttribute supports String values", failure.Message, StringComparison.Ordinal);
        Assert.IsType<ConfigurationValidationException>(exception);
    }

    [Fact]
    public void Init_WithInheritedConfigValueValidationAttribute_AppliesRule()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new DerivedInheritedScalarConfig(), "denied"));

        Assert.Contains("Only 'allowed' is accepted.", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithCustomConfigValueValidationAttribute_AppliesRule()
    {
        var config = new CustomScalarAttributeConfig();

        Init(config, "allowed");
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new CustomScalarAttributeConfig(), "denied"));

        Assert.Equal("allowed", config.Value);
        Assert.Contains("Only 'allowed' is accepted.", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithContextAwareAttribute_ReceivesGuaranteedValidationContextValues()
    {
        var config = new ContextAwareScalarConfig();

        Init(config, "value");

        Assert.Equal("value", config.Value);
    }

    [Fact]
    public void Init_WithValidateValueFailure_ThrowsStructuredValidationException()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new HookFailureConfig(), "value"));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(["HookMember"], failure.MemberNames);
        Assert.Contains("Hook failure.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithValidateValueSuccessNoise_IgnoresNullSuccessAndNullEnumerable()
    {
        Init(new HookSuccessNoiseConfig(), "value");
        Init(new HookNullEnumerableConfig(), "value");
    }

    [Fact]
    public void Init_WithValidateValueException_BubblesOriginalException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Init(new HookThrowsConfig(), "value"));

        Assert.Equal("Hook broke.", exception.Message);
    }

    [Fact]
    public void Init_WithAttributeAndHookFailures_CollectsAttributeFailuresFirst()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new AttributeAndHookFailureConfig(), "abc"));

        Assert.Equal(2, exception.Failures.Count);
        Assert.Contains("at least 5", exception.Failures[0].Message, StringComparison.Ordinal);
        Assert.Equal("Hook failure.", exception.Failures[1].Message);
    }

    [Fact]
    public void Struct_Init_WithValidateValueHook_RunsWithoutVirtualInit()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            InitStruct(new HookStructConfig(), 3));

        Assert.Contains("Struct hook failure.", Assert.Single(exception.Failures).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WithObjectConfig_IgnoresWrapperScalarAttributesAndHook()
    {
        ObjectConfigWithScalarValidation.HookRan = false;
        var config = new ObjectConfigWithScalarValidation();

        Init(
            config,
            new AnnotatedOptions
            {
                Name = "payments",
                RetryCount = 3
            });

        Assert.False(ObjectConfigWithScalarValidation.HookRan);
        Assert.True(config.HasValue);
    }

    [Fact]
    public void Init_WithScalarFailure_UsesValueFailureLabelWithoutChangingObjectLabel()
    {
        var scalarException = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new NotEmptyStringConfig(), string.Empty));
        var objectException = Assert.Throws<ConfigurationValidationException>(() =>
            Init(new ValidatableOptionsConfig(), new ValidatableOptions { Invalid = true }));

        Assert.Contains("- <value>:", scalarException.Message, StringComparison.Ordinal);
        Assert.Contains("- <object>:", objectException.Message, StringComparison.Ordinal);
    }

    private static void Init<T>(Config<T> config, T? value)
        where T : class
    {
        Init(config, value, "App.Settings");
    }

    private static void Init<T>(Config<T> config, T? value, string key)
        where T : class
    {
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<T>("Production", key))
            .Returns(value);

        ((IConfig)config).Init(configManager, environmentProvider, key);
    }

    private static void InitStruct<T>(ConfigStruct<T> config, T? value)
        where T : struct
    {
        var configManager = A.Fake<IConfigManager>();
        var environmentProvider = A.Fake<IEnvironmentProvider>();

        A.CallTo(() => environmentProvider.Environment).Returns("Production");
        A.CallTo(() => configManager.GetValue<T?>("Production", "App.Settings"))
            .Returns(value);

        ((IConfig)config).Init(configManager, environmentProvider, "App.Settings");
    }
}
