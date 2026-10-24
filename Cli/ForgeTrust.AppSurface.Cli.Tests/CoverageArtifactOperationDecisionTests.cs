using System.ComponentModel;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactOperationDecisionTests
{
    private const string LockFileName = ".appsurface-coverage-operation.lock";

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 6)]
    public void UnixLock_ShouldUseSharedOrExclusiveNonblockingFlags_AndUnlockOnlyOnce(bool exclusive, int expectedFlags)
    {
        using var directory = LeaseDirectory.Create();
        var operations = new List<int>();
        var options = new CoverageArtifactOperationLeaseOptions
        {
            IsWindows = false,
            UnixFlock = (_, operation) =>
            {
                operations.Add(operation);
                return new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0);
            },
        };

        var lease = CoverageArtifactOperationLease.Acquire(directory.Path, exclusive, options);
        Assert.Equal(expectedFlags, operations.Single());

        lease.Dispose();
        lease.Dispose();

        Assert.Equal([expectedFlags, 8], operations);
        Assert.Throws<ObjectDisposedException>(lease.VerifyBinding);
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 3u)]
    public void WindowsLock_ShouldUseSharedOrExclusiveFailFastFlags(bool exclusive, uint expectedFlags)
    {
        using var directory = LeaseDirectory.Create();
        SafeFileHandle? lockHandle = null;
        var lockCalls = 0;
        var bindingCalls = 0;
        var options = WindowsOptions(
            onOpened: (_, handle) => lockHandle = handle,
            tryLock: (_, flags) =>
            {
                lockCalls++;
                Assert.Equal(expectedFlags, flags);
                return new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0);
            },
            verifyBinding: (retainedDirectory, handle, expectedIdentity) =>
            {
                bindingCalls++;
                retainedDirectory.VerifyArtifactBinding();
                Assert.Equal(expectedIdentity, CoverageRunOutputLease.GetArtifactObjectIdentity(handle));
            });

        var lease = CoverageArtifactOperationLease.Acquire(directory.Path, exclusive, options);
        lease.VerifyBinding();
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, lockCalls);
        Assert.Equal(2, bindingCalls);
        Assert.NotNull(lockHandle);
        Assert.True(lockHandle.IsClosed);
        Assert.Throws<ObjectDisposedException>(lease.VerifyBinding);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(35)]
    public void UnixLock_ShouldMapContentionErrorsToAscov202(int errorCode)
    {
        using var directory = LeaseDirectory.Create();
        var unlockCalls = 0;
        var options = new CoverageArtifactOperationLeaseOptions
        {
            IsWindows = false,
            UnixFlock = (_, operation) =>
            {
                if (operation == 8) unlockCalls++;
                return new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: errorCode);
            },
        };

        var exception = Assert.Throws<CoverageExecutionException>(
            () => CoverageArtifactOperationLease.Acquire(directory.Path, exclusive: true, options));

        Assert.Equal("ASCOV202", exception.Code);
        Assert.Equal(0, unlockCalls);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(158)]
    public void WindowsLock_ShouldMapContentionErrorsToAscov202(int errorCode)
    {
        using var directory = LeaseDirectory.Create();
        SafeFileHandle? lockHandle = null;
        var options = WindowsOptions(
            onOpened: (_, handle) => lockHandle = handle,
            tryLock: (_, _) => new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: errorCode));

        var exception = Assert.Throws<CoverageExecutionException>(
            () => CoverageArtifactOperationLease.Acquire(directory.Path, exclusive: false, options));

        Assert.Equal("ASCOV202", exception.Code);
        Assert.NotNull(lockHandle);
        Assert.True(lockHandle.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedNativeLockError_ShouldThrowIOExceptionWithNativeCode(bool windows)
    {
        using var directory = LeaseDirectory.Create();
        const int errorCode = 5;
        var options = windows
            ? WindowsOptions(tryLock: (_, _) => new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: errorCode))
            : new CoverageArtifactOperationLeaseOptions
            {
                IsWindows = false,
                UnixFlock = (_, _) => new CoverageArtifactNativeLockResult(Acquired: false, ErrorCode: errorCode),
            };

        var exception = Assert.Throws<IOException>(
            () => CoverageArtifactOperationLease.Acquire(directory.Path, exclusive: true, options));

        Assert.Equal(errorCode, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
    }

    [Fact]
    public void UnixBindingFailureAfterAcquisition_ShouldUnlockAndDisposeRetainedDirectory()
    {
        using var directory = LeaseDirectory.Create();
        SafeFileHandle? retainedDirectoryHandle = null;
        var operations = new List<int>();
        var bindingFailure = new IOException("simulated directory binding failure");
        var options = new CoverageArtifactOperationLeaseOptions
        {
            IsWindows = false,
            UnixFlock = (_, operation) =>
            {
                operations.Add(operation);
                return new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0);
            },
            VerifyUnixBinding = current =>
            {
                retainedDirectoryHandle = current.ArtifactDirectoryHandle;
                throw bindingFailure;
            },
        };

        var actual = Assert.Throws<IOException>(
            () => CoverageArtifactOperationLease.Acquire(directory.Path, exclusive: true, options));

        Assert.Same(bindingFailure, actual);
        Assert.Equal([6, 8], operations);
        Assert.NotNull(retainedDirectoryHandle);
        Assert.True(retainedDirectoryHandle.IsClosed);
    }

    [Fact]
    public void WindowsBindingFailureAfterAcquisition_ShouldDisposeLockAndRetainedDirectory()
    {
        using var directory = LeaseDirectory.Create();
        SafeFileHandle? retainedDirectoryHandle = null;
        SafeFileHandle? lockHandle = null;
        var bindingFailure = new IOException("simulated Windows lock binding failure");
        var options = WindowsOptions(
            onOpened: (current, handle) =>
            {
                retainedDirectoryHandle = current.ArtifactDirectoryHandle;
                lockHandle = handle;
            },
            tryLock: (_, _) => new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0),
            verifyBinding: (_, _, _) => throw bindingFailure);

        var actual = Assert.Throws<IOException>(
            () => CoverageArtifactOperationLease.Acquire(directory.Path, exclusive: true, options));

        Assert.Same(bindingFailure, actual);
        Assert.NotNull(lockHandle);
        Assert.True(lockHandle.IsClosed);
        Assert.NotNull(retainedDirectoryHandle);
        Assert.True(retainedDirectoryHandle.IsClosed);
    }

    private static CoverageArtifactOperationLeaseOptions WindowsOptions(
        Action<CoverageRunOutputLease, SafeFileHandle>? onOpened = null,
        Func<SafeFileHandle, uint, CoverageArtifactNativeLockResult>? tryLock = null,
        Action<CoverageRunOutputLease, SafeFileHandle, string>? verifyBinding = null)
        => new()
        {
            IsWindows = true,
            OpenWindowsLock = directory =>
            {
                var handle = File.OpenHandle(
                    TestPathUtils.PathUnder(directory.ArtifactDirectoryPath, LockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                onOpened?.Invoke(directory, handle);
                return handle;
            },
            TryWindowsLock = tryLock ?? ((_, _) => new CoverageArtifactNativeLockResult(Acquired: true, ErrorCode: 0)),
            VerifyWindowsLockBinding = verifyBinding ?? ((directory, handle, expectedIdentity) =>
            {
                directory.VerifyArtifactBinding();
                Assert.Equal(expectedIdentity, CoverageRunOutputLease.GetArtifactObjectIdentity(handle));
            }),
        };

    private sealed class LeaseDirectory : IDisposable
    {
        private LeaseDirectory(string path)
        {
            Path = path;
        }

        internal string Path { get; }

        internal static LeaseDirectory Create()
        {
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "coverage-operation-decision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new LeaseDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
