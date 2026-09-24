namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class PlatformLocalSecretMaintenanceLeaseTests
{
    [Fact]
    public void Acquire_CancelledRequestNeverCreatesStateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cancelled-lease-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PlatformLocalSecretMaintenanceLease.Acquire(
            directory, "App", "Development", null, TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Acquire_CancellationInterruptsContendingWriterBeforeDeadline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cancelled-contender-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var lease = PlatformLocalSecretMaintenanceLease.Acquire(directory, "App", "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            using var started = new ManualResetEventSlim();
            Exception? failure = null;
            var contender = new Thread(() =>
            {
                started.Set();
                failure = Record.Exception(() =>
                {
                    using var blocked = PlatformLocalSecretMaintenanceLease.Acquire(directory, "app", "development", "other-prefix", TimeSpan.FromSeconds(10), cancellation.Token);
                });
            });
            contender.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
            Assert.False(contender.Join(TimeSpan.FromMilliseconds(75)));
            cancellation.Cancel();
            Assert.True(contender.Join(TimeSpan.FromSeconds(2)));
            Assert.IsType<OperationCanceledException>(failure);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Acquire_UnsafeSymlinkLeasePathStopsBeforeOpeningTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "unsafe-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var target = Path.Combine(directory, "target");
            File.WriteAllText(target, "unchanged-marker");
            var link = Path.Combine(directory, "lease");
            File.CreateSymbolicLink(link, target);
            Assert.Throws<IOException>(() => PlatformLocalSecretMaintenanceLease.AcquireFile(link, TimeSpan.Zero, CancellationToken.None));
            Assert.Equal("unchanged-marker", File.ReadAllText(target));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Acquire_ShouldExcludeIndependentProcessWithDifferentTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "platform-process-" + Guid.NewGuid().ToString("N"));
        System.Diagnostics.Process? process = null;
        try
        {
            using (PlatformLocalSecretMaintenanceLease.Acquire(directory, "App", "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None))
            {
                var signal = Path.Combine(directory, "started");
                process = LocalSecretTestProcess.Start("lease", directory, signal);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(signal), TimeSpan.FromSeconds(10)));
                Assert.False(process.WaitForExit(100));
            }
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Acquire_ShouldExcludeAnotherHandleForTheSamePackageIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appsurface-lease-test-{Guid.NewGuid():N}");
        try
        {
            using var first = PlatformLocalSecretMaintenanceLease.Acquire(
                directory, "MyApp", "Development", "Payments", TimeSpan.FromSeconds(1), CancellationToken.None);

            Exception? failure = null;
            var contender = new Thread(() => failure = Record.Exception(() =>
            {
                using var lease = PlatformLocalSecretMaintenanceLease.Acquire(
                    directory, "MyApp", "Development", "Payments", TimeSpan.FromMilliseconds(75), CancellationToken.None);
            }));
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(2)));
            Assert.IsType<IOException>(failure);

            first.Dispose();
            using var afterRelease = PlatformLocalSecretMaintenanceLease.Acquire(
                directory, "MyApp", "Development", "Payments", TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Acquire_ShouldUseDifferentLocksForDifferentPackageIdentities()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appsurface-lease-test-{Guid.NewGuid():N}");
        try
        {
            using var first = PlatformLocalSecretMaintenanceLease.Acquire(
                directory, "MyApp", "Development", "Payments", TimeSpan.FromSeconds(1), CancellationToken.None);
            using var second = PlatformLocalSecretMaintenanceLease.Acquire(
                directory, "DifferentApp", "Development", "Orders", TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
