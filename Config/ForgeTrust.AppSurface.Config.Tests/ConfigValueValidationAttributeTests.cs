using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace ForgeTrust.AppSurface.Config.Tests;

public class ConfigValueValidationAttributeTests
{
    [Fact]
    public void BaseAttribute_AllowsMultipleInheritedClassUsage()
    {
        var usage = typeof(ConfigValueValidationAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.True(usage.AllowMultiple);
        Assert.True(usage.Inherited);
    }

    [Fact]
    public void NotEmpty_ValidatesStringsAndGuids()
    {
        var attribute = new ConfigValueNotEmptyAttribute();

        AssertValid(attribute, "value");
        AssertValid(attribute, Guid.NewGuid());
        AssertValid(attribute, null);
        AssertInvalid(attribute, string.Empty, "must not be empty");
        AssertInvalid(attribute, "   ", "must not be empty");
        AssertInvalid(attribute, Guid.Empty, "must not be empty");
    }

    [Fact]
    public void NotEmpty_UnsupportedTypeReturnsValidationFailure()
    {
        var attribute = new ConfigValueNotEmptyAttribute();

        AssertInvalid(attribute, 1, "ConfigValueNotEmptyAttribute supports String and Guid values");
    }

    [Fact]
    public void Range_IntConstructor_ValidatesIntAndDoubleValues()
    {
        var attribute = new ConfigValueRangeAttribute(1, 5);

        Assert.Equal(1, attribute.Minimum);
        Assert.Equal(5, attribute.Maximum);
        AssertValid(attribute, null);
        AssertValid(attribute, attribute.Minimum);
        AssertValid(attribute, 3);
        AssertValid(attribute, 3.5);
        AssertValid(attribute, attribute.Maximum);
        AssertInvalid(attribute, 0, "between 1 and 5");
        AssertInvalid(attribute, 0.5, "between 1 and 5");
        AssertInvalid(attribute, 6, "between 1 and 5");
        AssertInvalid(attribute, 6.0, "between 1 and 5");
        AssertInvalid(attribute, 3m, "supports Int32 and Double values");
    }

    [Fact]
    public void Range_DoubleConstructor_ValidatesIntAndDoubleValues()
    {
        var attribute = new ConfigValueRangeAttribute(1.5, 5.5);

        Assert.Equal(1.5, attribute.Minimum);
        Assert.Equal(5.5, attribute.Maximum);
        AssertValid(attribute, null);
        AssertValid(attribute, attribute.Minimum);
        AssertValid(attribute, 3);
        AssertValid(attribute, 3.5);
        AssertValid(attribute, attribute.Maximum);
        AssertInvalid(attribute, 1, "between 1.5 and 5.5");
        AssertInvalid(attribute, 1.0, "between 1.5 and 5.5");
        AssertInvalid(attribute, 6, "between 1.5 and 5.5");
        AssertInvalid(attribute, 6.0, "between 1.5 and 5.5");
        AssertInvalid(attribute, 3m, "supports Int32 and Double values");
    }

    [Fact]
    public void Range_ConstructorsRejectInvertedBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigValueRangeAttribute(5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigValueRangeAttribute(5.5, 1.5));
    }

    [Fact]
    public void Range_DoubleConstructorRejectsNaNBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigValueRangeAttribute(double.NaN, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigValueRangeAttribute(1.5, double.NaN));
    }

    [Fact]
    public void Range_CustomErrorMessagePreservesDisplayNamePlaceholder()
    {
        var attribute = new ConfigValueRangeAttribute(1, 5)
        {
            ErrorMessage = "{0} must be between {1} and {2}."
        };

        AssertInvalid(attribute, 0, "Test must be between 1 and 5.");
    }

    [Fact]
    public void MinLength_ValidatesStringValues()
    {
        var attribute = new ConfigValueMinLengthAttribute(3);

        Assert.Equal(3, attribute.Length);
        AssertValid(attribute, null);
        AssertValid(attribute, "abc");
        AssertInvalid(attribute, "ab", "at least 3");
        AssertInvalid(attribute, 3, "ConfigValueMinLengthAttribute supports String values");
    }

    [Fact]
    public void MinLength_RejectsNegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigValueMinLengthAttribute(-1));
    }

    [Fact]
    public void MinLength_CustomErrorMessagePreservesDisplayNamePlaceholder()
    {
        var attribute = new ConfigValueMinLengthAttribute(3)
        {
            ErrorMessage = "{0} must be at least {1} characters."
        };

        AssertInvalid(attribute, "ab", "Test must be at least 3 characters.");
    }

    private static void AssertValid(ValidationAttribute attribute, object? value)
    {
        var results = Validate(attribute, value);
        Assert.Empty(results);
    }

    private static void AssertInvalid(ValidationAttribute attribute, object? value, string expectedMessage)
    {
        var result = Assert.Single(Validate(attribute, value));
        Assert.Contains(expectedMessage, result.ErrorMessage, StringComparison.Ordinal);
    }

    private static List<ValidationResult> Validate(ValidationAttribute attribute, object? value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateValue(
            value,
            new ValidationContext(new object())
            {
                DisplayName = "Test"
            },
            results,
            [attribute]);

        return results;
    }
}
