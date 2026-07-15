using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.Tests;

public sealed class DeploymentValueContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("1job")]
    [InlineData("job_name")]
    [InlineData("job.name")]
    public void LogicalIdRejectsNonCanonicalValues(string? value)
    {
        var error = Assert.Throws<ArgumentException>(() => new DeploymentLogicalId(value!));
        Assert.Contains("ASDEPLOY101", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalIdRejectsMoreThanSixtyThreeCharacters()
    {
        Assert.Throws<ArgumentException>(() => new DeploymentLogicalId("a" + new string('b', 63)));
    }

    [Fact]
    public void LogicalIdPreservesCanonicalValue()
    {
        var id = new DeploymentLogicalId("migration-job-1");
        Assert.Equal("migration-job-1", id.Value);
        Assert.Equal(id.Value, id.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("registry.example/app:latest")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("REGISTRY/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("registry/app@sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("registry/app@sha256:aaaa")]
    public void ImageRejectsMutableOrMalformedValues(string? value)
    {
        var error = Assert.Throws<ArgumentException>(() => new ImmutableImageReference(value!));
        Assert.Contains("ASDEPLOY102", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagePreservesFullImmutableIdentity()
    {
        var value = $"us-docker.pkg.dev/project/repository/migrations@sha256:{new string('a', 64)}";
        var image = new ImmutableImageReference(value);
        Assert.Equal(value, image.Value);
        Assert.Equal(value, image.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void SourceRevisionRejectsMalformedValues(string? value)
    {
        var error = Assert.Throws<ArgumentException>(() => new SourceRevision(value!));
        Assert.Contains("ASDEPLOY103", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceRevisionPreservesFullCommit()
    {
        var revision = new SourceRevision(new string('b', 40));
        Assert.Equal(new string('b', 40), revision.Value);
        Assert.Equal(revision.Value, revision.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExecutionPolicyRejectsNonPositiveTasks(int tasks)
    {
        Assert.Contains("ASDEPLOY104", Assert.Throws<ArgumentOutOfRangeException>(() => new DeploymentExecutionPolicy(tasks, 1, 0, TimeSpan.FromMinutes(1))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void ExecutionPolicyRejectsInvalidParallelism(int parallelism)
    {
        Assert.Contains("ASDEPLOY105", Assert.Throws<ArgumentOutOfRangeException>(() => new DeploymentExecutionPolicy(2, parallelism, 0, TimeSpan.FromMinutes(1))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPolicyRejectsNegativeRetries()
    {
        Assert.Contains("ASDEPLOY106", Assert.Throws<ArgumentOutOfRangeException>(() => new DeploymentExecutionPolicy(1, 1, -1, TimeSpan.FromMinutes(1))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidTimeouts))]
    public void ExecutionPolicyRejectsInvalidTimeout(TimeSpan timeout)
    {
        Assert.Contains("ASDEPLOY107", Assert.Throws<ArgumentOutOfRangeException>(() => new DeploymentExecutionPolicy(1, 1, 0, timeout)).Message, StringComparison.Ordinal);
    }

    public static TheoryData<TimeSpan> InvalidTimeouts => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromTicks(-1),
        TimeSpan.FromHours(24) + TimeSpan.FromTicks(1),
    };

    [Fact]
    public void ExecutionPolicyRetainsExplicitBounds()
    {
        var policy = new DeploymentExecutionPolicy(4, 2, 3, TimeSpan.FromHours(24));
        Assert.Equal(4, policy.Tasks);
        Assert.Equal(2, policy.Parallelism);
        Assert.Equal(3, policy.Retries);
        Assert.Equal(TimeSpan.FromHours(24), policy.Timeout);
    }
}
