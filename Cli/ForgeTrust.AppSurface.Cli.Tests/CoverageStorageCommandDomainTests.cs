using System.Text.Json;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies that optional domain observation never prevents rendering the primary storage result.</summary>
public sealed class CoverageStorageCommandDomainTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("state")]
    [InlineData("timeout")]
    public void DomainId_NonfatalFailuresReturnUnknownWithoutLeakingExceptionText(string kind)
    {
        Exception failure = kind switch
        {
            "json" => new JsonException("/private/machine/domain"),
            "state" => new InvalidOperationException("/private/machine/domain"),
            _ => new TimeoutException("/private/machine/domain")
        };

        Assert.Equal("domain-unknown", CoverageStorageCommandBase.DomainId(() => throw failure));
    }

    [Fact]
    public void DomainId_DoesNotSwallowFatalFailures()
    {
        var failure = new OutOfMemoryException("fatal observation failure");
        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(
            () => CoverageStorageCommandBase.DomainId(() => throw failure)));
    }
}
