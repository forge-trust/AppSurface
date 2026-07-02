namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerResultContractTests
{
    [Fact]
    public void PublicEnums_Should_KeepStableNumericValues()
    {
        Assert.Equal(0, (int)GoogleSecretManagerResultStatus.Unclaimed);
        Assert.Equal(1, (int)GoogleSecretManagerResultStatus.Found);
        Assert.Equal(2, (int)GoogleSecretManagerResultStatus.Missing);
        Assert.Equal(3, (int)GoogleSecretManagerResultStatus.AccessDenied);
        Assert.Equal(4, (int)GoogleSecretManagerResultStatus.Unavailable);
        Assert.Equal(5, (int)GoogleSecretManagerResultStatus.InvalidResource);
        Assert.Equal(6, (int)GoogleSecretManagerResultStatus.InvalidPayload);
        Assert.Equal(7, (int)GoogleSecretManagerResultStatus.ConversionFailed);
        Assert.Equal(8, (int)GoogleSecretManagerResultStatus.Cancelled);
        Assert.Equal(9, (int)GoogleSecretManagerResultStatus.ProviderFailed);
    }
}
