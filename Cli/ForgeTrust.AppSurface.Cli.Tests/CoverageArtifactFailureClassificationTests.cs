using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactFailureClassificationTests
{
    public static TheoryData<Exception> OptionalFailures => new()
    {
        new IOException(), new InvalidDataException(), new UnauthorizedAccessException(), new JsonException(),
        new PlatformNotSupportedException(), new TimeoutException(), new NotSupportedException(),
        new InvalidOperationException(), new ArgumentException(), new CoverageExecutionException("busy", 1),
        new EntryPointNotFoundException(), new DllNotFoundException()
    };

    [Theory]
    [MemberData(nameof(OptionalFailures))]
    public void KnownOptionalFailuresAreSecondaryToCoverage(Exception error)
        => Assert.True(CoverageArtifactSession.IsOptionalFailure(error));

    [Fact]
    public void ProgrammingFailureAndCallerCancellationAreNotSwallowedByOptionalFailurePolicy()
    {
        Assert.False(CoverageArtifactSession.IsOptionalFailure(new NullReferenceException()));
        Assert.False(CoverageArtifactSession.IsOptionalFailure(new OperationCanceledException()));
    }
}
