using System.ComponentModel;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Direct exception-family truth tables; no owner, process, handle, admission or native authority is created.</summary>
public sealed class SupervisionExceptionPolicyTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(EvidenceAdmissionException))]
    public void BroadPolicyAcceptsOrdinaryRecoverableFamilies(Type type)
    {
        Assert.True(SupervisionExceptionPolicy.IsRecoverable(Create(type)));
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    public void BroadPolicyRejectsExactlyTheThreeFatalRuntimeFamilies(Type type)
    {
        Assert.False(SupervisionExceptionPolicy.IsRecoverable(Create(type)));
    }

    [Fact]
    public void BroadPolicyPreservesTheOriginalNullFilterBehavior()
    {
        Assert.True(SupervisionExceptionPolicy.IsRecoverable(null!));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("platform")]
    [InlineData("argument")]
    [InlineData("admission")]
    [InlineData("disposed")]
    [InlineData("invalid-operation")]
    [InlineData("overflow")]
    public void BothWorkspacePoliciesAcceptEachOriginalNarrowFamily(string family)
    {
        var error = CreateNarrow(family);
        Assert.True(SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(error));
        Assert.True(SupervisionExceptionPolicy.IsApplicationWorkspaceRecoverable(error));
    }

    [Fact]
    public void Win32IsApplicationOnlyAndNullRemainsNarrowlyRejected()
    {
        Assert.True(SupervisionExceptionPolicy.IsApplicationWorkspaceRecoverable(new Win32Exception(13)));
        Assert.False(SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(new Win32Exception(13)));
        Assert.False(SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(null!));
        Assert.False(SupervisionExceptionPolicy.IsApplicationWorkspaceRecoverable(null!));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("cancelled")]
    [InlineData("aggregate-io")]
    public void NarrowPoliciesDoNotUnwrapOrBroadenUnlistedCanaries(string kind)
    {
        var error = kind switch
        {
            "ordinary" => new Exception("canary"),
            "cancelled" => new OperationCanceledException("canary"),
            "aggregate-io" => new AggregateException(new IOException("canary")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        Assert.False(SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(error));
        Assert.False(SupervisionExceptionPolicy.IsApplicationWorkspaceRecoverable(error));
        Assert.True(SupervisionExceptionPolicy.IsRecoverable(error));
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(StackOverflowException))]
    [InlineData(typeof(AccessViolationException))]
    [InlineData(typeof(OperationCanceledException))]
    public void NarrowWorkspacePoliciesRejectFatalAndUnlistedFamilies(Type type)
    {
        var error = Create(type);
        Assert.False(SupervisionExceptionPolicy.IsProducerWorkspaceRecoverable(error));
        Assert.False(SupervisionExceptionPolicy.IsApplicationWorkspaceRecoverable(error));
    }

    private static Exception Create(Type type) => type == typeof(IOException) ? new IOException("test")
        : type == typeof(ArgumentException) ? new ArgumentException("test")
        : type == typeof(ObjectDisposedException) ? new ObjectDisposedException("test")
        : type == typeof(EvidenceAdmissionException) ? LinuxProtectedDeployment.InvalidDeployment()
        : type == typeof(OutOfMemoryException) ? new OutOfMemoryException("test")
        : type == typeof(StackOverflowException) ? new StackOverflowException("test")
        : type == typeof(AccessViolationException) ? new AccessViolationException("test")
        : type == typeof(OperationCanceledException) ? new OperationCanceledException("test")
        : throw new ArgumentOutOfRangeException(nameof(type), type, null);

    private static Exception CreateNarrow(string family) => family switch
    {
        "io" => new IOException("test"),
        "unauthorized" => new UnauthorizedAccessException("test"),
        "platform" => new PlatformNotSupportedException("test"),
        "argument" => new ArgumentException("test"),
        "admission" => LinuxProtectedDeployment.InvalidDeployment(),
        "disposed" => new ObjectDisposedException("test"),
        "invalid-operation" => new InvalidOperationException("test"),
        "overflow" => new OverflowException("test"),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };
}
