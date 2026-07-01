namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigValueConverterTests
{
    [Fact]
    public void TryConvert_Should_ConvertScalarEnumGuidAndJsonValues()
    {
        Assert.True(ConfigValueConverter.TryConvert("443", out int port));
        Assert.True(ConfigValueConverter.TryConvert("singlemachineselfhosted", out TestMode mode));
        Assert.True(ConfigValueConverter.TryConvert("47f6ca0f-5fdc-45d4-87d0-f5d9a69195f4", out Guid tenant));
        Assert.True(ConfigValueConverter.TryConvert("""{"Name":"Stripe","Retries":3}""", out SecretPayload? payload));

        Assert.Equal(443, port);
        Assert.Equal(TestMode.SingleMachineSelfHosted, mode);
        Assert.Equal(Guid.Parse("47f6ca0f-5fdc-45d4-87d0-f5d9a69195f4"), tenant);
        Assert.Equal(new SecretPayload("Stripe", 3), payload);
    }

    [Fact]
    public void TryConvert_Should_ReturnFalseWithoutThrowingForInvalidValues()
    {
        var converted = ConfigValueConverter.TryConvert("not-an-int-secret", typeof(int), out var value);

        Assert.False(converted);
        Assert.Null(value);
    }

    [Fact]
    public void TryConvert_Should_ReturnNullForEmptyNullableValue()
    {
        var converted = ConfigValueConverter.TryConvert(string.Empty, typeof(int?), out var value);

        Assert.True(converted);
        Assert.Null(value);
    }

    private enum TestMode
    {
        SingleMachineSelfHosted
    }

    private sealed record SecretPayload(string Name, int Retries);
}
