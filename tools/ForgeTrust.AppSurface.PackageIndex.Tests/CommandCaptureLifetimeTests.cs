using System.Diagnostics;
using System.Globalization;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class CommandCaptureLifetimeTests
{
    [UnixFileSystemFact]
    public async Task ParentExitPolicyReturnsRealExitCodeWhileDescendantStillOwnsBothPipes()
    {
        foreach (var exitCode in new[] { 0, 23 })
        {
            await using var fixture = await InheritedPipes.StartAsync(completeOnExit: true, exitCode);
            fixture.ReleaseParent();
            await fixture.Parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // The descendant cannot close its pipes before explicit release in fixture disposal.
            var result = await fixture.Operation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(exitCode, result.ExitCode);
            Assert.True(result.StandardOutputTruncated);
            Assert.True(result.StandardErrorTruncated);
            Assert.False(fixture.Descendant.HasExited);
            Assert.Contains("[output incomplete after process exit]", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("[output incomplete after process exit]", result.StandardError, StringComparison.Ordinal);
        }
    }

    [UnixFileSystemFact]
    public async Task DefaultPolicyStillWaitsForDescendantEofAndRetainsCompleteOutput()
    {
        await using var fixture = await InheritedPipes.StartAsync(completeOnExit: false, exitCode: 0);
        fixture.ReleaseParent();
        await fixture.Parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Operation.IsCompleted);
        fixture.ReleaseDescendant();
        var result = await fixture.Operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
        Assert.Contains("child-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("child-error", result.StandardError, StringComparison.Ordinal);
    }

    [UnixFileSystemFact]
    public async Task ParentExitPolicyKeepsTimeoutAndCallerCancellationAuthoritative()
    {
        using var cancellation = new CancellationTokenSource();
        await using (var cancelled = await InheritedPipes.StartAsync(true, 0, cancellation.Token))
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Operation);
        }

        await using var timedOut = await InheritedPipes.StartAsync(true, 0, timeoutMilliseconds: 2_000);
        var result = await timedOut.Operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("timed out", result.StandardError, StringComparison.Ordinal);
    }

    [UnixFileSystemFact]
    public async Task PartialStartupCancelsOwnedProcessesAndRetainsRootUntilIdentityIsVerified()
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-inherited-pipes-fault-" + Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
                InheritedPipes.StartAsync(true, 0, ownedRoot: root, omitChildReady: true));

        Assert.Contains("retained", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(root));
        foreach (var file in new[] { "parent.pid", "child.pid" })
        {
            var pid = int.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(root, file)), CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.HasExited, "An owned startup process remained alive after cancellation.");
            }
            catch (ArgumentException)
            {
                // The OS has already reaped the owned process.
            }
        }
        // Delete only after the stopped identities above were verified; failed verification retains evidence.
        Directory.Delete(root, recursive: true);
    }

    [UnixFileSystemFact]
    public async Task ParentExitPolicyHandlesAnImmediatelyExitingCommand()
    {
        var result = await new CliWrapCommandRunner().RunAsync(
            new ExternalCommandRequest("/bin/sh", ["-c", "exit 23"], Path.GetTempPath(),
                "immediate exit", "checking process exit", 5_000,
                CapturePolicy: new ExternalCapturePolicy(256, CompleteOnProcessExit: true)), CancellationToken.None);

        Assert.Equal(23, result.ExitCode);
    }

    /// <summary>Owns a direct child and a pipe-inheriting descendant with explicit file release barriers.</summary>
    private sealed class InheritedPipes : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _ownedCancellation;

        private InheritedPipes(string? ownedRoot, CancellationToken cancellationToken)
        {
            _root = ownedRoot ?? TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-inherited-pipes-" + Guid.NewGuid().ToString("N"));
            _ownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        internal Task<ExternalCommandResult> Operation { get; private set; } = null!;
        internal Process Parent { get; private set; } = null!;
        internal Process Descendant { get; private set; } = null!;

        internal static async Task<InheritedPipes> StartAsync(bool completeOnExit, int exitCode,
            CancellationToken cancellationToken = default, int timeoutMilliseconds = 10_000,
            string? ownedRoot = null, bool omitChildReady = false)
        {
            var fixture = new InheritedPipes(ownedRoot, cancellationToken);
            Directory.CreateDirectory(fixture._root);
            var childScript = """
                printf '%s' "$$" > "$1/child.pid"
                printf ready > "$1/child.ready"
                i=0
                while [ ! -f "$1/child.release" ] && [ "$i" -lt 400 ]; do sleep 0.05; i=$((i+1)); done
                printf child-output
                printf child-error >&2
                """;
            if (omitChildReady)
            {
                childScript = childScript.Replace("printf ready > \"$1/child.ready\"", string.Empty, StringComparison.Ordinal);
            }
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture._root, "child.sh"), childScript);
            var parentPath = TestPathUtils.PathUnder(fixture._root, "parent.sh");
            await File.WriteAllTextAsync(parentPath, """
                /bin/sh "$1/child.sh" "$1" &
                printf '%s' "$$" > "$1/parent.pid"
                printf ready > "$1/parent.ready"
                i=0
                while [ ! -f "$1/parent.release" ] && [ "$i" -lt 400 ]; do sleep 0.05; i=$((i+1)); done
                exit "$2"
                """);
            fixture.Operation = new CliWrapCommandRunner().RunAsync(
                new ExternalCommandRequest("/bin/sh", [parentPath, fixture._root, exitCode.ToString(CultureInfo.InvariantCulture)],
                    fixture._root, "pipe inheritance fixture", "observing the direct child", timeoutMilliseconds,
                    CapturePolicy: new ExternalCapturePolicy(256, CompleteOnProcessExit: completeOnExit)), fixture._ownedCancellation.Token);
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await fixture.WaitForFileAsync("parent.ready", startup.Token);
                fixture.Parent = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(fixture._root, "parent.pid")), CultureInfo.InvariantCulture));
                await fixture.WaitForFileAsync("child.ready", startup.Token);
                fixture.Descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(fixture._root, "child.pid")), CultureInfo.InvariantCulture));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal void ReleaseParent() => File.WriteAllText(TestPathUtils.PathUnder(_root, "parent.release"), "release");
        internal void ReleaseDescendant() => File.WriteAllText(TestPathUtils.PathUnder(_root, "child.release"), "release");

        private async Task WaitForFileAsync(string fileName, CancellationToken cancellationToken)
        {
            var path = TestPathUtils.PathUnder(_root, fileName);
            while (!File.Exists(path)) await Task.Delay(10, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            // Cancel before releasing the parent barrier, while the runner can still terminate its owned tree.
            _ownedCancellation.Cancel();
            ReleaseParent();
            ReleaseDescendant();
            try
            {
                try { await Operation.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (_ownedCancellation.IsCancellationRequested) { }
                if (Parent is not null) await Parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (Descendant is not null) await Descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (Parent is null || Descendant is null)
                {
                    throw new Xunit.Sdk.XunitException("Partial startup identities could not be verified; the owned root was retained.");
                }
            }
            finally
            {
                Parent?.Dispose();
                Descendant?.Dispose();
                _ownedCancellation.Dispose();
            }
            Directory.Delete(_root, recursive: true);
        }
    }
}
