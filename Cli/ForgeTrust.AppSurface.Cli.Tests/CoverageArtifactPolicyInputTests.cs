using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactPolicyInputTests
{
    [Fact]
    public void Read_RejectsNullAndNonReadableStreams()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageArtifactPolicy.Read(null!));

        var unreadable = new MemoryStream();
        unreadable.Dispose();
        var exception = Assert.Throws<ArgumentException>(() => CoverageArtifactPolicy.Read(unreadable));

        Assert.Equal("policyStream", exception.ParamName);
    }
}
